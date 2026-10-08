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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct EngineDesc
        {
            public int cylinders;
            public int layout;
            public float vAngleDeg;
            public float displacementL;
            public int diesel;
            public float redlineRpm;
            public float idleRpm;
            public float powerHp;
            public float torqueNm;

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
                return displacementL.ToString("0.###") + "L " + (Layout)layout + cylinders
                    + (layout == (int)Layout.V ? " (" + vAngleDeg + "°)" : "")
                    + (diesel != 0 ? " diesel" : " gasoline") + ", idle " + idleRpm + " / redline " + redlineRpm + " rpm";
            }
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int es_parse_engine_name([MarshalAs(UnmanagedType.LPStr)] string name, out EngineDesc desc);

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
            return true;
        }
    }
}
