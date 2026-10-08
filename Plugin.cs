using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EngineSim
{
    // =====================================================================
    //  Engine Sim: replaces the stock engine sound (SkrilStudio's
    //  RealisticEngineSound) with AngeTheGreat's engine-sim, run natively in
    //  audio-only mode. Each simulated engine is built from the vehicle's
    //  fitted engine part, held at the NWH engine's RPM by the sim's dyno
    //  (transmission in neutral) and fed its throttle. Every running engine
    //  is a candidate, nearest to the listener first, up to MaxEngines.
    // =====================================================================
    public enum SimQuality { High, Balanced, Fast }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.burner.apocalypter.enginesim";
        public const string NAME = "Engine Sim";
        public const string VERSION = "1.0.0";

        public static ManualLogSource Log;
        public static ConfigEntry<bool> EnabledEntry;
        public static ConfigEntry<int> MaxEnginesEntry;
        public static ConfigEntry<SimQuality> QualityEntry;
        public static ConfigEntry<float> VolumeEntry;
        public static ConfigEntry<float> LatencyEntry;
        public static ConfigEntry<float> WearIntensityEntry;
        public static ConfigEntry<float> IdleAirEntry;
        public static ConfigEntry<bool> VerboseLogEntry;
        public static ConfigEntry<string> BaseIrEntry;
        public static ConfigEntry<float> BaseFlowEntry, BasePipeEntry, BaseHeaderEntry, BaseLoudnessEntry, BaseNoiseEntry, BaseJitterEntry;

        // raised when a setting that shapes running engines changes: they are rebuilt
        public static bool RestartRequested;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;

            // opt in to the Apocasetter Mods window (the entry itself is hidden there)
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            EnabledEntry = Config.Bind("General", "Enabled", true,
                "Replace the stock engine sound with the simulated one. Off = the game's own engine sound.");
            MaxEnginesEntry = Config.Bind("Performance", "MaxEngines", 2,
                new ConfigDescription("How many engines are simulated at once, nearest first; the rest keep the stock sound. " +
                    "Each one takes most of a CPU core.", new AcceptableValueRange<int>(1, 8)));
            QualityEntry = Config.Bind("Performance", "SimQuality", SimQuality.Balanced,
                "Gas-flow detail of the simulation. High is engine-sim's own setting; Balanced and Fast cost less CPU " +
                "(a V8 needs about 25% / 45% less) and sound slightly smoother. Lower it if the sound breaks up. " +
                "Applies to engines started after the change.");
            VolumeEntry = Config.Bind("Audio", "Volume", 0.8f,
                new ConfigDescription("Loudness of the simulated engines.", new AcceptableValueRange<float>(0f, 2f)));
            LatencyEntry = Config.Bind("Audio", "Latency", 0.05f,
                new ConfigDescription("How far (seconds) the simulation runs ahead of what you hear. " +
                    "Lower reacts faster to the throttle; too low and the sound breaks up.", new AcceptableValueRange<float>(0.02f, 0.2f)));
            WearIntensityEntry = Config.Bind("Audio", "WearIntensity", 1f,
                new ConfigDescription("How much engine condition is heard. Below 70 % condition a worn engine stumbles on " +
                    "misfires (worst at idle and on one cylinder) and hunts at idle. 0 = always sounds new, 2 = twice as harsh.",
                    new AcceptableValueRange<float>(0f, 2f)));
            IdleAirEntry = Config.Bind("Audio", "IdleAir", 0.08f,
                new ConfigDescription("Air the simulated engine idles on (a throttle floor). Without it the engine is only " +
                    "being spun at idle, not running, and sounds hollow; it is also what makes misfires of a worn engine heard.",
                    new AcceptableValueRange<float>(0f, 0.2f)));
            VerboseLogEntry = Config.Bind("Debug", "VerboseLog", false,
                "Write extra diagnostic lines to BepInEx\\LogOutput.log.");

            var dir = Path.GetDirectoryName(Info.Location);
            BindExhaustBase(Path.Combine(dir, "ir"));
            string error;
            if (!Native.Preload(dir, out error))
            {
                Log.LogError("Native library not loaded, engine sounds stay stock: " + error);
                return;
            }
            EngineManager.IrDirectory = Path.Combine(dir, "ir");
            ExhaustProfiles.Init(Paths.ConfigPath);

            new Harmony(GUID).PatchAll(typeof(Plugin).Assembly);

            _runner = new GameObject("EngineSim Runner");
            _runner.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(_runner);
            _runner.AddComponent<EngineManager>();

            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        // The sound with no exhaust fitted; every exhaust in EngineSim.exhausts.cfg is relative to it.
        private void BindExhaustBase(string irDir)
        {
            const string S = "Exhaust";
            var irs = Directory.Exists(irDir)
                ? Directory.GetFiles(irDir, "*.wav").Select(Path.GetFileName).OrderBy(n => n).ToArray()
                : new string[0];
            if (irs.Length == 0) irs = new[] { "mild_exhaust.wav" };
            BaseIrEntry = Config.Bind(S, "BaseImpulseResponse", irs.Contains("mild_exhaust.wav") ? "mild_exhaust.wav" : irs[0],
                new ConfigDescription("The exhaust acoustics every engine is played through (a recording in plugins/EngineSim/ir). " +
                    "mild_exhaust is the cleanest; the minimal_muffling ones are harsh and ring at idle.", new AcceptableValueList<string>(irs)));
            BaseFlowEntry = Config.Bind(S, "BaseOutletFlow", 1.0f,
                new ConfigDescription("How freely the exhaust flows out. Below 1 restrictive, above 1 free-flowing. " +
                    "Outside 0.8 - 1.2 engine-sim's exhaust model squeals or rasps at idle.", new AcceptableValueRange<float>(0.8f, 1.2f)));
            BasePipeEntry = Config.Bind(S, "BasePipeLength", 0.15f,
                new ConfigDescription("Pipe length after the collector with no exhaust fitted, metres. Past 0.5 m the pipe " +
                    "rings and rasps at idle.", new AcceptableValueRange<float>(0.15f, 0.5f)));
            BaseHeaderEntry = Config.Bind(S, "BaseHeaderLength", 1.0f,
                new ConfigDescription("Header (primary tube) length scale. Longer than 1 makes engine-sim's exhaust model " +
                    "oscillate (a metallic squeal at idle), so it stops at 1.", new AcceptableValueRange<float>(0.5f, 1f)));
            BaseLoudnessEntry = Config.Bind(S, "BaseLoudness", 1.0f,
                new ConfigDescription("Loudness with no exhaust fitted; exhausts are this times their own loudness.", new AcceptableValueRange<float>(0f, 2f)));
            BaseNoiseEntry = Config.Bind(S, "BaseAirNoise", 0f,
                new ConfigDescription("Air noise in the sound, 0 = automatic (suits the impulse response).", new AcceptableValueRange<float>(0f, 1f)));
            BaseJitterEntry = Config.Bind(S, "BaseJitter", 0f,
                new ConfigDescription("Pulse-timing jitter, 0 = automatic.", new AcceptableValueRange<float>(0f, 1f)));

            foreach (var e in new ConfigEntryBase[] { BaseIrEntry, BaseFlowEntry, BasePipeEntry, BaseHeaderEntry, BaseNoiseEntry, BaseJitterEntry, QualityEntry })
                e.ConfigFile.SettingChanged += (sender, args) => { if (args.ChangedSetting == e) RestartRequested = true; };
        }

        public static void Verbose(string msg)
        {
            if (VerboseLogEntry != null && VerboseLogEntry.Value) Log.LogInfo("[verbose] " + msg);
        }
    }
}
