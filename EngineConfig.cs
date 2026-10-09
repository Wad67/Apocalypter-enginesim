using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EngineSim
{
    // BepInEx/config/EngineSim.engines.cfg: every value engine-sim builds an engine from, one section
    // per engine part, keyed by the part prefab's Easy Save id (ES3Prefab.prefabId).
    //
    //   [7876660059110608148]   # 2.8L V6 115HP 183Nm Gasoline
    //   cylinders = 6
    //   ...
    //
    // The game's engines ship fully written out (engines.default.cfg, embedded). A section only needs
    // the five basic keys; any other key left out gets a generic default worked out from those five.
    internal static class EngineConfig
    {
        public const string Header = "# Engine Sim engines (format 2).";

        private enum Kind { Float, PerCylinder, PerCylinderInt, PerBank, PortFlow, Timing }

        private sealed class Key
        {
            public string Name, Field;
            public Kind Kind;
            public Key(string name, string field, Kind kind) { Name = name; Field = field; Kind = kind; }
        }

        // the five basic keys, which the defaults are worked out from
        private static readonly string[] BasicKeys = { "cylinders", "layout", "bank_angle", "displacement", "fuel" };

        private static readonly Key[] Keys =
        {
            new Key("firing_angles", "firingAngleDeg", Kind.PerCylinder),
            new Key("cylinder_banks", "cylinderBank", Kind.PerCylinderInt),
            new Key("bore", "boreMm", Kind.Float),
            new Key("stroke", "strokeMm", Kind.Float),
            new Key("rod_length", "rodLengthMm", Kind.Float),
            new Key("compression_height", "compressionHeightMm", Kind.Float),
            new Key("compression_ratio", "compressionRatio", Kind.Float),
            new Key("piston_mass", "pistonMassG", Kind.Float),
            new Key("rod_mass", "rodMassG", Kind.Float),
            new Key("crank_mass", "crankMassKg", Kind.Float),
            new Key("flywheel_mass", "flywheelMassKg", Kind.Float),
            new Key("flywheel_radius", "flywheelRadiusMm", Kind.Float),
            new Key("friction_torque", "frictionTorqueNm", Kind.Float),
            new Key("starter_torque", "starterTorqueNm", Kind.Float),
            new Key("starter_speed", "starterSpeedRpm", Kind.Float),
            new Key("blowby", "blowbyCfm", Kind.Float),
            new Key("plenum_volume", "plenumVolumeL", Kind.Float),
            new Key("plenum_area", "plenumAreaCm2", Kind.Float),
            new Key("intake_flow", "intakeFlowCfm", Kind.Float),
            new Key("runner_flow", "runnerFlowCfm", Kind.Float),
            new Key("runner_length", "runnerLengthMm", Kind.Float),
            new Key("idle_throttle_plate", "idleThrottlePlate", Kind.Float),
            new Key("throttle_gamma", "throttleGamma", Kind.Float),
            new Key("intake_velocity_decay", "intakeVelocityDecay", Kind.Float),
            new Key("intake_port_flow", "intakePortCfm", Kind.PortFlow),
            new Key("exhaust_port_flow", "exhaustPortCfm", Kind.PortFlow),
            new Key("head_flow_scale", "headFlowScale", Kind.PerBank),
            new Key("intake_runner_volume", "intakeRunnerVolumeCc", Kind.Float),
            new Key("intake_runner_area", "intakeRunnerAreaCm2", Kind.Float),
            new Key("exhaust_runner_volume", "exhaustRunnerVolumeCc", Kind.Float),
            new Key("exhaust_runner_area", "exhaustRunnerAreaCm2", Kind.Float),
            new Key("intake_cam_duration", "intakeCamDurationDeg", Kind.Float),
            new Key("intake_cam_lift", "intakeCamLiftMm", Kind.Float),
            new Key("intake_cam_gamma", "intakeCamGamma", Kind.Float),
            new Key("intake_lobe_center", "intakeLobeCenterDeg", Kind.Float),
            new Key("exhaust_cam_duration", "exhaustCamDurationDeg", Kind.Float),
            new Key("exhaust_cam_lift", "exhaustCamLiftMm", Kind.Float),
            new Key("exhaust_cam_gamma", "exhaustCamGamma", Kind.Float),
            new Key("exhaust_lobe_center", "exhaustLobeCenterDeg", Kind.Float),
            new Key("cam_base_radius", "camBaseRadiusMm", Kind.Float),
            new Key("collector_diameter", "collectorDiameterMm", Kind.Float),
            new Key("outlet_flow", "outletFlowCfm", Kind.Float),
            new Key("primary_length", "primaryLengthMm", Kind.Float),
            new Key("primary_flow", "primaryFlowCfm", Kind.Float),
            new Key("exhaust_velocity_decay", "exhaustVelocityDecay", Kind.Float),
            new Key("exhaust_volume", "exhaustVolume", Kind.PerBank),
            new Key("header_lengths", "headerLengthMm", Kind.PerCylinder),
            new Key("sound_attenuation", "soundAttenuation", Kind.PerCylinder),
            new Key("timing", "timingAdvanceDeg", Kind.Timing),
            new Key("burning_efficiency", "burningEfficiency", Kind.Float),
            new Key("simulation_frequency", "simulationFrequency", Kind.Float),
            new Key("min_gas_steps", "minGasStepsPerSecond", Kind.Float),
            new Key("air_noise", "airNoise", Kind.Float),
            new Key("jitter", "jitter", Kind.Float),
        };

        // ---- writing ---------------------------------------------------------

        // the game's engines with every key (engines.default.cfg in the project, embedded)
        public static string DefaultFile()
        {
            using (var stream = typeof(EngineConfig).Assembly.GetManifestResourceStream("EngineSim.engines.default.cfg"))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        // ---- reading ---------------------------------------------------------

        private sealed class Section
        {
            public long Id;
            public string Label;
            public int LineNo;
            public readonly Dictionary<string, KeyValuePair<int, string>> Values = new Dictionary<string, KeyValuePair<int, string>>();
        }

        public static Dictionary<long, EngineSpec> Load(string path)
        {
            var table = new Dictionary<long, EngineSpec>();
            var name = Path.GetFileName(path);
            foreach (var sec in Sections(path, name))
            {
                try
                {
                    table[sec.Id] = Build(sec, name);
                }
                catch (FormatException e)
                {
                    Plugin.Log.LogWarning(name + " [" + sec.Id + "] (line " + sec.LineNo + ") ignored: " + e.Message);
                }
            }
            return table;
        }

        private static IEnumerable<Section> Sections(string path, string name)
        {
            Section cur = null;
            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                ++lineNo;
                var line = raw;
                string comment = null;
                int hash = line.IndexOf('#');
                if (hash >= 0) { comment = line.Substring(hash + 1).Trim(); line = line.Substring(0, hash); }
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (cur != null) yield return cur;
                    long id;
                    cur = null;
                    if (!long.TryParse(line.Substring(1, line.Length - 2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                        Plugin.Log.LogWarning(name + " line " + lineNo + ": '" + line + "' is not a part id; section ignored");
                    else
                        cur = new Section { Id = id, Label = comment, LineNo = lineNo };
                    continue;
                }
                int eq = line.IndexOf('=');
                if (cur == null) continue;
                if (eq <= 0) { Plugin.Log.LogWarning(name + " line " + lineNo + " ignored: no '='"); continue; }
                cur.Values[line.Substring(0, eq).Trim().ToLowerInvariant()] = new KeyValuePair<int, string>(lineNo, line.Substring(eq + 1).Trim());
            }
            if (cur != null) yield return cur;
        }

        private static EngineSpec Build(Section sec, string fileName)
        {
            string v;
            int cylinders = (int)Basic(sec, "cylinders");
            v = Required(sec, "layout").ToUpperInvariant();
            var layout = v == "I" ? Native.Layout.Inline : v == "V" ? Native.Layout.V : v == "B" ? Native.Layout.Boxer
                : throw new FormatException("layout must be I, V or B");
            float bank = sec.Values.ContainsKey("bank_angle") ? Basic(sec, "bank_angle") : (layout == Native.Layout.V ? 90f : 0f);
            float litres = Basic(sec, "displacement");
            v = Required(sec, "fuel").ToLowerInvariant();
            bool diesel = v == "diesel" ? true : v == "gasoline" ? false : throw new FormatException("fuel must be gasoline or diesel");

            Native.EngineParams p;
            if (Native.es_default_params(cylinders, (int)layout, bank, litres, diesel ? 1 : 0, out p) == 0)
                throw new FormatException("cylinders must be 1 - 16 and displacement 0.02 - 30 L");

            object boxed = p;   // fields are set by reflection on the boxed copy
            foreach (var k in Keys)
            {
                KeyValuePair<int, string> raw;
                if (!sec.Values.TryGetValue(k.Name, out raw)) continue;
                try { Apply(k, raw.Value, boxed, cylinders); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning(fileName + " line " + raw.Key + ": " + k.Name + " ignored (default kept): " + e.Message);
                }
            }
            foreach (var key in sec.Values.Keys)
                if (!BasicKeys.Contains(key) && !Keys.Any(k => k.Name == key))
                    Plugin.Log.LogWarning(fileName + " line " + sec.Values[key].Key + ": unknown key '" + key + "'");

            return new EngineSpec { Id = sec.Id, Label = sec.Label, Params = (Native.EngineParams)boxed };
        }

        private static string Required(Section sec, string key)
        {
            KeyValuePair<int, string> raw;
            if (!sec.Values.TryGetValue(key, out raw)) throw new FormatException("'" + key + "' is missing");
            return raw.Value;
        }

        private static float Basic(Section sec, string key) { return Num(Required(sec, key)); }

        private static void Apply(Key k, string value, object boxed, int cylinders)
        {
            var field = typeof(Native.EngineParams).GetField(k.Field);
            switch (k.Kind)
            {
            case Kind.Float:
                field.SetValue(boxed, Num(value));
                break;
            case Kind.PerCylinder:
                Fill((float[])field.GetValue(boxed), List(value, cylinders, cylinders, k.Name));
                break;
            case Kind.PerCylinderInt:
            {
                var src = List(value, cylinders, cylinders, k.Name);
                if (src.Any(x => x != 0 && x != 1)) throw new FormatException("banks are 0 or 1");
                var dst = (int[])field.GetValue(boxed);
                for (int i = 0; i < src.Length; ++i) dst[i] = (int)src[i];
                break;
            }
            case Kind.PerBank:
            {
                var src = List(value, 1, Native.MaxBanks, k.Name);
                var dst = (float[])field.GetValue(boxed);
                for (int i = 0; i < dst.Length; ++i) dst[i] = src[Math.Min(i, src.Length - 1)];
                break;
            }
            case Kind.PortFlow:
                Fill((float[])field.GetValue(boxed), List(value, Native.PortFlowSamples, Native.PortFlowSamples, k.Name));
                break;
            case Kind.Timing:
            {
                var pairs = value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (pairs.Length < 1 || pairs.Length > Native.MaxTimingSamples) throw new FormatException("needs 1 - 12 rpm:degrees pairs");
                var rpm = (float[])typeof(Native.EngineParams).GetField("timingRpm").GetValue(boxed);
                var deg = (float[])field.GetValue(boxed);
                for (int i = 0; i < pairs.Length; ++i)
                {
                    var rd = pairs[i].Split(':');
                    if (rd.Length != 2) throw new FormatException("'" + pairs[i] + "' is not rpm:degrees");
                    rpm[i] = Num(rd[0]);
                    deg[i] = Num(rd[1]);
                }
                typeof(Native.EngineParams).GetField("timingSamples").SetValue(boxed, pairs.Length);
                break;
            }
            }
        }

        private static float[] List(string value, int min, int max, string name)
        {
            var parts = value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(Num).ToArray();
            if (parts.Length < min || parts.Length > max)
                throw new FormatException(min == max ? "needs " + min + " values" : "needs " + min + " - " + max + " values");
            return parts;
        }

        private static void Fill(float[] dst, float[] src) { Array.Copy(src, dst, src.Length); }

        private static float Num(string s) { return float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture); }
    }
}
