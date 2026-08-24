using System.Text.Json;

namespace HyperWhisper.Diagnostics;

public sealed class PrivacySafeRotatingLogger : IDisposable
{
    private readonly string _directory;
    private readonly string _currentPath;
    private readonly int _maxFileBytes;
    private readonly int _maxFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public PrivacySafeRotatingLogger(string directory, int maxFileBytes = 256 * 1024, int maxFiles = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxFileBytes is < 256 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (maxFiles is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        _directory = Path.GetFullPath(directory);
        _currentPath = Path.Combine(_directory, "diagnostics.log");
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
    }

    public async Task<DiagnosticWriteResult> WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                OwnerOnlyPermissions.CreateDirectory(_directory);
                var line = JsonSerializer.Serialize(diagnosticEvent, DiagnosticsJson.Context.DiagnosticEvent) + "\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                if (bytes.Length > _maxFileBytes) return DiagnosticWriteResult.Fail(DiagnosticFailure.LogUnavailable);
                var length = File.Exists(_currentPath) ? new FileInfo(_currentPath).Length : 0;
                if (length + bytes.Length > _maxFileBytes) Rotate();
                await using var stream = new FileStream(_currentPath, OwnerOnlyPermissions.CreateFileOptions(
                    FileMode.Append, FileAccess.Write, FileShare.Read));
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                OwnerOnlyPermissions.ApplyFile(_currentPath);
                return DiagnosticWriteResult.Ok;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return DiagnosticWriteResult.Fail(DiagnosticFailure.Cancelled); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        { return DiagnosticWriteResult.Fail(DiagnosticFailure.LogUnavailable); }
    }

    private void Rotate()
    {
        if (_maxFiles == 1)
        {
            if (File.Exists(_currentPath)) File.Delete(_currentPath);
            return;
        }
        var oldest = Path.Combine(_directory, $"diagnostics.log.{_maxFiles - 1}");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _maxFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"diagnostics.log.{index}");
            if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"diagnostics.log.{index + 1}"));
        }
        if (File.Exists(_currentPath)) File.Move(_currentPath, Path.Combine(_directory, "diagnostics.log.1"));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}

internal static class OwnerOnlyPermissions
{
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectoryMode = OwnerFileMode | UnixFileMode.UserExecute;

    internal static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            Directory.CreateDirectory(path, OwnerDirectoryMode);
        else
            Directory.CreateDirectory(path);
        ApplyDirectory(path);
    }

    internal static FileStreamOptions CreateFileOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = share,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            options.UnixCreateMode = OwnerFileMode;
        return options;
    }

    internal static void ApplyDirectory(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            File.SetUnixFileMode(path, OwnerDirectoryMode);
    }

    internal static void ApplyFile(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            File.SetUnixFileMode(path, OwnerFileMode);
    }
}
