using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EngineSim
{
    // An exhaust's sound, relative to the base sound with no exhaust fitted. The base is set in
    // the mod options ([Exhaust] Base*); BepInEx/config/EngineSim.exhausts.cfg says how each
    // exhaust part differs from it.
    internal sealed class ExhaustProfile
    {
        public string Pattern;
        public string Ir;          // impulse response in plugins/EngineSim/ir; null = the base one
        public float FlowScale;    // x base outlet flow: < 1 restrictive, > 1 free-flowing
        public float ExtraLengthM; // + base pipe length
        public float HeaderScale;  // x base header (primary tube) length
        public float Loudness;     // x base loudness
        public float AirNoise;     // 0 = the base value
        public float Jitter;       // 0 = the base value

        public override string ToString()
        {
            return Pattern + " (" + (Ir ?? "base sound") + ", flow x" + FlowScale + ", pipe +" + ExtraLengthM + " m, headers x"
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

    internal static class ExhaustProfiles
    {
        public const string None = "none";
        public const string BaseIr = "-";

        private const string Defaults =
@"# Engine Sim exhaust profiles (format 5).
# Every exhaust is described RELATIVE TO THE BASE SOUND with no exhaust fitted. The base sound is set in
# the mod options (Mods menu, Engine Sim, [Exhaust] Base...). Exhausts make no difference in the vanilla
# game, so these are made up: muffled exhausts get less flow, a little more pipe and less loudness.
#
# The part fitted under a vehicle's hinge_exhaust is matched against these lines top to bottom; a line
# matches when its name appears in the part name (case-insensitive). 'none' = no exhaust fitted,
# '*' = anything else.
#
# name = impulse response, flow x, pipe + m, headers x, loudness x [, air noise, jitter]
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
exhaust_the_four     = -, 1.2,  0.05, 1.0, 1.0
exhaust_the_six      = -, 1.2,  0.05, 1.0, 1.0
exhaust_quad_big     = -, 1.2,  0.35, 1.0, 0.9
exhaust_duo_big      = -, 1.1,  0.25, 1.0, 0.85
exhaust_motorcycle   = -, 1.0,  0.20, 1.0, 0.9
rustallion_exhaust   = -, 0.95, 0.25, 1.0, 0.85
poloska_exhaust      = -, 0.9,  0.30, 1.0, 0.8
exhaust_single       = -, 0.85, 0.30, 1.0, 0.75
*                    = -, 0.95, 0.25, 1.0, 0.85
";

        // format 4 (first relative release): replaced on load if left unedited
        private const string DefaultsV4 =
@"# Engine Sim exhaust profiles (format 4).
# Every exhaust is described RELATIVE TO THE BASE SOUND with no exhaust fitted. The base sound is set in
# the mod options (Mods menu, Engine Sim, [Exhaust] Base...). Exhausts make no difference in the vanilla
# game, so these are made up: muffled exhausts get a longer pipe, less flow and less loudness.
#
# The part fitted under a vehicle's hinge_exhaust is matched against these lines top to bottom; a line
# matches when its name appears in the part name (case-insensitive). 'none' = no exhaust fitted,
# '*' = anything else.
#
# name = impulse response, flow x, pipe + m, headers x, loudness x [, air noise, jitter]
#   impulse response: '-' = the base one, or a .wav in BepInEx/plugins/EngineSim/ir
#   flow x: times the base outlet flow (below 1 restrictive, above 1 free-flowing). The result is kept
#     within 0.8 - 1.2: beyond that engine-sim's exhaust model oscillates or rasps at idle.
#   pipe + m: metres added to the base pipe length (a longer pipe is more muffled, and quieter)
#   headers x: times the base header length
#   loudness x: times the base loudness
#   air noise, jitter: 0..1, optional; replace the base values

none                 = -, 1.0, 0.00, 1.0, 1.0
exhaust_the_four     = -, 1.5, 0.10, 1.2, 0.95
exhaust_the_six      = -, 1.5, 0.10, 1.2, 0.95
exhaust_quad_big     = -, 1.3, 0.35, 1.0, 0.9
exhaust_duo_big      = -, 1.1, 0.50, 1.0, 0.85
exhaust_motorcycle   = -, 1.0, 0.30, 1.0, 0.9
rustallion_exhaust   = -, 1.0, 0.60, 1.0, 0.85
poloska_exhaust      = -, 0.8, 0.80, 1.0, 0.8
exhaust_single       = -, 0.7, 1.00, 1.0, 0.75
*                    = -, 1.0, 0.60, 1.0, 0.85
";

        // earlier releases' defaults (absolute values): replaced on load if left unedited
        private const string DefaultsV3 =
@"# Engine Sim exhaust profiles (format 3).
# The part fitted under a vehicle's hinge_exhaust is matched against these lines top to bottom;
# a line matches when its name appears in the part name (case-insensitive). 'none' = no exhaust
# fitted, '*' = anything else. Exhausts make no difference in the vanilla game: these are made up.
#
# name = impulse response, outlet flow, pipe length (m), header length scale, loudness [, air noise, jitter]
#   impulse response: a .wav in BepInEx/plugins/EngineSim/ir, from most to least muffled:
#     mild_exhaust.wav, mild_exhaust_reverb.wav, smooth_39.wav, minimal_muffling_02.wav,
#     minimal_muffling_01.wav, minimal_muffling_03.wav
#   outlet flow: 1 = normal, below 1 restrictive, above 1 free-flowing (subtle: engine-sim levels it out)
#   loudness: gain on the final sound (engine-sim evens out loudness, so set the difference here)
#   air noise, jitter: 0..1, optional. The less muffled the impulse response, the lower these must be,
#     or the noise drowns the engine (the minimal_muffling ones want ~0.15). Left out = chosen to suit
#     the impulse response. minimal_muffling_01 / 03 squeal at idle on big engines; 02 is the rawest that doesn't.

none                 = minimal_muffling_02.wav, 4.0, 0.15, 0.5, 1.5
exhaust_motorcycle   = minimal_muffling_01.wav, 1.2, 0.80, 1.0, 1.0
exhaust_single       = mild_exhaust.wav,        0.7, 2.50, 1.0, 0.8
exhaust_duo_big      = minimal_muffling_02.wav, 1.6, 2.00, 1.0, 1.15
exhaust_quad_big     = minimal_muffling_02.wav, 2.2, 1.80, 1.0, 1.3
exhaust_the_four     = minimal_muffling_02.wav, 2.5, 0.50, 1.5, 1.35
exhaust_the_six      = minimal_muffling_02.wav, 2.5, 0.50, 1.5, 1.35
poloska_exhaust      = mild_exhaust.wav,        0.8, 2.00, 1.0, 0.85
rustallion_exhaust   = minimal_muffling_02.wav, 1.2, 1.50, 1.0, 1.0
*                    = mild_exhaust.wav,        1.0, 1.50, 1.0, 1.0
";

        private const string DefaultsV2 =
@"# Engine Sim exhaust profiles (format 2).
# The part fitted under a vehicle's hinge_exhaust is matched against these lines top to bottom;
# a line matches when its name appears in the part name (case-insensitive). 'none' = no exhaust
# fitted, '*' = anything else. Exhausts make no difference in the vanilla game: these are made up.
#
# name = impulse response, outlet flow, pipe length (m), header length scale, loudness [, air noise, jitter]
#   impulse response: a .wav in BepInEx/plugins/EngineSim/ir, from most to least muffled:
#     mild_exhaust.wav, mild_exhaust_reverb.wav, smooth_39.wav, minimal_muffling_02.wav,
#     minimal_muffling_01.wav, minimal_muffling_03.wav
#   outlet flow: 1 = normal, below 1 restrictive, above 1 free-flowing (subtle: engine-sim levels it out)
#   loudness: gain on the final sound (engine-sim evens out loudness, so set the difference here)
#   air noise, jitter: 0..1, optional. The less muffled the impulse response, the lower these must be,
#     or the noise drowns the engine (the minimal_muffling ones want ~0.15). Left out = chosen to suit
#     the impulse response.

none                 = minimal_muffling_03.wav, 4.0, 0.15, 0.5, 1.5
exhaust_motorcycle   = minimal_muffling_01.wav, 1.2, 0.80, 1.0, 1.0
exhaust_single       = mild_exhaust.wav,        0.7, 2.50, 1.0, 0.8
exhaust_duo_big      = minimal_muffling_02.wav, 1.6, 2.00, 1.0, 1.15
exhaust_quad_big     = minimal_muffling_03.wav, 2.2, 1.80, 1.0, 1.3
exhaust_the_four     = minimal_muffling_01.wav, 2.5, 0.50, 1.5, 1.35
exhaust_the_six      = minimal_muffling_01.wav, 2.5, 0.50, 1.5, 1.35
poloska_exhaust      = mild_exhaust.wav,        0.8, 2.00, 1.0, 0.85
rustallion_exhaust   = minimal_muffling_02.wav, 1.2, 1.50, 1.0, 1.0
*                    = mild_exhaust.wav,        1.0, 1.50, 1.0, 1.0
";

        private const string DefaultsV1 =
@"# Engine Sim exhaust profiles.
# The part fitted under a vehicle's hinge_exhaust is matched against these lines top to bottom;
# a line matches when its name appears in the part name (case-insensitive). 'none' = no exhaust
# fitted, '*' = anything else.
#
# name = impulse response, outlet flow, pipe length (m), header length scale, loudness
#   impulse response: a .wav in BepInEx/plugins/EngineSim/ir
#     mild_exhaust.wav, mild_exhaust_reverb.wav, minimal_muffling_01/02/03.wav, sharp_01.wav, smooth_39.wav
#   outlet flow: 1 = normal, below 1 restrictive (muffled), above 1 free-flowing
#   loudness: gain on the final sound (engine-sim evens out loudness, so set the difference here)

none                 = sharp_01.wav,            4.0, 0.15, 0.5, 1.6
exhaust_motorcycle   = minimal_muffling_01.wav, 1.2, 0.80, 1.0, 1.0
exhaust_single       = mild_exhaust.wav,        0.7, 2.50, 1.0, 0.8
exhaust_duo_big      = minimal_muffling_02.wav, 1.6, 2.00, 1.0, 1.15
exhaust_quad_big     = minimal_muffling_03.wav, 2.2, 1.80, 1.0, 1.3
exhaust_the_four     = minimal_muffling_01.wav, 2.5, 0.50, 1.5, 1.35
exhaust_the_six      = minimal_muffling_01.wav, 2.5, 0.50, 1.5, 1.35
poloska_exhaust      = mild_exhaust.wav,        0.8, 2.00, 1.0, 0.85
rustallion_exhaust   = minimal_muffling_02.wav, 1.2, 1.50, 1.0, 1.0
*                    = mild_exhaust.wav,        1.0, 1.50, 1.0, 1.0
";

        private static List<ExhaustProfile> _profiles;
        private static string _path;
        private static DateTime _loadedStamp;

        public static void Init(string configDir)
        {
            _path = Path.Combine(configDir, "EngineSim.exhausts.cfg");
            if (!File.Exists(_path))
            {
                File.WriteAllText(_path, Defaults);
            }
            else
            {
                var text = Normalize(File.ReadAllText(_path));
                bool relative = text.StartsWith("# Engine Sim exhaust profiles (format 4)") || text.StartsWith("# Engine Sim exhaust profiles (format 5)");
                if (text == Normalize(DefaultsV4))
                {
                    File.WriteAllText(_path, Defaults);
                }
                else if (!relative)
                {
                    bool untouched = text == Normalize(DefaultsV1) || text == Normalize(DefaultsV2) || text == Normalize(DefaultsV3);
                    if (!untouched)
                    {
                        // an edited file in the old absolute format would be misread as relative values
                        File.Copy(_path, _path + ".old", true);
                        Plugin.Log.LogWarning(Path.GetFileName(_path) + " was in the old format (absolute values); kept as "
                            + Path.GetFileName(_path) + ".old and replaced with the new relative defaults");
                    }
                    File.WriteAllText(_path, Defaults);
                }
            }
            Load();
        }

        // re-read when the file was edited, so changes apply to the next engine that starts
        private static void Load()
        {
            var list = new List<ExhaustProfile>();
            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(_path))
            {
                ++lineNo;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                var f = eq > 0 ? line.Substring(eq + 1).Split(',') : new string[0];
                try
                {
                    if (f.Length != 5 && f.Length != 7) throw new FormatException("expected 5 or 7 values after '='");
                    var ir = f[0].Trim();
                    list.Add(new ExhaustProfile
                    {
                        Pattern = line.Substring(0, eq).Trim().ToLowerInvariant(),
                        Ir = ir == BaseIr || ir.Length == 0 ? null : ir,
                        FlowScale = Num(f[1]),
                        ExtraLengthM = Num(f[2]),
                        HeaderScale = Num(f[3]),
                        Loudness = Num(f[4]),
                        AirNoise = f.Length > 5 ? Num(f[5]) : 0f,
                        Jitter = f.Length > 6 ? Num(f[6]) : 0f,
                    });
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning(Path.GetFileName(_path) + " line " + lineNo + " ignored: " + e.Message);
                }
            }
            _profiles = list;
            _loadedStamp = File.GetLastWriteTimeUtc(_path);
        }

        private static string Normalize(string s) { return s.Replace("\r\n", "\n").Trim(); }

        private static float Num(string s) { return float.Parse(s.Trim(), CultureInfo.InvariantCulture); }

        public static ExhaustProfile Find(string exhaustPart)
        {
            if (_path != null && File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedStamp) Load();
            var name = (exhaustPart ?? "*").ToLowerInvariant();
            foreach (var p in _profiles)
            {
                if (p.Pattern == "*") return p;
                if (p.Pattern == None ? name == None : name.Contains(p.Pattern)) return p;
            }
            return null;   // no '*' line: the base sound unchanged
        }

        // base (mod options) combined with the profile of the fitted exhaust
        public static ExhaustSound Resolve(string exhaustPart, out ExhaustProfile profile)
        {
            profile = Find(exhaustPart);
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
