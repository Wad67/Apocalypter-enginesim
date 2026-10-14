using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EngineSim
{
    // P/Invoke surface of enginesim_native.dll (native/bridge/enginesim_api.h).
    // A session is single-threaded: only its EngineSession sim thread calls into it.
    internal static class Native
    {
        private const string Dll = "enginesim_native";

        public const int SampleRate = 44100;

        public enum Layout { Inline = 0, V = 1, Boxer = 2 }

        public const int MaxCylinders = 16, MaxBanks = 2, PortFlowSamples = 15, MaxTimingSamples = 12;

        // es_engine_params: everything engine-sim is built from (EngineSim.engines.cfg). Field order
        // and sizes match enginesim_api.h exactly. Per-cylinder arrays are indexed by cylinder number - 1.
        [StructLayout(LayoutKind.Sequential)]
        public struct EngineParams
        {
            public int cylinders;
            public int layout;
            public float bankAngleDeg;
            public float displacementL;
            public int diesel;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxCylinders)] public float[] firingAngleDeg;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxCylinders)] public int[] cylinderBank;

            public float boreMm, strokeMm, rodLengthMm, compressionHeightMm, compressionRatio;
            public float pistonMassG, rodMassG;
            public float crankMassKg, flywheelMassKg, flywheelRadiusMm, frictionTorqueNm;
            public float starterTorqueNm, starterSpeedRpm;
            public float blowbyCfm;

            public float plenumVolumeL, plenumAreaCm2, intakeFlowCfm, runnerFlowCfm, runnerLengthMm;
            public float idleThrottlePlate;
            public float throttleGamma, intakeVelocityDecay;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = PortFlowSamples)] public float[] intakePortCfm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = PortFlowSamples)] public float[] exhaustPortCfm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxBanks)] public float[] headFlowScale;
            public float intakeRunnerVolumeCc, intakeRunnerAreaCm2, exhaustRunnerVolumeCc, exhaustRunnerAreaCm2;
            public float intakeCamDurationDeg, intakeCamLiftMm, intakeCamGamma, intakeLobeCenterDeg;
            public float exhaustCamDurationDeg, exhaustCamLiftMm, exhaustCamGamma, exhaustLobeCenterDeg;
            public float camBaseRadiusMm;

            public float collectorDiameterMm, outletFlowCfm, primaryLengthMm, primaryFlowCfm, exhaustVelocityDecay;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxBanks)] public float[] exhaustVolume;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxCylinders)] public float[] headerLengthMm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxCylinders)] public float[] soundAttenuation;

            public int timingSamples;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTimingSamples)] public float[] timingRpm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTimingSamples)] public float[] timingAdvanceDeg;

            public float burningEfficiency;
            public float simulationFrequency;
            public float minGasStepsPerSecond;
            public float airNoise, jitter;
            public float mixture;   // air-fuel ratio scale (Engine Tuning Toolkit); 0 = as built

            public override string ToString()
            {
                return (Layout)layout + cylinders.ToString() + (layout == (int)Layout.V ? " (" + bankAngleDeg + "°)" : "")
                    + ", bore " + boreMm.ToString("0.#") + " x stroke " + strokeMm.ToString("0.#") + " mm"
                    + (diesel != 0 ? ", diesel" : ", gasoline");
            }
        }

        // es_engine_desc: an engine to simulate, with the game's idle and rev limit and the fitted exhaust
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct EngineDesc
        {
            public EngineParams engine;
            public float redlineRpm;
            public float idleRpm;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string exhaustIr;
            public float exhaustFlow;
            public float exhaustLengthM;
            public float headerScale;
            public float airNoise;
            public float jitter;
            public int fluidSteps;

            public override string ToString()
            {
                return engine + ", idle " + idleRpm + " / limit " + redlineRpm + " rpm";
            }
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int es_desc_size();

        // the native library's es_engine_desc is the same size as ours (the plugin and native library match)
        public static bool LayoutMatches(out string error)
        {
            int native = es_desc_size(), managed = Marshal.SizeOf(typeof(EngineDesc));
            error = native == managed ? null : "engine description is " + native + " bytes in enginesim_native.dll but "
                + managed + " in EngineSim.dll; the two files are from different versions";
            return error == null;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_default_params(int cylinders, int layout, float bankAngleDeg, float displacementL,
            int diesel, out EngineParams p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr es_create(ref EngineDesc desc, [MarshalAs(UnmanagedType.LPStr)] string irDirectory);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_destroy(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_target_rpm(IntPtr s, double rpm);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_throttle(IntPtr s, double throttle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_ignition(IntPtr s, int on);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_condition(IntPtr s, double condition);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_wear_intensity(IntPtr s, double intensity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_rev_limiter(IntPtr s, double rpm, double cutSeconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_boost(IntPtr s, double pressureRatio);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_idle_air(IntPtr s, double idleAir);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_target_latency(IntPtr s, double seconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_volume(IntPtr s, double volume);

        // drive mode: the engine powers the game's drivetrain (see enginesim_api.h)

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_drive_mode(IntPtr s, int on);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_starter(IntPtr s, int on);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_idle_rpm(IntPtr s, double rpm);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_vvt(IntPtr s, int on);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_fluid_steps(IntPtr s, int steps);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double es_get_torque(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_fuel(IntPtr s, int available);

        public const int MaxTurbos = 4;

        // es_induction_desc: forced induction as machines (drive mode)
        [StructLayout(LayoutKind.Sequential)]
        public struct InductionDesc
        {
            public float superchargerSize;
            public float superchargerReliefPsi;
            public int turbos;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTurbos)] public float[] turboSize;
            public float wastegatePsi;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_induction(IntPtr s, ref InductionDesc desc);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_get_induction(IntPtr s, out double boostPsi, [Out] double[] turboSpeed, out double superchargerKw);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_get_blowoffs(IntPtr s, out double psi);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double es_get_crank_inertia(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double es_get_starter_load(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_get_starter_jammed(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_starting(IntPtr s, double hardStart, int starterGivesUp);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_heat(IntPtr s, double heat);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_lowpass(IntPtr s, double crankHz, double idleHz, double openAt, double idleRpm);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_set_effects(IntPtr s, double backfireGain, double backfireChance, double valveGain);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_get_backfires(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void es_get_exhaust(IntPtr s, out double flow, out double fuel, out double temperature);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double es_get_torque_raw(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_update(IntPtr s, double dt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_read(IntPtr s, short[] dst, int max);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double es_get_rpm(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double es_get_processing_time_us(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr es_last_error();

        public static string LastError()
        {
            var p = es_last_error();
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        // Mono resolves DllImport("enginesim_native") against modules already loaded, so load
        // ours from the plugin folder first (it is not on the game's DLL search path).
        public static bool Preload(string pluginDir, out string error)
        {
            error = null;
            var path = Path.Combine(pluginDir, Dll + ".dll");
            if (!File.Exists(path)) { error = path + " is missing"; return false; }
            if (LoadLibraryW(path) == IntPtr.Zero)
            {
                error = "LoadLibrary(" + path + ") failed, error " + Marshal.GetLastWin32Error();
                return false;
            }
            return LayoutMatches(out error);
        }
    }
}
