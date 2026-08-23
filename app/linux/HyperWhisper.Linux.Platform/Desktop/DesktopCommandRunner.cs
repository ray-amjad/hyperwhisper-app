namespace HyperWhisper.Linux.Platform.Desktop;

internal interface IDesktopCommandRunner
{
    Task<Injection.ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout);
}

internal sealed class DesktopCommandRunner : IDesktopCommandRunner
{
    public Task<Injection.ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout) =>
        Injection.ExternalProcessRunner.RunAsync(executable, arguments, input, cancellationToken, timeout);
}
