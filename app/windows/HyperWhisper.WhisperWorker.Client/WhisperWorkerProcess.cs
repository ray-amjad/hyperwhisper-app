using System.Diagnostics;
using System.Text;

namespace HyperWhisper.WhisperWorker;

/// <summary>One running worker, as the client sees it: two text pipes and an exit.</summary>
public interface IWhisperWorkerProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>The next stdout line, or null at end of stream (the worker exited).</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Closes stdin. The worker exits on EOF.</summary>
    void CloseInput();

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill();
}

/// <summary>Starts a worker. The real one runs the exe; tests hand in a fake.</summary>
public interface IWhisperWorkerLauncher
{
    IWhisperWorkerProcess Start();
}

/// <summary>
/// Starts <c>HyperWhisper.WhisperWorker.exe</c> the way the Parakeet daemon is
/// started: no window, UTF-8 pipes with no BOM, the exe's own folder as the
/// working directory so its native runtimes resolve, and stderr forwarded to
/// the log line by line.
/// </summary>
public sealed class WhisperWorkerProcessLauncher : IWhisperWorkerLauncher
{
    private readonly string _exePath;
    private readonly Action<string>? _stderr;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    public WhisperWorkerProcessLauncher(
        string exePath,
        Action<string>? stderr = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        _exePath = exePath;
        _stderr = stderr;
        _environment = environment;
    }

    public string ExePath => _exePath;

    public IWhisperWorkerProcess Start()
    {
        if (!File.Exists(_exePath))
        {
            throw new WhisperWorkerException(
                WhisperWorkerErrorCodes.StartFailed,
                $"The Whisper worker is missing: {Path.GetFileName(_exePath)} was not found next to the app.");
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        if (_environment is not null)
        {
            foreach (var (key, value) in _environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned no process.");
        }
        catch (Exception exception) when (exception is not WhisperWorkerException)
        {
            throw new WhisperWorkerException(
                WhisperWorkerErrorCodes.StartFailed,
                $"The Whisper worker could not be started: {exception.Message}",
                exception);
        }

        return new RealProcess(process, _stderr);
    }

    private sealed class RealProcess : IWhisperWorkerProcess
    {
        private readonly Process _process;
        private readonly Task _stderrPump;
        private int _inputClosed;

        public RealProcess(Process process, Action<string>? stderr)
        {
            _process = process;
            Id = process.Id;
            _stderrPump = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        // One failed callback must not end the pump: an undrained
                        // stderr pipe fills and blocks the worker mid-request.
                        try
                        {
                            stderr?.Invoke(line);
                        }
                        catch
                        {
                            // The line is lost; keep draining.
                        }
                    }
                }
                catch
                {
                    // The pipe closes with the process.
                }
            });
        }

        public int Id { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _inputClosed) != 0)
            {
                throw new IOException("The worker's input is closed.");
            }
            var input = _process.StandardInput;
            await input.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void CloseInput()
        {
            if (Interlocked.Exchange(ref _inputClosed, 1) != 0) return;
            try { _process.StandardInput.Close(); }
            catch { /* the worker is gone already */ }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
        }

        public void Dispose()
        {
            CloseInput();
            // Bounded: the pump ends when the pipe closes, which a killed or exited
            // process has done; never hang a dispose on it.
            _stderrPump.Wait(TimeSpan.FromSeconds(1));
            _process.Dispose();
        }
    }
}
