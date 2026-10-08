using System.Threading;

namespace EngineSim
{
    // Single-producer / single-consumer sample ring: the sim thread writes,
    // Unity's audio thread reads. No locks; each side owns one counter.
    internal sealed class AudioRing
    {
        private readonly float[] _buffer;
        private readonly int _mask;
        private long _write;   // only the producer stores
        private long _read;    // only the consumer stores

        public AudioRing(int capacityPowerOfTwo)
        {
            _buffer = new float[capacityPowerOfTwo];
            _mask = capacityPowerOfTwo - 1;
        }

        public int Capacity { get { return _buffer.Length; } }

        public int Count { get { return (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read)); } }

        public int Free { get { return _buffer.Length - Count; } }

        // producer
        public void Write(short[] src, int count)
        {
            long w = _write;
            for (int i = 0; i < count; ++i) _buffer[(int)(w + i) & _mask] = src[i] * (1f / 32768f);
            Volatile.Write(ref _write, w + count);
        }

        // consumer
        public int Read(float[] dst, int offset, int count)
        {
            long r = _read;
            int available = (int)(Volatile.Read(ref _write) - r);
            if (count > available) count = available;
            for (int i = 0; i < count; ++i) dst[offset + i] = _buffer[(int)(r + i) & _mask];
            Volatile.Write(ref _read, r + count);
            return count;
        }
    }
}
