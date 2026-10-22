using System.Diagnostics;

namespace HyperWhisper.Linux.Platform.Injection;

/// <summary>
/// <paramref name="Error"/> is the helper's stderr, kept only when the caller asked for it
/// (<c>captureError</c>); otherwise it is null, which means "not read", never "empty".
/// </summary>
internal sealed record ExternalProcessResult(int ExitCode, byte[] Output, byte[]? Error = null);

internal static class ExternalProcessRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    internal const int MaximumErrorBytes = 16 * 1024;

    /// <summary>
    /// Runs the helper with its stdout and stderr on /dev/null. A selection helper (wl-copy, xclip
    /// -in) forks a child that keeps serving the clipboard and inherits every open descriptor; with
    /// pipes on stdout or stderr that child holds them open, the reader never sees EOF, and the
    /// call times out even though the clipboard was set (#1528). The shell only redirects and then
    /// execs the helper, so the exit code and the process id stay the helper's own.
    /// </summary>
    private const string DiscardOutputScript = "exec \"$0\" \"$@\" >/dev/null 2>&1";

    public static async Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan? timeout = null,
        int maximumOutputBytes = int.MaxValue, bool discardOutput = false, bool captureError = false)
    {
        if (discardOutput && captureError) throw new ArgumentException("A discarded run has no stderr to capture.", nameof(captureError));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        var start = new ProcessStartInfo(discardOutput ? "/bin/sh" : executable)
        {
            UseShellExecute = false,
            // A discarded run still gets a stdin pipe, so a helper never reads the app's own stdin.
            RedirectStandardInput = input is not null || discardOutput,
            RedirectStandardOutput = !discardOutput,
            RedirectStandardError = !discardOutput,
        };
        if (discardOutput)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(DiscardOutputScript);
            start.ArgumentList.Add(executable);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The helper process could not start.");
        try
        {
            if (discardOutput)
            {
                if (input is not null)
                    await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                // Only the helper's own exit is awaited; a forked selection server may outlive it.
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return new ExternalProcessResult(process.ExitCode, []);
            }
            // stderr is always drained so a chatty helper never blocks on a full pipe; only a caller
            // that asked for it keeps the first MaximumErrorBytes of it.
            var stderr = captureError
                ? ReadCappedAsync(process.StandardError.BaseStream, MaximumErrorBytes, deadline.Token)
                : DiscardAsync(process.StandardError.BaseStream, deadline.Token);
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes, deadline.Token);
            // Await the bounded reader first so an oversized stream faults
            // immediately and the outer handler kills a producer blocked on stdout.
            var bytes = await stdout.ConfigureAwait(false);
            await Task.WhenAll(stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new ExternalProcessResult(process.ExitCode, bytes, await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The desktop helper exceeded its time limit.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The helper output exceeded its configured limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static async Task<byte[]?> DiscardAsync(Stream source, CancellationToken token)
    {
        await source.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
        return null;
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream source, int maximumBytes, CancellationToken token)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[4 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return kept.ToArray();
            var room = maximumBytes - (int)kept.Length;
            if (room > 0) kept.Write(buffer, 0, Math.Min(room, read));
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
