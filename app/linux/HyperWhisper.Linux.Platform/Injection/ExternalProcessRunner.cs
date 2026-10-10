using System.Diagnostics;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed record ExternalProcessResult(int ExitCode, byte[] Output);

internal static class ExternalProcessRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

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
        int maximumOutputBytes = int.MaxValue, bool discardOutput = false)
    {
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
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
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
            return new ExternalProcessResult(process.ExitCode, bytes);
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

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
