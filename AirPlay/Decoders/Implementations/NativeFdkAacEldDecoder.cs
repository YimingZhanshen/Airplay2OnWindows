/*
 * AAC-ELD Decoder using the native FDK-AAC library via P/Invoke.
 *
 * This decoder feeds raw (decrypted) AAC-ELD frames directly to FDK-AAC
 * using TT_MP4_RAW transport type with AudioSpecificConfig for AAC-ELD.
 * No ADTS/LOAS framing is needed - the raw access units are passed directly.
 *
 * Requires libfdk-aac-2.dll (Windows) or libfdk-aac.so.2 (Linux) in the
 * application directory or system PATH.
 *
 * AudioSpecificConfig: f8e85000 (AOT=39, 44100Hz, stereo, bitDepth=16)
 * This matches both UxPlay and itskenny0/airplayreceiver reference implementations.
 *
 * Reference: itskenny0/airplayreceiver AACDecoder.cs
 */

using System;
using System.Runtime.InteropServices;
using AirPlay.Models.Enums;

namespace AirPlay.Decoders.Implementations
{
    public unsafe class NativeFdkAacEldDecoder : IDecoder, IDisposable
    {
        private const int TT_MP4_RAW = 0;

        private IntPtr _decoder;
        private int _pcmPktSize;
        private bool _disposed;
        private int _decodeCallCount;
        private int _fillErrorCount;
        private int _decodeErrorCount;
        private int _successCount;

        public AudioFormat Type => AudioFormat.AAC_ELD;

        public NativeFdkAacEldDecoder()
        {
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _pcmPktSize = frameLength * channels * (bitDepth / 8);

            Console.WriteLine($"[DEBUG-FDK] Config called: sampleRate={sampleRate}, channels={channels}, bitDepth={bitDepth}, frameLength={frameLength}, pcmOutputSize={_pcmPktSize}");

            // Open FDK-AAC decoder with TT_MP4_RAW transport (raw access units)
            _decoder = aacDecoder_Open(TT_MP4_RAW, 1);
            if (_decoder == IntPtr.Zero)
            {
                Console.WriteLine("[DEBUG-FDK] aacDecoder_Open returned null - library loaded but Open failed");
                return -1;
            }
            Console.WriteLine($"[DEBUG-FDK] aacDecoder_Open succeeded: handle=0x{_decoder:X}");

            // Build AudioSpecificConfig for AAC-ELD
            // Uses exact same format as itskenny0/airplayreceiver and UxPlay: f8e85000
            var asc = BuildAudioSpecificConfig(39, sampleRate, channels, frameLength);

            // Configure decoder with the ASC
            int ret = ConfigRaw(asc);
            if (ret != 0)
            {
                Console.WriteLine($"[DEBUG-FDK] aacDecoder_ConfigRaw FAILED: error=0x{ret:X} ({(AACDecoderError)ret})");
                aacDecoder_Close(_decoder);
                _decoder = IntPtr.Zero;
                return ret;
            }

            Console.WriteLine($"[DEBUG-FDK] Decoder configured successfully: {sampleRate}Hz, {channels}ch, {bitDepth}bit, frameLength={frameLength}, pcmSize={_pcmPktSize}");
            return 0;
        }

        public int GetOutputStreamLength()
        {
            return _pcmPktSize;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int pcm_pkt_size)
        {
            if (_decoder == IntPtr.Zero)
            {
                _decodeCallCount++;
                if (_decodeCallCount <= 3)
                    Console.WriteLine("[DEBUG-FDK] DecodeFrame called but decoder is null");
                return -1;
            }

            _decodeCallCount++;

            // Log first few frames and periodically
            bool shouldLog = _decodeCallCount <= 5 || _decodeCallCount % 200 == 0;

            if (shouldLog)
            {
                var hexDump = input.Length >= 16
                    ? BitConverter.ToString(input, 0, Math.Min(16, input.Length))
                    : BitConverter.ToString(input);
                Console.WriteLine($"[DEBUG-FDK] DecodeFrame #{_decodeCallCount}: inputLen={input.Length}, first16={hexDump}");
            }

            int ret;

            // Fill the decoder's internal buffer with the raw AAC frame
            ret = Fill(input);
            if (ret != 0)
            {
                _fillErrorCount++;
                if (_fillErrorCount <= 5 || _fillErrorCount % 200 == 0)
                    Console.WriteLine($"[DEBUG-FDK] aacDecoder_Fill error #{_fillErrorCount}: 0x{ret:X} ({(AACDecoderError)ret}), inputLen={input.Length}");
                return ret;
            }

            // Decode one frame
            ret = InternalDecodeFrame(ref output, pcm_pkt_size);
            if (ret != 0)
            {
                _decodeErrorCount++;
                if (_decodeErrorCount <= 5 || _decodeErrorCount % 200 == 0)
                    Console.WriteLine($"[DEBUG-FDK] aacDecoder_DecodeFrame error #{_decodeErrorCount}: 0x{ret:X} ({(AACDecoderError)ret}), pcmSize={pcm_pkt_size}");
                return ret;
            }

            _successCount++;
            if (_successCount <= 5 || _successCount % 200 == 0)
            {
                // Check if output is silence (all zeros)
                bool isSilence = true;
                for (int i = 0; i < Math.Min(output.Length, 64); i++)
                {
                    if (output[i] != 0) { isSilence = false; break; }
                }
                Console.WriteLine($"[DEBUG-FDK] Decode SUCCESS #{_successCount}: outputLen={pcm_pkt_size}, isSilence={isSilence}, totalErrors(fill={_fillErrorCount},decode={_decodeErrorCount})");
            }

            return 0;
        }

        private int Fill(byte[] pBuffer)
        {
            uint bufferSize = (uint)pBuffer.Length;
            uint bytesValid = (uint)pBuffer.Length;

            IntPtr ptr = Marshal.AllocHGlobal(pBuffer.Length);
            try
            {
                Marshal.Copy(pBuffer, 0, ptr, pBuffer.Length);

                IntPtr* pBufferPtr = stackalloc IntPtr[1];
                pBufferPtr[0] = ptr;

                int ret = (int)aacDecoder_Fill(_decoder, pBufferPtr, &bufferSize, &bytesValid);
                if (ret == 0 && _decodeCallCount <= 5)
                    Console.WriteLine($"[DEBUG-FDK] Fill OK: bufferSize={pBuffer.Length}, bytesValid after={bytesValid}");
                return ret;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private int InternalDecodeFrame(ref byte[] output, int pcm_pkt_size)
        {
            IntPtr ptr = Marshal.AllocHGlobal(pcm_pkt_size);
            try
            {
                int ret = (int)aacDecoder_DecodeFrame(_decoder, ptr, pcm_pkt_size, 0);
                if (ret == 0)
                {
                    Marshal.Copy(ptr, output, 0, pcm_pkt_size);
                }
                return ret;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private int ConfigRaw(byte[] config)
        {
            uint length = (uint)config.Length;

            IntPtr ptr = Marshal.AllocHGlobal(config.Length);
            try
            {
                Marshal.Copy(config, 0, ptr, config.Length);

                IntPtr* confPtr = stackalloc IntPtr[1];
                confPtr[0] = ptr;

                return (int)aacDecoder_ConfigRaw(_decoder, confPtr, &length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Build AudioSpecificConfig for AAC-ELD per ISO 14496-3.
        /// Uses the exact same format as itskenny0/airplayreceiver and UxPlay:
        /// AOT(5+6) + FreqIdx(4) + Channels(4) + BitDepth(5) + padding(8)
        /// For AAC-ELD 44100Hz stereo 16bit: f8 e8 50 00
        /// </summary>
        private static byte[] BuildAudioSpecificConfig(int audioObjectType, int sampleRate, int channels, int frameLength)
        {
            int frequencyIndex = GetSampleRateIndex(sampleRate);
            int bitDepth = 16;

            string bin;
            if (audioObjectType >= 31)
            {
                // Extended AOT encoding: 5-bit escape (11111) + 6-bit AOT-32
                bin = Convert.ToString(31, 2).PadLeft(5, '0');
                bin += Convert.ToString(audioObjectType - 32, 2).PadLeft(6, '0');
            }
            else
            {
                bin = Convert.ToString(audioObjectType, 2).PadLeft(5, '0');
            }

            bin += Convert.ToString(frequencyIndex, 2).PadLeft(4, '0');
            bin += Convert.ToString(channels, 2).PadLeft(4, '0');
            bin += Convert.ToString(bitDepth, 2).PadLeft(5, '0');
            bin += "00000000"; // padding byte

            int nBytes = bin.Length / 8;
            byte[] bytes = new byte[nBytes];
            for (int i = 0; i < nBytes; i++)
            {
                bytes[i] = Convert.ToByte(bin.Substring(8 * i, 8), 2);
            }

            Console.WriteLine($"[DEBUG-FDK] AudioSpecificConfig: {BitConverter.ToString(bytes)} (AOT={audioObjectType}, freq={sampleRate}[idx={frequencyIndex}], ch={channels}, bitDepth={bitDepth}, frameLen={frameLength})");
            return bytes;
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
            if (!_disposed && _decoder != IntPtr.Zero)
            {
                _disposed = true;
                Console.WriteLine($"[DEBUG-FDK] Disposing decoder: totalCalls={_decodeCallCount}, successes={_successCount}, fillErrors={_fillErrorCount}, decodeErrors={_decodeErrorCount}");
                aacDecoder_Close(_decoder);
                _decoder = IntPtr.Zero;
            }
        }

        // ---- Native P/Invoke declarations ----
        // FDK-AAC library names vary by platform:
        // Windows: libfdk-aac-2.dll
        // Linux: libfdk-aac.so.2

        private const string FDK_AAC_LIB = "libfdk-aac-2";

        [DllImport(FDK_AAC_LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aacDecoder_Open(int transportFmt, uint nrOfLayers);

        [DllImport(FDK_AAC_LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_ConfigRaw(IntPtr decoder, IntPtr* conf, uint* length);

        [DllImport(FDK_AAC_LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_Fill(IntPtr decoder, IntPtr* pBuffer, uint* bufferSize, uint* pBytesValid);

        [DllImport(FDK_AAC_LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_DecodeFrame(IntPtr decoder, IntPtr output, int pcm_pkt_size, uint flags);

        [DllImport(FDK_AAC_LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern void aacDecoder_Close(IntPtr decoder);
    }
}
