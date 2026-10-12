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
        public const string VERSION = "1.4.0";

        public static ManualLogSource Log;
        public static ConfigEntry<bool> EnabledEntry;
        public static ConfigEntry<int> MaxEnginesEntry;
        public static ConfigEntry<SimQuality> QualityEntry;
        public static ConfigEntry<float> VolumeEntry;
        public static ConfigEntry<float> LatencyEntry;
        public static ConfigEntry<float> DistanceAttenuationEntry, DistanceScaleEntry;
        public static ConfigEntry<bool> FilterEntry;
        public static ConfigEntry<string> BackfireSoundEntry;
        public static ConfigEntry<float> FilterCrankEntry, FilterIdleEntry, FilterOpenAtEntry;
        public static ConfigEntry<float> BackfireEntry, BackfireChanceEntry, ValveTickEntry, EngineRockEntry, SmokeEntry, ChassisTorqueEntry;
        public static ConfigEntry<float> WearIntensityEntry;
        public static ConfigEntry<float> IdleAirEntry;
        public static ConfigEntry<bool> VerboseLogEntry;
        public static ConfigEntry<string> BaseIrEntry;
        public static ConfigEntry<bool> InductionEntry;
        public static ConfigEntry<bool> SimDriveEntry, MatchGamePowerEntry, StarterGivesUpEntry;
        public static ConfigEntry<float> HardStartingEntry;
        public static ConfigEntry<float> TurboVolumeEntry, TurboPitchLowEntry, TurboPitchHighEntry, TurboFullSpoolEntry, TurboSpinDownEntry;
        public static ConfigEntry<float> SuperchargerVolumeEntry, SuperchargerPitchEntry;
        public static ConfigEntry<float> BaseFlowEntry, BasePipeEntry, BaseHeaderEntry, BaseLoudnessEntry, BaseNoiseEntry, BaseJitterEntry;

        // raised when a setting that shapes running engines changes: they are rebuilt
        public static bool RestartRequested;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;

            // opt in to the Apocasetter Mods window (the entry itself is hidden there)
            Config.Bind("General", "Apocasetter", true, "Show in the Apocasetter Mods menu.");
            EnabledEntry = Config.Bind("General", "Enabled", true, "Replace the stock engine sound.");
            MaxEnginesEntry = Config.Bind("Performance", "MaxEngines", 2, new ConfigDescription("Engines simulated at once, turning ones first, then the nearest. Each uses most of a CPU core.", new AcceptableValueRange<int>(1, 8)));
            QualityEntry = Config.Bind("Performance", "SimQuality", SimQuality.Balanced, "Gas-flow detail. Lower costs less CPU. Applies to engines started after the change.");
            VolumeEntry = Config.Bind("Audio", "Volume", 0.8f, new ConfigDescription("Engine loudness.", new AcceptableValueRange<float>(0f, 2f)));
            DistanceAttenuationEntry = Config.Bind("Audio", "DistanceAttenuation", 1f, new ConfigDescription("Fade with distance. 1 = game's rolloff, 0 = none.", new AcceptableValueRange<float>(0f, 1f)));
            DistanceScaleEntry = Config.Bind("Audio", "DistanceScale", 1f, new ConfigDescription("Scales rolloff distances. Above 1 carries further.", new AcceptableValueRange<float>(0.25f, 4f)));
            FilterEntry = Config.Bind("Audio", "LowRpmFilter", true, "Low-pass on the engine sound at low rpm.");
            FilterCrankEntry = Config.Bind("Audio", "LowRpmFilterCrank", 400f, new ConfigDescription("Low-rpm filter cutoff at cranking speed, Hz.", new AcceptableValueRange<float>(100f, 5000f)));
            FilterIdleEntry = Config.Bind("Audio", "LowRpmFilterIdle", 2500f, new ConfigDescription("Low-rpm filter cutoff at idle, Hz.", new AcceptableValueRange<float>(300f, 20000f)));
            FilterOpenAtEntry = Config.Bind("Audio", "LowRpmFilterOpenAt", 1.5f, new ConfigDescription("Low-rpm filter fully open at this multiple of idle.", new AcceptableValueRange<float>(1.05f, 4f)));
            LatencyEntry = Config.Bind("Audio", "Latency", 0.05f, new ConfigDescription("Seconds the simulation runs ahead. Lower reacts faster; too low breaks up.", new AcceptableValueRange<float>(0.02f, 0.2f)));
            WearIntensityEntry = Config.Bind("Audio", "WearIntensity", 1f, new ConfigDescription("How much engine condition is heard. 0 = always new.", new AcceptableValueRange<float>(0f, 2f)));
            IdleAirEntry = Config.Bind("Audio", "IdleAir", 0.08f, new ConfigDescription("Idle air (throttle floor) when the game drives the engine.", new AcceptableValueRange<float>(0f, 0.2f)));
            VerboseLogEntry = Config.Bind("Debug", "VerboseLog", false, "Extra diagnostics in the BepInEx log.");

            BindInduction();
            BackfireEntry = Config.Bind("Effects", "Backfires", 1f, new ConfigDescription("Backfire loudness. 0 = off.", new AcceptableValueRange<float>(0f, 2f)));
            BackfireSoundEntry = Config.Bind("Effects", "BackfireSound", "gunshot_6", new ConfigDescription("Backfire report sound.", new AcceptableValueList<string>("gunshots", "gunshot_1", "gunshot_3", "gunshot_6", "gunshot_7", "generated")));
            BackfireChanceEntry = Config.Bind("Effects", "BackfireChance", 0.01f, new ConfigDescription("Chance unburnt fuel in a hot exhaust backfires.", new AcceptableValueRange<float>(0f, 1f)));
            ValveTickEntry = Config.Bind("Effects", "ValveTick", 1f, new ConfigDescription("Valvetrain tick loudness. 0 = off.", new AcceptableValueRange<float>(0f, 4f)));
            EngineRockEntry = Config.Bind("Effects", "EngineRock", 1f, new ConfigDescription("Engine rocking on its mounts. 0 = game's shake.", new AcceptableValueRange<float>(0f, 3f)));
            SmokeEntry = Config.Bind("Effects", "ExhaustSmoke", 1f, new ConfigDescription("Exhaust smoke amount. 0 = game's smoke.", new AcceptableValueRange<float>(0f, 3f)));
            ChassisTorqueEntry = Config.Bind("Effects", "ChassisTorque", 1f, new ConfigDescription("Engine torque reaction on the body. 0 = off.", new AcceptableValueRange<float>(0f, 5f)));
            BindExperimental();
            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(InductionSound));   // works without the native library

            var dir = Path.GetDirectoryName(Info.Location);
            BindExhaustBase(Path.Combine(dir, "ir"));
            string error;
            if (!Native.Preload(dir, out error))
            {
                Log.LogError("Native library not loaded, engine sounds stay stock: " + error);
                return;
            }
            EngineManager.IrDirectory = Path.Combine(dir, "ir");
            PartTable.Init(Paths.ConfigPath);

            harmony.PatchAll(typeof(ResPatch));
            harmony.PatchAll(typeof(ResActivePatch));
            harmony.PatchAll(typeof(DrivelinePatch));
            harmony.PatchAll(typeof(RigidWheelPatch));
            harmony.PatchAll(typeof(StartStopPatch));
            harmony.PatchAll(typeof(StopPatch));
            harmony.PatchAll(typeof(StartPatch));
            harmony.PatchAll(typeof(InductionMachines.GaugePatch));

            _runner = new GameObject("EngineSim Runner");
            _runner.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(_runner);
            _runner.AddComponent<EngineManager>();
            FsmDump.Init();

            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        private void BindExperimental()
        {
            const string S = "Experimental";
            SimDriveEntry = Config.Bind(S, "⚠ Simulated drivetrain ⚠", false, "EXPERIMENTAL: the simulated engine powers the vehicle. Restarts engines.");
            MatchGamePowerEntry = Config.Bind(S, "Match game power", true, "Simulated drivetrain: scale torque to the game's rated power.");
            HardStartingEntry = Config.Bind(S, "Hard starting", 1f, new ConfigDescription("Simulated drivetrain: cold and worn engines need cranking. 0 = start instantly.", new AcceptableValueRange<float>(0f, 1f)));
            StarterGivesUpEntry = Config.Bind(S, "Starter gives up", true, "Simulated drivetrain: an overloaded starter only clicks.");
            foreach (var e in new ConfigEntryBase[] { SimDriveEntry, MatchGamePowerEntry })
                e.ConfigFile.SettingChanged += (sender, args) => { if (args.ChangedSetting == e) RestartRequested = true; };
        }

        // The game's own turbo / supercharger spool sound, re-proportioned (InductionSound).
        private void BindInduction()
        {
            const string S = "Induction";
            InductionEntry = Config.Bind(S, "ReplaceSpoolSound", true, "Replace the turbo / supercharger sound.");
            TurboVolumeEntry = Config.Bind(S, "TurboVolume", 0.5f, new ConfigDescription("Turbo whistle loudness.", new AcceptableValueRange<float>(0f, 1f)));
            TurboPitchLowEntry = Config.Bind(S, "TurboPitchLow", 0.3f, new ConfigDescription("Turbo pitch, barely spinning.", new AcceptableValueRange<float>(0.1f, 1.5f)));
            TurboPitchHighEntry = Config.Bind(S, "TurboPitchHigh", 0.8f, new ConfigDescription("Turbo pitch, fully spooled.", new AcceptableValueRange<float>(0.5f, 3f)));
            TurboFullSpoolEntry = Config.Bind(S, "TurboFullSpoolRpm", 0.65f, new ConfigDescription("Fraction of the rev limit where a turbo fully spools (game drivetrain only).", new AcceptableValueRange<float>(0.3f, 1f)));
            TurboSpinDownEntry = Config.Bind(S, "TurboSpinDown", 0.8f, new ConfigDescription("Turbo coast-down seconds (game drivetrain only).", new AcceptableValueRange<float>(0.1f, 3f)));
            SuperchargerVolumeEntry = Config.Bind(S, "SuperchargerVolume", 0.5f, new ConfigDescription("Supercharger whine loudness.", new AcceptableValueRange<float>(0f, 1f)));
            SuperchargerPitchEntry = Config.Bind(S, "SuperchargerPitch", 0.5f, new ConfigDescription("Supercharger pitch at the rev limit.", new AcceptableValueRange<float>(0.5f, 3f)));
        }

        // The sound with no exhaust fitted; every exhaust in EngineSim.exhausts.cfg is relative to it.
        private void BindExhaustBase(string irDir)
        {
            const string S = "Exhaust";
            var irs = Directory.Exists(irDir)
                ? Directory.GetFiles(irDir, "*.wav").Select(Path.GetFileName).OrderBy(n => n).ToArray()
                : new string[0];
            if (irs.Length == 0) irs = new[] { "mild_exhaust.wav" };
            BaseIrEntry = Config.Bind(S, "BaseImpulseResponse", irs.Contains("mild_exhaust.wav") ? "mild_exhaust.wav" : irs[0], new ConfigDescription("Exhaust acoustics recording (plugins/EngineSim/ir).", new AcceptableValueList<string>(irs)));
            BaseFlowEntry = Config.Bind(S, "BaseOutletFlow", 1.0f, new ConfigDescription("Exhaust outlet flow. Below 1 restrictive.", new AcceptableValueRange<float>(0.8f, 1.2f)));
            BasePipeEntry = Config.Bind(S, "BasePipeLength", 0.15f, new ConfigDescription("Pipe length with no exhaust fitted, metres.", new AcceptableValueRange<float>(0.15f, 0.5f)));
            BaseHeaderEntry = Config.Bind(S, "BaseHeaderLength", 1.0f, new ConfigDescription("Header length scale.", new AcceptableValueRange<float>(0.5f, 1f)));
            BaseLoudnessEntry = Config.Bind(S, "BaseLoudness", 1.0f, new ConfigDescription("Loudness with no exhaust fitted.", new AcceptableValueRange<float>(0f, 2f)));
            BaseNoiseEntry = Config.Bind(S, "BaseAirNoise", 0f, new ConfigDescription("Air noise. 0 = automatic.", new AcceptableValueRange<float>(0f, 1f)));
            BaseJitterEntry = Config.Bind(S, "BaseJitter", 0f, new ConfigDescription("Pulse-timing jitter. 0 = automatic.", new AcceptableValueRange<float>(0f, 1f)));

            foreach (var e in new ConfigEntryBase[] { BaseIrEntry, BaseFlowEntry, BasePipeEntry, BaseHeaderEntry, BaseNoiseEntry, BaseJitterEntry, QualityEntry })
                e.ConfigFile.SettingChanged += (sender, args) => { if (args.ChangedSetting == e) RestartRequested = true; };
        }

        public static void Verbose(string msg)
        {
            if (VerboseLogEntry != null && VerboseLogEntry.Value) Log.LogInfo("[verbose] " + msg);
        }
    }
}
