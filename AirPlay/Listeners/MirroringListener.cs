using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Services.Implementations;
using AirPlay.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPlay.Listeners
{
    public class MirroringListener : BaseTcpListener
    {
        public const string AIR_PLAY_STREAM_KEY = "AirPlayStreamKey";
        public const string AIR_PLAY_STREAM_IV = "AirPlayStreamIV";

        private readonly IRtspReceiver _receiver;
        private readonly string _sessionId;
        private readonly IBufferedCipher _aesCtrDecrypt;
        private readonly OmgHax _omgHax = new OmgHax();

        private byte[] _og = new byte[16];
        private int _nextDecryptCount;

        public MirroringListener(IRtspReceiver receiver, string sessionId, ushort port) : base(port, true)
        {
            _receiver = receiver;
            _sessionId = sessionId;

            _aesCtrDecrypt = CipherUtilities.GetCipher("AES/CTR/NoPadding");
        }

        public override async Task OnRawReceivedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            // Get session by active-remove header value
            var session = await SessionManager.Current.GetSessionAsync(_sessionId);

            // If we have not decripted session AesKey
            if (session.DecryptedAesKey == null)
            {
                byte[] decryptedAesKey = new byte[16];
                _omgHax.DecryptAesKey(session.KeyMsg, session.AesKey, decryptedAesKey);
                session.DecryptedAesKey = decryptedAesKey;
            }

            InitAesCtrCipher(session.DecryptedAesKey, session.EcdhShared, session.StreamConnectionId);

            var headerBuffer = new byte[128];

            do
            {
                // Read the first 4 bytes to determine packet type
                int readStart = 0;
                int ret;
                do
                {
                    ret = await stream.ReadAsync(headerBuffer, readStart, 4 - readStart, cancellationToken);
                    if (ret <= 0)
                    {
                        goto exit_loop;
                    }
                    readStart += ret;
                } while (readStart < 4);

                if ((headerBuffer[0] == 80 && headerBuffer[1] == 79 && headerBuffer[2] == 83 && headerBuffer[3] == 84) || (headerBuffer[0] == 71 && headerBuffer[1] == 69 && headerBuffer[2] == 84))
                {
                    // Request is POST or GET (skip)
                    continue;
                }

                // Read remaining 124 bytes of the 128-byte header
                do
                {
                    ret = await stream.ReadAsync(headerBuffer, readStart, 128 - readStart, cancellationToken);
                    if (ret <= 0)
                    {
                        goto exit_loop;
                    }
                    readStart += ret;
                } while (readStart < 128);

                var header = new MirroringHeader(headerBuffer);

                // Update session dimensions from codec config (PayloadType 1)
                if (header.PayloadType == 1)
                {
                    if (header.WidthSource > 0)
                        session.WidthSource = header.WidthSource;
                    if (header.HeightSource > 0)
                        session.HeightSource = header.HeightSource;
                }

                if (header.PayloadSize <= 0)
                {
                    continue;
                }

                try
                {
                    byte[] payload = new byte[header.PayloadSize];

                    readStart = 0;
                    do
                    {
                        ret = await stream.ReadAsync(payload, readStart, header.PayloadSize - readStart, cancellationToken);
                        if (ret <= 0)
                        {
                            goto exit_loop;
                        }
                        readStart += ret;
                    } while (readStart < header.PayloadSize);

                    if (header.PayloadType == 0)
                    {
                        DecryptVideoData(payload, out byte[] output);
                        // Use the PTS from this specific frame's header
                        long framePts = header.PayloadPts;
                        int width = session.WidthSource ?? 1920;
                        int height = session.HeightSource ?? 1080;
                        ProcessVideo(output, session.SpsPps, framePts, width, height);
                    }
                    else if (header.PayloadType == 1)
                    {
                        ProcessSpsPps(payload, out byte[] spsPps);
                        session.SpsPps = spsPps;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Mirroring error: {e}");
                }

                // Save current session periodically
                await SessionManager.Current.CreateOrUpdateSessionAsync(_sessionId, session);

            } while (client.Connected && stream.CanRead && !cancellationToken.IsCancellationRequested);

            exit_loop:
            Console.WriteLine($"Closing mirroring connection..");
        }

        private void DecryptVideoData(byte[] videoData, out byte[] output)
        {
            if (_nextDecryptCount > 0)
            {
                for (int i = 0; i < _nextDecryptCount; i++)
                {
                    videoData[i] = (byte)(videoData[i] ^ _og[(16 - _nextDecryptCount) + i]);
                }
            }

            int encryptlen = ((videoData.Length - _nextDecryptCount) / 16) * 16;
            _aesCtrDecrypt.ProcessBytes(videoData, _nextDecryptCount, encryptlen, videoData, _nextDecryptCount);

            int restlen = (videoData.Length - _nextDecryptCount) % 16;
            int reststart = videoData.Length - restlen;
            _nextDecryptCount = 0;
            if (restlen > 0)
            {
                Array.Fill(_og, (byte)0);
                Array.Copy(videoData, reststart, _og, 0, restlen);
                _aesCtrDecrypt.ProcessBytes(_og, 0, 16, _og, 0);
                Array.Copy(_og, 0, videoData, reststart, restlen);
                _nextDecryptCount = 16 - restlen;
            }

            output = videoData;
        }

        private void InitAesCtrCipher(byte[] aesKey, byte[] ecdhShared, string streamConnectionId)
        {
            byte[] eaesKey = Utilities.Hash(aesKey, ecdhShared);

            byte[] skey = Encoding.UTF8.GetBytes($"{AIR_PLAY_STREAM_KEY}{streamConnectionId}");
            byte[] hash1 = Utilities.Hash(skey, Utilities.CopyOfRange(eaesKey, 0, 16));

            byte[] siv = Encoding.UTF8.GetBytes($"{AIR_PLAY_STREAM_IV}{streamConnectionId}");
            byte[] hash2 = Utilities.Hash(siv, Utilities.CopyOfRange(eaesKey, 0, 16));

            byte[] decryptAesKey = new byte[16];
            byte[] decryptAesIV = new byte[16];
            Array.Copy(hash1, 0, decryptAesKey, 0, 16);
            Array.Copy(hash2, 0, decryptAesIV, 0, 16);

            var keyParameter = ParameterUtilities.CreateKeyParameter("AES", decryptAesKey);
            var cipherParameters = new ParametersWithIV(keyParameter, decryptAesIV, 0, decryptAesIV.Length);

            _aesCtrDecrypt.Init(false, cipherParameters);
        }

        private void ProcessVideo(byte[] payload, byte[] spsPps, long pts, int widthSource, int heightSource)
        {
            if (payload == null || payload.Length < 5)
                return;

            // Convert AVCC format (4-byte NALU length prefix) to Annex B format (00 00 00 01 start codes)
            int offset = 0;
            while (offset + 4 <= payload.Length)
            {
                int nc_len = ((payload[offset] & 0xFF) << 24) | ((payload[offset + 1] & 0xFF) << 16) |
                             ((payload[offset + 2] & 0xFF) << 8) | (payload[offset + 3] & 0xFF);

                if (nc_len <= 0 || offset + 4 + nc_len > payload.Length)
                {
                    break;
                }

                // Replace NALU length with Annex B start code
                payload[offset] = 0;
                payload[offset + 1] = 0;
                payload[offset + 2] = 0;
                payload[offset + 3] = 1;

                offset += 4 + nc_len;
            }

            if (spsPps == null || spsPps.Length == 0)
                return;

            var h264Data = new H264Data();
            h264Data.FrameType = payload[4] & 0x1f;

            if (h264Data.FrameType == 5)
            {
                // IDR frame - prepend SPS/PPS
                var payloadOut = new byte[payload.Length + spsPps.Length];
                Array.Copy(spsPps, 0, payloadOut, 0, spsPps.Length);
                Array.Copy(payload, 0, payloadOut, spsPps.Length, payload.Length);

                h264Data.Data = payloadOut;
                h264Data.Length = payloadOut.Length;
            }
            else
            {
                h264Data.Data = payload;
                h264Data.Length = payload.Length;
            }

            h264Data.Pts = pts;
            h264Data.Width = widthSource;
            h264Data.Height = heightSource;

            _receiver.OnData(h264Data);
        }

        private void ProcessSpsPps(byte[] payload, out byte[] spsPps)
        {
            var h264 = new H264Codec();

            h264.Version = payload[0];
            h264.ProfileHigh = payload[1];
            h264.Compatibility = payload[2];
            h264.Level = payload[3];
            h264.Reserved6AndNal = payload[4];
            h264.Reserved3AndSps = payload[5];
            h264.LengthOfSps = (short)(((payload[6] & 255) << 8) + (payload[7] & 255));

            var sequence = new byte[h264.LengthOfSps];
            Array.Copy(payload, 8, sequence, 0, h264.LengthOfSps);
            h264.SequenceParameterSet = sequence;
            h264.NumberOfPps = payload[h264.LengthOfSps + 8];
            h264.LengthOfPps = (short)(((payload[h264.LengthOfSps + 9] & 0xFF) << 8) + (payload[h264.LengthOfSps + 10] & 0xFF));

            var picture = new byte[h264.LengthOfPps];
            Array.Copy(payload, h264.LengthOfSps + 11, picture, 0, h264.LengthOfPps);
            h264.PictureParameterSet = picture;

            if (h264.LengthOfSps + h264.LengthOfPps < 102400)
            {
                var spsPpsLen = h264.LengthOfSps + h264.LengthOfPps + 8;
                spsPps = new byte[spsPpsLen];

                spsPps[0] = 0;
                spsPps[1] = 0;
                spsPps[2] = 0;
                spsPps[3] = 1;

                Array.Copy(h264.SequenceParameterSet, 0, spsPps, 4, h264.LengthOfSps);

                spsPps[h264.LengthOfSps + 4] = 0;
                spsPps[h264.LengthOfSps + 5] = 0;
                spsPps[h264.LengthOfSps + 6] = 0;
                spsPps[h264.LengthOfSps + 7] = 1;

                Array.Copy(h264.PictureParameterSet, 0, spsPps, h264.LengthOfSps + 8, h264.LengthOfPps);
            }
            else
            {
                spsPps = null;
            }
        }
    }
}
