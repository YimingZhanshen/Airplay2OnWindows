/*
 * AAC-ELD Decoder using FFmpeg as a subprocess.
 * 
 * The fdk-aac NuGet package returns error 0x5 (AAC_DEC_UNSUPPORTED_ER_FORMAT)
 * for AAC-ELD because the pre-built binary doesn't include ER format support.
 * SharpJaad.AAC also doesn't support AAC-ELD.
 *
 * This decoder pipes raw AAC-ELD frames wrapped in LOAS/LATM through FFmpeg
 * for decoding. ADTS format does NOT support AAC-ELD (its 2-bit profile field
 * cannot represent AOT 39). LATM supports all AAC profiles including AAC-ELD.
 *
 * Reference: UxPlay uses GStreamer's avdec_aac (which wraps FFmpeg) for the same purpose.
 */

using System;
using System.Collections.Generic;
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
        private bool _firstFrame = true;
        private int _freqIdx;

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
            _freqIdx = GetSampleRateIndex(sampleRate);

            try
            {
                // Start FFmpeg process:
                // Input: AAC-ELD frames wrapped in LOAS/LATM via stdin pipe
                // Output: raw PCM S16LE via stdout pipe
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -loglevel error -f latm -i pipe:0 -f s16le -acodec pcm_s16le -ar {sampleRate} -ac {channels} pipe:1",
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
                    try { _ffmpegProcess.StandardError.ReadToEnd(); }
                    catch { /* ignore */ }
                });
                stderrThread.IsBackground = true;
                stderrThread.Start();

                _initialized = true;
                Console.WriteLine($"FFmpeg AAC-ELD decoder started (LATM): {sampleRate}Hz, {channels}ch, {bitDepth}bit, frameLength={frameLength}");
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
                return -1;

            try
            {
                // Build LOAS/LATM frame wrapping the raw AAC-ELD data
                byte[] loasFrame = BuildLoasFrame(input, _firstFrame);
                _firstFrame = false;

                _ffmpegInput.Write(loasFrame, 0, loasFrame.Length);
                _ffmpegInput.Flush();

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

                if (totalRead < bytesToRead)
                {
                    // Partial read - zero fill the rest
                    Array.Clear(output, totalRead, bytesToRead - totalRead);
                }

                return 0;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Build a LOAS/LATM frame containing the raw AAC-ELD data.
        /// LOAS (Low Overhead Audio Stream) sync layer wraps an AudioMuxElement.
        /// The first frame includes the full StreamMuxConfig; subsequent frames
        /// reuse it (useSameStreamMux=1).
        /// </summary>
        private byte[] BuildLoasFrame(byte[] aacFrame, bool includeConfig)
        {
            // Build the AudioMuxElement as a bitstream
            var bits = new BitWriter();

            if (includeConfig)
            {
                // useSameStreamMux = 0 (include StreamMuxConfig)
                bits.WriteBits(0, 1);
                WriteStreamMuxConfig(bits);
            }
            else
            {
                // useSameStreamMux = 1 (reuse previous config)
                bits.WriteBits(1, 1);
            }

            // PayloadLengthInfo for allStreamsSameTimeFraming=1, progSIndx=0, laySIndx=0
            // Length is encoded as: N bytes of 0xFF followed by a final byte < 0xFF
            int remaining = aacFrame.Length;
            while (remaining >= 255)
            {
                bits.WriteBits(255, 8);
                remaining -= 255;
            }
            bits.WriteBits(remaining, 8);

            // PayloadMux: the raw AAC-ELD frame data
            for (int i = 0; i < aacFrame.Length; i++)
            {
                bits.WriteBits(aacFrame[i], 8);
            }

            // otherDataPresent = 0 already implied by StreamMuxConfig setting

            byte[] audioMuxElement = bits.ToByteArray();

            // LOAS sync layer: 0x56E0 | (length & 0x1FFF)
            int audioMuxLength = audioMuxElement.Length;
            byte[] loasFrame = new byte[3 + audioMuxLength];
            loasFrame[0] = 0x56;
            loasFrame[1] = (byte)(0xE0 | ((audioMuxLength >> 8) & 0x1F));
            loasFrame[2] = (byte)(audioMuxLength & 0xFF);
            Array.Copy(audioMuxElement, 0, loasFrame, 3, audioMuxLength);

            return loasFrame;
        }

        /// <summary>
        /// Write StreamMuxConfig for AAC-ELD into the bitstream.
        /// ISO 14496-3 Table 1.42
        /// </summary>
        private void WriteStreamMuxConfig(BitWriter bits)
        {
            // audioMuxVersion = 0
            bits.WriteBits(0, 1);
            // allStreamsSameTimeFraming = 1
            bits.WriteBits(1, 1);
            // numSubFrames = 0 (1 subframe)
            bits.WriteBits(0, 6);
            // numProgram = 0 (1 program)
            bits.WriteBits(0, 4);
            // numLayer = 0 (1 layer)
            bits.WriteBits(0, 3);

            // AudioSpecificConfig for AAC-ELD (ISO 14496-3 Table 1.15)
            WriteAudioSpecificConfig(bits);

            // frameLengthType = 0 (variable frame length)
            bits.WriteBits(0, 3);
            // latmBufferFullness = 0xFF (variable bitrate)
            bits.WriteBits(0xFF, 8);

            // otherDataPresent = 0
            bits.WriteBits(0, 1);
            // crcCheckPresent = 0
            bits.WriteBits(0, 1);
        }

        /// <summary>
        /// Write AudioSpecificConfig for AAC-ELD.
        /// ISO 14496-3 Table 1.15
        /// </summary>
        private void WriteAudioSpecificConfig(BitWriter bits)
        {
            // audioObjectType = 39 (AAC-ELD)
            // Since 39 >= 31, write 5 bits of 31 + 6 bits of (39-32) = 7
            bits.WriteBits(31, 5);
            bits.WriteBits(7, 6);

            // samplingFrequencyIndex
            bits.WriteBits(_freqIdx, 4);
            // If freqIdx == 0xF, write 24-bit samplingFrequency (not needed for standard rates)

            // channelConfiguration
            bits.WriteBits(_channels, 4);

            // SBR/PS extension: not present for AAC-ELD
            // (AAC-ELD specific config follows)

            // ELDSpecificConfig (ISO 14496-3 Table 4.180)
            // frameLengthFlag: 0 = 512 samples (480 after windowing), 1 = 480 samples
            bits.WriteBits(_frameLength == 480 ? 1 : 0, 1);
            // aacSectionDataResilienceFlag = 0
            bits.WriteBits(0, 1);
            // aacScalefactorDataResilienceFlag = 0
            bits.WriteBits(0, 1);
            // aacSpectralDataResilienceFlag = 0
            bits.WriteBits(0, 1);

            // ldSbrPresentFlag = 0 (no LD-SBR)
            bits.WriteBits(0, 1);
            // No further extension data
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

        /// <summary>
        /// Helper class for writing individual bits into a byte array.
        /// Used to construct LOAS/LATM bitstream fields.
        /// </summary>
        private class BitWriter
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _currentByte = 0;
            private int _bitsInCurrentByte = 0;

            public void WriteBits(int value, int numBits)
            {
                for (int i = numBits - 1; i >= 0; i--)
                {
                    _currentByte = (_currentByte << 1) | ((value >> i) & 1);
                    _bitsInCurrentByte++;

                    if (_bitsInCurrentByte == 8)
                    {
                        _bytes.Add((byte)_currentByte);
                        _currentByte = 0;
                        _bitsInCurrentByte = 0;
                    }
                }
            }

            public byte[] ToByteArray()
            {
                if (_bitsInCurrentByte > 0)
                {
                    // Pad remaining bits with zeros
                    _currentByte <<= (8 - _bitsInCurrentByte);
                    _bytes.Add((byte)_currentByte);
                }
                return _bytes.ToArray();
            }
        }
    }
}
