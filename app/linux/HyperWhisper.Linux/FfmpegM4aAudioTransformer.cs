using System.Diagnostics;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.Linux;

internal sealed class FfmpegM4aAudioTransformer(
    Func<bool> enabled,
    string ffmpegExecutable = "ffmpeg") : ICompletedAudioTransformer
{
    private readonly Func<bool> _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegExecutable)
        ? throw new ArgumentException("An FFmpeg executable is required.", nameof(ffmpegExecutable))
        : ffmpegExecutable;

    public async Task<PlatformResult<string>> TransformAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled() || !string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            return PlatformResult<string>.Success(path);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            return PlatformResult<string>.Failure("storage.m4a_source_missing", "The completed WAV recording is unavailable.");

        var destination = Path.ChangeExtension(path, ".m4a");
        var temporary = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(_ffmpeg)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", path,
                "-vn", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", "-f", "mp4", temporary,
            }) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start())
                return Failure("storage.m4a_ffmpeg_unavailable", "FFmpeg could not be started.");
            using var cancellation = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false); // Drain, but never surface content-bearing paths.
            if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                return Failure("storage.m4a_encode_failed", "FFmpeg could not encode the completed recording.");
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, destination, overwrite: true);
            File.Delete(path);
            return PlatformResult<string>.Success(destination);
        }
        catch (OperationCanceledException)
        {
            return Failure("storage.m4a_cancelled", "M4A encoding was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Failure("storage.m4a_ffmpeg_unavailable", "FFmpeg could not encode the completed recording.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }

        PlatformResult<string> Failure(string code, string message) =>
            PlatformResult<string>.Failure(code, message);
    }
}
