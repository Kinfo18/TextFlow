using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using TextFlow.Contracts.Voice;
using TextFlow.Core.Voice;

namespace TextFlow.Infrastructure.Voice;

/// <summary>
/// Starts <c>TextFlow.VoiceWorker.exe</c> (V0.2 H1.1) and connects to it over a named pipe that TextFlow creates:
/// a new random name per worker, and only the current user may open it. The worker joins a kill-on-close job.
/// </summary>
public sealed class NamedPipeVoiceWorkerLauncher : IVoiceWorkerLauncher, IDisposable
{
    public const string WorkerFileName = "TextFlow.VoiceWorker.exe";

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly string _workerPath;
    private readonly TimeSpan _connectTimeout;
    private readonly KillOnCloseJob _job = new();

    public NamedPipeVoiceWorkerLauncher(string? workerPath = null, TimeSpan? connectTimeout = null)
    {
        _workerPath = workerPath ?? DefaultWorkerPath;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    /// <summary>Next to TextFlow.exe: the build and the installer put it there.</summary>
    public static string DefaultWorkerPath => Path.Combine(AppContext.BaseDirectory, WorkerFileName);

    public async Task<IVoiceWorkerConnection> LaunchAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_workerPath))
        {
            throw new FileNotFoundException("Voice worker not found.", _workerPath);
        }

        var pipeName = $"TextFlow.Voice.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            process = StartWorker(pipeName);
            var connected = pipe.WaitForConnectionAsync(cancellationToken);
            var exited = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(connected, exited).WaitAsync(_connectTimeout, cancellationToken).ConfigureAwait(false);
            if (first != connected)
            {
                throw new IOException($"The voice worker exited before connecting (code {process.ExitCode}).");
            }

            await connected.ConfigureAwait(false);
            return new Connection(process, pipe);
        }
        catch
        {
            if (process is not null)
            {
                Connection.TryKill(process);
                process.Dispose();
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => _job.Dispose();

    private Process StartWorker(string pipeName)
    {
        var start = new ProcessStartInfo(_workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
            ArgumentList =
            {
                VoiceProtocol.PipeArgument, pipeName,
                VoiceProtocol.ParentArgument, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        var process = Process.Start(start) ?? throw new IOException("The voice worker did not start.");
        try
        {
            _job.Add(process);
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // Died at once: LaunchAsync reports it through the exit check.
        }
        catch
        {
            // Not in the job, so it would outlive a TextFlow crash: never leave it running.
            Connection.TryKill(process);
            process.Dispose();
            throw;
        }

        return process;
    }

    private sealed class Connection : IVoiceWorkerConnection
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _pipe;

        public Connection(Process process, NamedPipeServerStream pipe)
        {
            _process = process;
            _pipe = pipe;
            ProcessId = process.Id;
            Exited = WaitForExitCodeAsync(process);
        }

        public int ProcessId { get; }

        public Stream Stream => _pipe;

        public Task<int> Exited { get; }

        public void Kill() => TryKill(_process);

        public async ValueTask DisposeAsync()
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
        }

        internal static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        private static async Task<int> WaitForExitCodeAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1; // Process disposed before it was seen to exit
            }
        }
    }
}
