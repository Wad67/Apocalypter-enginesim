using System.Threading;
using UnityEngine;

namespace EngineSim
{
    // Plays a session's ring buffer on Unity's audio thread.
    //
    // OnAudioFilterRead is called once per DSP buffer at a steady rate, unlike a
    // streamed clip's PCMReaderCallback, which Unity calls in read-ahead bursts
    // that drain the ring faster than real time (heard as regular gaps).
    //
    // The AudioSource is 2D and plays a looping clip only to keep the filter
    // running and route through the game's mixer; its samples are ignored.
    // An engine is heard by distance only, never panned by listener angle:
    // the gain comes from EngineSession (RES's own rolloff) and goes to the
    // front left / right channels equally.
    // (The low-rpm filter is applied in the native library, before backfires and valve tick.)
    internal sealed class AudioOutput : MonoBehaviour
    {
        private AudioRing _ring;
        private double _step;   // source samples per output frame (44100 / output rate)
        private readonly float[] _src = new float[8192];
        private int _srcCount, _srcPos;
        private float _a, _b;   // interpolation endpoints
        private double _frac;
        private bool _primed;
        private float _targetGain, _gain;   // distance gain: main thread sets the target, ramped per buffer

        private int _requestSize = 1024;   // source samples per callback, largest seen
        private long _underrunFrames;

        public int RequestSize { get { return Volatile.Read(ref _requestSize); } }
        public long UnderrunFrames { get { return Interlocked.Read(ref _underrunFrames); } }

        public static AudioClip CreateCarrier(int outputRate)
        {
            var clip = AudioClip.Create("EngineSim carrier", outputRate, 1, outputRate, false);
            var ones = new float[outputRate];
            for (int i = 0; i < ones.Length; ++i) ones[i] = 1f;
            clip.SetData(ones, 0);
            return clip;
        }

        public void SetGain(float gain) { Volatile.Write(ref _targetGain, gain); }

        public void Init(AudioRing ring, int outputRate)
        {
            _step = (double)Native.SampleRate / outputRate;
            _ring = ring;
        }

        private bool Next(out float sample)
        {
            if (_srcPos >= _srcCount)
            {
                _srcCount = _ring.Read(_src, 0, _src.Length);
                _srcPos = 0;
                if (_srcCount == 0) { sample = 0; return false; }
            }
            sample = _src[_srcPos++];
            return true;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_ring == null) return;
            int frames = data.Length / channels;

            int need = (int)(frames * _step) + 2;
            if (need > Volatile.Read(ref _requestSize)) Volatile.Write(ref _requestSize, need);

            // wait for a little audio before starting, so the first buffer isn't half silence
            if (!_primed)
            {
                if (_ring.Count < need * 2) { System.Array.Clear(data, 0, data.Length); return; }
                Next(out _a);
                Next(out _b);
                _primed = true;
            }

            float g0 = _gain, g1 = Volatile.Read(ref _targetGain);
            float dg = (g1 - g0) / frames;
            _gain = g1;
            int outChannels = channels < 2 ? channels : 2;   // front left / right; surround channels stay silent

            long underrun = 0;
            for (int f = 0; f < frames; ++f)
            {
                while (_frac >= 1.0)
                {
                    _frac -= 1.0;
                    _a = _b;
                    float s;
                    if (Next(out s)) _b = s;
                    else { _b *= 0.995f; ++underrun; }   // fade instead of clicking to zero
                }
                float v = (float)(_a + (_b - _a) * _frac) * (g0 + dg * f);
                _frac += _step;

                int o = f * channels;
                for (int c = 0; c < outChannels; ++c) data[o + c] = v;
                for (int c = outChannels; c < channels; ++c) data[o + c] = 0f;
            }
            if (underrun > 0) Interlocked.Add(ref _underrunFrames, underrun);
        }
    }
}
