using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

/// <summary>
/// Resolves the payload layout produced by the Windows project. Whisper uses
/// DirectCompute on Windows; the portable backend enum has no DirectCompute value,
/// so this locator exposes that library through Cpu without claiming CUDA/Vulkan.
/// </summary>
public sealed class WindowsNativeRuntimeLocator : PlatformContracts.INativeRuntimeLocator
{
    private readonly string _baseDirectory;
    private readonly string _architectureDirectory;

    public WindowsNativeRuntimeLocator() : this(AppContext.BaseDirectory) { }

    internal WindowsNativeRuntimeLocator(string baseDirectory, Architecture? architecture = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("A runtime base directory is required.", nameof(baseDirectory));
        _baseDirectory = Path.GetFullPath(baseDirectory);
        var selectedArchitecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        _architectureDirectory = selectedArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => selectedArchitecture.ToString().ToLowerInvariant()
        };
        var rid = $"win-{_architectureDirectory}";
        Capabilities = new PlatformContracts.NativeRuntimeCapabilities(
            rid,
            selectedArchitecture.ToString(),
            SupportsWhisper: true,
            SupportsParakeet: _architectureDirectory is "x64" or "arm64",
            SupportsLocalLlm: _architectureDirectory == "x64",
            ComputeBackends: new HashSet<PlatformContracts.NativeComputeBackend>
            {
                PlatformContracts.NativeComputeBackend.Cpu
            });
    }

    public PlatformContracts.NativeRuntimeCapabilities Capabilities { get; }

    public PlatformContracts.PlatformResult<string> FindLibrary(
        string component,
        PlatformContracts.NativeComputeBackend backend)
    {
        ValidateComponent(component);
        if (backend != PlatformContracts.NativeComputeBackend.Cpu)
            return PlatformContracts.PlatformResult<string>.Failure(
                "native_runtime.unsupported_backend",
                "The Windows adapter does not expose DirectCompute as CUDA or Vulkan.");

        var normalized = component.Trim().ToLowerInvariant();
        var candidates = normalized switch
        {
            "hyperwhisper_core" or "hyperwhisper-core" => new[]
            {
                Path.Combine(_baseDirectory, "hyperwhisper_core.dll")
            },
            "whisper" => new[]
            {
                Path.Combine(_baseDirectory, "runtimes", $"win-{_architectureDirectory}", "native", "whisper.dll")
            },
            _ => new[]
            {
                Path.Combine(_baseDirectory, "runtimes", $"win-{_architectureDirectory}", "native", normalized + ".dll")
            }
        };
        return FindExisting(candidates, "native_runtime.library_not_found", "The requested Windows native library is not installed.");
    }

    public PlatformContracts.PlatformResult<string> FindExecutable(string component)
    {
        ValidateComponent(component);
        var normalized = component.Trim().ToLowerInvariant();
        var candidates = normalized switch
        {
            "parakeet" or "parakeet-engine" => new[]
            {
                Path.Combine(_baseDirectory, "parakeet-engine", "parakeet-engine.exe"),
                Path.Combine(_baseDirectory, "Resources", "parakeet-engine", _architectureDirectory, "parakeet-engine.exe")
            },
            _ => new[] { Path.Combine(_baseDirectory, normalized + ".exe") }
        };
        return FindExisting(candidates, "native_runtime.executable_not_found", "The requested Windows native executable is not installed.");
    }

    private static PlatformContracts.PlatformResult<string> FindExisting(
        IEnumerable<string> candidates,
        string errorCode,
        string errorMessage)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return PlatformContracts.PlatformResult<string>.Success(Path.GetFullPath(candidate));
        }
        return PlatformContracts.PlatformResult<string>.Failure(errorCode, errorMessage);
    }

    private static void ValidateComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component))
            throw new ArgumentException("A native component name is required.", nameof(component));
        if (component.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0
            || component.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("A native component must be a simple name.", nameof(component));
    }
}

public sealed class WindowsChildProcessLauncher : PlatformContracts.IChildProcessLauncher
{
    public PlatformContracts.PlatformResult<PlatformContracts.IChildProcess> Start(
        PlatformContracts.ChildProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new ArgumentException("An executable path is required.", nameof(request));

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(request.ExecutablePath),
                WorkingDirectory = request.WorkingDirectory == null
                    ? Path.GetDirectoryName(Path.GetFullPath(request.ExecutablePath))!
                    : Path.GetFullPath(request.WorkingDirectory),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = request.RedirectStandardInput,
                RedirectStandardOutput = request.RedirectStandardOutput,
                RedirectStandardError = request.RedirectStandardError
            };
            foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
            foreach (var variable in request.Environment)
            {
                if (variable.Value == null)
                    startInfo.Environment.Remove(variable.Key);
                else
                    startInfo.Environment[variable.Key] = variable.Value;
            }

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return PlatformContracts.PlatformResult<PlatformContracts.IChildProcess>.Failure(
                    "process.start_failed", "Windows did not start the child process.");
            return PlatformContracts.PlatformResult<PlatformContracts.IChildProcess>.Success(
                new WindowsChildProcess(process));
        }
        catch (Exception ex)
        {
            process?.Dispose();
            LoggingService.Error("WindowsChildProcessLauncher: process start failed", ex);
            return PlatformContracts.PlatformResult<PlatformContracts.IChildProcess>.Failure(
                "process.start_failed", "Windows could not start the child process.");
        }
    }
}

internal sealed class WindowsChildProcess : PlatformContracts.IChildProcess
{
    private Process? _process;

    internal WindowsChildProcess(Process process)
        => _process = process ?? throw new ArgumentNullException(nameof(process));

    private Process Process => _process ?? throw new ObjectDisposedException(nameof(WindowsChildProcess));
    public int Id => Process.Id;
    public bool HasExited => Process.HasExited;
    public int? ExitCode => Process.HasExited ? Process.ExitCode : null;
    public Stream? StandardInput => Process.StartInfo.RedirectStandardInput ? Process.StandardInput.BaseStream : null;
    public Stream? StandardOutput => Process.StartInfo.RedirectStandardOutput ? Process.StandardOutput.BaseStream : null;
    public Stream? StandardError => Process.StartInfo.RedirectStandardError ? Process.StandardError.BaseStream : null;

    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        var process = Process;
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    public async ValueTask TerminateAsync(CancellationToken cancellationToken = default)
    {
        var process = Process;
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"WindowsChildProcess: cleanup failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}
