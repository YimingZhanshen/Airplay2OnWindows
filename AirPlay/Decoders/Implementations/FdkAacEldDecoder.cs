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
        private bool _disposed;

        public AudioFormat Type => AudioFormat.AAC_ELD;

        public FdkAacEldDecoder()
        {
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
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

                // Decode one frame
                // Output buffer needs to be large enough for decoded PCM
                // FDK AAC outputs 16-bit interleaved PCM
                int outputSamples = _pcmOutputSize / 2; // 16-bit samples
                short[] pcmBuffer = new short[outputSamples];

                fixed (short* pcmPtr = pcmBuffer)
                {
                    int err = aacDecoder_DecodeFrame(_handle, (IntPtr)pcmPtr, outputSamples, 0);
                    if (err != AAC_DEC_OK)
                    {
                        return err;
                    }

                    // Convert short[] to byte[]
                    int byteLen = Math.Min(outputSamples * 2, output.Length);
                    Buffer.BlockCopy(pcmBuffer, 0, output, 0, byteLen);
                }
            }

            return 0;
        }

        /// <summary>
        /// Build an AudioSpecificConfig for AAC-ELD.
        /// See ISO 14496-3 for the ASC bit format.
        /// </summary>
        private static byte[] BuildAacEldAsc(int sampleRate, int channels, int frameLength)
        {
            // AOT = 39 (AAC-ELD) requires extended AOT encoding:
            // audioObjectType (5 bits) = 31 (escape), then audioObjectTypeExt (6 bits) = 39-32 = 7
            // samplingFrequencyIndex (4 bits): 4 = 44100Hz
            // channelConfiguration (4 bits): 2 = stereo
            // ELD specific config: frameLengthFlag (1 bit) = 0 for 480 samples

            int freqIndex = GetSampleRateIndex(sampleRate);

            // Bit layout:
            // 5 bits: 11111 (escape = 31)
            // 6 bits: 000111 (39 - 32 = 7)
            // 4 bits: freqIndex
            // 4 bits: channelConfig
            // 3 bits: 000 (ELD specific: frameLengthFlag=0 for 480, plus padding)
            // Plus SBR config flags

            // Build bit by bit
            var bits = new System.Collections.BitArray(64);
            int pos = 0;

            // audioObjectType = 31 (escape) -> 5 bits: 11111
            bits[pos++] = true;
            bits[pos++] = true;
            bits[pos++] = true;
            bits[pos++] = true;
            bits[pos++] = true;

            // audioObjectTypeExt = 7 (39 - 32) -> 6 bits: 000111
            bits[pos++] = false;
            bits[pos++] = false;
            bits[pos++] = false;
            bits[pos++] = true;
            bits[pos++] = true;
            bits[pos++] = true;

            // samplingFrequencyIndex -> 4 bits
            bits[pos++] = (freqIndex & 8) != 0;
            bits[pos++] = (freqIndex & 4) != 0;
            bits[pos++] = (freqIndex & 2) != 0;
            bits[pos++] = (freqIndex & 1) != 0;

            // channelConfiguration -> 4 bits
            bits[pos++] = (channels & 8) != 0;
            bits[pos++] = (channels & 4) != 0;
            bits[pos++] = (channels & 2) != 0;
            bits[pos++] = (channels & 1) != 0;

            // ELD specific config:
            // frameLengthFlag: 0 = 480 samples, 1 = 512 samples
            bits[pos++] = (frameLength == 512);

            // LDSBR present flag = 0
            bits[pos++] = false;

            // ELD extension type = 0 (end)
            bits[pos++] = false;
            bits[pos++] = false;
            bits[pos++] = false;
            bits[pos++] = false;

            // Convert bits to bytes
            int byteLen = (pos + 7) / 8;
            byte[] result = new byte[byteLen];
            for (int i = 0; i < pos; i++)
            {
                if (bits[i])
                {
                    result[i / 8] |= (byte)(0x80 >> (i % 8));
                }
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
