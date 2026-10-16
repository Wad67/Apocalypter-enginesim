using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using SkrilStudio;
using UnityEngine;

namespace EngineSim
{
    // A hand crank: an "engine" that is a person turning a handle. Fitted in a vehicle's engine hinge (a SigmaMod part
    // tagged sigma_behaviour = hand_crank, built on a game engine so it fits the hinge), it drives the drivetrain with
    // the torque a person's arms make, and nothing else: no cylinders, no fuel, no key, no engine sound.
    //
    // Arms, like any muscle, push hardest held still and less the faster they have to move: the torque falls in a
    // straight line from crank_torque (N m, the handle held still) to nothing at crank_speed (rpm, as fast as the
    // handle can be turned). Defaults 60 N m and 160 rpm: a two-handed crank at about 300 N on a 0.2 m arm, the most
    // power at half that speed, about 250 W (a third of a horsepower: a hard sprint for the arms). Seated, the throttle
    // is how hard the person cranks; standing by it, holding the left button on it is cranking with everything. Turned
    // backwards (the vehicle rolling back) they push with their full held torque; faster than they can turn it, they
    // let it run. The handle's own friction (crank_friction, N m) and its inertia (crank_inertia, kg m2) are the rest.
    //
    // The crank turns the drivetrain directly: the clutch stays engaged (the game's automatic clutch only lets in
    // above idle, and a person has no idle). The game's own engine model is out of the way: its torque is the
    // person's, its stalling, auto-start and start / stop do nothing, and its engine sound is silent.
    //
    // The person grunts and groans as they push (their own hurt sounds), sooner and louder the closer to their
    // strongest they work. A part tagged crank_spin (axis: crank_axis, x, y or z; default x) turns with the crank.
    internal sealed class HandCrankPart : MonoBehaviour
    {
        public float Torque = 60f, Speed = 160f, Friction = 1.5f, Inertia = 0.08f;
        public float Rpm;          // the crank's speed now (set while fitted; 0 loose)
        public float Effort;       // how hard it is being turned now, 0 - 1

        // the left click on it is this part's (cranking), and Unity's message for "clicking this does something" is
        // how other mods (Burner's Utilities' hands) see that and leave the click alone
        private void OnMouseDown() { }
    }

    internal sealed class CrankSpin : MonoBehaviour
    {
        public Vector3 Axis = Vector3.right;
        private HandCrankPart _crank;
        private float _nextLog;
        private void Update()
        {
            if (_crank == null) _crank = GetComponentInParent<HandCrankPart>();
            if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 2f;
                Plugin.Verbose($"crank_spin {name}: crank {(_crank != null ? _crank.name + " at " + _crank.Rpm.ToString("0") + " rpm" : "none above")}, about {Axis}, now {transform.localEulerAngles}");
            }
            if (_crank == null || Mathf.Abs(_crank.Rpm) < 0.01f) return;
            transform.localRotation *= Quaternion.AngleAxis(_crank.Rpm * 6f * Time.deltaTime, Axis);   // rpm -> degrees a second
        }
    }

    // SigmaMod's tags (registered only when SigmaMod is there)
    internal static class HandCrankTags
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register()
        {
            SigmaMod.CustomPrefabs.RegisterBehaviour("hand_crank", (go, props, info) =>
            {
                var p = go.GetComponent<HandCrankPart>() ?? go.AddComponent<HandCrankPart>();
                p.Torque = Num(props, "crank_torque", p.Torque);
                p.Speed = Num(props, "crank_speed", p.Speed);
                p.Friction = Num(props, "crank_friction", p.Friction);
                p.Inertia = Num(props, "crank_inertia", p.Inertia);
            });
            SigmaMod.CustomPrefabs.RegisterBehaviour("crank_spin", (go, props, info) =>
            {
                var s = go.GetComponent<CrankSpin>() ?? go.AddComponent<CrankSpin>();
                string axis = props != null && props.TryGetValue("crank_axis", out var a) ? Convert.ToString(a).Trim().ToLowerInvariant() : "x";
                s.Axis = axis == "y" ? Vector3.up : axis == "z" ? Vector3.forward : Vector3.right;
                Plugin.Verbose($"crank_spin on {go.name} of {go.transform.root.name}, about {axis}");
            });
            Plugin.Log.LogInfo("hand crank: SigmaMod tags hand_crank and crank_spin registered");
        }

        private static float Num(Dictionary<string, object> props, string key, float def)
        {
            if (props == null || !props.TryGetValue(key, out var v) || v == null) return def;
            try { return Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return def; }
        }
    }

    // Finds fitted cranks and runs each one's coupling; the left click on a crank and the person's voice.
    internal sealed class HandCranks : MonoBehaviour
    {
        private const float Reach = 2f;   // m, as the game's grab
        private static readonly Dictionary<EngineComponent, Coupling> Active = new Dictionary<EngineComponent, Coupling>();
        private float _nextScan, _nextGrunt;
        private AudioSource _voice;

        public static bool Coupled(EngineComponent engine) => engine != null && Active.ContainsKey(engine);
        public static bool Coupled(VehicleController vc) => vc != null && vc.powertrain != null && Coupled(vc.powertrain.engine);
        // the torque the crank's person and friction put on the shaft this physics step
        public static bool Torque(EngineComponent engine, out float torque)
        {
            torque = 0f;
            if (engine == null || !Active.TryGetValue(engine, out var c)) return false;
            torque = c.LastTorque;
            return true;
        }
        // VerboseLog: how the driveline step went for a cranked engine
        public static void Stepped(EngineComponent engine, string how) { if (Active.TryGetValue(engine, out var c)) c.Step = how; }

        private void Update()
        {
            if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 0.5f; Scan(); }
            HandCrankPart clicked = Clicked();
            float hardest = 0f;
            foreach (var c in Active.Values)
            {
                c.ByHand = c.Part == clicked;
                c.Update();
                hardest = Mathf.Max(hardest, c.Strain);
            }
            Voice(hardest);
        }

        private void Scan()
        {
            foreach (var engine in Active.Keys.ToList())
            {
                var c = Active[engine];
                if (engine == null || c.Part == null || !Fitted(c.Part, c.Vc)) { c.Release(); Active.Remove(engine); }
            }
            foreach (var vc in FindObjectsOfType<VehicleController>())
            {
                if (vc == null || vc.powertrain == null || vc.powertrain.engine == null || Active.ContainsKey(vc.powertrain.engine)) continue;
                var part = vc.GetComponentsInChildren<HandCrankPart>(false).FirstOrDefault(p => Fitted(p, vc));
                if (part != null) Active[vc.powertrain.engine] = new Coupling(vc, part);
            }
        }

        // in the vehicle's engine hinge: bolted on (the game's fitted tag, no body of its own) under that vehicle
        private static bool Fitted(HandCrankPart p, VehicleController vc)
        {
            return p != null && vc != null && p.isActiveAndEnabled && p.CompareTag("vehPart") && p.GetComponent<Rigidbody>() == null && p.transform.IsChildOf(vc.transform);
        }

        // the crank the player is holding the left button on, within reach, through the crosshair
        private static HandCrankPart Clicked()
        {
            if (!Input.GetMouseButton(0) || Cursor.lockState != CursorLockMode.Locked) return null;
            var cam = Camera.main;
            if (cam == null) return null;
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, Reach, ~0, QueryTriggerInteraction.Collide)) return null;
            var part = hit.collider.GetComponentInParent<HandCrankPart>();
            return part != null && Active.Values.Any(c => c.Part == part) ? part : null;
        }

        // the person's own hurt sounds, sooner and louder the closer to their strongest they push
        private void Voice(float strain)
        {
            if (strain < 0.25f || Time.time < _nextGrunt) return;
            _nextGrunt = Time.time + Mathf.Lerp(2.5f, 0.8f, strain) * UnityEngine.Random.Range(0.8f, 1.2f);
            var clip = PlayerVoice();
            if (clip == null) return;
            if (_voice == null) { _voice = gameObject.AddComponent<AudioSource>(); _voice.spatialBlend = 0f; _voice.playOnAwake = false; }
            _voice.pitch = UnityEngine.Random.Range(0.85f, 1.0f);
            _voice.PlayOneShot(clip, Mathf.Lerp(0.25f, 0.9f, strain));
        }

        // what the player's DamageEffectSound FSM plays when they are hurt (the voice the player has)
        private static AudioClip PlayerVoice()
        {
            var g = FsmVariables.GlobalVariables.FindFsmGameObject("Player");
            var player = g != null ? g.Value : null;
            if (player == null) return null;
            foreach (var fsm in player.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "DamageEffectSound") continue;
                foreach (var state in fsm.FsmStates)
                    foreach (var a in state.Actions.OfType<PlayRandomSound>())
                    {
                        var clips = a.audioClips?.Select(x => x?.Value as AudioClip).Where(x => x != null).ToArray();
                        if (clips != null && clips.Length > 0) return clips[UnityEngine.Random.Range(0, clips.Length)];
                    }
            }
            return null;
        }

        // one fitted crank: the game's engine model run on the person's torque, as it was put back when it comes off
        private sealed class Coupling
        {
            public readonly VehicleController Vc;
            public readonly HandCrankPart Part;
            public bool ByHand;
            public float Strain;   // the torque the person makes, of the most they can
            public float LastTorque;
            public string Step = "never";
            private float _nextLog;
            private readonly EngineComponent _engine;
            private readonly EngineComponent.CalculateTorque _vanilla, _ours;
            private readonly bool _stalling, _autoStart;
            private readonly float _inertia, _idle, _limit, _engageRpm, _engageRange, _engageOffset;
            private readonly RealisticEngineSound _sound;
            private float _effort;

            public Coupling(VehicleController vc, HandCrankPart part)
            {
                Vc = vc; Part = part;
                _engine = vc.powertrain.engine;
                var clutch = vc.powertrain.clutch;
                _vanilla = _engine.calculateTorqueDelegate;
                _stalling = _engine.stallingEnabled; _autoStart = _engine.autoStartOnThrottle;
                _inertia = _engine.inertia; _idle = _engine.idleRPM; _limit = _engine.revLimiterRPM;
                _engageRpm = clutch.engagementRPM; _engageRange = clutch.engagementRange; _engageOffset = clutch.throttleEngagementOffsetRPM;
                _ours = Torque;
                if (_engine.starterActive) _engine.starterActive = false;
                _engine.stallingEnabled = false;
                _engine.autoStartOnThrottle = false;
                _engine.inertia = Mathf.Max(0.001f, part.Inertia);
                _engine.idleRPM = 0f;
                _engine.revLimiterRPM = Mathf.Max(1f, part.Speed);
                // engaged at any speed: a person has no idle (the game's automatic clutch lets in from idle up)
                clutch.engagementRPM = -1f; clutch.engagementRange = 1f; clutch.throttleEngagementOffsetRPM = 0f;
                _engine.calculateTorqueDelegate = _ours;
                _sound = vc.GetComponentInChildren<RealisticEngineSound>(true);
                if (_sound != null) ResPatch.Take(_sound);
                Plugin.Log.LogInfo($"hand crank fitted to {vc.name}: {part.Torque:0} N m held still, nothing at {part.Speed:0} rpm, at most {part.Torque * part.Speed * Mathf.PI / 30f / 4f:0} W");
            }

            // main thread, every frame: how hard the person cranks (seated: the throttle; by hand: everything)
            public void Update()
            {
                if (_engine.calculateTorqueDelegate != _ours) _engine.calculateTorqueDelegate = _ours;   // re-initialised by the game
                float seated = Vc.input != null ? Mathf.Clamp01(Vc.input.InputSwappedThrottle) : 0f;
                _effort = Mathf.Max(seated, ByHand ? 1f : 0f);
                Part.Effort = _effort;
                Part.Rpm = _engine.OutputRPM;
                if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= _nextLog)
                {
                    _nextLog = Time.unscaledTime + 1f;
                    var pt = Vc.powertrain; var cl = pt.clutch; var gb = pt.transmission;
                    Plugin.Verbose($"crank {Vc.name}: {_engine.OutputRPM:0} rpm, crank {LastTorque:0.0} N m (effort {_effort:0.00}), engine out {_engine.outputTorque:0.0}, " +
                        $"clutch {cl.controlType} in {cl.clutchInput:0.00} engaged {cl.Engagement:0.00} slip {cl.slipTorque:0} out {cl.outputTorque:0.0}, gear {gb.Gear} ratio {gb.currentGearRatio:0.00}, " +
                        $"inertia {_engine.inertia:0.000} + down {_engine.Output?.QueryInertia() ?? 0f:0.000}, speed {Vc.Speed:0.00} m/s, wheels {string.Join(" ", pt.wheels.Select(x => x.inputAngularVelocity.ToString("0.0")))} rad/s, " +
                        $"powertrain active {pt.IsActive}, step {Step}");
                }
            }

            // the game's engine asks for its torque every physics step
            private float Torque(float angularVelocity, float dt)
            {
                float rpm = angularVelocity * 30f / Mathf.PI;
                float held = Part.Torque * _effort;
                // falling from held still to nothing at the most the arms turn it; turned backwards, pushed at full
                float person = rpm <= 0f ? held : held * Mathf.Max(0f, 1f - rpm / Mathf.Max(1f, Part.Speed));
                float friction = Mathf.Abs(rpm) < 1f ? -Part.Friction * rpm : -Mathf.Sign(rpm) * Part.Friction;
                Strain = Part.Torque > 0f ? person / Part.Torque : 0f;
                LastTorque = person + friction;
                return LastTorque;
            }

            public void Release()
            {
                if (_engine != null)
                {
                    if (_engine.calculateTorqueDelegate == _ours) _engine.calculateTorqueDelegate = _vanilla;
                    _engine.stallingEnabled = _stalling; _engine.autoStartOnThrottle = _autoStart;
                    _engine.inertia = _inertia; _engine.idleRPM = _idle; _engine.revLimiterRPM = _limit;
                }
                if (Vc != null && Vc.powertrain != null && Vc.powertrain.clutch != null)
                {
                    var clutch = Vc.powertrain.clutch;
                    clutch.engagementRPM = _engageRpm; clutch.engagementRange = _engageRange; clutch.throttleEngagementOffsetRPM = _engageOffset;
                }
                if (_sound != null) ResPatch.Release(_sound);
                if (Part != null) { Part.Rpm = 0f; Part.Effort = 0f; }
                Strain = 0f;
                Plugin.Log.LogInfo("hand crank taken off " + (Vc != null ? Vc.name : "a vehicle"));
            }
        }
    }
}
