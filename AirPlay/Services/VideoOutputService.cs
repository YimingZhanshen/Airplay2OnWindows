using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;

namespace AirPlay.Services
{
    /// <summary>
    /// Video output service that writes H.264 Annex B data to a named pipe.
    /// External video players (e.g. ffplay, mpv, vlc) can connect to the pipe
    /// to display the AirPlay mirrored screen in real-time.
    ///
    /// Usage on Windows:
    ///   ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay \\.\pipe\AirPlayVideo
    /// Usage on Linux:
    ///   ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay /tmp/airplay_video
    /// </summary>
    public class VideoOutputService : IDisposable
    {
        private const string PIPE_NAME = "AirPlayVideo";
        private const string UNIX_PIPE_PATH = "/tmp/airplay_video";
        private const int CONNECT_TIMEOUT_MS = 100;

        private NamedPipeServerStream _pipeServer;
        private FileStream _unixPipeStream;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private bool _connected = false;
        private long _frameCount = 0;
        private CancellationTokenSource _cts;
        private Task _acceptTask;

        public event EventHandler<string> OnStatusChanged;

        /// <summary>
        /// Initialize the video output pipe.
        /// </summary>
        public void Initialize()
        {
            lock (_lock)
            {
                if (_disposed) return;

                _cts = new CancellationTokenSource();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    InitializeWindowsPipe();
                }
                else
                {
                    InitializeUnixPipe();
                }
            }
        }

        private void InitializeWindowsPipe()
        {
            try
            {
                _pipeServer = new NamedPipeServerStream(
                    PIPE_NAME,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Console.WriteLine($"Video pipe created: \\\\.\\pipe\\{PIPE_NAME}");
                Console.WriteLine($"  Connect with: ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay \\\\.\\pipe\\{PIPE_NAME}");

                // Start waiting for client connection in background
                _acceptTask = Task.Run(() => WaitForPipeConnection(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create video pipe: {ex.Message}");
            }
        }

        private void InitializeUnixPipe()
        {
            try
            {
                // Create a FIFO (named pipe) on Unix
                if (File.Exists(UNIX_PIPE_PATH))
                {
                    File.Delete(UNIX_PIPE_PATH);
                }

                // Use mkfifo to create a named pipe
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "mkfifo",
                    Arguments = UNIX_PIPE_PATH,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();

                Console.WriteLine($"Video FIFO created: {UNIX_PIPE_PATH}");
                Console.WriteLine($"  Connect with: ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay {UNIX_PIPE_PATH}");

                // Open the FIFO in a background task (blocks until reader connects)
                _acceptTask = Task.Run(() => WaitForUnixPipeConnection(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create video FIFO: {ex.Message}");
            }
        }

        private async Task WaitForPipeConnection(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine("Waiting for video player to connect...");
                    await _pipeServer.WaitForConnectionAsync(token);

                    lock (_lock)
                    {
                        _connected = true;
                    }
                    Console.WriteLine("Video player connected!");
                    OnStatusChanged?.Invoke(this, "connected");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pipe connection error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
        }

        private void WaitForUnixPipeConnection(CancellationToken token)
        {
            try
            {
                // This will block until a reader connects
                _unixPipeStream = new FileStream(UNIX_PIPE_PATH, FileMode.Open, FileAccess.Write);
                lock (_lock)
                {
                    _connected = true;
                }
                Console.WriteLine("Video player connected to FIFO!");
                OnStatusChanged?.Invoke(this, "connected");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"FIFO connection error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Write H.264 frame data to the pipe.
        /// </summary>
        public void WriteFrame(H264Data data)
        {
            lock (_lock)
            {
                if (_disposed || !_connected) return;
                if (data.Data == null || data.Length <= 0) return;

                try
                {
                    bool isKeyFrame = data.FrameType == 5;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (_pipeServer != null && _pipeServer.IsConnected)
                        {
                            _pipeServer.Write(data.Data, 0, data.Length);
                            if (isKeyFrame) _pipeServer.Flush();
                        }
                    }
                    else
                    {
                        if (_unixPipeStream != null && _unixPipeStream.CanWrite)
                        {
                            _unixPipeStream.Write(data.Data, 0, data.Length);
                            if (isKeyFrame) _unixPipeStream.Flush();
                        }
                    }

                    _frameCount++;

                    if (_frameCount % 300 == 0)
                    {
                        Console.WriteLine($"Video: {_frameCount} frames written | Type: {(data.FrameType == 5 ? "IDR" : "P")} | {data.Width}x{data.Height}");
                    }
                }
                catch (IOException)
                {
                    // Pipe broken — reader disconnected
                    Console.WriteLine("Video player disconnected");
                    _connected = false;
                    HandleDisconnect();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Video write error: {ex.Message}");
                }
            }
        }

        private void HandleDisconnect()
        {
            OnStatusChanged?.Invoke(this, "disconnected");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    _pipeServer?.Disconnect();
                }
                catch { }

                // Restart waiting for new connection
                _acceptTask = Task.Run(() => WaitForPipeConnection(_cts.Token));
            }
        }

        /// <summary>
        /// Handle a mirroring session stop/flush.
        /// </summary>
        public void HandleFlush()
        {
            lock (_lock)
            {
                _frameCount = 0;
                Console.WriteLine("Video output flushed");
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                try
                {
                    _pipeServer?.Dispose();
                }
                catch { }

                try
                {
                    _unixPipeStream?.Dispose();
                }
                catch { }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(UNIX_PIPE_PATH))
                {
                    try { File.Delete(UNIX_PIPE_PATH); } catch { }
                }

                _pipeServer = null;
                _unixPipeStream = null;

                Console.WriteLine($"Video disposed ({_frameCount} frames total)");
            }
        }
    }
}
