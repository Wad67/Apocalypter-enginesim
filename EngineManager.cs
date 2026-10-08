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
        private readonly Dictionary<RealisticEngineSound, float> _lastRunning = new Dictionary<RealisticEngineSound, float>();
        private readonly HashSet<string> _reported = new HashSet<string>();
        private float _nextScan;
        private float _nextStats;
        private AudioListener _listener;

        public static string IrDirectory;

        private void Update()
        {
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

            if (_listener == null || !_listener.isActiveAndEnabled) _listener = FindListener();
            var ear = _listener != null ? _listener.transform : null;
            foreach (var s in _sessions.Values)
            {
                Feed(s);
                s.UpdateDistance(ear);
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

        private static void Feed(EngineSession s)
        {
            if (s.Vc == null) return;
            var engine = s.Vc.powertrain.engine;
            float rpm = engine.OutputRPM;
            // below this the game's engine is stopped; let the sim crank coast instead of holding it
            s.SetControls(rpm > 50f ? rpm : 0f, engine.ThrottlePosition, engine.IsRunning, ReadCondition(s));
            // the game's rev limit (its own cut is a one-frame flag, or none: RPM is just clamped), and
            // forced induction as an intake pressure ratio matching the game's power gain
            var fi = engine.forcedInduction;
            s.SetLimits(engine.revLimiterRPM,
                engine.revLimiterCutoffDuration > 0.02f ? engine.revLimiterCutoffDuration : 0.08f,
                fi != null && fi.useForcedInduction ? fi.PowerGainMultiplier : 1f);
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

                if (engine.IsRunning || engine.starterActive || engine.OutputRPM > 50f) _lastRunning[res] = now;
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
                else if (FindEnginePart(s.Vc) != s.PartName) StopSession(res, "engine changed");
                else if (FindExhaustPart(s.Vc) != s.ExhaustPart) StopSession(res, "exhaust changed");
            }

            foreach (var res in wanted)
                if (!_sessions.ContainsKey(res)) TryStart(res);

            foreach (var res in _lastRunning.Keys.Where(r => r == null).ToList()) _lastRunning.Remove(res);
        }

        private void TryStart(RealisticEngineSound res)
        {
            var vc = res.GetComponentInParent<VehicleController>();
            string part = FindEnginePart(vc);
            if (part == null)
            {
                ReportOnce(vc.name + "/none", "no engine part found under " + vc.name + "'s hinge_engine; keeping the stock sound");
                return;
            }

            Native.EngineDesc desc;
            if (Native.es_parse_engine_name(part, out desc) == 0)
            {
                ReportOnce(part, "cannot read engine part '" + part + "'; keeping the stock sound");
                return;
            }
            desc.fluidSteps = Plugin.QualityEntry.Value == SimQuality.High ? 8 : Plugin.QualityEntry.Value == SimQuality.Fast ? 4 : 6;
            var engine = vc.powertrain.engine;
            if (engine.revLimiterRPM > 500f) desc.redlineRpm = engine.revLimiterRPM;
            if (engine.idleRPM > 100f) desc.idleRpm = engine.idleRPM;

            string exhaust = FindExhaustPart(vc);
            ExhaustProfile profile;
            var sound = ExhaustProfiles.Resolve(exhaust, out profile);
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
                PartDump.Once(FindPartObject(vc, "hinge_engine"), part);
                PartDump.Once(FindPartObject(vc, "hinge_exhaust"), exhaust);
                PartDump.InductionOnce(vc);
            }

            var mixer = ChosenMixer(res) ?? res.audioMixer;
            var session = new EngineSession(res, vc, part, exhaust, loudness, desc, IrDirectory, mixer);
            _sessions[res] = session;
            ResPatch.Take(res);
            Plugin.Log.LogInfo("Simulating " + vc.name + ": '" + part + "' -> " + desc
                + "; exhaust '" + (exhaust ?? "(no exhaust slot)") + "' -> " + (profile != null ? profile.Pattern : "base") + ": " + sound);
        }

        private void StopSession(RealisticEngineSound res, string why)
        {
            EngineSession s;
            if (!_sessions.TryGetValue(res, out s)) return;
            _sessions.Remove(res);
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
                var part = FindPartObject(s.Vc, "hinge_engine");
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

        // The fitted engine is a part under the vehicle's "hinge_engine", named like "2.8L V6 115HP 183Nm Gasoline".
        internal static string FindEnginePart(VehicleController vc)
        {
            if (vc == null) return null;
            var hinge = FindDeep(vc.transform, "hinge_engine");
            if (hinge == null) return null;
            foreach (Transform child in hinge)
            {
                if (!child.gameObject.activeSelf) continue;
                string name = CleanName(child.name);
                Native.EngineDesc unused;
                if (Native.es_parse_engine_name(name, out unused) != 0) return name;
            }
            return null;
        }

        // The fitted exhaust under "hinge_exhaust": its part name, "none" for an empty slot,
        // null when the vehicle has no exhaust slot at all.
        internal static string FindExhaustPart(VehicleController vc)
        {
            if (vc == null) return null;
            var hinge = FindDeep(vc.transform, "hinge_exhaust");
            if (hinge == null) return null;
            foreach (Transform child in hinge)
                if (child.gameObject.activeSelf) return CleanName(child.name);
            return ExhaustProfiles.None;
        }

        private static GameObject FindPartObject(VehicleController vc, string hingeName)
        {
            var hinge = FindDeep(vc.transform, hingeName);
            if (hinge == null) return null;
            foreach (Transform child in hinge)
                if (child.gameObject.activeSelf) return child.gameObject;
            return null;
        }

        // "(Clone)" from instantiation, and the instance number the game appends ("Gasoline160", "exhaust_the_four61")
        private static string CleanName(string name)
        {
            return System.Text.RegularExpressions.Regex.Replace(name.Replace("(Clone)", "").Trim(), @"\d+$", "");
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
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
