using NAudio.Wave;
using System;
using System.Collections.Concurrent;

namespace AirPlay.Services
{
    /// <summary>
    /// A thread-safe IWaveProvider that buffers PCM audio data in a queue.
    /// NAudio's output device pulls data from this provider via Read().
    /// </summary>
    public class StreamingWaveProvider : IWaveProvider
    {
        private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
        private readonly int _maxQueueSize;
        private byte[] _currentBuffer;
        private int _currentOffset;
        private float _volume = 1.0f;

        public WaveFormat WaveFormat { get; }
        public int QueuedBuffers => _queue.Count;
        public long TotalBytesRead { get; private set; }
        public DateTime LastReadTime { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// Volume level (0.0 = mute, 1.0 = full volume).
        /// Applied to PCM samples during Read().
        /// </summary>
        public float Volume
        {
            get => _volume;
            set => _volume = Math.Max(0.0f, Math.Min(1.0f, value));
        }

        public StreamingWaveProvider(WaveFormat waveFormat, int maxQueueSize = 500)
        {
            WaveFormat = waveFormat;
            _maxQueueSize = maxQueueSize;
        }

        /// <summary>
        /// Add PCM samples to the streaming queue.
        /// Returns false if the queue is full and the sample was dropped.
        /// </summary>
        public bool AddSamples(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0)
                return false;

            if (_queue.Count >= _maxQueueSize)
                return false;

            var copy = new byte[count];
            Array.Copy(buffer, offset, copy, 0, count);
            _queue.Enqueue(copy);
            return true;
        }

        /// <summary>
        /// Clear the buffer queue (e.g., on track change).
        /// </summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
            _currentBuffer = null;
            _currentOffset = 0;
        }

        /// <summary>
        /// Called by NAudio's output device to pull audio data.
        /// Returns silence (zeros) if no data is available.
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            LastReadTime = DateTime.UtcNow;
            int bytesWritten = 0;

            while (bytesWritten < count)
            {
                // If we have a partial buffer from previous read, use it
                if (_currentBuffer != null && _currentOffset < _currentBuffer.Length)
                {
                    int available = _currentBuffer.Length - _currentOffset;
                    int toCopy = Math.Min(available, count - bytesWritten);
                    Array.Copy(_currentBuffer, _currentOffset, buffer, offset + bytesWritten, toCopy);
                    _currentOffset += toCopy;
                    bytesWritten += toCopy;

                    if (_currentOffset >= _currentBuffer.Length)
                    {
                        _currentBuffer = null;
                        _currentOffset = 0;
                    }
                }
                else if (_queue.TryDequeue(out var next))
                {
                    _currentBuffer = next;
                    _currentOffset = 0;
                }
                else
                {
                    // No more data — fill remaining with silence
                    Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);
                    bytesWritten = count;
                }
            }

            TotalBytesRead += bytesWritten;

            // Apply volume scaling to 16-bit PCM samples
            if (_volume < 0.999f)
            {
                for (int i = offset; i < offset + bytesWritten; i += 2)
                {
                    if (i + 1 < offset + bytesWritten)
                    {
                        short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                        sample = (short)(sample * _volume);
                        buffer[i] = (byte)(sample & 0xFF);
                        buffer[i + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                }
            }

            return bytesWritten;
        }
    }
}
