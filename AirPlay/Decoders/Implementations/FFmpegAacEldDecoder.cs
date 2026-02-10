/*
 * AAC-ELD Decoder using FFmpeg as a subprocess.
 * 
 * The fdk-aac NuGet package returns error 0x5 (AAC_DEC_UNSUPPORTED_ER_FORMAT)
 * for AAC-ELD because the pre-built binary doesn't include ER format support.
 * SharpJaad.AAC also doesn't support AAC-ELD.
 *
 * This decoder pipes raw AAC-ELD frames wrapped in ADTS headers through FFmpeg
 * for decoding. While ADTS headers can only encode AAC-LC profile (2-bit field),
 * FFmpeg's AAC decoder handles the actual bitstream content regardless of the
 * ADTS profile indicator. The ADTS header serves as a framing/sync mechanism.
 *
 * Reference: UxPlay uses GStreamer's avdec_aac (which wraps FFmpeg) for the same purpose.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AirPlay.Models.Enums;

namespace AirPlay.Decoders.Implementations
{
    public class FFmpegAacEldDecoder : IDecoder, IDisposable
    {
        private const int MAX_READ_ATTEMPTS = 50;
        private const int PROCESS_EXIT_TIMEOUT_MS = 1000;

        private Process _ffmpegProcess;
        private BinaryWriter _ffmpegInput;
        private Stream _ffmpegOutput;
        private int _pcmOutputSize;
        private int _channels;
        private int _sampleRate;
        private int _frameLength;
        private bool _disposed;
        private bool _initialized;
        private readonly byte[] _adtsHeader = new byte[7];
        private int _adtsFreqIdx;
        private int _decodeCallCount;
        private int _totalFramesDecoded;

        public AudioFormat Type => AudioFormat.AAC_ELD;

        public FFmpegAacEldDecoder()
        {
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _frameLength = frameLength;
            _pcmOutputSize = frameLength * channels * (bitDepth / 8);
            _adtsFreqIdx = GetSampleRateIndex(sampleRate);

            try
            {
                // Start FFmpeg process:
                // Input: AAC frames wrapped in ADTS headers via stdin pipe
                // Output: raw PCM S16LE via stdout pipe
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -loglevel error -f aac -i pipe:0 -f s16le -acodec pcm_s16le -ar {sampleRate} -ac {channels} pipe:1",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                _ffmpegProcess = new Process { StartInfo = psi };
                _ffmpegProcess.Start();

                _ffmpegInput = new BinaryWriter(_ffmpegProcess.StandardInput.BaseStream);
                _ffmpegOutput = _ffmpegProcess.StandardOutput.BaseStream;

                // Drain stderr in background to prevent pipe deadlock
                var stderrThread = new Thread(() =>
                {
                    try
                    {
                        using var reader = _ffmpegProcess.StandardError;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            // Log all FFmpeg stderr output for debugging
                            Console.WriteLine($"[DEBUG-FFMPEG-STDERR] {line}");
                        }
                    }
                    catch { /* ignore */ }
                });
                stderrThread.IsBackground = true;
                stderrThread.Start();

                _initialized = true;
                Console.WriteLine($"FFmpeg AAC-ELD decoder started: {sampleRate}Hz, {channels}ch, {bitDepth}bit, frameLength={frameLength}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg AAC-ELD decoder failed to start: {ex.Message}");
                Console.WriteLine("Please ensure 'ffmpeg' is in your system PATH.");
                return -1;
            }
        }

        public int GetOutputStreamLength()
        {
            return _pcmOutputSize;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            if (!_initialized || _ffmpegProcess == null || _ffmpegProcess.HasExited)
            {
                _decodeCallCount++;
                if (_decodeCallCount <= 3 || _decodeCallCount % 500 == 0)
                    Console.WriteLine($"[DEBUG-FFMPEG] DecodeFrame called but process not ready: initialized={_initialized}, process={_ffmpegProcess != null}, hasExited={_ffmpegProcess?.HasExited}");
                return -1;
            }

            try
            {
                _decodeCallCount++;

                // Wrap the raw AAC frame in an ADTS header for FFmpeg to parse.
                // ADTS profile is set to AAC-LC (profile=2) since ADTS only has a 2-bit
                // profile field and cannot represent AAC-ELD. FFmpeg's decoder handles
                // the actual bitstream content regardless of the ADTS profile indicator.
                int frameLen = input.Length + 7; // ADTS header is 7 bytes
                BuildAdtsHeader(_adtsHeader, frameLen, 2, _adtsFreqIdx, _channels);

                // Write ADTS header + raw AAC data to FFmpeg stdin
                _ffmpegInput.Write(_adtsHeader, 0, 7);
                _ffmpegInput.Write(input, 0, input.Length);
                _ffmpegInput.Flush();

                if (_decodeCallCount <= 3)
                    Console.WriteLine($"[DEBUG-FFMPEG] Wrote frame #{_decodeCallCount}: inputLen={input.Length}, adtsFrameLen={frameLen}, expecting {_pcmOutputSize} bytes PCM output");

                // Read decoded PCM from FFmpeg stdout
                int bytesToRead = _pcmOutputSize;
                int totalRead = 0;
                int maxAttempts = MAX_READ_ATTEMPTS;

                while (totalRead < bytesToRead && maxAttempts > 0)
                {
                    int read = _ffmpegOutput.Read(output, totalRead, bytesToRead - totalRead);
                    if (read <= 0)
                    {
                        Thread.Sleep(1);
                        maxAttempts--;
                        continue;
                    }
                    totalRead += read;
                }

                _totalFramesDecoded++;

                if (_decodeCallCount <= 5 || _decodeCallCount % 500 == 0)
                    Console.WriteLine($"[DEBUG-FFMPEG] Frame #{_decodeCallCount}: read {totalRead}/{bytesToRead} bytes (attemptsLeft={maxAttempts}), totalDecoded={_totalFramesDecoded}");

                if (totalRead < bytesToRead)
                {
                    // Partial read - zero fill the rest
                    Array.Clear(output, totalRead, bytesToRead - totalRead);
                    if (_decodeCallCount <= 10)
                        Console.WriteLine($"[DEBUG-FFMPEG] WARNING: Partial read! Only {totalRead} of {bytesToRead} bytes, zero-filled remainder");
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (_decodeCallCount <= 5 || _decodeCallCount % 500 == 0)
                    Console.WriteLine($"[DEBUG-FFMPEG] DecodeFrame exception #{_decodeCallCount}: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Build a 7-byte ADTS header for wrapping raw AAC frames.
        /// Used as a framing/sync mechanism for FFmpeg's AAC demuxer.
        /// </summary>
        private static void BuildAdtsHeader(byte[] header, int packetLen, int profile, int freqIdx, int chanCfg)
        {
            header[0] = 0xFF;
            header[1] = 0xF1; // MPEG-4, Layer 0, no CRC
            header[2] = (byte)(((profile - 1) << 6) | (freqIdx << 2) | (chanCfg >> 2));
            header[3] = (byte)(((chanCfg & 3) << 6) | (packetLen >> 11));
            header[4] = (byte)((packetLen >> 3) & 0xFF);
            header[5] = (byte)(((packetLen & 7) << 5) | 0x1F);
            header[6] = 0xFC;
        }

        private static int GetSampleRateIndex(int sampleRate)
        {
            return sampleRate switch
            {
                96000 => 0, 88200 => 1, 64000 => 2, 48000 => 3,
                44100 => 4, 32000 => 5, 24000 => 6, 22050 => 7,
                16000 => 8, 12000 => 9, 11025 => 10, 8000 => 11,
                7350 => 12, _ => 4,
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    // Close stdin first for graceful FFmpeg shutdown
                    _ffmpegInput?.Close();

                    if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                    {
                        // Wait for graceful exit, then force kill if needed
                        if (!_ffmpegProcess.WaitForExit(PROCESS_EXIT_TIMEOUT_MS))
                        {
                            _ffmpegProcess.Kill();
                            _ffmpegProcess.WaitForExit(PROCESS_EXIT_TIMEOUT_MS);
                        }
                    }
                    _ffmpegProcess?.Dispose();
                }
                catch { /* ignore cleanup errors */ }
            }
        }
    }
}
