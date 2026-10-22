namespace HyperWhisper.Linux.Platform.Desktop;

internal interface IDesktopCommandRunner
{
    Task<Injection.ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout);

    /// <summary>
    /// Runs like <see cref="RunAsync"/> and also returns the helper's stderr (capped) in
    /// <see cref="Injection.ExternalProcessResult.Error"/>. A runner that cannot read it leaves
    /// <c>Error</c> null, which a caller must treat as unknown, not as an empty stderr.
    /// </summary>
    Task<Injection.ExternalProcessResult> RunCapturingErrorAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout) =>
        RunAsync(executable, arguments, input, cancellationToken, timeout);
}

internal sealed class DesktopCommandRunner : IDesktopCommandRunner
{
    public Task<Injection.ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout) =>
        Injection.ExternalProcessRunner.RunAsync(executable, arguments, input, cancellationToken, timeout);
    public Task<Injection.ExternalProcessResult> RunCapturingErrorAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout) =>
        Injection.ExternalProcessRunner.RunAsync(executable, arguments, input, cancellationToken, timeout, captureError: true);
}
