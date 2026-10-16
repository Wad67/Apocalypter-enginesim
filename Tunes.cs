using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ES3Internal;
using UnityEngine;

namespace EngineSim
{
    // An engine's tune: what the Engine Tuning Toolkit can set on that one engine, with the tools a toolkit has (no
    // machining: bore, stroke, cylinders, cam profiles and compression stay as built).
    //   ignition   degrees added to the whole advance curve (turning the distributor)
    //   intakeCam  degrees the intake cam is advanced (an adjustable cam gear): its lobe centre moves earlier
    //   exhaustCam degrees the exhaust cam is advanced: its lobe centre moves earlier
    //   idle       rpm added to the idle speed (the idle screw)
    //   limiter    rpm added to the rev limit (the ignition cut-out)
    //   mixture    the carburettor's mixture screw: air-fuel ratio as a multiple of engine-sim's power mixture
    //              (lambda 0.8, 11.8:1 for petrol); not for diesels (the injection pump sets theirs)
    //   springs    the distributor's advance springs: the rpm the advance curve comes in at, as a multiple
    //   linkage    the throttle linkage: pedal to plate curve, as a multiple (above 1 progressive)
    //   filter     the air filter: wide-open intake flow, as a multiple (below 1 clogged, above an open element)
    //   cut        seconds the limiter cuts the ignition for (0: the game's)
    //   vvt        variable intake cam timing: the intake cams phased with rpm and load while running (advanced
    //              low down, retarded near the limit)
    //   intake / exhaust duration, lift: the cams' lobes (a hotter or milder cam): degrees added to the duration, and
    //              lift as a multiple. The valve tick follows (when each valve closes, and how hard it seats)
    internal sealed class Tune
    {
        public float Ignition, IntakeCam, ExhaustCam, Idle, Limiter, Cut;
        public float Mixture = 1f, Springs = 1f, Linkage = 1f, Filter = 1f;
        public float IntakeDuration, ExhaustDuration, IntakeLift = 1f, ExhaustLift = 1f;
        public float Wastegate, Relief, Pulley = 1f;
        public bool Vvt;   // turbo wastegate psi added; supercharger relief psi added, pulley drive ratio

        public const float IgnitionRange = 10f, CamRange = 8f, IdleMin = -300f, IdleMax = 500f, LimiterMin = -1500f, LimiterMax = 3000f;
        public const float MixtureMin = 0.75f, MixtureMax = 1.45f, SpringsMin = 0.6f, SpringsMax = 1.4f, LinkageMin = 0.5f, LinkageMax = 2f;
        public const float FilterMin = 0.8f, FilterMax = 1.3f, CutMax = 0.3f, PetrolAfr = 11.76f;
        public const float DurationRange = 30f, LiftMin = 0.8f, LiftMax = 1.25f;
        public const float WastegateMin = -6f, WastegateMax = 10f, ReliefMin = -4f, ReliefMax = 8f, PulleyMin = 0.8f, PulleyMax = 1.3f;

        public bool IsStock => Ignition == 0 && IntakeCam == 0 && ExhaustCam == 0 && Idle == 0 && Limiter == 0 && Cut == 0
                               && Mixture == 1 && Springs == 1 && Linkage == 1 && Filter == 1
                               && IntakeDuration == 0 && ExhaustDuration == 0 && IntakeLift == 1 && ExhaustLift == 1
                               && Wastegate == 0 && Relief == 0 && Pulley == 1 && !Vvt;

        public Tune Clone() => (Tune)MemberwiseClone();

        // the engine's parameters with the tune on them (arrays it changes are copied: the rest are shared with
        // every engine of that kind)
        public void Apply(ref Native.EngineDesc desc)
        {
            if (Ignition != 0f)
            {
                var advance = (float[])desc.engine.timingAdvanceDeg.Clone();
                for (int i = 0; i < desc.engine.timingSamples; i++) advance[i] += Ignition;
                desc.engine.timingAdvanceDeg = advance;
            }
            if (Springs != 1f)
            {
                var rpm = (float[])desc.engine.timingRpm.Clone();
                for (int i = 0; i < desc.engine.timingSamples; i++) rpm[i] *= Springs;
                desc.engine.timingRpm = rpm;
            }
            if (desc.engine.diesel == 0) desc.engine.mixture = Mixture;
            desc.engine.throttleGamma *= Linkage;
            desc.engine.intakeFlowCfm *= Filter;
            desc.engine.intakeCamDurationDeg += IntakeDuration;
            desc.engine.exhaustCamDurationDeg += ExhaustDuration;
            desc.engine.intakeCamLiftMm *= IntakeLift;
            desc.engine.exhaustCamLiftMm *= ExhaustLift;
            desc.engine.intakeLobeCenterDeg -= IntakeCam;    // degrees after TDC: advanced is less
            desc.engine.exhaustLobeCenterDeg += ExhaustCam;  // degrees before TDC: advanced is more
            if (Idle != 0f && desc.idleRpm > 0f) desc.idleRpm = Mathf.Max(200f, desc.idleRpm + Idle);
            if (Limiter != 0f && desc.redlineRpm > 0f) desc.redlineRpm = Mathf.Max(desc.idleRpm + 500f, desc.redlineRpm + Limiter);
        }

        public override string ToString() =>
            $"ignition {Ignition:+0.#;-0.#;0} deg, springs x{Springs:0.00}, intake cam {IntakeCam:+0.#;-0.#;0} deg, exhaust cam {ExhaustCam:+0.#;-0.#;0} deg, " +
            $"intake {IntakeDuration:+0;-0;0} deg x{IntakeLift:0.00} lift, exhaust {ExhaustDuration:+0;-0;0} deg x{ExhaustLift:0.00} lift{(Vvt ? ", vvt" : "")}, mixture {Mixture * PetrolAfr:0.0}:1, filter x{Filter:0.00}, linkage x{Linkage:0.00}, idle {Idle:+0;-0;0} rpm, limiter {Limiter:+0;-0;0} rpm" + (Cut > 0 ? $" cutting {Cut:0.00} s" : "");

        // as stored: the offsets and multiples in order
        public float[] Values => new[] { Ignition, IntakeCam, ExhaustCam, Idle, Limiter, Mixture, Springs, Linkage, Filter, Cut,
                                         IntakeDuration, IntakeLift, ExhaustDuration, ExhaustLift, Wastegate, Relief, Pulley, Vvt ? 1f : 0f };

        public static Tune From(float[] v)
        {
            float At(int i, float d) => i < v.Length ? v[i] : d;
            return new Tune { Ignition = At(0, 0), IntakeCam = At(1, 0), ExhaustCam = At(2, 0), Idle = At(3, 0), Limiter = At(4, 0),
                              Mixture = At(5, 1), Springs = At(6, 1), Linkage = At(7, 1), Filter = At(8, 1), Cut = At(9, 0),
                              IntakeDuration = At(10, 0), IntakeLift = At(11, 1), ExhaustDuration = At(12, 0), ExhaustLift = At(13, 1),
                              Wastegate = At(14, 0), Relief = At(15, 0), Pulley = At(16, 1), Vvt = At(17, 0) > 0.5f };
        }

        // the fitted machines with the tune on them: the turbos' wastegate (its spring or a boost controller), the
        // supercharger's pulley (how fast it is driven: as much more air a turn) and relief valve
        public void ApplyInduction(ref Native.InductionDesc d)
        {
            if (d.turbos > 0) d.wastegatePsi = Mathf.Max(1f, d.wastegatePsi + Wastegate);
            if (d.superchargerSize > 0f)
            {
                d.superchargerSize *= Pulley;
                d.superchargerReliefPsi = Mathf.Max(1f, d.superchargerReliefPsi + Relief);
            }
        }
    }

    // Tunes by engine and save slot, in BepInEx/config/EngineSim.tunes.cfg (one line each: slot | engine = values).
    // An engine is known by its saved name (the key the game saves it under); where two engines share a name (both
    // fitted when their vehicles were made), by its vehicle's name as well.
    internal static class Tunes
    {
        private static string _path;
        private static Dictionary<string, Tune> _tunes;

        public static void Init(string configDir) { _path = Path.Combine(configDir, "EngineSim.tunes.cfg"); }

        // the save slot being played (Easy Save's default file)
        private static string Slot
        {
            get
            {
                try { return ES3Settings.defaultSettings.path ?? "default"; }
                catch { return "default"; }
            }
        }

        public static string Key(GameObject engine)
        {
            if (engine == null) return null;
            var vehicle = engine.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>();
            return vehicle != null && Shared(engine) ? vehicle.name + "/" + engine.name : engine.name;
        }

        // another loaded engine part with the same name
        private static bool Shared(GameObject engine)
        {
            var mine = engine.GetComponent<ES3Prefab>();
            foreach (var other in Resources.FindObjectsOfTypeAll<ES3Prefab>())
                if (other != mine && other.gameObject.scene.IsValid() && other.name == engine.name) return true;
            return false;
        }

        public static Tune For(GameObject engine)
        {
            Load();
            string key = Key(engine);
            if (key == null) return null;
            return _tunes.TryGetValue(Slot + "|" + key, out var t) ? t : null;
        }

        public static void Set(GameObject engine, Tune tune)
        {
            Load();
            string key = Key(engine);
            if (key == null) return;
            if (tune == null || tune.IsStock) _tunes.Remove(Slot + "|" + key);
            else _tunes[Slot + "|" + key] = tune.Clone();
            Save();
            Plugin.Log.LogInfo($"Tune for {key} ({Slot}): " + (tune == null || tune.IsStock ? "stock" : tune.ToString()));
        }

        private static void Load()
        {
            if (_tunes != null) return;
            _tunes = new Dictionary<string, Tune>();
            if (_path == null || !File.Exists(_path)) return;
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.LastIndexOf('=');
                if (eq < 0) continue;
                var v = line.Substring(eq + 1).Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f).ToArray();
                if (v.Length < 5) continue;
                _tunes[line.Substring(0, eq).Trim()] = Tune.From(v);
            }
        }

        private static void Save()
        {
            if (_path == null) return;
            var lines = new List<string>
            {
                "# Engine Sim tunes, set with the Engine Tuning Toolkit: save slot | engine = ignition deg, intake cam deg,",
                "# exhaust cam deg, idle rpm, limiter rpm (offsets from the engine as built), mixture, advance springs,",
                "# throttle linkage, air filter (multiples of as built), limiter cut s (0: the game's), intake duration deg,",
                "# intake lift (multiple), exhaust duration deg, exhaust lift (multiple), turbo wastegate psi, supercharger",
                "# relief psi, supercharger pulley (multiple), variable intake cam timing (1 on)",
            };
            foreach (var kv in _tunes.OrderBy(k => k.Key, StringComparer.Ordinal))
                lines.Add(kv.Key + " = " + string.Join(", ", kv.Value.Values.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()));
            try { File.WriteAllLines(_path, lines.ToArray()); }
            catch (Exception e) { Plugin.Log.LogWarning("tunes: " + e.Message); }
        }
    }
}
