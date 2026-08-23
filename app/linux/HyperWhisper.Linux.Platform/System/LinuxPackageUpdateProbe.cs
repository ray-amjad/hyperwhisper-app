using System.Text;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;

namespace HyperWhisper.Linux.Platform.SystemIntegration;

public enum LinuxPackageUpdateState
{
    NotChecked,
    Current,
    UpdateAvailable,
    NotPackageManaged,
    Unavailable,
    Failed,
}

public sealed record LinuxPackageUpdateStatus(
    LinuxPackageUpdateState State,
    string? InstalledVersion = null,
    string? CandidateVersion = null);

/// <summary>Reads the package manager's existing metadata. It never refreshes caches or changes packages.</summary>
public sealed class LinuxPackageUpdateProbe
{
    private const string PackageName = "hyperwhisper";
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _aptCache;
    private readonly string? _packageKit;

    public LinuxPackageUpdateProbe() : this(
        new DesktopCommandRunner(),
        CommandClipboardBackend.FindExecutable("apt-cache"),
        CommandClipboardBackend.FindExecutable("pkcon"))
    { }

    internal LinuxPackageUpdateProbe(IDesktopCommandRunner runner, string? aptCache, string? packageKit)
    {
        _runner = runner;
        _aptCache = aptCache;
        _packageKit = packageKit;
    }

    public async Task<LinuxPackageUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_aptCache is not null)
            {
                var result = await _runner.RunAsync(
                    _aptCache, ["policy", PackageName], null, cancellationToken, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                return result.ExitCode == 0 ? ParseAptPolicy(result.Output) : new(LinuxPackageUpdateState.Failed);
            }

            if (_packageKit is not null)
            {
                var result = await _runner.RunAsync(
                    _packageKit, ["--noninteractive", "--plain", "get-updates"], null,
                    cancellationToken, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                if (result.ExitCode != 0) return new(LinuxPackageUpdateState.Failed);
                var output = DecodeBounded(result.Output);
                return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => ContainsPackageToken(line, PackageName))
                    ? new(LinuxPackageUpdateState.UpdateAvailable)
                    : new(LinuxPackageUpdateState.Current);
            }

            return new(LinuxPackageUpdateState.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(LinuxPackageUpdateState.Failed); }
    }

    internal static LinuxPackageUpdateStatus ParseAptPolicy(byte[] bytes)
    {
        string? installed = null;
        string? candidate = null;
        foreach (var line in DecodeBounded(bytes).Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Installed:", StringComparison.Ordinal))
                installed = SafeVersion(line["Installed:".Length..]);
            else if (line.StartsWith("Candidate:", StringComparison.Ordinal))
                candidate = SafeVersion(line["Candidate:".Length..]);
        }

        if (installed is null || installed == "(none)")
            return new(LinuxPackageUpdateState.NotPackageManaged);
        if (candidate is null || candidate == "(none)")
            return new(LinuxPackageUpdateState.Failed);
        return new(
            string.Equals(installed, candidate, StringComparison.Ordinal)
                ? LinuxPackageUpdateState.Current
                : LinuxPackageUpdateState.UpdateAvailable,
            installed,
            candidate);
    }

    private static string DecodeBounded(byte[] output) =>
        Encoding.UTF8.GetString(output.AsSpan(0, Math.Min(output.Length, 64 * 1024)));

    private static string? SafeVersion(string value)
    {
        value = value.Trim();
        if (value is "(none)") return value;
        return value.Length is > 0 and <= 96
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or ':' or '~' or '_' or '-')
            ? value
            : null;
    }

    private static bool ContainsPackageToken(string line, string package)
    {
        var tokens = line.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(token => string.Equals(token, package, StringComparison.Ordinal)
            || token.StartsWith(package + ";", StringComparison.Ordinal));
    }
}
