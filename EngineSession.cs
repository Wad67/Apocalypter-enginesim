using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Threading;
using NWH.VehiclePhysics2;
using SkrilStudio;
using UnityEngine;
using UnityEngine.Audio;

namespace EngineSim
{
    // One simulated engine: a native engine-sim session driven on its own thread,
    // played through an AudioSource on the vehicle's RES object.
    //
    //   main thread : writes the control fields, owns the AudioSource
    //   sim thread  : the only caller of the native session; fills the ring
    //   audio thread: AudioOutput.OnAudioFilterRead, only reads the ring
    internal sealed class EngineSession
    {
        private const int RingSize = 16384;   // upper bound only; the sim keeps it at TargetFill

        public readonly RealisticEngineSound Res;
        public readonly VehicleController Vc;
        public readonly string PartName;     // the engine's label in EngineSim.engines.cfg, for the log
        public readonly long EngineId, ExhaustId;
        public readonly GameObject EnginePart;
        public readonly GameObject ExhaustPart;   // the fitted exhaust part, null if none
        public HutongGames.PlayMaker.FsmFloat ConditionVar;   // main thread only; see EngineManager.ReadCondition
        private readonly float _loudness;
        public readonly Native.EngineDesc Desc;

        // controls (main thread -> sim thread)
        private float _targetRpm, _throttle, _latency, _condition = 1f;
        private float _revLimit, _limiterCut = 0.08f, _boost = 1f;

        // forced induction as machines (drive mode): what is fitted, and what it does
        private Native.InductionDesc _induction;
        private int _inductionVersion, _inductionApplied;
        private readonly object _inductionLock = new object();
        private float _boostPsi, _superchargerKw, _blowOffPsi;
        private int _blowOffs;
        private readonly float[] _turboSpeed = new float[Native.MaxTurbos];
        public bool Machines { get; private set; }
        public float NextInductionScan;
        public string InductionKey = "";
        private int _ignition;

        // drive mode ([Experimental] simulated drivetrain): the engine powers the game's drivetrain
        public readonly bool Drive;
        private readonly float _driveTorqueScale;
        // the clutch the engine needs, N m (drive mode; 0 = leave the game's): 2.5 x its peak torque. An engine whose
        // table gives no peak_torque has it measured, once a game, by a dyno pull of the engine as new at 1 atm; until
        // then the game's clutch stays (sized from the game's power figure, which falls with condition)
        public float ClutchTorque { get { return 2.5f * PeakTorque * _driveTorqueScale; } }
        private Dyno _peakPull;
        private static readonly Dictionary<long, float> Measured = new Dictionary<long, float>();
        private int _starter, _fuel = 1;
        private float _idleRpm;
        private float _torque, _crankInertia, _starterLoad, _rawTorque;
        private float _heat;
        private int _starterJammed;
        private readonly double[] _turboRead = new double[Native.MaxTurbos];
        private int _backfires;
        private float _exhaustFlow, _exhaustFuel;
        private volatile bool _driveReady;

        // status (sim thread -> main thread)
        private volatile bool _run = true;
        private volatile bool _ready;
        private volatile string _error;
        private float _rpm, _processingUs;

        private readonly AudioRing _ring = new AudioRing(RingSize);
        private readonly Thread _thread;
        private readonly string _irDir;
        private GameObject _audioObject;
        private AudioSource _source;
        private readonly AudioOutput _output;
        private readonly Rolloff _rolloff;

        public bool Failed { get { return _error != null; } }
        public string Error { get { return _error; } }
        public bool Ready { get { return _ready; } }
        public float Rpm { get { return Volatile.Read(ref _rpm); } }
        public float ProcessingUs { get { return Volatile.Read(ref _processingUs); } }
        public long UnderrunSamples { get { return _output != null ? _output.UnderrunFrames : 0; } }
        public int RingFill { get { return _ring.Count; } }

        public bool DriveReady { get { return _driveReady; } }
        // engine torque at the crank for the game's drivetrain, N m (drive mode)
        public float DriveTorque { get { return Volatile.Read(ref _torque) * _driveTorqueScale; } }
        public float CrankInertia { get { return Volatile.Read(ref _crankInertia); } }
        // engine torque over the sim's last update, N m, unsmoothed (the engine rocking on its mounts)
        public float RawTorque { get { return Volatile.Read(ref _rawTorque); } }
        public float PeakTorque { get { return Volatile.Read(ref _peakTorque); } }
        private float _peakTorque;

        // main thread: the peak pull's result, when there is one
        public void MeasurePeak()
        {
            if (_peakTorque > 0f || !Drive) return;
            lock (Measured)
                if (Measured.TryGetValue(EngineId, out float known)) { Volatile.Write(ref _peakTorque, known); return; }
            if (_peakPull == null)
            {
                _peakPull = Dyno.Peak(Desc);   // as fitted, tune included
                Plugin.Log.LogInfo(PartName + ": no peak_torque in its table; measuring it for the clutch");
                return;
            }
            if (!_peakPull.Finished) return;
            float peak = 0f;
            for (int i = 0; i < _peakPull.Torque.Length; i++) if (_peakPull.Ready[i]) peak = Mathf.Max(peak, _peakPull.Torque[i]);
            if (_peakPull.Error != null || peak <= 0f)
            {
                Plugin.Log.LogWarning(PartName + ": its peak torque couldn't be measured (" + (_peakPull.Error ?? "no torque") + "); the game's clutch stays");
                peak = -1f;   // not again
            }
            else Plugin.Log.LogInfo(PartName + ": peak torque " + peak.ToString("0") + " N m measured (put peak_torque = " + peak.ToString("0") + " in its table to skip this)");
            lock (Measured) Measured[EngineId] = peak;
            Volatile.Write(ref _peakTorque, peak);
            _peakPull = null;
        }
        public int TakeBackfires() { return Interlocked.Exchange(ref _backfires, 0); }
        // exhaust over the sim's last update: flow out of the cylinders (mol/s) and its unburnt fuel fraction
        public float ExhaustFlow { get { return Volatile.Read(ref _exhaustFlow); } }
        public float ExhaustFuel { get { return Volatile.Read(ref _exhaustFuel); } }
        public float Condition { get { return Volatile.Read(ref _condition); } }
        // intake pressure ratio: the machines' (drive mode, when fitted) or the game's
        public float Boost { get { return Machines ? 1f + Volatile.Read(ref _boostPsi) / 14.696f : Volatile.Read(ref _boost); } }
        public float BoostPsi { get { return Machines ? Volatile.Read(ref _boostPsi) : (Volatile.Read(ref _boost) - 1f) * 14.696f; } }
        public float SuperchargerKw { get { return Volatile.Read(ref _superchargerKw); } }
        // the turbos' blow-off valve lifting since the last call, and the boost (psi) it let go of
        public int TakeBlowOffs(out float psi) { psi = Volatile.Read(ref _blowOffPsi); return Interlocked.Exchange(ref _blowOffs, 0); }
        public float TurboSpeed(int i) { return i >= 0 && i < Native.MaxTurbos ? Volatile.Read(ref _turboSpeed[i]) : 0f; }

        public void SetInduction(Native.InductionDesc desc)
        {
            lock (_inductionLock) { _induction = desc; ++_inductionVersion; }
            Machines = desc.superchargerSize > 0f || desc.turbos > 0;
        }

        // the most exhaust the engine moves without boost (mol/s): its displacement at its rev limit
        public float MaxExhaustFlow
        {
            get
            {
                var p = Desc.engine;
                double litres = System.Math.PI / 4 * p.boreMm * p.boreMm * p.strokeMm * p.cylinders * 1e-6;
                return (float)System.Math.Max(0.5, litres * System.Math.Max(1000f, Desc.redlineRpm) / 120.0 * 0.0416);
            }
        }
        // how hard the starter works, 0..1 (drive mode)
        public float StarterLoad { get { return Volatile.Read(ref _starterLoad); } }
        // the starter gave up (held against more than it can turn): its solenoid only clicks
        public bool StarterJammed { get { return Volatile.Read(ref _starterJammed) != 0; } }
        // engine temperature for starting, 0 cold .. 1 warm (drive mode; the game's oil temperature)
        public void SetHeat(float heat) { Volatile.Write(ref _heat, Mathf.Clamp01(heat)); }

        // the Engine Tuning Toolkit's tune on this engine (null: as built)
        public Tune Tune;
        private int _fluidSteps, _fluidStepsApplied;
        public int FluidSteps { set { Volatile.Write(ref _fluidSteps, value); } }   // quality, changed while running
        public float StockLimit;   // the game engine's own rev limit, while the tune moves it (0: not moved)

        public EngineSession(RealisticEngineSound res, VehicleController vc, string partName, FittedParts parts, float loudness,
            Native.EngineDesc desc, string irDir, AudioMixerGroup mixer, bool drive, float driveTorqueScale, float peakTorque)
        {
            Drive = drive;
            _driveTorqueScale = driveTorqueScale;
            _peakTorque = peakTorque;
            Res = res;
            Vc = vc;
            PartName = partName;
            EngineId = parts.EngineId;
            ExhaustId = parts.ExhaustId;
            EnginePart = parts.Engine;
            ExhaustPart = parts.Exhaust;
            _loudness = loudness;
            Desc = desc;
            _irDir = irDir;
            _latency = Plugin.LatencyEntry.Value;

            _audioObject = new GameObject("EngineSim Audio");
            _audioObject.transform.SetParent(res.transform, false);
            _source = _audioObject.AddComponent<AudioSource>();
            _source.outputAudioMixerGroup = mixer;
            _source.spatialBlend = 0f;   // 2D: distance is applied by AudioOutput, and there is no panning
            _source.dopplerLevel = 0f;
            _source.volume = 1f;
            _rolloff = new Rolloff(res);
            _source.loop = true;
            _source.playOnAwake = false;
            int outputRate = AudioSettings.outputSampleRate;
            int dspLength, dspBuffers;
            AudioSettings.GetDSPBufferSize(out dspLength, out dspBuffers);
            Plugin.Verbose("Audio out " + outputRate + " Hz, DSP buffer " + dspLength + " x " + dspBuffers);
            _source.clip = AudioOutput.CreateCarrier(outputRate);
            _output = _audioObject.AddComponent<AudioOutput>();
            _output.Init(_ring, outputRate);
            _source.Play();

            _thread = new Thread(SimLoop) { IsBackground = true, Name = "EngineSim " + partName, Priority = System.Threading.ThreadPriority.AboveNormal };
            _thread.Start();
        }

        // ---- main thread ---------------------------------------------------

        public void SetLimits(float revLimitRpm, float limiterCutSeconds, float boostRatio)
        {
            Volatile.Write(ref _revLimit, revLimitRpm);
            Volatile.Write(ref _limiterCut, limiterCutSeconds);
            Volatile.Write(ref _boost, boostRatio);
        }

        public void SetControls(float targetRpm, float throttle, bool ignition, float condition)
        {
            Volatile.Write(ref _condition, condition);
            Volatile.Write(ref _targetRpm, targetRpm);
            Volatile.Write(ref _throttle, throttle);
            Volatile.Write(ref _ignition, ignition ? 1 : 0);
            Volatile.Write(ref _latency, Plugin.LatencyEntry.Value);

            // NWH2_RES2 deactivates the RES object with the ignition; a re-activated source does not resume on its own
            if (_source != null && _source.isActiveAndEnabled && !_source.isPlaying) _source.Play();
        }

        // drive mode: the starter, the fuel supply and the idle speed (the key is SetControls' ignition)
        public void SetDrive(bool starter, bool fuel, float idleRpm)
        {
            Volatile.Write(ref _starter, starter ? 1 : 0);
            Volatile.Write(ref _fuel, fuel ? 1 : 0);
            Volatile.Write(ref _idleRpm, idleRpm);
        }

        // the game's distance attenuation for a sound at this point (the engine's own rolloff)
        public float DistanceGain(Vector3 at)
        {
            var ear = Listener.Current;
            return ear == null ? 1f : _rolloff.Gain(Vector3.Distance(ear.transform.position, at));
        }

        public void UpdateDistance(Transform listener)
        {
            if (_output == null || _audioObject == null) return;
            float gain = listener == null ? 1f
                : _rolloff.Gain(Vector3.Distance(listener.position, _audioObject.transform.position));
            // volume and exhaust loudness in float, after engine-sim's int16 stage, so they can't clip it
            _output.SetGain(gain * Plugin.VolumeEntry.Value * _loudness);
        }

        public void Stop()
        {
            _run = false;   // the sim thread destroys the native session itself
            if (_audioObject != null) UnityEngine.Object.Destroy(_audioObject);
            _audioObject = null;
            _source = null;
        }

        // ---- sim thread ------------------------------------------------------

        private void SimLoop()
        {
            IntPtr session = IntPtr.Zero;
            try
            {
                var desc = Desc;
                session = Native.es_create(ref desc, _irDir);
                if (session == IntPtr.Zero)
                {
                    _error = Native.LastError();
                    return;
                }

                // engine-sim applies its volume before the int16 clamp: keep it at 1, gain is applied in AudioOutput
                Native.es_set_volume(session, 1.0);
                if (Drive)
                {
                    Native.es_set_drive_mode(session, 1);
                    Volatile.Write(ref _crankInertia, (float)Native.es_get_crank_inertia(session));
                }
                var chunk = new short[4096];
                var clock = Stopwatch.StartNew();
                double last = clock.Elapsed.TotalSeconds;
                _ready = true;

                while (_run)
                {
                    Native.es_set_target_rpm(session, Volatile.Read(ref _targetRpm));
                    Native.es_set_throttle(session, Volatile.Read(ref _throttle));
                    Native.es_set_ignition(session, Volatile.Read(ref _ignition));
                    Native.es_set_target_latency(session, Volatile.Read(ref _latency));
                    Native.es_set_condition(session, Volatile.Read(ref _condition));
                    Native.es_set_wear_intensity(session, Plugin.WearIntensityEntry.Value);
                    Native.es_set_rev_limiter(session, Volatile.Read(ref _revLimit), Volatile.Read(ref _limiterCut));
                    Native.es_set_boost(session, Volatile.Read(ref _boost));
                    lock (_inductionLock)
                    {
                        if (_inductionApplied != _inductionVersion)
                        {
                            if (_induction.turboSize == null) _induction.turboSize = new float[Native.MaxTurbos];
                            Native.es_set_induction(session, ref _induction);
                            _inductionApplied = _inductionVersion;
                        }
                    }
                    Native.es_set_idle_air(session, Plugin.IdleAirEntry.Value);
                    Native.es_set_vvt(session, Tune != null && Tune.Vvt ? 1 : 0);
                    int steps = Volatile.Read(ref _fluidSteps);
                    if (steps > 0 && steps != _fluidStepsApplied) { Native.es_set_fluid_steps(session, steps); _fluidStepsApplied = steps; }
                    Native.es_set_lowpass(session, Plugin.FilterEntry.Value ? Plugin.FilterCrankEntry.Value : 0f, Plugin.FilterIdleEntry.Value,
                        Plugin.FilterOpenAtEntry.Value, Drive ? Volatile.Read(ref _idleRpm) : Desc.idleRpm);
                    Native.es_set_effects(session, Plugin.BackfireEntry.Value, Plugin.BackfireChanceEntry.Value, Plugin.ValveTickEntry.Value);
                    if (Drive)
                    {
                        Native.es_set_starter(session, Volatile.Read(ref _starter));
                        Native.es_set_fuel(session, Volatile.Read(ref _fuel));
                        Native.es_set_idle_rpm(session, Volatile.Read(ref _idleRpm));
                        Native.es_set_starting(session, Plugin.HardStartingEntry.Value, Plugin.StarterGivesUpEntry.Value ? 1 : 0);
                        Native.es_set_heat(session, Volatile.Read(ref _heat));
                    }

                    double now = clock.Elapsed.TotalSeconds;
                    Native.es_update(session, now - last);
                    last = now;
                    if (Drive)
                    {
                        Volatile.Write(ref _torque, (float)Native.es_get_torque(session));
                        Volatile.Write(ref _starterLoad, (float)Native.es_get_starter_load(session));
                        Volatile.Write(ref _starterJammed, Native.es_get_starter_jammed(session));
                        double psi, kw;
                        Native.es_get_induction(session, out psi, _turboRead, out kw);
                        Volatile.Write(ref _boostPsi, (float)psi);
                        Volatile.Write(ref _superchargerKw, (float)kw);
                        double letGo;
                        int blowOffs = Native.es_get_blowoffs(session, out letGo);
                        if (blowOffs > 0) { Volatile.Write(ref _blowOffPsi, (float)letGo); Interlocked.Add(ref _blowOffs, blowOffs); }
                        for (int i = 0; i < Native.MaxTurbos; ++i) Volatile.Write(ref _turboSpeed[i], (float)_turboRead[i]);
                        _driveReady = true;
                    }

                    // Keep the ring at the target fill, no more: the synth then backs up,
                    // the sim sees its input latency rise and slows down (Simulator::startFrame).
                    // The target is the latency setting, but at least two of Unity's reads.
                    int target = Math.Min(RingSize, Math.Max(2 * _output.RequestSize,
                        (int)(Volatile.Read(ref _latency) * Native.SampleRate)));
                    int free = target - _ring.Count;
                    while (free > 0)
                    {
                        int n = Native.es_read(session, chunk, Math.Min(free, chunk.Length));
                        if (n <= 0) break;
                        _ring.Write(chunk, n);
                        free -= n;
                    }

                    Volatile.Write(ref _rpm, (float)Native.es_get_rpm(session));
                    Volatile.Write(ref _rawTorque, (float)Native.es_get_torque_raw(session));
                    Interlocked.Add(ref _backfires, Native.es_get_backfires(session));
                    double flow, fuel, temperature;
                    Native.es_get_exhaust(session, out flow, out fuel, out temperature);
                    Volatile.Write(ref _exhaustFlow, (float)flow);
                    Volatile.Write(ref _exhaustFuel, (float)fuel);
                    Volatile.Write(ref _processingUs, (float)Native.es_get_processing_time_us(session));
                    Thread.Sleep(2);
                }
            }
            catch (Exception e)
            {
                _error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                _ready = false;
                _driveReady = false;
                if (session != IntPtr.Zero) Native.es_destroy(session);
            }
        }
    }

    // The active AudioListener (the camera's), looked up at most twice a second.
    internal static class Listener
    {
        private static AudioListener _current;
        private static float _nextLookup;

        public static AudioListener Current
        {
            get
            {
                if ((_current == null || !_current.isActiveAndEnabled) && Time.unscaledTime >= _nextLookup)
                {
                    _nextLookup = Time.unscaledTime + 0.5f;
                    _current = null;
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                        if (l.isActiveAndEnabled) { _current = l; break; }
                }
                return _current != null && _current.isActiveAndEnabled ? _current : null;
            }
        }
    }

    // RES's distance attenuation, evaluated ourselves (see AudioOutput for why).
    // Taken from one of RES's own engine AudioSources when it has made one, so a
    // custom rolloff curve carries over; otherwise from RES's settings.
    internal sealed class Rolloff
    {
        private readonly AudioRolloffMode _mode;
        private readonly float _min, _max, _blend;
        private readonly AnimationCurve _custom;

        // from another source's 3D settings (the game's own sound being replaced)
        public Rolloff(AudioSource template)
        {
            _mode = template.rolloffMode;
            _min = Mathf.Max(0.01f, template.minDistance);
            _max = Mathf.Max(_min + 0.01f, template.maxDistance);
            _blend = Mathf.Clamp01(template.spatialBlend);
            if (_mode == AudioRolloffMode.Custom) _custom = template.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
        }

        public Rolloff(RealisticEngineSound res)
        {
            var template = ResPatch.AnyEngineSource(res);
            _mode = template != null ? template.rolloffMode : res.audioRolloffMode;
            _min = Mathf.Max(0.01f, template != null ? template.minDistance : res.minDistance);
            _max = Mathf.Max(_min + 0.01f, template != null ? template.maxDistance : res.maxDistance);
            _blend = Mathf.Clamp01(template != null ? template.spatialBlend : res.spatialBlend);
            if (_mode == AudioRolloffMode.Custom && template != null)
                _custom = template.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
        }

        // the game's rolloff, scaled by [Audio] DistanceScale and blended in by DistanceAttenuation
        public float Gain(float distance)
        {
            distance /= Plugin.DistanceScaleEntry.Value;
            float g;
            switch (_mode)
            {
            case AudioRolloffMode.Linear:
                g = 1f - Mathf.Clamp01((distance - _min) / (_max - _min));
                break;
            case AudioRolloffMode.Custom:
                g = _custom != null ? Mathf.Clamp01(_custom.Evaluate(distance / _max)) : 1f - Mathf.Clamp01(distance / _max);
                break;
            default:   // Logarithmic, as Unity: min/d, held from maxDistance on
                g = _min / Mathf.Clamp(distance, _min, _max);
                break;
            }
            return Mathf.Lerp(1f, g, _blend * Plugin.DistanceAttenuationEntry.Value);
        }
    }
}
