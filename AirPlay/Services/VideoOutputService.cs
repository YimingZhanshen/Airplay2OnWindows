using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;

namespace AirPlay.Services
{
    /// <summary>
    /// Video output service that writes H.264 Annex B data to a named pipe.
    /// Automatically launches ffplay when mirroring starts and kills it when mirroring stops.
    /// Supports repeated start/stop cycles without requiring a program restart.
    /// </summary>
    public class VideoOutputService : IDisposable
    {
        private const string PIPE_NAME = "AirPlayVideo";
        private const string UNIX_PIPE_PATH = "/tmp/airplay_video";

        private NamedPipeServerStream _pipeServer;
        private FileStream _unixPipeStream;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private bool _connected = false;
        private long _frameCount = 0;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private Process _ffplayProcess;

        public event EventHandler<string> OnStatusChanged;

        /// <summary>
        /// Start a new mirroring session: create the pipe, launch ffplay, and wait for connection.
        /// Can be called multiple times across mirroring sessions.
        /// </summary>
        public void StartMirroring()
        {
            lock (_lock)
            {
                if (_disposed) return;

                // Clean up any previous session
                CleanupSession();

                _cts = new CancellationTokenSource();
                _frameCount = 0;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    StartWindowsSession();
                }
                else
                {
                    StartUnixSession();
                }
            }
        }

        /// <summary>
        /// Stop the current mirroring session: kill ffplay and clean up the pipe.
        /// </summary>
        public void StopMirroring()
        {
            lock (_lock)
            {
                Console.WriteLine($"Mirroring stopped ({_frameCount} frames written)");
                CleanupSession();
            }
        }

        private void StartWindowsSession()
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

                // Launch ffplay to connect to the pipe
                LaunchFfplay($"\\\\.\\pipe\\{PIPE_NAME}");

                // Wait for ffplay to connect in background
                _acceptTask = Task.Run(() => WaitForPipeConnection(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start video session: {ex.Message}");
            }
        }

        private void StartUnixSession()
        {
            try
            {
                if (File.Exists(UNIX_PIPE_PATH))
                {
                    File.Delete(UNIX_PIPE_PATH);
                }

                var mkfifo = Process.Start(new ProcessStartInfo
                {
                    FileName = "mkfifo",
                    Arguments = UNIX_PIPE_PATH,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                mkfifo?.WaitForExit();

                Console.WriteLine($"Video FIFO created: {UNIX_PIPE_PATH}");

                // Launch ffplay to read from the FIFO
                LaunchFfplay(UNIX_PIPE_PATH);

                // Open the FIFO for writing (blocks until reader connects)
                _acceptTask = Task.Run(() => WaitForUnixPipeConnection(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start video session: {ex.Message}");
            }
        }

        private void LaunchFfplay(string pipePath)
        {
            try
            {
                var ffplayPath = FindFfplay();
                if (ffplayPath == null)
                {
                    Console.WriteLine("ffplay not found. Please ensure ffplay is in the application directory or PATH.");
                    Console.WriteLine($"  You can manually connect with: ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay {pipePath}");
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffplayPath,
                    Arguments = $"-f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay \"{pipePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                _ffplayProcess = Process.Start(psi);
                if (_ffplayProcess != null)
                {
                    Console.WriteLine($"ffplay launched (PID: {_ffplayProcess.Id})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch ffplay: {ex.Message}");
                Console.WriteLine($"  You can manually connect with: ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay {pipePath}");
            }
        }

        private string FindFfplay()
        {
            // Check application directory first
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var ffplayName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffplay.exe" : "ffplay";
            var localPath = Path.Combine(appDir, ffplayName);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            // Fall back to PATH
            try
            {
                var whichCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
                var psi = new ProcessStartInfo
                {
                    FileName = whichCmd,
                    Arguments = "ffplay",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                var output = proc?.StandardOutput.ReadToEnd()?.Trim();
                proc?.WaitForExit();
                var firstLine = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (proc?.ExitCode == 0 && !string.IsNullOrWhiteSpace(firstLine))
                {
                    return firstLine;
                }
            }
            catch { }

            return null;
        }

        private void StopFfplay()
        {
            if (_ffplayProcess != null)
            {
                try
                {
                    if (!_ffplayProcess.HasExited)
                    {
                        Console.WriteLine($"Stopping ffplay (PID: {_ffplayProcess.Id})...");
                        _ffplayProcess.Kill();
                        if (!_ffplayProcess.WaitForExit(3000))
                        {
                            Console.WriteLine("Warning: ffplay did not exit within timeout");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping ffplay: {ex.Message}");
                }
                finally
                {
                    _ffplayProcess.Dispose();
                    _ffplayProcess = null;
                }
            }
        }

        private void CleanupSession()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _connected = false;

            try { _pipeServer?.Dispose(); } catch { }
            _pipeServer = null;

            try { _unixPipeStream?.Dispose(); } catch { }
            _unixPipeStream = null;

            StopFfplay();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(UNIX_PIPE_PATH))
            {
                try { File.Delete(UNIX_PIPE_PATH); } catch { }
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
                    Console.WriteLine("Video player disconnected");
                    _connected = false;
                    OnStatusChanged?.Invoke(this, "disconnected");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Video write error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                CleanupSession();

                Console.WriteLine("Video output service disposed");
            }
        }
    }
}
