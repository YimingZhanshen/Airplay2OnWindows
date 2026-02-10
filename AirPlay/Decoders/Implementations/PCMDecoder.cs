using System;
using AirPlay.Models.Enums;

namespace AirPlay
{
    public class PCMDecoder : IDecoder
    {
        private int _pcmSize;

        public AudioFormat Type => AudioFormat.PCM;

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _pcmSize = frameLength * channels * (bitDepth / 8);
            return 0;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            int copyLen = Math.Min(input.Length, output.Length);
            Array.Copy(input, 0, output, 0, copyLen);
            return 0;
        }

        public int GetOutputStreamLength()
        {
            return _pcmSize > 0 ? _pcmSize : 1024 * 4; // default: 1024 samples * stereo * 16-bit
        }
    }
}
