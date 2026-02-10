using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Models.Enums;
using AirPlay.Services.Implementations;
using AirPlay.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPlay.Listeners
{
    public class AudioListener : BaseUdpListener
    {
        public const int RAOP_PACKET_LENGTH = 50000;
        public const int RAOP_BUFFER_LENGTH = 1024; //512;
        public const ulong OFFSET_1900_TO_1970 = 2208988800UL;

        private readonly IRtspReceiver _receiver;
        private readonly string _sessionId;
        private readonly OmgHax _omgHax = new OmgHax();

        private IDecoder _decoder;
        private readonly object _decoderLock = new object();
        private readonly object _bufferLock = new object();
        private ulong _sync_time;
        private ulong _sync_timestamp;
        private ushort _controlSequenceNumber = 0;
        private RaopBuffer _raopBuffer;
        private Socket _cSocket;

        private readonly DumpConfig _dConfig;

        private bool _isMirroring = false;

        public AudioListener(IRtspReceiver receiver, string sessionId, ushort cport, ushort dport, DumpConfig dConfig, bool isMirroring = false) : base(cport, dport)
        {
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _dConfig = dConfig ?? throw new ArgumentNullException(nameof(dConfig));
            _isMirroring = isMirroring;

            _raopBuffer = RaopBufferInit();
        }

        public override async Task OnRawCSocketAsync(Socket cSocket, CancellationToken cancellationToken)
        {
            Console.WriteLine("[DEBUG-C] OnRawCSocketAsync started");

            _cSocket = cSocket;

            // Each handler gets its own cipher instance (cipher is stateful, not thread-safe)
            var aesCbcDecrypt = CipherUtilities.GetCipher("AES/CBC/NoPadding");

            // Get session by active-remove header value
            var session = await SessionManager.Current.GetSessionAsync(_sessionId);
            Console.WriteLine($"[DEBUG-C] Session loaded: AesKey={session.AesKey != null}, AesIv={session.AesIv != null}, EcdhShared={session.EcdhShared != null}, KeyMsg={session.KeyMsg != null}, AudioFormat={session.AudioFormat}");

            // If we have not decripted session AesKey
            if (session.DecryptedAesKey == null)
            {
                byte[] decryptedAesKey = new byte[16];
                _omgHax.DecryptAesKey(session.KeyMsg, session.AesKey, decryptedAesKey);
                session.DecryptedAesKey = decryptedAesKey;
                Console.WriteLine("[DEBUG-C] AES key decrypted");
            }

            // Initialize decoder (needed for type 0x56 audio packets during mirroring)
            InitializeDecoder(session);
            Console.WriteLine($"[DEBUG-C] Decoder initialized: type={_decoder?.Type}, outputLen={_decoder?.GetOutputStreamLength()}");

            await SessionManager.Current.CreateOrUpdateSessionAsync(_sessionId, session);

            var packet = new byte[RAOP_PACKET_LENGTH];
            int cPacketCount = 0;
            int c56Count = 0;
            int c54Count = 0;
            int cOtherCount = 0;
            int cQueuedCount = 0;
            int cDequeuedCount = 0;
            int cPcmDelivered = 0;
            int cSocketErrors = 0;
            string exitReason = "loop-end";

            Console.WriteLine("[DEBUG-C] Entering receive loop...");

            do
            {
                try
                {
                    var cret = cSocket.Receive(packet, 0, RAOP_PACKET_LENGTH, SocketFlags.None, out SocketError error);
                    if(error != SocketError.Success)
                    {
                        cSocketErrors++;
                        if (cSocketErrors <= 5)
                            Console.WriteLine($"[DEBUG-C] Socket.Receive error: {error}");
                        continue;
                    }

                    cPacketCount++;
                    if (cPacketCount == 1)
                        Console.WriteLine($"[DEBUG-C] First packet received! size={cret}");

                    var mem = new MemoryStream(packet);
                    using (var reader = new BinaryReader(mem))
                    {
                        mem.Position = 1;
                        int type_c = reader.ReadByte() & ~0x80;
                        if (type_c == 0x56)
                        {
                            c56Count++;
                            InitAesCbcCipher(aesCbcDecrypt, session.DecryptedAesKey, session.EcdhShared, session.AesIv);

                            mem.Position = 4;
                            var data = reader.ReadBytes(cret - 4);

                            if (c56Count <= 3)
                                Console.WriteLine($"[DEBUG-C] 0x56 packet #{c56Count}: dataLen={data.Length}, seqNum={(ushort)((data[2] << 8) | data[3])}");

                            int ret;
                            lock (_bufferLock)
                            {
                                ret = RaopBufferQueue(_raopBuffer, data, (ushort)data.Length, session, aesCbcDecrypt);
                            }

                            if (c56Count <= 3)
                                Console.WriteLine($"[DEBUG-C] RaopBufferQueue returned: {ret}");

                            if (ret > 0) cQueuedCount++;

                            // Dequeue and play audio received on control socket (used during screen mirroring)
                            var pcmBatch = new System.Collections.Generic.List<PcmData>();
                            byte[] audiobuf;
                            int audiobuflen = 0;
                            uint timestamp = 0;
                            lock (_bufferLock)
                            {
                                while ((audiobuf = RaopBufferDequeue(_raopBuffer, ref audiobuflen, ref timestamp, true)) != null)
                                {
                                    if (audiobuf.Length == 0 || audiobuflen <= 0)
                                        continue;

                                    cDequeuedCount++;
                                    var pcmData = new PcmData();
                                    pcmData.Length = audiobuflen;
                                    pcmData.Data = audiobuf;
                                    pcmData.Pts = (ulong)(timestamp - _sync_timestamp) * 1000000UL / 44100 + _sync_time;

                                    pcmBatch.Add(pcmData);
                                }
                            }

                            if (c56Count <= 3 && pcmBatch.Count > 0)
                                Console.WriteLine($"[DEBUG-C] Dequeued {pcmBatch.Count} PCM frames, first len={pcmBatch[0].Length}");

                            // Deliver PCM outside the lock to avoid blocking the data handler
                            foreach (var pcm in pcmBatch)
                            {
                                _receiver.OnPCMData(pcm);
                                cPcmDelivered++;
                            }
                        }
                        else if (type_c == 0x54)
                        {
                            c54Count++;
                            mem.Position = 8;
                            uint ntp_seconds = (uint)reader.ReadInt32();
                            uint ntp_fraction = (uint)reader.ReadInt32();
                            ulong ntp_time = ((ulong)ntp_seconds * 1000000UL) + (((ulong)ntp_fraction * 1000000UL) >> 32);
                            uint rtp_timestamp = (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]);
                            uint next_timestamp = (uint)((packet[16] << 24) | (packet[17] << 16) | (packet[18] << 8) | packet[19]);

                            _sync_time = ntp_time - OFFSET_1900_TO_1970 * 1000000UL;
                            _sync_timestamp = rtp_timestamp;

                            if (c54Count <= 3)
                                Console.WriteLine($"[DEBUG-C] 0x54 sync packet #{c54Count}: rtp_ts={rtp_timestamp}");
                        }
                        else
                        {
                            cOtherCount++;
                            if (cOtherCount <= 5)
                                Console.WriteLine($"[DEBUG-C] Unknown packet type: 0x{type_c:X2}, size={cret}");
                        }
                    }

                    // Log summary periodically
                    if (cPacketCount % 500 == 0)
                    {
                        Console.WriteLine($"[DEBUG-C] Stats: packets={cPacketCount}, 0x56={c56Count}, 0x54={c54Count}, other={cOtherCount}, queued={cQueuedCount}, dequeued={cDequeuedCount}, pcmDelivered={cPcmDelivered}, socketErrors={cSocketErrors}");
                    }

                    Array.Fill<byte>(packet, 0);
                }
                catch (ObjectDisposedException)
                {
                    exitReason = "ObjectDisposedException (socket closed)";
                    break;
                }
                catch (SocketException ex)
                {
                    exitReason = $"SocketException: {ex.SocketErrorCode} - {ex.Message}";
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG-C] Exception in receive loop: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"[DEBUG-C] Stack trace: {ex.StackTrace}");
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested)
                exitReason = "CancellationToken requested";

            Console.WriteLine($"[DEBUG-C] Closing audio control socket. Reason: {exitReason}");
            Console.WriteLine($"[DEBUG-C] Final stats: packets={cPacketCount}, 0x56={c56Count}, 0x54={c54Count}, other={cOtherCount}, queued={cQueuedCount}, dequeued={cDequeuedCount}, pcmDelivered={cPcmDelivered}, socketErrors={cSocketErrors}");
        }

        public override async Task OnRawDSocketAsync(Socket dSocket, CancellationToken cancellationToken)
        {
            Console.WriteLine("[DEBUG-D] OnRawDSocketAsync started");

            // Each handler gets its own cipher instance (cipher is stateful, not thread-safe)
            var aesCbcDecrypt = CipherUtilities.GetCipher("AES/CBC/NoPadding");

            // Get current session
            var session = await SessionManager.Current.GetSessionAsync(_sessionId);
            Console.WriteLine($"[DEBUG-D] Session loaded: AesKey={session.AesKey != null}, AesIv={session.AesIv != null}, EcdhShared={session.EcdhShared != null}, KeyMsg={session.KeyMsg != null}, AudioFormat={session.AudioFormat}");

            // If we have not decripted session AesKey
            if (session.DecryptedAesKey == null)
            {
                byte[] decryptedAesKey = new byte[16];
                _omgHax.DecryptAesKey(session.KeyMsg, session.AesKey, decryptedAesKey);
                session.DecryptedAesKey = decryptedAesKey;
                Console.WriteLine("[DEBUG-D] AES key decrypted");
            }

            // Initialize decoder
            InitializeDecoder(session);
            Console.WriteLine($"[DEBUG-D] Decoder initialized: type={_decoder?.Type}, outputLen={_decoder?.GetOutputStreamLength()}");

            await SessionManager.Current.CreateOrUpdateSessionAsync(_sessionId, session);

            var packet = new byte[RAOP_PACKET_LENGTH];
            int dPacketCount = 0;
            int dQueuedCount = 0;
            int dDequeuedCount = 0;
            int dPcmDelivered = 0;
            int dSocketErrors = 0;
            string exitReason = "loop-end";

            Console.WriteLine("[DEBUG-D] Entering receive loop...");

            do
            {
                try
                {
                    var dret = dSocket.Receive(packet, 0, RAOP_PACKET_LENGTH, SocketFlags.None, out SocketError error);
                    if (error != SocketError.Success)
                    {
                        dSocketErrors++;
                        if (dSocketErrors <= 5)
                            Console.WriteLine($"[DEBUG-D] Socket.Receive error: {error}");
                        continue;
                    }

                    dPacketCount++;
                    if (dPacketCount == 1)
                        Console.WriteLine($"[DEBUG-D] First packet received! size={dret}");

                    // RTP payload type
                    int type_d = packet[1] & ~0x80;

                    if (dPacketCount <= 3)
                        Console.WriteLine($"[DEBUG-D] Packet #{dPacketCount}: type=0x{type_d:X2}, size={dret}");

                    if (packet.Length >= 12)
                    {
                        InitAesCbcCipher(aesCbcDecrypt, session.DecryptedAesKey, session.EcdhShared, session.AesIv);

                        // During screen mirroring, skip resend waiting (real-time audio can't wait)
                        bool no_resend = _isMirroring;
                        int buf_ret;
                        byte[] audiobuf;
                        int audiobuflen = 0;
                        uint timestamp = 0;

                        lock (_bufferLock)
                        {
                            buf_ret = RaopBufferQueue(_raopBuffer, packet, (ushort)dret, session, aesCbcDecrypt);
                        }

                        if (dPacketCount <= 3)
                            Console.WriteLine($"[DEBUG-D] RaopBufferQueue returned: {buf_ret}");

                        if (buf_ret > 0) dQueuedCount++;

                        // Dequeue all available frames from buffer
                        var pcmBatch = new System.Collections.Generic.List<PcmData>();
                        lock (_bufferLock)
                        {
                            while ((audiobuf = RaopBufferDequeue(_raopBuffer, ref audiobuflen, ref timestamp, no_resend)) != null)
                            {
                                if (audiobuf.Length == 0 || audiobuflen <= 0)
                                    continue;

                                dDequeuedCount++;
                                var pcmData = new PcmData();
                                pcmData.Length = audiobuflen;
                                pcmData.Data = audiobuf;

                                pcmData.Pts = (ulong)(timestamp - _sync_timestamp) * 1000000UL / 44100 + _sync_time;

                                pcmBatch.Add(pcmData);
                            }
                        }

                        if (dPacketCount <= 3 && pcmBatch.Count > 0)
                            Console.WriteLine($"[DEBUG-D] Dequeued {pcmBatch.Count} PCM frames, first len={pcmBatch[0].Length}");

                        // Deliver PCM outside the lock to avoid blocking the control handler
                        foreach (var pcm in pcmBatch)
                        {
                            _receiver.OnPCMData(pcm);
                            dPcmDelivered++;
                        }

                        /* Handle possible resend requests (not needed during mirroring) */
                        if (!no_resend)
                        {
                            RaopBufferHandleResends(_raopBuffer, _cSocket, _controlSequenceNumber);
                        }
                    }

                    // Log summary periodically
                    if (dPacketCount % 500 == 0)
                    {
                        Console.WriteLine($"[DEBUG-D] Stats: packets={dPacketCount}, queued={dQueuedCount}, dequeued={dDequeuedCount}, pcmDelivered={dPcmDelivered}, socketErrors={dSocketErrors}");
                    }

                    Array.Clear(packet, 0, packet.Length);
                }
                catch (ObjectDisposedException)
                {
                    exitReason = "ObjectDisposedException (socket closed)";
                    break;
                }
                catch (SocketException ex)
                {
                    exitReason = $"SocketException: {ex.SocketErrorCode} - {ex.Message}";
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG-D] Exception in receive loop: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"[DEBUG-D] Stack trace: {ex.StackTrace}");
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested)
                exitReason = "CancellationToken requested";

            Console.WriteLine($"[DEBUG-D] Closing audio data socket. Reason: {exitReason}");
            Console.WriteLine($"[DEBUG-D] Final stats: packets={dPacketCount}, queued={dQueuedCount}, dequeued={dDequeuedCount}, pcmDelivered={dPcmDelivered}, socketErrors={dSocketErrors}");
        }

        public Task FlushAsync(int nextSequence)
        {
            Console.WriteLine($"[DEBUG-FLUSH] FlushAsync called: nextSequence={nextSequence}, isMirroring={_isMirroring}");
            lock (_bufferLock)
            {
                RaopBufferFlush(_raopBuffer, nextSequence);
            }
            _receiver.OnAudioFlush();
            return Task.CompletedTask;
        }

        private void InitAesCbcCipher(IBufferedCipher aesCbcDecrypt, byte[] aesKey, byte[] ecdhShared, byte[] aesIv)
        {
            byte[] hash = Utilities.Hash(aesKey, ecdhShared);
            byte[] eaesKey = Utilities.CopyOfRange(hash, 0, 16);

            var keyParameter = ParameterUtilities.CreateKeyParameter("AES", eaesKey);
            var cipherParameters = new ParametersWithIV(keyParameter, aesIv, 0, aesIv.Length);

            aesCbcDecrypt.Init(false, cipherParameters);
        }

        private RaopBuffer RaopBufferInit()
        {
            // Use max possible decoded PCM size: 1024 samples * 2 channels * 2 bytes (AAC-main)
            var audio_buffer_size = 1024 * 4;
            var raop_buffer = new RaopBuffer();

            raop_buffer.BufferSize = audio_buffer_size * RAOP_BUFFER_LENGTH;
            raop_buffer.Buffer = new byte[raop_buffer.BufferSize];

            for (int i=0; i < RAOP_BUFFER_LENGTH; i++) {
		        var entry = raop_buffer.Entries[i];
                entry.AudioBufferSize = audio_buffer_size;
		        entry.AudioBufferLen = 0;
		        entry.AudioBuffer = new byte[audio_buffer_size];
		        Array.Copy(raop_buffer.Buffer, i * audio_buffer_size, entry.AudioBuffer, 0, audio_buffer_size);

                raop_buffer.Entries[i] = entry;
            }

            raop_buffer.IsEmpty = true;

	        return raop_buffer;
        }

        private int _queueCallCount = 0;

        public int RaopBufferQueue(RaopBuffer raop_buffer, byte[] data, ushort datalen, Session session, IBufferedCipher aesCbcDecrypt)
        {
            int encryptedlen;
            RaopBufferEntry entry;

            _queueCallCount++;

            /* Check packet data length is valid */
            if (datalen < 12 || datalen > RAOP_PACKET_LENGTH)
            {
                if (_queueCallCount <= 5)
                    Console.WriteLine($"[DEBUG-QUEUE] #{_queueCallCount}: REJECT invalid length={datalen}");
                return -1;
            }

            var seqnum = (ushort)((data[2] << 8) | data[3]);
            if (datalen == 16 && data[12] == 0x0 && data[13] == 0x68 && data[14] == 0x34 && data[15] == 0x0)
            {
                if (_queueCallCount <= 5)
                    Console.WriteLine($"[DEBUG-QUEUE] #{_queueCallCount}: no-data marker, seqnum={seqnum}");
                return 0;
            }

            if (_queueCallCount <= 10 || _queueCallCount % 500 == 0)
                Console.WriteLine($"[DEBUG-QUEUE] #{_queueCallCount}: seqnum={seqnum}, datalen={datalen}, payloadSize={datalen - 12}, bufferEmpty={raop_buffer.IsEmpty}, firstSeq={raop_buffer.FirstSeqNum}, lastSeq={raop_buffer.LastSeqNum}");

            // Ignore, old (use wraparound-aware comparison for 16-bit sequence numbers)
            if (!raop_buffer.IsEmpty && SeqBefore(seqnum, raop_buffer.FirstSeqNum))
            {
                if (_queueCallCount <= 10)
                    Console.WriteLine($"[DEBUG-QUEUE] #{_queueCallCount}: SKIP old seqnum={seqnum} < firstSeqNum={raop_buffer.FirstSeqNum}");
                return 0;
            }

            /* Check that there is always space in the buffer, otherwise flush */
            /* Use wraparound-aware gap detection: if seqnum is more than RAOP_BUFFER_LENGTH ahead of FirstSeqNum, flush */
            if (!raop_buffer.IsEmpty && (ushort)(seqnum - raop_buffer.FirstSeqNum) >= RAOP_BUFFER_LENGTH)
            {
                RaopBufferFlush(raop_buffer, seqnum);
            }

            entry = raop_buffer.Entries[seqnum % RAOP_BUFFER_LENGTH];
            if (entry.Available && entry.SeqNum == seqnum)
            {
                /* Packet resent, we can safely ignore */
                return 0;
            }

            entry.Flags = data[0];
            entry.Type = data[1];
            entry.SeqNum = seqnum;

            entry.TimeStamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
            entry.SSrc = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);
            entry.Available = true;

            int payloadsize = datalen - 12;
            var raw = new byte[payloadsize];

            encryptedlen = payloadsize / 16 * 16;

            if (encryptedlen > 0)
            {
                aesCbcDecrypt.ProcessBytes(data, 12, encryptedlen, data, 12);
                Array.Copy(data, 12, raw, 0, encryptedlen);
            }

            Array.Copy(data, 12 + encryptedlen, raw, encryptedlen, payloadsize - encryptedlen);

#if DUMP
            /* RAW -> DUMP */
            var fPath = Path.Combine(_dConfig.Path, "frames/");
            File.WriteAllBytes($"{fPath}raw_{seqnum}", raw);
#endif
            /* RAW -> PCM */
            if (_queueCallCount <= 10 || _queueCallCount % 200 == 0)
            {
                // Log first bytes of decrypted payload for verification
                // Valid AAC-ELD frames start with: 0x8c, 0x8d, 0x8e, 0x80, 0x81, 0x82, 0x20
                var hexDump = raw.Length >= 16 
                    ? BitConverter.ToString(raw, 0, Math.Min(16, raw.Length)) 
                    : BitConverter.ToString(raw);
                Console.WriteLine($"[DEBUG-DECRYPT] #{_queueCallCount}: raw[0]=0x{raw[0]:X2}, len={raw.Length}, first16={hexDump}");
            }
            var length = _decoder.GetOutputStreamLength();
            var output = new byte[length];

            var res = _decoder.DecodeFrame(raw, ref output, length);
            if (res != 0)
            {
                output = new byte[length];
                if (_queueCallCount <= 10 || _queueCallCount % 200 == 0)
                    Console.WriteLine($"[DEBUG-DECODE] #{_queueCallCount}: ERROR decoder={_decoder.Type}, code=0x{res:X} ({res}), inputLen={raw.Length}, outputLen={length}");
            }
            else
            {
                if (_queueCallCount <= 10 || _queueCallCount % 200 == 0)
                {
                    // Check if PCM output is silence
                    bool isSilence = true;
                    for (int i = 0; i < Math.Min(output.Length, 64); i++)
                    {
                        if (output[i] != 0) { isSilence = false; break; }
                    }
                    Console.WriteLine($"[DEBUG-DECODE] #{_queueCallCount}: OK decoder={_decoder.Type}, inputLen={raw.Length}, outputLen={length}, silence={isSilence}");
                }
            }

#if DUMP
            var pPath = Path.Combine(_dConfig.Path, "pcm/");
            Console.WriteLine($"RES: {res}");
            Console.WriteLine($"PCM: {output.Length}");
            Console.WriteLine($"LNG: {length}");
            File.WriteAllBytes($"{pPath}raw_{seqnum}", output);
#endif
            Array.Copy(output, 0, entry.AudioBuffer, 0, output.Length);
            entry.AudioBufferLen = output.Length;

            /* Update the raop_buffer seqnums */
            if (raop_buffer.IsEmpty)
            {
                raop_buffer.FirstSeqNum = seqnum;
                raop_buffer.LastSeqNum = seqnum;
                raop_buffer.IsEmpty = false;
            }

            if (SeqBefore(raop_buffer.LastSeqNum, seqnum))
            {
                raop_buffer.LastSeqNum = seqnum;
            }

            // Update entries
            raop_buffer.Entries[seqnum % RAOP_BUFFER_LENGTH] = entry;

            return 1;
        }

        public byte[] RaopBufferDequeue(RaopBuffer raop_buffer, ref int length, ref uint pts, bool noResend)
        {
            int buflen;
            RaopBufferEntry entry;

            /* Calculate number of entries in the current buffer (use ushort arithmetic to handle wraparound) */
            buflen = (ushort)(raop_buffer.LastSeqNum - raop_buffer.FirstSeqNum + 1);

            /* Cannot dequeue from empty buffer */
            if (raop_buffer.IsEmpty || buflen <= 0)
            {
                return null;
            }

            /* Get the first buffer entry for inspection */
            entry = raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH];
            if (noResend)
            {
                /* If we do no resends, always return the first entry */
                entry.Available = false;

                /* Return entry audio buffer */
                length = entry.AudioBufferLen;
                pts = entry.TimeStamp;
                entry.AudioBufferLen = 0;

                raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH] = entry;
                raop_buffer.FirstSeqNum += 1;

                return entry.AudioBuffer;
            }
            else if (!entry.Available)
            {
                /* Check how much we have space left in the buffer */
                if (buflen < RAOP_BUFFER_LENGTH)
                {
                    /* Entry not yet available, wait for resend - return null to stop dequeue loop */
                    return null;
                }

                /* Risk of buffer overrun, skip this entry and advance to prevent getting stuck */
                entry.AudioBufferLen = 0;
                raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH] = entry;
                raop_buffer.FirstSeqNum += 1;
                return null;
            }

            entry.Available = false;

            /* Return entry audio buffer */
            length = entry.AudioBufferLen;
            pts = entry.TimeStamp;
            entry.AudioBufferLen = 0;

            raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH] = entry;
            raop_buffer.FirstSeqNum += 1;

            var result = new byte[length];
            Array.Copy(entry.AudioBuffer, 0, result, 0, length);
            return result;
        }

        private void RaopBufferFlush(RaopBuffer raop_buffer, int next_seq)
        {
            int i;
            for (i = 0; i < RAOP_BUFFER_LENGTH; i++)
            {
                raop_buffer.Entries[i].Available = false;
                raop_buffer.Entries[i].AudioBufferLen = 0;
            }
            if (next_seq < 0 || next_seq > 0xffff)
            {
                raop_buffer.IsEmpty = true;
            }
            else
            {
                raop_buffer.FirstSeqNum = (ushort)next_seq;
                raop_buffer.LastSeqNum = (ushort)(next_seq - 1);
            }
        }

        private void RaopBufferHandleResends(RaopBuffer raop_buffer, Socket cSocket, ushort control_seqnum)
        {
            RaopBufferEntry entry;

            if (Utilities.SeqNumCmp(raop_buffer.FirstSeqNum, raop_buffer.LastSeqNum) < 0)
            {
                int seqnum, count;

                for (seqnum = raop_buffer.FirstSeqNum; Utilities.SeqNumCmp(seqnum, raop_buffer.LastSeqNum) < 0; seqnum++)
                {
                    entry = raop_buffer.Entries[seqnum % RAOP_BUFFER_LENGTH];
                    if (entry.Available)
                    {
                        break;
                    }
                }
                if (Utilities.SeqNumCmp(seqnum, raop_buffer.FirstSeqNum) == 0)
                {
                    return;
                }
                count = Utilities.SeqNumCmp(seqnum, raop_buffer.FirstSeqNum);
                RaopRtpResendCallback(cSocket, control_seqnum, raop_buffer.FirstSeqNum, (ushort)count);
            }
        }

        private int RaopRtpResendCallback(Socket cSocket, ushort control_seqnum, ushort seqnum, ushort count)
        {
            var packet = new byte[8];
            ushort ourseqnum;

            int ret;
            ourseqnum = control_seqnum++;

            /* Fill the request buffer */
            packet[0] = 0x80;
            packet[1] = 0x55|0x80;
            packet[2] = (byte)(ourseqnum >> 8);
            packet[3] = (byte)ourseqnum;
            packet[4] = (byte)(seqnum >> 8);
            packet[5] = (byte)seqnum;
            packet[6] = (byte)(count >> 8);
            packet[7] = (byte)count;

            ret = cSocket.Send(packet, 0, packet.Length, SocketFlags.None);
            if (ret == -1) {
                Console.WriteLine("Resend packet - failed to send request");
            }

            return 0;
        }

        /// <summary>
        /// Wraparound-aware comparison for 16-bit sequence numbers.
        /// Returns true if s1 is strictly before s2 in the circular sequence space.
        /// When (s1 - s2) interpreted as signed 16-bit is negative, s1 is before s2.
        /// </summary>
        private static bool SeqBefore(ushort s1, ushort s2)
        {
            return ((short)(s1 - s2)) < 0;
        }

        private void InitializeDecoder (Session session)
        {
            lock (_decoderLock)
            {
                if (_decoder != null) return;

                var audioFormat = session.AudioFormat;
                var spf = session.AudioSamplesPerFrame;

                if (audioFormat == AudioFormat.ALAC)
                {
                    // RTP info: 96 AppleLossless, 96 352 0 16 40 10 14 2 255 0 0 44100
                    // (ALAC -> PCM)

                    var frameLength = spf > 0 ? spf : 352;
                    var numChannels = 2;
                    var bitDepth = 16;
                    var sampleRate = 44100;

                    _decoder = new ALACDecoder();
                    _decoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                }
                else if (audioFormat == AudioFormat.AAC)
                {
                    // RTP info: 96 mpeg4-generic/44100/2, 96 mode=AAC-main; constantDuration=1024
                    // (AAC-MAIN -> PCM)

                    var frameLength = spf > 0 ? spf : 1024;
                    var numChannels = 2;
                    var bitDepth = 16;
                    var sampleRate = 44100;

                    _decoder = new AACDecoder(TransportType.TT_MP4_RAW, AudioObjectType.AOT_AAC_MAIN, 1);
                    _decoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                }
                else if(audioFormat == AudioFormat.AAC_ELD)
                {
                    // RTP info: 96 mpeg4-generic/44100/2, 96 mode=AAC-eld; constantDuration=480
                    // (AAC-ELD -> PCM)
                    // Try native FDK-AAC first (direct P/Invoke, no framing needed),
                    // then FFmpeg subprocess with LOAS wrapping as fallback.

                    var frameLength = spf > 0 ? spf : 480;
                    var numChannels = 2;
                    var bitDepth = 16;
                    var sampleRate = 44100;

                    // Try 1: Native FDK-AAC library (same approach as itskenny0/airplayreceiver)
                    try
                    {
                        var nativeDecoder = new Decoders.Implementations.NativeFdkAacEldDecoder();
                        var ret = nativeDecoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                        if (ret == 0)
                        {
                            _decoder = nativeDecoder;
                            Console.WriteLine("[DEBUG] Using native FDK-AAC decoder for AAC-ELD");
                        }
                        else
                        {
                            Console.WriteLine($"[DEBUG] Native FDK-AAC config error: 0x{ret:X}, trying FFmpeg...");
                            nativeDecoder.Dispose();
                        }
                    }
                    catch (DllNotFoundException ex)
                    {
                        Console.WriteLine($"[DEBUG] Native FDK-AAC library not found: {ex.Message}");
                        Console.WriteLine("[DEBUG] To use native decoder, place libfdk-aac-2.dll (Windows) or libfdk-aac.so.2 (Linux) in app directory");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DEBUG] Native FDK-AAC failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    // Try 2: FFmpeg subprocess with LOAS wrapping
                    if (_decoder == null)
                    {
                        try
                        {
                            var ffmpegDecoder = new Decoders.Implementations.FFmpegAacEldDecoder();
                            var ret = ffmpegDecoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                            if (ret == 0)
                            {
                                _decoder = ffmpegDecoder;
                                Console.WriteLine("[DEBUG] Using FFmpeg subprocess decoder for AAC-ELD");
                            }
                            else
                            {
                                Console.WriteLine($"[DEBUG] FFmpeg decoder config failed (error {ret}), falling back to SharpJaad AAC-LC");
                                ffmpegDecoder.Dispose();
                                _decoder = new AACDecoder(TransportType.TT_MP4_RAW, AudioObjectType.AOT_AAC_LC, 1);
                                _decoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DEBUG] FFmpeg decoder unavailable ({ex.Message}), falling back to SharpJaad AAC-LC");
                            _decoder = new AACDecoder(TransportType.TT_MP4_RAW, AudioObjectType.AOT_AAC_LC, 1);
                            _decoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                        }
                    }
                }
                else if (audioFormat == AudioFormat.PCM)
                {
                    // Raw PCM audio - no decoding needed
                    _decoder = new PCMDecoder();
                }
                else
                {
                    // Determine format from compression type if audioFormat is unknown
                    if (session.AudioCompressionType == 1)
                    {
                        // ct=1 = ALAC
                        var frameLength = spf > 0 ? spf : 352;
                        _decoder = new ALACDecoder();
                        _decoder.Config(44100, 2, 16, frameLength);
                    }
                    else if (session.AudioCompressionType == 0)
                    {
                        // ct=0 = PCM
                        _decoder = new PCMDecoder();
                    }
                    else
                    {
                        // Default fallback
                        _decoder = new PCMDecoder();
                    }
                }
            }
        }
    }
}
