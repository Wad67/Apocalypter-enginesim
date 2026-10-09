using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using SkrilStudio;
using UnityEngine;
using UnityEngine.Audio;

namespace EngineSim
{
    // Decides which engines are simulated: every running combustion engine with a
    // RealisticEngineSound, nearest to the listener first, up to MaxEngines.
    // The rest keep their stock sound. Lives on a hidden DontDestroyOnLoad runner
    // (the game destroys plugin GameObjects on scene load).
    internal sealed class EngineManager : MonoBehaviour
    {
        private const float ScanInterval = 0.5f;
        private const float KeepAliveSeconds = 3f;   // keep a session this long after its engine stops (spin-down, restarts)

        private static readonly AccessTools.FieldRef<RealisticEngineSound, AudioMixerGroup> ChosenMixer =
            AccessTools.FieldRefAccess<RealisticEngineSound, AudioMixerGroup>("_choosedAudioMixer");

        private readonly Dictionary<RealisticEngineSound, EngineSession> _sessions = new Dictionary<RealisticEngineSound, EngineSession>();
        private readonly Dictionary<RealisticEngineSound, DriveCoupling> _couplings = new Dictionary<RealisticEngineSound, DriveCoupling>();
        private readonly Dictionary<RealisticEngineSound, EngineEffects> _effects = new Dictionary<RealisticEngineSound, EngineEffects>();
        private readonly Dictionary<RealisticEngineSound, float> _lastRunning = new Dictionary<RealisticEngineSound, float>();
        private readonly HashSet<string> _reported = new HashSet<string>();
        private float _nextScan;
        private float _nextStats;
        private AudioListener _listener;

        public static string IrDirectory;

        private void Update()
        {
            FsmDump.Tick();
            if (!Plugin.EnabledEntry.Value)
            {
                if (_sessions.Count > 0) StopAll("disabled");
                return;
            }

            if (Plugin.RestartRequested)
            {
                Plugin.RestartRequested = false;
                StopAll("settings changed");   // restarted by the next scan with the new settings
                _nextScan = 0;
            }

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanInterval;
                Scan();
            }

            // with the simulated drivetrain: a turbo and a supercharger fit together
            if (Plugin.SimDriveEntry.Value) TwinchargeMount.Update(_sessions.Values);

            if (_listener == null || !_listener.isActiveAndEnabled) _listener = FindListener();
            var ear = _listener != null ? _listener.transform : null;
            foreach (var kv in _sessions)
            {
                DriveCoupling coupling;
                _couplings.TryGetValue(kv.Key, out coupling);
                Feed(kv.Value, coupling);
                kv.Value.UpdateDistance(ear);
                if (coupling != null) coupling.Update();
                EngineEffects fx;
                if (_effects.TryGetValue(kv.Key, out fx)) fx.Update();
            }

            if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= _nextStats && _sessions.Count > 0)
            {
                _nextStats = Time.unscaledTime + 5f;
                foreach (var s in _sessions.Values)
                    Plugin.Log.LogInfo("[stats] " + s.PartName + ": game " + s.Vc.powertrain.engine.OutputRPM.ToString("0")
                        + " / sim " + s.Rpm.ToString("0") + " rpm, physics " + (s.ProcessingUs / 1000f).ToString("0.0")
                        + " ms/frame, throttle " + s.Vc.powertrain.engine.ThrottlePosition.ToString("0.00") + ", boost x" + (s.Vc.powertrain.engine.forcedInduction != null && s.Vc.powertrain.engine.forcedInduction.useForcedInduction ? s.Vc.powertrain.engine.forcedInduction.PowerGainMultiplier.ToString("0.00") : "1") + ", condition " + (s.ConditionVar != null ? s.ConditionVar.Value.ToString("0") + " %" : "n/a") + ", ring " + s.RingFill + " samples, underrun " + s.UnderrunSamples + " frames");
            }
        }

        private void OnDestroy() { StopAll("runner destroyed"); }

        // The engine's torque reaction on the vehicle body, about the crank axis (the engine part's own
        // axis, the one the game's shake and EngineRock turn it about). The physics runs at 50-100 Hz:
        // the torque is smoothed to what it can carry (~10 Hz), which keeps stumbles, start / stop
        // kicks and a twin's idle beat, not a V8's idle buzz.
        private readonly Dictionary<EngineSession, float> _chassisTorque = new Dictionary<EngineSession, float>();

        private void FixedUpdate()
        {
            float strength = Plugin.ChassisTorqueEntry.Value;
            if (strength <= 0f || _sessions.Count == 0) return;
            float dt = Time.fixedDeltaTime;
            foreach (var s in _sessions.Values)
            {
                if (s.Vc == null || s.EnginePart == null || !s.Ready) continue;
                var rb = s.Vc.vehicleRigidbody;
                if (rb == null) continue;
                float smoothed;
                _chassisTorque.TryGetValue(s, out smoothed);
                smoothed += (s.RawTorque - smoothed) * (1f - Mathf.Exp(-dt / 0.016f));
                _chassisTorque[s] = smoothed;
                Vector3 axis = s.EnginePart.transform.TransformDirection(Vector3.forward);
                rb.AddTorque(axis * (smoothed * strength), ForceMode.Force);
            }
        }

        private static void Feed(EngineSession s, DriveCoupling coupling)
        {
            if (s.Vc == null) return;
            var engine = s.Vc.powertrain.engine;
            float rpm = engine.OutputRPM;
            if (s.Drive)
            {
                // the game's speed, set by the simulated engine's own torque; the throttle is cut while shifting
                bool shiftCut = s.Vc.powertrain.transmission.isShifting && rpm > engine.idleRPM;
                bool key = coupling != null ? coupling.KeyOn : engine.ignition;
                s.SetControls(rpm, shiftCut ? 0f : engine.ThrottlePosition, key, ReadCondition(s));
            }
            else
            {
                // below this the game's engine is stopped; let the sim crank coast instead of holding it
                s.SetControls(rpm > 50f ? rpm : 0f, engine.ThrottlePosition, engine.IsRunning, ReadCondition(s));
            }
            // the game's rev limit (its own cut is a one-frame flag, or none: RPM is just clamped), and
            // forced induction as intake pressure: the simulated engine's power rises about 1.65x as fast
            // as the pressure ratio (measured on its engines), so this gives the game's power gain
            // (with the simulated drivetrain the fitted units are machines on the simulated engine instead)
            var fi = engine.forcedInduction;
            float gain = fi != null && fi.useForcedInduction ? fi.PowerGainMultiplier : 1f;
            if (s.Drive) InductionMachines.Update(s);
            s.SetLimits(engine.revLimiterRPM,
                engine.revLimiterCutoffDuration > 0.02f ? engine.revLimiterCutoffDuration : 0.08f,
                s.Drive ? 1f : 1f + (gain - 1f) / 1.65f);
        }

        private void Scan()
        {
            var listener = FindListener();
            Vector3 ear = listener != null ? listener.transform.position : Vector3.zero;
            float now = Time.unscaledTime;

            // candidates: running combustion engines within their sound's range
            var candidates = new List<KeyValuePair<RealisticEngineSound, float>>();
            foreach (var res in UnityEngine.Object.FindObjectsOfType<RealisticEngineSound>())
            {
                var vc = res.GetComponentInParent<VehicleController>();
                if (vc == null || vc.powertrain == null || vc.powertrain.engine == null) continue;
                var engine = vc.powertrain.engine;
                if (engine.engineType != EngineComponent.EngineType.ICE) continue;

                // with the simulated drivetrain an engine whose key is on is kept (not turning, or about to crank)
                if (engine.IsRunning || engine.starterActive || engine.OutputRPM > 50f
                    || (Plugin.SimDriveEntry.Value && DriveCoupling.KeyIsOn(vc))) _lastRunning[res] = now;
                float last;
                if (!_lastRunning.TryGetValue(res, out last) || now - last > KeepAliveSeconds) continue;

                float d = listener != null ? Vector3.Distance(ear, res.transform.position) : 0f;
                if (d > res.maxDistance * 1.1f) continue;
                candidates.Add(new KeyValuePair<RealisticEngineSound, float>(res, d));
            }

            var wanted = new HashSet<RealisticEngineSound>(candidates
                .OrderBy(c => c.Value)
                .Take(Plugin.MaxEnginesEntry.Value)
                .Select(c => c.Key));

            foreach (var res in _sessions.Keys.ToList())
            {
                var s = _sessions[res];
                if (res == null || !wanted.Contains(res)) StopSession(res, "out of range / not running");
                else if (s.Failed) StopSession(res, "failed: " + s.Error);
                else
                {
                    var fitted = PartTable.Fitted(s.Vc);
                    if (fitted.EngineId != s.EngineId) StopSession(res, "engine changed");
                    else if (fitted.ExhaustId != s.ExhaustId) StopSession(res, "exhaust changed");
                }
            }

            foreach (var res in wanted)
                if (!_sessions.ContainsKey(res)) TryStart(res);

            foreach (var res in _lastRunning.Keys.Where(r => r == null).ToList()) _lastRunning.Remove(res);
        }

        private void TryStart(RealisticEngineSound res)
        {
            var vc = res.GetComponentInParent<VehicleController>();
            var fitted = PartTable.Fitted(vc);
            var spec = PartTable.Engine(fitted.EngineId);
            if (spec == null)
            {
                if (_reported.Add("noengine:" + vc.GetInstanceID()))
                {
                    var unlisted = PartTable.UnlistedParts(vc).Select(p => p.prefabId + " (" + p.name + ")").ToArray();
                    Plugin.Log.LogWarning(vc.name + " has no engine listed in EngineSim.engines.cfg fitted; keeping the stock sound. "
                        + "Its other fitted parts (part id, object): " + string.Join(", ", unlisted));
                }
                return;
            }

            string part = spec.Label ?? spec.Id.ToString();
            var desc = new Native.EngineDesc { engine = spec.Params };
            desc.fluidSteps = Plugin.QualityEntry.Value == SimQuality.High ? 8 : Plugin.QualityEntry.Value == SimQuality.Fast ? 4 : 6;
            var engine = vc.powertrain.engine;
            if (engine.revLimiterRPM > 500f) desc.redlineRpm = engine.revLimiterRPM;
            if (engine.idleRPM > 100f) desc.idleRpm = engine.idleRPM;

            ExhaustProfile profile;
            var sound = PartTable.ResolveExhaust(fitted.ExhaustId, out profile);
            if (!System.IO.File.Exists(System.IO.Path.Combine(IrDirectory, sound.Ir)))
            {
                ReportOnce("ir:" + sound.Ir, "impulse response " + sound.Ir + " not found in " + IrDirectory + "; using the built-in choice");
                sound.Ir = null;
            }
            desc.exhaustIr = sound.Ir;
            desc.exhaustFlow = sound.Flow;
            desc.exhaustLengthM = sound.LengthM;
            desc.headerScale = sound.HeaderScale;
            desc.airNoise = sound.AirNoise;
            desc.jitter = sound.Jitter;
            float loudness = sound.Loudness;

            if (Plugin.VerboseLogEntry.Value)
            {
                PartDump.Once(fitted.Engine, "engine " + fitted.EngineId);
                PartDump.Once(fitted.Exhaust, "exhaust " + fitted.ExhaustId);
            }

            var mixer = ChosenMixer(res) ?? res.audioMixer;
            bool drive = Plugin.SimDriveEntry.Value;
            // with the simulated drivetrain: the fitted machines
            string inductionKey = "";
            var induction = new Native.InductionDesc { turboSize = new float[Native.MaxTurbos] };
            if (drive) induction = InductionMachines.Describe(fitted, out inductionKey);
            var session = new EngineSession(res, vc, part, fitted, loudness, desc, IrDirectory, mixer,
                drive, Plugin.MatchGamePowerEntry.Value ? spec.DriveTorqueScale : 1f, spec.PeakTorque);
            _sessions[res] = session;
            if (drive)
            {
                session.InductionKey = inductionKey;
                session.SetInduction(induction);
                _couplings[res] = new DriveCoupling(session);
                Plugin.Log.LogInfo(vc.name + ": forced induction " + InductionMachines.Describe(induction));
            }
            _effects[res] = new EngineEffects(session);
            ResPatch.Take(res);
            Plugin.Log.LogInfo("Simulating " + vc.name + ": engine " + spec.Id + " '" + part + "' -> " + desc
                + "; exhaust " + (fitted.ExhaustId != 0 ? fitted.ExhaustId + " -> " : "none fitted -> ")
                + (profile != null ? profile.ToString() : "base") + ": " + sound);
        }

        private void StopSession(RealisticEngineSound res, string why)
        {
            EngineSession s;
            if (!_sessions.TryGetValue(res, out s)) return;
            _sessions.Remove(res);
            DriveCoupling coupling;
            if (_couplings.TryGetValue(res, out coupling))
            {
                coupling.Release();   // the game's own engine model again
                _couplings.Remove(res);
            }
            EngineEffects fx;
            if (_effects.TryGetValue(res, out fx))
            {
                fx.Release();
                _effects.Remove(res);
            }
            _chassisTorque.Remove(s);
            s.Stop();
            if (res != null) ResPatch.Release(res);
            else ResPatch.Prune();
            Plugin.Verbose("Stopped " + s.PartName + ": " + why);
        }

        private void StopAll(string why)
        {
            foreach (var res in _sessions.Keys.ToList()) StopSession(res, why);
        }

        private void ReportOnce(string key, string msg)
        {
            if (_reported.Add(key)) Plugin.Log.LogWarning(msg);
        }

        // The engine part's wear: FSM "Condition", float "Condition", 0..100 (what Apocapatrol sets on spawn).
        // Unknown = as new.
        private static float ReadCondition(EngineSession s)
        {
            if (s.ConditionVar == null)
            {
                var part = s.EnginePart;
                if (part == null) return 1f;
                foreach (var fsm in part.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    if (fsm.FsmName != "Condition" || !fsm.Fsm.Initialized) continue;
                    s.ConditionVar = fsm.FsmVariables.GetFsmFloat("Condition");
                    if (s.ConditionVar != null)
                    {
                        Plugin.Verbose(s.PartName + " condition: " + s.ConditionVar.Value.ToString("0") + " %");
                        break;
                    }
                }
                if (s.ConditionVar == null) return 1f;
            }
            return Mathf.Clamp01(s.ConditionVar.Value / 100f);
        }

        private static AudioListener FindListener()
        {
            foreach (var l in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                if (l.isActiveAndEnabled) return l;
            return null;
        }
    }

    // Mutes RealisticEngineSound's engine loops on vehicles we simulate. Its wind
    // noise and reverse whine / beep keep playing; RES recreates nothing we mute,
    // so releasing a vehicle is just unmuting.
    [HarmonyPatch(typeof(RealisticEngineSound), "Update")]
    internal static class ResPatch
    {
        private static readonly HashSet<RealisticEngineSound> Taken = new HashSet<RealisticEngineSound>();

        private static readonly AccessTools.FieldRef<RealisticEngineSound, AudioSource>[] EngineSources = new[]
        {
            "engineIdle", "lowOff", "lowOn", "medOff", "medOn", "highOff", "highOn", "maxRPM",
            "idle_lowOff", "idle_lowOn", "low_medOff", "low_medOn", "med_highOff", "med_highOn",
            "very_highOff", "very_highOn", "aggressivnessOn", "aggressivnessOff", "valveShot"
        }.Select(n => AccessTools.FieldRefAccess<RealisticEngineSound, AudioSource>(n)).ToArray();

        public static void Take(RealisticEngineSound res) { Taken.Add(res); SetMuted(res, true); }

        public static void Release(RealisticEngineSound res) { Taken.Remove(res); SetMuted(res, false); }

        public static void Prune() { Taken.RemoveWhere(r => r == null); }

        // one of RES's own engine sources, to copy its 3D settings from
        public static AudioSource AnyEngineSource(RealisticEngineSound res)
        {
            foreach (var field in EngineSources)
            {
                var src = field(res);
                if (src != null) return src;
            }
            return null;
        }

        private static void Postfix(RealisticEngineSound __instance)
        {
            if (Taken.Contains(__instance)) SetMuted(__instance, true);
        }

        private static void SetMuted(RealisticEngineSound res, bool muted)
        {
            foreach (var field in EngineSources)
            {
                var src = field(res);
                if (src != null) src.mute = muted;
            }
        }
    }
}
