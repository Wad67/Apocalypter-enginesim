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
        public HutongGames.PlayMaker.FsmFloat ConditionVar;   // main thread only; see EngineManager.ReadCondition
        private readonly float _loudness;
        public readonly Native.EngineDesc Desc;

        // controls (main thread -> sim thread)
        private float _targetRpm, _throttle, _latency, _condition = 1f;
        private float _revLimit, _limiterCut = 0.08f, _boost = 1f;
        private int _ignition;

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

        public EngineSession(RealisticEngineSound res, VehicleController vc, string partName, FittedParts parts, float loudness,
            Native.EngineDesc desc, string irDir, AudioMixerGroup mixer)
        {
            Res = res;
            Vc = vc;
            PartName = partName;
            EngineId = parts.EngineId;
            ExhaustId = parts.ExhaustId;
            EnginePart = parts.Engine;
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
                    Native.es_set_idle_air(session, Plugin.IdleAirEntry.Value);

                    double now = clock.Elapsed.TotalSeconds;
                    Native.es_update(session, now - last);
                    last = now;

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
