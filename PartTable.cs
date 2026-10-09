using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ES3Internal;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace EngineSim
{
    // An engine part's entry in EngineSim.engines.cfg (EngineConfig): everything engine-sim builds it from.
    internal sealed class EngineSpec
    {
        public long Id;
        public string Label;   // the comment after the section header, for the log only
        public Native.EngineParams Params;
        public float DriveTorqueScale = 1f;   // [Experimental] simulated drivetrain: matches the game's rated power
        public float PeakTorque;              // simulated peak torque at 1 atm, N m (0 = unknown): sizes the clutch
    }

    // An exhaust's sound, relative to the base sound with no exhaust fitted. The base is set in
    // the mod options ([Exhaust] Base*); EngineSim.exhausts.cfg says how each exhaust part differs from it.
    internal sealed class ExhaustProfile
    {
        public string Key;         // prefab id, "none" or "default"
        public string Label;
        public string Ir;          // impulse response in plugins/EngineSim/ir; null = the base one
        public float FlowScale;    // x base outlet flow: < 1 restrictive, > 1 free-flowing
        public float ExtraLengthM; // + base pipe length
        public float HeaderScale;  // x base header (primary tube) length
        public float Loudness;     // x base loudness
        public float AirNoise;     // 0 = the base value
        public float Jitter;       // 0 = the base value

        public override string ToString()
        {
            return (Label ?? Key) + " (" + (Ir ?? "base sound") + ", flow x" + FlowScale + ", pipe +" + ExtraLengthM + " m, headers x"
                + HeaderScale + ", loudness x" + Loudness
                + (AirNoise > 0 ? ", noise " + AirNoise : "") + (Jitter > 0 ? ", jitter " + Jitter : "") + ")";
        }
    }

    // The resolved exhaust for one engine: base settings combined with the fitted part's profile.
    internal struct ExhaustSound
    {
        public string Ir;
        public float Flow, LengthM, HeaderScale, Loudness, AirNoise, Jitter;

        public override string ToString()
        {
            return Ir + ", flow " + Flow.ToString("0.##") + ", pipe " + LengthM.ToString("0.##") + " m, headers x"
                + HeaderScale.ToString("0.##") + ", loudness " + Loudness.ToString("0.##")
                + ", noise " + (AirNoise > 0 ? AirNoise.ToString("0.##") : "auto") + ", jitter " + (Jitter > 0 ? Jitter.ToString("0.##") : "auto");
        }
    }

    // The parts fitted to a vehicle: prefab id (0 = none) and the part's GameObject.
    internal struct FittedParts
    {
        public long EngineId, ExhaustId, InductionId;
        public GameObject Engine, Exhaust;
        // every fitted forced-induction part (with the simulated drivetrain a turbo and a supercharger
        // can be fitted together)
        public List<KeyValuePair<long, GameObject>> Induction;
    }

    // a forced-induction part: its set pressure (psi over ambient) and the relative size of each unit
    internal sealed class InductionPart
    {
        public float Psi;
        public float[] Sizes;
    }

    internal static class PartTable
    {
        public const string NoExhaust = "none";
        public const string DefaultExhaust = "default";
        private const string BaseIr = "-";

        public const string DefaultTurbo = "default_turbo", DefaultSupercharger = "default_supercharger";
        private const string InductionHeader = "# Engine Sim forced induction (format 2).";
        private const string InductionDefaults = InductionHeader + @"
# Each forced-induction part: the pressure it is set to and its units.
#
# part id = set pressure : size [, size ...]      # label (not read)
#   part id: the part prefab's Easy Save id (ES3Prefab.prefabId); 'default_turbo' and 'default_supercharger'
#     are for parts not listed here. Whether a part is a turbo or a supercharger is the part's own.
#   set pressure: psi over ambient. With the simulated drivetrain (experimental) a turbo's wastegate or a
#     supercharger's relief valve opens there, if the engine and the unit can get that far at all.
#     Without it this is not used: the game's own boost applies. The defaults are the game's power gain
#     as pressure (the simulated engine makes about 1 + 1.65 (p - 1) the torque at pressure ratio p).
#   size: 0.2 - 3, one per turbo (a part with several turbos lists each). 1 = a medium unit: for a
#     supercharger 2 L of air per crank revolution, for a turbo a 50 mm compressor wheel. A smaller turbo
#     spools sooner and whistles higher; a bigger supercharger moves more air, takes more power to drive,
#     and whines lower and louder.

default_turbo        = 4.5 : 1.0
default_supercharger = 4.5 : 1.0

8359398305947690842  = 0.45 : 0.55            # Turbo - Little Bee - Power x1.05
1254148493211391348  = 0.8  : 0.65            # Turbo - War Whistle - Power x1.09
8589483064466669470  = 4.45 : 0.85, 0.85      # Turbo - Double Trouble - Power x1.5 (twin)
8446457777719976447  = 5.3  : 1.4             # Turbo - Big Brutus - Power x1.6
865075917467842793   = 6.2  : 0.6, 0.6, 1.4   # Turbo - Triple Threat - Power x1.7 (two small, one large)

7322277502189585245  = 0.45 : 0.55            # Supercharger - Tiny Tim - Power x1.05
3060985782920507676  = 0.8  : 0.65            # Supercharger - Small Sam - Power x1.09
1280457821324750492  = 1.8  : 0.8             # Supercharger - Med Mark - Power x1.2
7895438034034407351  = 4.45 : 1.25            # supercharger_4 - Power x1.5
1235638759040636875  = 5.3  : 1.4             # Supercharger - The Average - Power x1.6
3556354107990682579  = 6.2  : 1.0, 1.0        # Supercharger - Double Barrel - Power x1.7 (twin)
8130352843546044666  = 7.1  : 1.7             # Supercharger - Big Joe - Power x1.8
3223342285364861050  = 8.0  : 1.85            # Supercharger - Screaming Skull - Power x1.9
";

        private const string ExhaustsHeader = "# Engine Sim exhaust profiles (format 6).";
        private const string ExhaustDefaults = ExhaustsHeader + @"
# Every exhaust is described RELATIVE TO THE BASE SOUND with no exhaust fitted. The base sound is set in
# the mod options (Mods menu, Engine Sim, [Exhaust] Base...). Exhausts make no difference in the vanilla
# game, so these are made up: muffled exhausts get less flow, a little more pipe and less loudness.
#
# part id = impulse response, flow x, pipe + m, headers x, loudness x [, air noise, jitter]   # label (not read)
#   part id: the exhaust part prefab's Easy Save id (ES3Prefab.prefabId); 'none' = no exhaust fitted,
#     'default' = an exhaust part not listed here.
#   impulse response: '-' = the base one, or a .wav in BepInEx/plugins/EngineSim/ir
#   flow x: times the base outlet flow (below 1 restrictive, above 1 free-flowing). The result is kept
#     within 0.8 - 1.2: beyond that engine-sim's exhaust model oscillates or rasps at idle.
#   pipe + m: metres added to the base pipe length; the result is kept within 0.15 - 0.5 m (longer pipes
#     ring and rasp at idle).
#   headers x: times the base header length, 0.5 to 1.0. Longer headers make engine-sim's exhaust model
#     oscillate (a metallic squeal at idle), so anything above 1.0 is treated as 1.0.
#   loudness x: times the base loudness
#   air noise, jitter: 0..1, optional; replace the base values

none                 = -, 1.0,  0.00, 1.0, 1.0
default              = -, 0.95, 0.25, 1.0, 0.85
8632022485459187021  = -, 0.95, 0.25, 1.0, 0.85    # exhaust_buggy
2804734035022541720  = -, 0.95, 0.25, 1.0, 0.85    # exhaust_duo
1793414009541011771  = -, 1.1,  0.25, 1.0, 0.85    # exhaust_duo_big
327631139404193261   = -, 1.0,  0.20, 1.0, 0.9     # exhaust_motorcycle
7434710111027667286  = -, 0.95, 0.25, 1.0, 0.85    # exhaust_quad
7511643549129607125  = -, 1.2,  0.35, 1.0, 0.9     # exhaust_quad_big
2560830693520208292  = -, 0.85, 0.30, 1.0, 0.75    # exhaust_single
4874496480344750803  = -, 0.85, 0.30, 1.0, 0.75    # exhaust_single_big
5392072180626433585  = -, 1.2,  0.05, 1.0, 1.0     # exhaust_the_four
6582790640607959259  = -, 1.2,  0.05, 1.0, 1.0     # exhaust_the_six
3832368787590666331  = -, 0.95, 0.25, 1.0, 0.85    # exhaust_v6
8862298570093993790  = -, 0.95, 0.25, 1.0, 0.85    # exhaust_v8
6045398300156428213  = -, 0.9,  0.30, 1.0, 0.8     # poloska_exhaust
8088368402979805175  = -, 0.95, 0.25, 1.0, 0.85    # rustallion_exhaust_1
3481429504808865570  = -, 0.95, 0.25, 1.0, 0.85    # rustallion_exhaust_2
6341429240891360207  = -, 0.95, 0.25, 1.0, 0.85    # rustallion_exhaust_3
";

        private sealed class TableFile
        {
            public string Path;
            public DateTime Stamp;
        }

        private static readonly TableFile EnginesFile = new TableFile(), ExhaustsFile = new TableFile(), InductionFile = new TableFile();
        private static Dictionary<string, InductionPart> _induction = new Dictionary<string, InductionPart>();
        private static Dictionary<long, EngineSpec> _engines = new Dictionary<long, EngineSpec>();
        private static Dictionary<string, ExhaustProfile> _exhausts = new Dictionary<string, ExhaustProfile>();

        public static void Init(string configDir)
        {
            EnginesFile.Path = Path.Combine(configDir, "EngineSim.engines.cfg");
            ExhaustsFile.Path = Path.Combine(configDir, "EngineSim.exhausts.cfg");
            InductionFile.Path = Path.Combine(configDir, "EngineSim.induction.cfg");
            Prepare(EnginesFile.Path, EngineConfig.Header, EngineConfig.DefaultFile());
            Prepare(ExhaustsFile.Path, ExhaustsHeader, ExhaustDefaults);
            Prepare(InductionFile.Path, InductionHeader, InductionDefaults);
            Reload(true);
        }

        // Writes the defaults if the file is missing. A file in an older format (exhaust profiles were
        // matched by part name before format 6, engines had one line each in format 1) is kept as .old
        // and replaced.
        private static void Prepare(string path, string header, string defaults)
        {
            if (File.Exists(path))
            {
                if (File.ReadAllText(path).StartsWith(header)) return;
                File.Copy(path, path + ".old", true);
                Plugin.Log.LogWarning(System.IO.Path.GetFileName(path) + " was in an older format; kept as "
                    + System.IO.Path.GetFileName(path) + ".old and replaced with the defaults");
            }
            File.WriteAllText(path, defaults);
        }

        // re-read when a file was edited, so changes apply to the next engine that starts
        private static void Reload(bool force)
        {
            if (force || Changed(EnginesFile))
            {
                EnginesFile.Stamp = File.GetLastWriteTimeUtc(EnginesFile.Path);
                _engines = EngineConfig.Load(EnginesFile.Path);
            }
            if (force || Changed(ExhaustsFile)) _exhausts = LoadExhausts();
            if (force || Changed(InductionFile)) _induction = LoadInduction();
        }

        private static bool Changed(TableFile f)
        {
            return File.Exists(f.Path) && File.GetLastWriteTimeUtc(f.Path) != f.Stamp;
        }

        private static Dictionary<string, ExhaustProfile> LoadExhausts()
        {
            var table = new Dictionary<string, ExhaustProfile>();
            ExhaustsFile.Stamp = File.GetLastWriteTimeUtc(ExhaustsFile.Path);
            foreach (var row in Rows(ExhaustsFile.Path))
            {
                try
                {
                    long unused;
                    var key = row.Key.ToLowerInvariant();
                    if (key != NoExhaust && key != DefaultExhaust && !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out unused))
                        throw new FormatException("'" + row.Key + "' is not a part id, 'none' or 'default'");
                    var f = row.Values;
                    if (f.Length != 5 && f.Length != 7) throw new FormatException("expected 5 or 7 values after '='");
                    var ir = f[0].Trim();
                    table[key] = new ExhaustProfile
                    {
                        Key = key,
                        Label = row.Label,
                        Ir = ir == BaseIr || ir.Length == 0 ? null : ir,
                        FlowScale = Num(f[1]),
                        ExtraLengthM = Num(f[2]),
                        HeaderScale = Num(f[3]),
                        Loudness = Num(f[4]),
                        AirNoise = f.Length > 5 ? Num(f[5]) : 0f,
                        Jitter = f.Length > 6 ? Num(f[6]) : 0f,
                    };
                }
                catch (Exception e) { row.Warn(ExhaustsFile.Path, e); }
            }
            return table;
        }

        private static Dictionary<string, InductionPart> LoadInduction()
        {
            var table = new Dictionary<string, InductionPart>();
            InductionFile.Stamp = File.GetLastWriteTimeUtc(InductionFile.Path);
            foreach (var row in Rows(InductionFile.Path))
            {
                try
                {
                    long unused;
                    var key = row.Key.ToLowerInvariant();
                    if (key != DefaultTurbo && key != DefaultSupercharger && !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out unused))
                        throw new FormatException("'" + row.Key + "' is not a part id, '" + DefaultTurbo + "' or '" + DefaultSupercharger + "'");
                    var all = string.Join(",", row.Values.ToArray());
                    int colon = all.IndexOf(':');
                    if (colon < 0) throw new FormatException("expected 'set pressure : size [, size ...]'");
                    float psi = Num(all.Substring(0, colon));
                    var sizes = all.Substring(colon + 1).Split(',').Select(v => Num(v)).ToArray();
                    if (psi < 0f || psi > 60f) throw new FormatException("set pressure is 0 - 60 psi");
                    if (sizes.Length < 1 || sizes.Length > 4) throw new FormatException("expected 1 - 4 sizes");
                    if (sizes.Any(x => x < 0.2f || x > 3f)) throw new FormatException("sizes are 0.2 - 3");
                    table[key] = new InductionPart { Psi = psi, Sizes = sizes };
                }
                catch (Exception e) { row.Warn(InductionFile.Path, e); }
            }
            return table;
        }

        // The turbos (or superchargers) of a forced-induction part: one relative size each. Parts not
        // listed (id 0 = not found) use the default line for their type.
        public static float[] InductionUnits(long id, bool supercharger) { return Induction(id, supercharger).Sizes; }

        public static InductionPart Induction(long id, bool supercharger)
        {
            Reload(false);
            InductionPart part;
            if (id != 0 && _induction.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out part)) return part;
            if (_induction.TryGetValue(supercharger ? DefaultSupercharger : DefaultTurbo, out part)) return part;
            return new InductionPart { Psi = 4.5f, Sizes = new[] { 1f } };
        }

        public static bool IsInduction(long id) { Reload(false); return _induction.ContainsKey(id.ToString(CultureInfo.InvariantCulture)); }

        private sealed class Row
        {
            public int LineNo;
            public string Key, Label;
            public string[] Values;

            public void Warn(string path, Exception e)
            {
                Plugin.Log.LogWarning(System.IO.Path.GetFileName(path) + " line " + LineNo + " ignored: " + e.Message);
            }
        }

        // "key = a, b, c   # label"
        private static IEnumerable<Row> Rows(string path)
        {
            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                ++lineNo;
                var line = raw;
                string label = null;
                int hash = line.IndexOf('#');
                if (hash >= 0) { label = line.Substring(hash + 1).Trim(); line = line.Substring(0, hash); }
                line = line.Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    Plugin.Log.LogWarning(System.IO.Path.GetFileName(path) + " line " + lineNo + " ignored: no '='");
                    continue;
                }
                yield return new Row
                {
                    LineNo = lineNo,
                    Key = line.Substring(0, eq).Trim(),
                    Label = label,
                    Values = line.Substring(eq + 1).Split(','),
                };
            }
        }

        private static float Num(string s) { return float.Parse(s.Trim(), CultureInfo.InvariantCulture); }

        public static EngineSpec Engine(long id)
        {
            Reload(false);
            EngineSpec spec;
            return _engines.TryGetValue(id, out spec) ? spec : null;
        }

        public static bool IsEngine(long id) { return _engines.ContainsKey(id); }

        public static bool IsExhaust(long id) { return _exhausts.ContainsKey(id.ToString(CultureInfo.InvariantCulture)); }

        // The parts fitted to a vehicle. A fitted part is an active part (it has an ES3Prefab) under the
        // vehicle without a Rigidbody: the game's attach logic removes a part's Rigidbody when it is
        // fitted and gives it one back when it comes off, so a loose part carried along is not taken.
        // Engines are the parts listed in the engine table; exhausts those in the exhaust table.
        public static FittedParts Fitted(VehicleController vc)
        {
            var fitted = new FittedParts { Induction = new List<KeyValuePair<long, GameObject>>() };
            if (vc == null) return fitted;
            foreach (var p in vc.GetComponentsInChildren<ES3Prefab>(false))
            {
                if (p.GetComponent<Rigidbody>() != null) continue;
                if (fitted.EngineId == 0 && IsEngine(p.prefabId)) { fitted.EngineId = p.prefabId; fitted.Engine = p.gameObject; }
                else if (fitted.ExhaustId == 0 && IsExhaust(p.prefabId)) { fitted.ExhaustId = p.prefabId; fitted.Exhaust = p.gameObject; }
                else if (_induction.ContainsKey(p.prefabId.ToString(CultureInfo.InvariantCulture)))
                {
                    if (fitted.InductionId == 0) fitted.InductionId = p.prefabId;
                    fitted.Induction.Add(new KeyValuePair<long, GameObject>(p.prefabId, p.gameObject));
                }
            }
            return fitted;
        }

        // Fitted parts in neither table, for the log when a vehicle has no listed engine: one of them
        // is the engine to add to EngineSim.engines.cfg.
        public static IEnumerable<ES3Prefab> UnlistedParts(VehicleController vc)
        {
            foreach (var p in vc.GetComponentsInChildren<ES3Prefab>(false))
                if (p.GetComponent<Rigidbody>() == null && !IsEngine(p.prefabId) && !IsExhaust(p.prefabId)
                    && !_induction.ContainsKey(p.prefabId.ToString(CultureInfo.InvariantCulture))) yield return p;
        }

        // base (mod options) combined with the profile of the fitted exhaust (id 0 = none fitted)
        public static ExhaustSound ResolveExhaust(long exhaustId, out ExhaustProfile profile)
        {
            Reload(false);
            var key = exhaustId == 0 ? NoExhaust : exhaustId.ToString(CultureInfo.InvariantCulture);
            if (!_exhausts.TryGetValue(key, out profile) && exhaustId != 0) _exhausts.TryGetValue(DefaultExhaust, out profile);
            var s = new ExhaustSound
            {
                Ir = Plugin.BaseIrEntry.Value,
                Flow = Plugin.BaseFlowEntry.Value,
                LengthM = Plugin.BasePipeEntry.Value,
                HeaderScale = Plugin.BaseHeaderEntry.Value,
                Loudness = Plugin.BaseLoudnessEntry.Value,
                AirNoise = Plugin.BaseNoiseEntry.Value,
                Jitter = Plugin.BaseJitterEntry.Value,
            };
            if (profile != null)
            {
                if (profile.Ir != null) s.Ir = profile.Ir;
                s.Flow *= profile.FlowScale;
                s.LengthM += profile.ExtraLengthM;
                s.HeaderScale *= profile.HeaderScale;
                s.Loudness *= profile.Loudness;
                if (profile.AirNoise > 0) s.AirNoise = profile.AirNoise;
                if (profile.Jitter > 0) s.Jitter = profile.Jitter;
            }
            // Keep engine-sim's exhaust model where it is stable (mapped on a V8 at idle): outlet flow
            // above ~1.2x or headers above 1x break into a 1-2 kHz oscillation (a metallic squeal),
            // pipes past ~0.5 m or flow under ~0.8x ring and rasp.
            s.Flow = Math.Min(1.2f, Math.Max(0.8f, s.Flow));
            s.LengthM = Math.Min(0.5f, Math.Max(0.15f, s.LengthM));
            s.HeaderScale = Math.Min(1f, Math.Max(0.5f, s.HeaderScale));
            return s;
        }
    }
}
