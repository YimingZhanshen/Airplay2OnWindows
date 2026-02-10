using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlay.Listeners
{
    public class BaseUdpListener : BaseListener
    {
        public const int CloseTimeout = 1000;

        private readonly Socket _cSocket;
        private readonly Socket _dSocket;
        private readonly ushort _cPort;
        private readonly ushort _dPort;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public BaseUdpListener(ushort cPort, ushort dPort)
        {
            _cPort = cPort;
            _dPort = dPort;
            _cSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            _dSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);

            _cSocket.Bind(new IPEndPoint(IPAddress.Any, cPort));
            _dSocket.Bind(new IPEndPoint(IPAddress.Any, dPort));

            _cancellationTokenSource = new CancellationTokenSource();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);

            Console.WriteLine($"[DEBUG-UDP] StartAsync: cSocket on port {_cPort}, dSocket on port {_dPort}");
            Task.Run(() => OnRawCSocketAsync(_cSocket, source.Token), source.Token);
            Task.Run(() => OnRawDSocketAsync(_dSocket, source.Token), source.Token);

            return Task.CompletedTask;
        }

        public override Task StopAsync()
        {
            Console.WriteLine("[DEBUG-UDP] StopAsync called - cancelling tokens and closing sockets");
            _cancellationTokenSource.Cancel();

            _cSocket.Close(CloseTimeout);
            _dSocket.Close(CloseTimeout);
            Console.WriteLine("[DEBUG-UDP] StopAsync completed");

            return Task.CompletedTask;
        }

        public virtual Task OnRawCSocketAsync(Socket cSocket, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnRawDSocketAsync(Socket dSocket, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
