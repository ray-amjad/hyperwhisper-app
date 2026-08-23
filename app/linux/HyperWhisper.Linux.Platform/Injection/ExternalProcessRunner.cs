using System.Diagnostics;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed record ExternalProcessResult(int ExitCode, byte[] Output);

internal static class ExternalProcessRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan? timeout = null,
        int maximumOutputBytes = int.MaxValue)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The helper process could not start.");
        try
        {
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
