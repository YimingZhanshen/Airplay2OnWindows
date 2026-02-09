using NAudio.Wave;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlay.Services
{
    /// <summary>
    /// Audio output service using NAudio for Windows audio playback.
    /// Buffers decoded PCM data and plays through the default audio device.
    /// </summary>
    public class AudioOutputService : IDisposable
    {
        private const int COM_CLEANUP_DELAY_MS = 50;
        private const int PREBUFFER_PACKETS = 10;
        private const int LOG_FREQUENCY_PACKETS = 50000;

        private IWavePlayer _waveOut;
        private StreamingWaveProvider _streamProvider;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private int _sampleCount = 0;
        private bool _isPlaying = false;
        private bool _initialized = false;
        private WaveFormat _waveFormat;
        private bool _needsReinit = false;
        private float _volume = 1.0f;

        public event EventHandler PlaybackStoppedUnexpectedly;

        public void Initialize()
        {
            lock (_lock)
            {
                if (_disposed) return;

                Console.WriteLine("Initializing audio output...");

                // PCM format: 16-bit, 44.1kHz, stereo (as decoded by AAC-ELD/AAC/ALAC)
                _waveFormat = new WaveFormat(44100, 16, 2);

                // Create streaming provider that NAudio pulls from
                _streamProvider = new StreamingWaveProvider(_waveFormat, maxQueueSize: 250);

                // Create DirectSound device (but DON'T call Init() yet)
                _waveOut = new DirectSoundOut(40); // 40ms latency

                _waveOut.PlaybackStopped += OnPlaybackStopped;

                // Don't call Init() until we have audio data in the queue.
                // If Init() is called on an empty queue, DirectSound's render thread starts,
                // gets silence, and may terminate.
                _initialized = false;

                Console.WriteLine($"Audio device created: {_waveFormat.SampleRate}Hz, {_waveFormat.BitsPerSample}-bit, {_waveFormat.Channels} channels");
                Console.WriteLine($"  Will call Init() after prebuffering {PREBUFFER_PACKETS} packets");
            }
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs args)
        {
            if (_disposed) return;

            if (args.Exception != null)
            {
                Console.WriteLine($"Playback stopped with error: {args.Exception.Message}");

                lock (_lock)
                {
                    _isPlaying = false;
                    _needsReinit = true;
                }

                PlaybackStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Handle a track change (FLUSH event). Recreates the DirectSound device
        /// for a clean state.
        /// </summary>
        public void HandleFlush()
        {
            lock (_lock)
            {
                if (_disposed) return;

                Console.WriteLine("Track change detected - restarting audio output...");

                try
                {
                    if (_waveOut != null)
                    {
                        try
                        {
                            _waveOut.Stop();
                            _waveOut.Dispose();
                        }
                        catch { }
                        _waveOut = null;
                    }

                    _streamProvider?.Clear();

                    _initialized = false;
                    _isPlaying = false;
                    _sampleCount = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during flush cleanup: {ex.Message}");
                }
            }

            // Wait outside the lock for COM cleanup
            Thread.Sleep(COM_CLEANUP_DELAY_MS);

            lock (_lock)
            {
                try
                {
                    _waveOut = new DirectSoundOut(40);
                    _waveOut.PlaybackStopped += OnPlaybackStopped;
                    Console.WriteLine($"Audio device recreated for new track, waiting for {PREBUFFER_PACKETS} packets before Init()...");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to recreate audio device: {ex.Message}");
                    _needsReinit = true;
                }
            }
        }

        /// <summary>
        /// Set audio volume. AirPlay sends volume in dB scale:
        /// -144.0 = mute, -30.0 to 0.0 = normal range.
        /// Converts to linear scale (0.0 to 1.0) for NAudio.
        /// </summary>
        public void SetVolume(decimal airplayVolume)
        {
            lock (_lock)
            {
                if (_disposed) return;

                if (airplayVolume <= -144.0m)
                {
                    _volume = 0.0f;
                }
                else
                {
                    // Convert dB to linear: volume = 10^(dB/20)
                    // AirPlay range is roughly -30 to 0
                    _volume = (float)Math.Pow(10.0, (double)airplayVolume / 20.0);
                    _volume = Math.Max(0.0f, Math.Min(1.0f, _volume));
                }

                // Apply volume via StreamingWaveProvider (DirectSoundOut doesn't support Volume)
                if (_streamProvider != null)
                {
                    _streamProvider.Volume = _volume;
                }

                Console.WriteLine($"Volume set to {_volume:F2} (AirPlay dB: {airplayVolume:F1})");
            }
        }

        /// <summary>
        /// Add decoded PCM samples to the audio output queue.
        /// </summary>
        public void AddSamples(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_disposed || _needsReinit) return;
                if (_waveOut == null || _streamProvider == null) return;

                try
                {
                    if (buffer == null || count <= 0) return;
                    if (offset < 0 || offset + count > buffer.Length) return;

                    _streamProvider.AddSamples(buffer, offset, count);
                    _sampleCount++;

                    // Initialize DirectSound AFTER prebuffering
                    if (!_initialized && _sampleCount >= PREBUFFER_PACKETS)
                    {
                        Console.WriteLine($"Prebuffered {_streamProvider.QueuedBuffers} packets, calling Init()...");
                        _waveOut.Init(_streamProvider);
                        _initialized = true;
                    }

                    // Start playback after Init
                    if (_initialized && !_isPlaying)
                    {
                        _waveOut.Play();
                        _isPlaying = true;
                        Console.WriteLine($"Audio playback started ({_streamProvider.QueuedBuffers} packets buffered)");
                    }

                    // Log status periodically
                    if (_sampleCount % LOG_FREQUENCY_PACKETS == 0)
                    {
                        var state = _waveOut.PlaybackState;
                        var queued = _streamProvider.QueuedBuffers;
                        Console.WriteLine($"Audio: {_sampleCount} packets | Queue: {queued} | State: {state}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Audio error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Recreate the audio output after an error.
        /// </summary>
        public void Recreate()
        {
            lock (_lock)
            {
                Console.WriteLine("Recreating audio output...");

                try
                {
                    _waveOut?.Dispose();
                }
                catch { }

                Thread.Sleep(COM_CLEANUP_DELAY_MS);

                try
                {
                    _streamProvider?.Clear();
                    _waveOut = new DirectSoundOut(40);
                    _waveOut.PlaybackStopped += OnPlaybackStopped;
                    _initialized = false;
                    _isPlaying = false;
                    _sampleCount = 0;
                    _needsReinit = false;
                    Console.WriteLine("Audio output recreated successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to recreate audio output: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    _waveOut?.Stop();
                    _waveOut?.Dispose();
                }
                catch { }

                _streamProvider?.Clear();
                _waveOut = null;
                _streamProvider = null;

                Console.WriteLine($"Audio disposed (played {_sampleCount} packets total)");
            }
        }
    }
}
