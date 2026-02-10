using System;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;

namespace AirPlay
{
    public interface IAirPlayReceiver
    {
        event EventHandler<decimal> OnSetVolumeReceived;
        event EventHandler<H264Data> OnH264DataReceived;
        event EventHandler<PcmData> OnPCMDataReceived;
        event EventHandler OnAudioFlushReceived;
        event EventHandler OnMirroringStartedReceived;
        event EventHandler OnMirroringStoppedReceived;

        Task StartListeners(CancellationToken cancellationToken);

        Task StartMdnsAsync();
    }
}
