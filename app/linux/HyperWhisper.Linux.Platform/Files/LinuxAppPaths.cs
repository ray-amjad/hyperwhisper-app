using System.Runtime.InteropServices;
using System.Text.Json;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Files;

public sealed class LinuxAppPaths : IAppPaths
{
    private const string AppDirectoryName = "hyperwhisper";

    public LinuxAppPaths()
        : this(new ProcessEnvironment(), new LinuxUserIdentity())
    {
    }

    internal LinuxAppPaths(IProcessEnvironment environment, IUserIdentity user)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(user);

        var home = RequireAbsolute(environment.HomeDirectory, "HOME");
        DataDirectory = Child(XdgHome(environment, "XDG_DATA_HOME", Path.Combine(home, ".local/share")));
        ConfigDirectory = Child(XdgHome(environment, "XDG_CONFIG_HOME", Path.Combine(home, ".config")));
        CacheDirectory = Child(XdgHome(environment, "XDG_CACHE_HOME", Path.Combine(home, ".cache")));
        StateDirectory = Child(XdgHome(environment, "XDG_STATE_HOME", Path.Combine(home, ".local/state")));

        LogsDirectory = Path.Combine(StateDirectory, "logs");
        ModelsDirectory = Path.Combine(DataDirectory, "models");
        RecordingsDirectory = ResolveRecordingsDirectory(
            Path.Combine(DataDirectory, "recordings"),
            Path.Combine(ConfigDirectory, "settings.json"));

        var runtimeHome = AbsoluteOrNull(environment.Get("XDG_RUNTIME_DIR"));
        RuntimeDirectory = runtimeHome is null
            ? Path.Combine(DataDirectory, "runtime")
            : Child(runtimeHome);

        var temporaryHome = AbsoluteOrNull(environment.Get("TMPDIR")) ?? Path.GetTempPath();
        TemporaryDirectory = Path.Combine(
            Path.GetFullPath(temporaryHome),
            $"{AppDirectoryName}-{user.EffectiveUserId}");
    }

    public string DataDirectory { get; }
    public string ConfigDirectory { get; }
    public string CacheDirectory { get; }
    public string StateDirectory { get; }
    public string LogsDirectory { get; }
    public string ModelsDirectory { get; }
    public string RecordingsDirectory { get; }
    public string RuntimeDirectory { get; }
    public string TemporaryDirectory { get; }

    private static string XdgHome(IProcessEnvironment environment, string name, string fallback) =>
        AbsoluteOrNull(environment.Get(name)) ?? fallback;

    private static string Child(string parent) => Path.Combine(parent, AppDirectoryName);

    private static string? AbsoluteOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : null;

    private static string RequireAbsolute(string? path, string name) =>
        AbsoluteOrNull(path)
        ?? throw new InvalidOperationException($"{name} must resolve to an absolute directory.");

    private static string ResolveRecordingsDirectory(string fallback, string settingsPath)
    {
        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("storage.recordingsDirectory", out var configured)
                || configured.ValueKind != JsonValueKind.String)
                return fallback;
            var path = configured.GetString();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return fallback;
            var fullPath = Path.GetFullPath(path);
            return string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.Ordinal)
                ? fallback
                : fullPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return fallback;
        }
    }
}

internal interface IProcessEnvironment
{
    string? HomeDirectory { get; }
    string? Get(string name);
}

internal sealed class ProcessEnvironment : IProcessEnvironment
{
    public string? HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

internal interface IUserIdentity
{
    uint EffectiveUserId { get; }
}

internal sealed class LinuxUserIdentity : IUserIdentity
{
    public uint EffectiveUserId => GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
