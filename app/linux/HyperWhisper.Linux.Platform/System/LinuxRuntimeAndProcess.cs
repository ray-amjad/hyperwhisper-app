using System.Diagnostics;
using System.Runtime.InteropServices;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.SystemIntegration;

public sealed class LinuxNativeRuntimeLocator : INativeRuntimeLocator
{
    private readonly string _root;
    private readonly string _rid;
    public LinuxNativeRuntimeLocator() : this(AppContext.BaseDirectory) { }
    internal LinuxNativeRuntimeLocator(string root, Architecture? architecture = null)
    {
        _root = Path.GetFullPath(root);
        var selected = architecture ?? RuntimeInformation.ProcessArchitecture;
        _rid = selected == Architecture.X64 ? "linux-x64" : selected == Architecture.Arm64 ? "linux-arm64" : $"linux-{selected.ToString().ToLowerInvariant()}";
        var backends = new HashSet<NativeComputeBackend>();
        if (Candidates("whisper", NativeComputeBackend.Cpu).Concat(Candidates("llama", NativeComputeBackend.Cpu)).Any(File.Exists)) backends.Add(NativeComputeBackend.Cpu);
        if (Candidates("whisper", NativeComputeBackend.Vulkan).Concat(Candidates("llama", NativeComputeBackend.Vulkan)).Any(File.Exists)) backends.Add(NativeComputeBackend.Vulkan);
        if (Candidates("llama", NativeComputeBackend.Cuda).Any(File.Exists)) backends.Add(NativeComputeBackend.Cuda);
        Capabilities = new(_rid, selected.ToString(), Candidates("whisper", NativeComputeBackend.Cpu).Any(File.Exists),
            ExecutableCandidates("parakeet").Any(IsExecutable), Candidates("llama", NativeComputeBackend.Cpu).Any(File.Exists), backends);
    }
    public NativeRuntimeCapabilities Capabilities { get; }
    public PlatformResult<string> FindLibrary(string component, NativeComputeBackend backend)
    {
        Validate(component);
        var found = Candidates(component.Trim().ToLowerInvariant(), backend).FirstOrDefault(File.Exists);
        return found is null ? PlatformResult<string>.Failure("native_runtime.library_not_found", "The requested packaged Linux native library is unavailable.")
            : PlatformResult<string>.Success(Path.GetFullPath(found));
    }
    public PlatformResult<string> FindExecutable(string component)
    {
        Validate(component);
        var found = ExecutableCandidates(component.Trim().ToLowerInvariant()).FirstOrDefault(IsExecutable);
        return found is null ? PlatformResult<string>.Failure("native_runtime.executable_not_found", "The requested packaged Linux executable is unavailable.")
            : PlatformResult<string>.Success(Path.GetFullPath(found));
    }
    private IEnumerable<string> Candidates(string component, NativeComputeBackend backend)
    {
        var name = component.Replace('-', '_');
        var file = name is "llama" ? "libllama.so" : name is "whisper" ? "libwhisper.so" : $"lib{name}.so";
        var flavor = backend switch { NativeComputeBackend.Vulkan => "vulkan", NativeComputeBackend.Cuda => "cuda12", _ => "noavx" };
        if (component == "whisper")
        {
            if (backend == NativeComputeBackend.Cpu) yield return Path.Combine(_root, "runtimes", _rid, file);
            yield return Path.Combine(_root, "runtimes", backend == NativeComputeBackend.Vulkan ? "vulkan" : "cpu", _rid, file);
            if (backend == NativeComputeBackend.Cpu) yield return Path.Combine(_root, "runtimes", "noavx", _rid, file);
            yield return Path.Combine(_root, "runtimes", _rid, "native", backend == NativeComputeBackend.Cpu ? file : Path.Combine(flavor, file));
        }
        else
        {
            if (backend == NativeComputeBackend.Cpu)
            {
                foreach (var cpuFlavor in CpuFlavors()) yield return Path.Combine(_root, "runtimes", _rid, "native", cpuFlavor, file);
            }
            else yield return Path.Combine(_root, "runtimes", _rid, "native", flavor, file);
            if (backend == NativeComputeBackend.Cpu) yield return Path.Combine(_root, "runtimes", _rid, "native", file);
        }
    }
    private static IEnumerable<string> CpuFlavors()
    {
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) yield return "avx512";
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) yield return "avx2";
        if (System.Runtime.Intrinsics.X86.Avx.IsSupported) yield return "avx";
        yield return "noavx";
    }
    private IEnumerable<string> ExecutableCandidates(string component)
    {
        var name = component is "parakeet" or "parakeet-engine" ? "parakeet-engine" : component;
        yield return Path.Combine(_root, name, name);
        yield return Path.Combine(_root, "Resources", name, _rid.Replace("linux-", string.Empty), name);
        yield return Path.Combine(_root, name);
    }
    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch { return false; }
    }
    private static void Validate(string component)
    {
        if (string.IsNullOrWhiteSpace(component) || component.Contains("..", StringComparison.Ordinal)
            || component.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A native component must be a simple name.", nameof(component));
    }
}

public sealed class LinuxChildProcessLauncher : IChildProcessLauncher
{
    public PlatformResult<IChildProcess> Start(ChildProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Process? process = null;
        try
        {
            if (!Path.IsPathFullyQualified(request.ExecutablePath) || !File.Exists(request.ExecutablePath))
                return PlatformResult<IChildProcess>.Failure("process.invalid_executable", "A fully qualified existing executable is required.");
            var working = request.WorkingDirectory is null ? Path.GetDirectoryName(request.ExecutablePath)! : Path.GetFullPath(request.WorkingDirectory);
            var start = new ProcessStartInfo(Path.GetFullPath(request.ExecutablePath))
            {
                UseShellExecute = false, WorkingDirectory = working, RedirectStandardInput = request.RedirectStandardInput,
                RedirectStandardOutput = request.RedirectStandardOutput, RedirectStandardError = request.RedirectStandardError,
            };
            foreach (var argument in request.Arguments)
            {
                if (argument.Contains('\0')) throw new ArgumentException("Process arguments cannot contain NUL.", nameof(request));
                start.ArgumentList.Add(argument);
            }
            foreach (var pair in request.Environment)
            {
                if (pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0')) throw new ArgumentException("Invalid environment name.", nameof(request));
                if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value;
            }
            process = Process.Start(start);
            return process is null ? PlatformResult<IChildProcess>.Failure("process.start_failed", "The Linux child process did not start.")
                : PlatformResult<IChildProcess>.Success(new LinuxChildProcess(process));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { process?.Dispose(); return PlatformResult<IChildProcess>.Failure("process.start_failed", "The Linux child process could not start."); }
    }
}

internal sealed class LinuxChildProcess(Process process) : IChildProcess
{
    private Process? _process = process;
    private Process Process => _process ?? throw new ObjectDisposedException(nameof(LinuxChildProcess));
    public int Id => Process.Id;
    public bool HasExited => Process.HasExited;
    public int? ExitCode => HasExited ? Process.ExitCode : null;
    public Stream? StandardInput => Process.StartInfo.RedirectStandardInput ? Process.StandardInput.BaseStream : null;
    public Stream? StandardOutput => Process.StartInfo.RedirectStandardOutput ? Process.StandardOutput.BaseStream : null;
    public Stream? StandardError => Process.StartInfo.RedirectStandardError ? Process.StandardError.BaseStream : null;
    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    { var value = Process; await value.WaitForExitAsync(cancellationToken); return value.ExitCode; }
    public async ValueTask TerminateAsync(CancellationToken cancellationToken = default)
    { var value = Process; if (!value.HasExited) value.Kill(entireProcessTree: true); await value.WaitForExitAsync(cancellationToken); }
    public async ValueTask DisposeAsync()
    {
        var value = Interlocked.Exchange(ref _process, null);
        if (value is null) return;
        try { if (!value.HasExited) { value.Kill(entireProcessTree: true); await value.WaitForExitAsync(); } } catch { }
        value.Dispose();
    }
}
