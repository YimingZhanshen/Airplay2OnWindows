/*
 * AAC-ELD Decoder using the Fraunhofer FDK AAC native library (libAACdec.dll).
 * This decoder supports AAC-ELD (Enhanced Low Delay) which is used during
 * AirPlay screen mirroring. SharpJaad.AAC does not support this profile.
 */

using System;
using System.Runtime.InteropServices;
using AirPlay.Models.Enums;

namespace AirPlay.Decoders.Implementations
{
    public class FdkAacEldDecoder : IDecoder, IDisposable
    {
        private const string LibName = "libAACdec";

        // FDK AAC error codes
        private const int AAC_DEC_OK = 0;

        // Transport type
        private const int TT_MP4_RAW = 0;

        // AAC decoder P/Invoke declarations
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aacDecoder_Open(int transportFmt, uint nrOfLayers);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_ConfigRaw(IntPtr self, IntPtr conf, IntPtr length);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_Fill(IntPtr self, IntPtr pBuffer, IntPtr bufferSize, IntPtr bytesValid);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_DecodeFrame(IntPtr self, IntPtr pTimeData, int timeDataSize, uint flags);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aacDecoder_GetStreamInfo(IntPtr self);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void aacDecoder_Close(IntPtr self);

        private IntPtr _handle;
        private int _pcmOutputSize;
        private int _channels;
        private bool _disposed;

        // FDK AAC requires an output buffer of at least 2048 * channels samples
        // for internal processing, even if the actual frame is smaller (e.g. 480 samples)
        private const int FDK_MIN_FRAME_SAMPLES = 2048;

        public AudioFormat Type => AudioFormat.AAC_ELD;

        public FdkAacEldDecoder()
        {
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _channels = channels;

            // Open FDK AAC decoder with raw transport format
            _handle = aacDecoder_Open(TT_MP4_RAW, 1);
            if (_handle == IntPtr.Zero)
            {
                Console.WriteLine("FDK AAC: Failed to open decoder");
                return -1;
            }

            // Build ASC (Audio Specific Config) for AAC-ELD
            // AOT=39 (AAC-ELD), sampleRate=44100, channels=2, frameLength=480
            byte[] asc = BuildAacEldAsc(sampleRate, channels, frameLength);

            // Configure decoder with ASC
            unsafe
            {
                fixed (byte* ascPtr = asc)
                {
                    IntPtr pAsc = (IntPtr)ascPtr;
                    int ascLen = asc.Length;
                    IntPtr pAscLen = (IntPtr)(&ascLen);
                    IntPtr ppAsc = (IntPtr)(&pAsc);

                    int err = aacDecoder_ConfigRaw(_handle, ppAsc, pAscLen);
                    if (err != AAC_DEC_OK)
                    {
                        Console.WriteLine($"FDK AAC: ConfigRaw failed with error 0x{err:X}");
                        return err;
                    }
                }
            }

            // The actual PCM output per frame: frameLength * channels * bytesPerSample
            _pcmOutputSize = frameLength * channels * (bitDepth / 8);

            Console.WriteLine($"FDK AAC-ELD decoder configured: {sampleRate}Hz, {channels}ch, {bitDepth}bit, frameLength={frameLength}");
            return 0;
        }

        public int GetOutputStreamLength()
        {
            return _pcmOutputSize;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            if (_handle == IntPtr.Zero)
                return -1;

            unsafe
            {
                fixed (byte* inputPtr = input)
                {
                    IntPtr pInput = (IntPtr)inputPtr;
                    int inputSize = input.Length;
                    int bytesValid = input.Length;
                    IntPtr pInputSize = (IntPtr)(&inputSize);
                    IntPtr pBytesValid = (IntPtr)(&bytesValid);
                    IntPtr ppInput = (IntPtr)(&pInput);

                    // Fill the decoder input buffer
                    int err = aacDecoder_Fill(_handle, ppInput, pInputSize, pBytesValid);
                    if (err != AAC_DEC_OK)
                    {
                        return err;
                    }
                }

                // FDK AAC requires the output buffer to hold at least 2048 * channels samples
                // even though the actual decoded frame may be smaller (e.g. 480 samples for AAC-ELD)
                int decoderBufferSamples = FDK_MIN_FRAME_SAMPLES * _channels;
                short[] pcmBuffer = new short[decoderBufferSamples];

                fixed (short* pcmPtr = pcmBuffer)
                {
                    int err = aacDecoder_DecodeFrame(_handle, (IntPtr)pcmPtr, decoderBufferSamples, 0);
                    if (err != AAC_DEC_OK)
                    {
                        return err;
                    }

                    // Copy only the actual frame data (frameLength * channels * 2 bytes)
                    int byteLen = Math.Min(_pcmOutputSize, output.Length);
                    Buffer.BlockCopy(pcmBuffer, 0, output, 0, byteLen);
                }
            }

            return 0;
        }

        /// <summary>
        /// Build an AudioSpecificConfig for AAC-ELD.
        /// Uses the same format as the original airplayreceiver project which is
        /// known to work with the FDK AAC library.
        /// </summary>
        private static byte[] BuildAacEldAsc(int sampleRate, int channels, int frameLength)
        {
            int audioObjectType = 39; // AAC-ELD
            int freqIndex = GetSampleRateIndex(sampleRate);

            // Build ASC as a binary string (matching original airplayreceiver format)
            string bin;
            if (audioObjectType >= 31)
            {
                // Extended AOT: 5 bits escape (11111) + 6 bits (AOT - 32)
                bin = Convert.ToString(31, 2).PadLeft(5, '0');
                bin += Convert.ToString(audioObjectType - 32, 2).PadLeft(6, '0');
            }
            else
            {
                bin = Convert.ToString(audioObjectType, 2).PadLeft(5, '0');
            }

            // ELD specific config
            bin += Convert.ToString(freqIndex, 2).PadLeft(4, '0');
            bin += Convert.ToString(channels, 2).PadLeft(4, '0');

            // frameLengthFlag: 0 = 480 samples, 1 = 512 samples
            bin += (frameLength == 512) ? "1" : "0";

            // dependsOnCoreCoder = 0
            bin += "0";

            // extensionFlag = 0
            bin += "0";

            // Padding to byte boundary
            while (bin.Length % 8 != 0)
                bin += "0";

            int nBytes = bin.Length / 8;
            byte[] result = new byte[nBytes];
            for (int i = 0; i < nBytes; i++)
            {
                result[i] = Convert.ToByte(bin.Substring(8 * i, 8), 2);
            }

            return result;
        }

        private static int GetSampleRateIndex(int sampleRate)
        {
            return sampleRate switch
            {
                96000 => 0,
                88200 => 1,
                64000 => 2,
                48000 => 3,
                44100 => 4,
                32000 => 5,
                24000 => 6,
                22050 => 7,
                16000 => 8,
                12000 => 9,
                11025 => 10,
                8000 => 11,
                7350 => 12,
                _ => 4, // Default to 44100
            };
        }

        public void Dispose()
        {
            if (!_disposed && _handle != IntPtr.Zero)
            {
                aacDecoder_Close(_handle);
                _handle = IntPtr.Zero;
                _disposed = true;
            }
        }
    }
}
