using System;
using System.Threading;
using UnityEngine;

namespace EngineSim
{
    // A dyno pull for the Engine Tuning Toolkit: the engine as Engine Sim would make it with the window's tune (and
    // its vehicle's exhaust, machines and condition), held at each rpm from 1000 to its limit at full throttle the way
    // the game's engine holds it in drive mode. Each point is its own simulation off the main thread, several at
    // once (they don't depend on each other): it settles (the turbos spool, the plenum fills) and is then averaged:
    // torque, and boost after the last compressor.
    internal sealed class Dyno
    {
        public const float StepRpm = 500f, FirstRpm = 1000f;
        // settled within 1 s (2 s for a turbo to spool); the torque then swings over about 2 s, so that is averaged
        private const double Dt = 0.01, Settle = 1.0, SettleSpool = 2.0, Measure = 2.0;

        public readonly float[] Rpm, Torque, Boost;   // N m, psi
        public readonly bool[] Ready;
        public readonly bool Induction;
        private int _done, _next = -1, _workers;
        private volatile bool _cancel;
        public volatile string Error;

        public int Done => Volatile.Read(ref _done);
        private static int _running;
        public static int Running => Volatile.Read(ref _running);   // pulls under way (the engines' keeping up isn't judged)
        public bool Finished => Done >= Rpm.Length || Error != null;

        private readonly bool _vvt;

        private Dyno(Native.EngineDesc desc, Native.InductionDesc induction, float condition, bool vvt)
        {
            float top = Mathf.Max(FirstRpm + StepRpm, desc.redlineRpm);
            int n = Mathf.FloorToInt((top - FirstRpm) / StepRpm) + 1;
            if (FirstRpm + (n - 1) * StepRpm < top - 1f) n++;   // the limit itself as the last point
            Rpm = new float[n];
            for (int i = 0; i < n; i++) Rpm[i] = Mathf.Min(top, FirstRpm + i * StepRpm);
            Torque = new float[n];
            Boost = new float[n];
            Ready = new bool[n];
            Induction = induction.turbos > 0 || induction.superchargerSize > 0f;
            _vvt = vvt;
            if (induction.turboSize == null) induction.turboSize = new float[Native.MaxTurbos];
            // half the cores: the game and its engines' own simulations keep the rest
            int workers = Mathf.Clamp(SystemInfo.processorCount / 2, 1, n);
            _workers = workers;
            Interlocked.Increment(ref _running);
            for (int w = 0; w < workers; w++)
                new Thread(() => Work(desc, induction, condition)) { IsBackground = true, Name = "EngineSim dyno" }.Start();
        }

        // null when the part is not an engine Engine Sim simulates
        public static Dyno Start(GameObject enginePart, Tune tune)
        {
            if (!EngineManager.PullSetup(enginePart, tune, out var desc, out var induction, out float condition)) return null;
            return new Dyno(desc, induction, condition, tune != null && tune.Vvt);
        }

        // the engine as new at 1 atm (no machines), for its peak torque
        public static Dyno Peak(Native.EngineDesc desc) =>
            new Dyno(desc, new Native.InductionDesc { turboSize = new float[Native.MaxTurbos] }, 1f, false);

        public void Cancel() { _cancel = true; }

        private void Work(Native.EngineDesc desc, Native.InductionDesc induction, float condition)
        {
            try { Points(desc, induction, condition); }
            finally { if (Interlocked.Decrement(ref _workers) == 0) Interlocked.Decrement(ref _running); }
        }

        private void Points(Native.EngineDesc desc, Native.InductionDesc induction, float condition)
        {
            int i;
            while (!_cancel && Error == null && (i = Interlocked.Increment(ref _next)) < Rpm.Length)
            {
                IntPtr s = IntPtr.Zero;
                try
                {
                    s = Native.es_create(ref desc, EngineManager.IrDirectory);
                    if (s == IntPtr.Zero) { Error = Native.LastError(); return; }
                    Native.es_set_volume(s, 1.0);
                    Native.es_set_drive_mode(s, 1);
                    Native.es_set_idle_rpm(s, desc.idleRpm);
                    Native.es_set_rev_limiter(s, 0, 0);
                    Native.es_set_condition(s, condition);
                    Native.es_set_heat(s, 1.0);
                    Native.es_set_fuel(s, 1);
                    Native.es_set_induction(s, ref induction);
                    Native.es_set_vvt(s, _vvt ? 1 : 0);
                    Native.es_set_ignition(s, 1);
                    var pcm = new short[8192];
                    var spin = new double[Native.MaxTurbos];
                    double sum = 0, psi = 0, settle = Induction ? SettleSpool : Settle;
                    int count = 0;
                    for (double t = 0; t < settle + Measure && !_cancel; t += Dt)
                    {
                        Native.es_set_target_rpm(s, Rpm[i]);
                        Native.es_set_throttle(s, 1.0);
                        Native.es_update(s, Dt);
                        while (Native.es_read(s, pcm, pcm.Length) > 0) { }
                        if (t < settle) continue;
                        sum += Native.es_get_torque(s);
                        Native.es_get_induction(s, out double p, spin, out _);
                        psi += p;
                        count++;
                    }
                    if (count == 0) return;
                    Torque[i] = (float)(sum / count);
                    Boost[i] = (float)(psi / count);
                    Volatile.Write(ref Ready[i], true);
                    Interlocked.Increment(ref _done);
                }
                catch (Exception e) { Error = e.Message; }
                finally { if (s != IntPtr.Zero) Native.es_destroy(s); }
            }
        }

        public static float Kw(float rpm, float nm) => nm * rpm * Mathf.PI / 30f / 1000f;
    }
}
