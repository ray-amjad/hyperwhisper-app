namespace HyperWhisper.Platform.Abstractions;

public enum NativeComputeBackend
{
    Cpu,
    Cuda,
    Vulkan
}

public sealed record NativeRuntimeCapabilities(
    string RuntimeIdentifier,
    string Architecture,
    bool SupportsWhisper,
    bool SupportsParakeet,
    bool SupportsLocalLlm,
    IReadOnlySet<NativeComputeBackend> ComputeBackends);

/// <summary>
/// Locates shipped native payloads without embedding platform extensions or
/// directory layouts in shared services.
/// </summary>
public interface INativeRuntimeLocator
{
    NativeRuntimeCapabilities Capabilities { get; }
    PlatformResult<string> FindLibrary(string component, NativeComputeBackend backend);
    PlatformResult<string> FindExecutable(string component);
}

public sealed record ChildProcessStartRequest
{
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();
    public bool RedirectStandardInput { get; init; }
    public bool RedirectStandardOutput { get; init; }
    public bool RedirectStandardError { get; init; }
}

public interface IChildProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Stream? StandardInput { get; }
    Stream? StandardOutput { get; }
    Stream? StandardError { get; }

    ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    ValueTask TerminateAsync(CancellationToken cancellationToken = default);
}

public interface IChildProcessLauncher
{
    PlatformResult<IChildProcess> Start(ChildProcessStartRequest request);
}
