using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;

namespace EngineSim
{
    // [Experimental] simulated drivetrain: the simulated engine powers the vehicle in place of the
    // game's engine model. The game's engine (NWH EngineComponent) keeps integrating its speed and
    // driving the clutch and gearbox, but the torque it runs on is the simulated engine's
    // (calculateTorqueDelegate, which NWH calls every step whatever its own state), at the simulated
    // crank's inertia; the simulation holds its crank at that speed.
    //
    // The simulation's inputs are physical: the key (ignition), the starter, the throttle and the fuel
    // supply. Nothing decides whether the engine "runs": it fires when its crank turns with the key on
    // and fuel there, however it is turned (starter, wheels, a push), and stalls, starves or fails to
    // catch on its own. The key and the starter are the dashboard switch's (IgnitionSwitch).
    //
    // The game's own engine states are out of the way while coupled: its start, stop and start / stop
    // (starter coroutine, throttle auto-start, damage stop, stall stop, the start / stop key) do
    // nothing (StartPatch, StopPatch, StartStopPatch), its stalling is off, and its ignition flag just
    // mirrors the key for whatever else in the game reads it. Released, everything is put back.
    internal sealed class DriveCoupling
    {
        // the coupled engines, for the start / stop patches
        internal static readonly Dictionary<EngineComponent, DriveCoupling> Active = new Dictionary<EngineComponent, DriveCoupling>();

        private readonly EngineSession _session;
        private readonly EngineComponent _engine;
        private readonly EngineComponent.CalculateTorque _vanilla, _ours;
        private readonly bool _stalling, _autoStart;
        private readonly float _inertia, _slipTorque;
        private readonly IgnitionSwitch _switch;
        private bool _inertiaSet, _partGone, _key, _cranking, _lastKey, _lastCranking, _lastFuel = true;
        private float _nextStats;

        // Fuel: the vehicle's tank, through its FSM 'getFuel' (the tank part) and the tank's 'LiquidAmount'
        // (what the game's switch checks before starting: empty under 0.01).
        private const float EmptyTank = 0.01f;
        private FsmFloat _fuel;
        private float _nextFuelLook;

        // The game's engine temperature: the engine part's FSM 'engineOil_Temperature' keeps its oil
        // temperature, deg C, from the world's temperature up (heating with rpm, cooling stood). For
        // starting, 0 deg C is cold and 80 deg C warm.
        private const string OilFsm = "engineOil_Temperature", OilVar = "engineOil_temperature";
        private const float ColdC = 0f, WarmC = 80f;
        private FsmFloat _oilTemperature;
        private bool _oilLooked;

        // the starter: its sound, the battery dimming the lights, the solenoid clicking when it gives up
        private Light[] _lights;
        private float[] _lightBase;
        private float _lightLoad;
        private bool _clicking;
        private float _nextClick, _clickLoad;
        private static AudioClip[] _clicks;

        public bool KeyOn { get { return _key && !_partGone; } }

        // the session of a coupled engine, or null
        internal static EngineSession SessionOf(EngineComponent engine)
        {
            DriveCoupling coupling;
            return engine != null && Active.TryGetValue(engine, out coupling) && !coupling._partGone ? coupling._session : null;
        }

        // The engine part came off (taken out of the vehicle, or given its Rigidbody back as a loose part):
        // nothing of this coupling may drive the vehicle any more, and the game's own start and stop
        // apply again, until the session ends.
        internal bool PartGone { get { return _partGone; } }

        // whether a vehicle's key is on, coupled or not (for choosing which engines to simulate)
        private static readonly Dictionary<VehicleController, PlayMakerFSM> Switches = new Dictionary<VehicleController, PlayMakerFSM>();

        internal static bool KeyIsOn(VehicleController vc)
        {
            var engine = vc.powertrain.engine;
            DriveCoupling coupling;
            if (Active.TryGetValue(engine, out coupling)) return coupling.KeyOn;
            PlayMakerFSM fsm;
            if (!Switches.TryGetValue(vc, out fsm)) Switches[vc] = fsm = IgnitionSwitch.FindFsm(vc);
            return engine.ignition || IgnitionSwitch.IsOn(fsm);
        }

        public DriveCoupling(EngineSession session)
        {
            _session = session;
            var vc = session.Vc;
            _engine = vc.powertrain.engine;
            _vanilla = _engine.calculateTorqueDelegate;
            _stalling = _engine.stallingEnabled;
            _autoStart = _engine.autoStartOnThrottle;
            _inertia = _engine.inertia;
            _slipTorque = vc.powertrain.clutch.slipTorque;
            _ours = Torque;

            // the key as the game left it: its switch on, or its engine on
            _key = KeyIsOn(vc);
            Active[_engine] = this;
            _switch = IgnitionSwitch.TakeOver(vc, _key);
            if (_engine.starterActive) _engine.starterActive = false;   // its fixed-time starter
            _engine.stallingEnabled = false;
            _engine.autoStartOnThrottle = false;
            _engine.calculateTorqueDelegate = _ours;
            _engine.ignition = _key;
            Plugin.Verbose(session.PartName + ": coupled, key " + (_key ? "on" : "off") + (_switch == null ? ", no ignition switch" : ""));
        }

        // the game's engine asks for its torque every physics step
        private float Torque(float angularVelocity, float dt)
        {
            if (_partGone) return 0f;   // no engine
            if (!_session.DriveReady) return _vanilla(angularVelocity, dt);   // the simulation still starting up
            return _session.DriveTorque;
        }

        private bool EngineFitted()
        {
            var part = _session.EnginePart;
            return part != null && part.activeInHierarchy && part.GetComponent<Rigidbody>() == null
                && part.transform.IsChildOf(_session.Vc.transform);
        }

        // main thread, every frame
        public void Update()
        {
            if (_engine.calculateTorqueDelegate != _ours) _engine.calculateTorqueDelegate = _ours;   // re-initialised by the game
            if (!_partGone && !EngineFitted())
            {
                _partGone = true;
                StopStarter();
                _engine.ignition = false;
                Plugin.Verbose(_session.PartName + " was taken out; the vehicle has no engine");
            }
            if (_partGone)
            {
                _session.SetDrive(false, true, _engine.idleRPM);
                return;
            }

            // The clutch is sized for the engine, not for the game's power figure (which falls with
            // condition, down to a clutch that transmits a few N m); kept even if the game resets it.
            var clutchPart = _session.Vc.powertrain.clutch;
            if (_session.ClutchTorque > 0f && clutchPart.slipTorque != _session.ClutchTorque) clutchPart.slipTorque = _session.ClutchTorque;
            if (!_inertiaSet && _session.CrankInertia > 0f)
            {
                _engine.inertia = _session.CrankInertia;
                _inertiaSet = true;
            }
            UpdateHeat();
            bool fuel = HasFuel();

            // the key: the player's hand on the switch (the game's physics paused, nothing moves)
            if (_switch != null && Time.timeScale > 0.001f) _switch.Update();
            if (_switch != null) _key = _switch.On;
            bool crank = _switch != null && _switch.Cranking;
            if (crank && !_cranking) StartStarter();
            else if (!crank && _cranking) StopStarter();
            _cranking = crank;
            if (_cranking) Starter();
            _engine.ignition = _key;   // for whatever else in the game reads it
            if (_engine.starterActive) _engine.starterActive = false;

            if (_key != _lastKey || _cranking != _lastCranking || fuel != _lastFuel)
            {
                Plugin.Verbose(_session.PartName + ": key " + (_key ? "on" : "off") + ", starter " + (_cranking ? "on" : "off")
                    + (fuel ? "" : ", tank empty") + ", " + _engine.OutputRPM.ToString("0") + " rpm");
                _lastKey = _key;
                _lastCranking = _cranking;
                _lastFuel = fuel;
            }
            LogStats();

            _session.SetDrive(_cranking, fuel, _engine.idleRPM);
        }

        private bool HasFuel()
        {
            if (_fuel == null && Time.unscaledTime >= _nextFuelLook)
            {
                _nextFuelLook = Time.unscaledTime + 2f;   // the tank can be fitted later
                var getFuel = _session.Vc.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "getFuel");
                var tankVar = getFuel != null ? getFuel.FsmVariables.GetFsmGameObject("Fuel") : null;
                var tank = tankVar != null ? tankVar.Value : null;
                var amount = tank != null ? tank.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "LiquidAmount") : null;
                _fuel = amount != null ? amount.FsmVariables.GetFsmFloat("Liquid") : null;
                if (getFuel == null) _nextFuelLook = float.MaxValue;   // a vehicle without one: never starved
            }
            return _fuel == null ? _nextFuelLook == float.MaxValue : _fuel.Value >= EmptyTank;
        }

        private void UpdateHeat()
        {
            if (!_oilLooked && _session.EnginePart != null)
            {
                _oilLooked = true;
                var fsm = _session.EnginePart.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == OilFsm);
                _oilTemperature = fsm != null ? fsm.FsmVariables.GetFsmFloat(OilVar) : null;
                Plugin.Verbose(_session.PartName + (_oilTemperature != null
                    ? ": oil temperature " + _oilTemperature.Value.ToString("0") + " C"
                    : ": no oil temperature, starting as if at 30 C"));
            }
            float celsius = _oilTemperature != null ? _oilTemperature.Value : 30f;
            _session.SetHeat((celsius - ColdC) / (WarmC - ColdC));
        }

        private void LogStats()
        {
            if (!Plugin.VerboseLogEntry.Value || Time.unscaledTime < _nextStats) return;
            var vc = _session.Vc;
            var clutch = vc.powertrain.clutch;
            var gearbox = vc.powertrain.transmission;
            bool watch = _cranking || (gearbox.Gear != 0 && _engine.OutputRPM < _engine.idleRPM);
            _nextStats = Time.unscaledTime + (watch ? 1f : 5f);
            Plugin.Log.LogInfo("[drive] " + _session.PartName + ": key " + (_key ? "on" : "off") + ", " + _engine.OutputRPM.ToString("0")
                + " rpm, torque " + _session.DriveTorque.ToString("0") + " N m, starter " + (_cranking ? (_session.StarterJammed ? "clicking" : "on, load " + _session.StarterLoad.ToString("0.00")) : "off")
                + " | gear " + gearbox.Gear + ", clutch " + clutch.controlType + " input " + clutch.clutchInput.ToString("0.00")
                + " engagement " + clutch.Engagement.ToString("0.00") + " slip " + clutch.slipTorque.ToString("0") + " N m, clutch out "
                + clutch.OutputRPM.ToString("0") + " rpm, gearbox out " + gearbox.OutputRPM.ToString("0") + " rpm | speed "
                + vc.Speed.ToString("0.0") + " m/s, brakes " + vc.input.Brakes.ToString("0.00") + ", handbrake " + vc.input.Handbrake.ToString("0.00"));
        }

        // ---- the starter -------------------------------------------------------------------------

        private void StartStarter()
        {
            _lightLoad = 0f;
            _lights = _session.Vc.GetComponentsInChildren<Light>(true);
            _lightBase = _lights.Select(l => l.intensity).ToArray();
            _clicking = false;
        }

        private void StopStarter()
        {
            _cranking = false;
            _clicking = false;
            StopCrankSound();
            if (_lights != null)
            {
                for (int i = 0; i < _lights.Length; ++i)
                    if (_lights[i] != null) _lights[i].intensity = _lightBase[i];
                _lights = null;
            }
        }

        private void Starter()
        {
            if (_session.StarterJammed) DimLights(Click());
            else
            {
                UpdateCrankSound();
                DimLights(_session.StarterLoad);
            }
        }

        // The switch's starter sound (the game plays it for a fixed time at a fixed pitch) follows the
        // starter: pitch by how fast it turns the engine against its free speed.
        private void UpdateCrankSound()
        {
            var a = _switch != null ? _switch.Audio : null;
            if (a == null) return;
            if (!a.isPlaying && _switch.StarterClip != null)
            {
                a.clip = _switch.StarterClip;
                a.loop = true;
                a.volume = 0.7f;   // the game's own
                a.Play();
            }
            float starterRpm = Mathf.Max(100f, _session.Desc.engine.starterSpeedRpm);
            a.pitch = Mathf.Clamp(_engine.OutputRPM / starterRpm, 0.2f, 1.5f);
        }

        private void StopCrankSound()
        {
            var a = _switch != null ? _switch.Audio : null;
            if (a == null) return;
            a.Stop();
            a.pitch = 1f;
        }

        // The starter gave up: its solenoid pulls in, the current through it drags the battery down until it
        // drops out, the battery recovers and it pulls in again, ~8 - 14 times a second, and the starter
        // never turns. Each pull-in is a burst of current (the lights flick down with it). Returns the load.
        private float Click()
        {
            if (!_clicking)
            {
                _clicking = true;
                StopCrankSound();
                _nextClick = 0f;
                Plugin.Verbose(_session.PartName + ": the starter can't turn it, its solenoid is clicking");
            }
            _nextClick -= Time.deltaTime;
            _clickLoad = Mathf.Max(0f, _clickLoad - Time.deltaTime / 0.03f);
            if (_nextClick > 0f) return _clickLoad;
            _nextClick = Random.Range(0.07f, 0.12f);
            _clickLoad = 1f;
            var source = _switch != null ? _switch.Audio : null;
            if (source == null) return _clickLoad;
            if (_clicks == null) _clicks = new[] { MakeClick(1), MakeClick(2), MakeClick(3), MakeClick(4) };
            source.pitch = Random.Range(0.94f, 1.06f);
            source.PlayOneShot(_clicks[Random.Range(0, _clicks.Length)], Random.Range(0.7f, 1f) * Plugin.VolumeEntry.Value);
            return _clickLoad;
        }

        // a solenoid's plunger hitting its stop: a metallic clack (two rings of the housing and a tick of
        // noise) on a dull thump of the starter's body
        private static AudioClip MakeClick(int seed)
        {
            var rng = new System.Random(seed * 7919);
            int rate = AudioSettings.outputSampleRate, n = (int)(0.06f * rate);
            float f1 = 1700f + 400f * (float)rng.NextDouble(), f2 = 2900f + 700f * (float)rng.NextDouble();
            var data = new float[n];
            for (int i = 0; i < n; ++i)
            {
                float t = (float)i / rate;
                float ring = 0.5f * Mathf.Sin(2f * Mathf.PI * f1 * t) * Mathf.Exp(-t / 0.006f)
                    + 0.3f * Mathf.Sin(2f * Mathf.PI * f2 * t) * Mathf.Exp(-t / 0.004f);
                float tick = (2f * (float)rng.NextDouble() - 1f) * Mathf.Exp(-t / 0.0008f);
                float thump = 0.6f * Mathf.Sin(2f * Mathf.PI * 140f * t) * Mathf.Exp(-t / 0.012f);
                data[i] = 0.8f * (ring + 0.6f * tick + thump);
            }
            var clip = AudioClip.Create("EngineSim solenoid " + seed, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // The battery sags while the starter works: every light on the vehicle dims with its load.
        private void DimLights(float load)
        {
            if (_lights == null) return;
            // the filaments cool and heat over about a tenth of a second
            _lightLoad += (Mathf.Clamp01(load) - _lightLoad) * (1f - Mathf.Exp(-Time.deltaTime / 0.1f));
            float factor = 1f - 0.7f * _lightLoad;
            for (int i = 0; i < _lights.Length; ++i)
                if (_lights[i] != null) _lights[i].intensity = _lightBase[i] * factor;
        }

        public void Release()
        {
            if (_engine == null) return;
            StopStarter();
            Active.Remove(_engine);
            bool turning = _engine.OutputRPM > 0.5f * _engine.idleRPM;
            if (_engine.calculateTorqueDelegate == _ours) _engine.calculateTorqueDelegate = _vanilla;
            _engine.stallingEnabled = _stalling;
            _engine.autoStartOnThrottle = _autoStart;
            if (_inertiaSet) _engine.inertia = _inertia;
            if (_session.ClutchTorque > 0f) _session.Vc.powertrain.clutch.slipTorque = _slipTorque;
            // the game's engine and switch as the key left them
            _engine.ignition = KeyOn;
            if (_switch != null) _switch.Release(turning);
        }
    }

    // A coupled engine is started, stopped and stalled by its simulation and its key alone: the game's
    // start / stop requests do nothing (its starter coroutine, throttle auto-start, damage and stall
    // stops, the start / stop key). Once its part is gone they apply again, except a start: see StartPatch.
    [HarmonyLib.HarmonyPatch(typeof(EngineComponent), nameof(EngineComponent.StartStopEngine))]
    internal static class StartStopPatch
    {
        private static bool Prefix(EngineComponent __instance)
        {
            DriveCoupling coupling;
            return !DriveCoupling.Active.TryGetValue(__instance, out coupling) || coupling.PartGone;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(EngineComponent), nameof(EngineComponent.StopEngine))]
    internal static class StopPatch
    {
        private static bool Prefix(EngineComponent __instance)
        {
            DriveCoupling coupling;
            return !DriveCoupling.Active.TryGetValue(__instance, out coupling) || coupling.PartGone;
        }
    }

    // Starts: none for a coupled engine. And with the simulated drivetrain, none for a vehicle whose
    // engine hinge is empty: the game's engine model (NWH) outlives its part, and would otherwise start
    // on the throttle (auto-start) or the switch and drive the vehicle on the last engine's figures. Read
    // from the hinge's own FSM ('getEngine', state 'noEngine'); cached per vehicle for half a second.
    [HarmonyLib.HarmonyPatch(typeof(EngineComponent), nameof(EngineComponent.StartEngine))]
    internal static class StartPatch
    {
        private sealed class Hinge { public PlayMakerFSM Fsm; public bool Looked; public float NextCheck; public bool Empty; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VehicleController, Hinge> Hinges =
            new System.Runtime.CompilerServices.ConditionalWeakTable<VehicleController, Hinge>();

        private static bool Prefix(EngineComponent __instance)
        {
            DriveCoupling coupling;
            if (DriveCoupling.Active.TryGetValue(__instance, out coupling) && !coupling.PartGone) return false;
            if (!Plugin.SimDriveEntry.Value) return true;
            var vc = __instance.vehicleController;
            if (vc == null) return true;
            var h = Hinges.GetOrCreateValue(vc);
            if (!h.Looked)
            {
                h.Looked = true;
                h.Fsm = vc.GetComponentsInChildren<PlayMakerFSM>(true)
                    .FirstOrDefault(f => f.FsmName == "getEngine" && f.Fsm != null && f.Fsm.HasState("noEngine"));
            }
            if (h.Fsm == null) return true;
            if (Time.unscaledTime >= h.NextCheck)
            {
                h.NextCheck = Time.unscaledTime + 0.5f;
                h.Empty = h.Fsm.ActiveStateName == "noEngine";
            }
            if (h.Empty && __instance.ignition) __instance.StopEngine();
            return !h.Empty;
        }
    }
}
