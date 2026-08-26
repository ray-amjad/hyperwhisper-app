using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class PortableSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IPrivateFileService _files;
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public PortableSettingsService(IPrivateFileService files, IAppPaths paths)
        : this(files, Path.Combine(
            (paths ?? throw new ArgumentNullException(nameof(paths))).ConfigDirectory,
            "settings.json"))
    {
    }

    public PortableSettingsService(IPrivateFileService files, string path)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public PlatformResult Load()
    {
        var result = _files.ReadAllText(_path);
        if (result.IsFailure)
            return PlatformResult.Failure(result.Error!.Code, result.Error.Message);
        _values.Clear();
        if (result.Value == null) return PlatformResult.Success();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Value, SerializerOptions);
            if (loaded != null)
                foreach (var entry in loaded) _values[entry.Key] = entry.Value.Clone();
            return PlatformResult.Success();
        }
        catch (JsonException)
        {
            return PlatformResult.Failure("settings.invalid_json", "The settings file is not valid JSON.");
        }
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        ValidateKey(key);
        return _values.TryGetValue(key, out var value)
            ? value.Deserialize<T>(SerializerOptions)
            : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        ValidateKey(key);
        _values[key] = JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    public IReadOnlyDictionary<string, JsonElement> Snapshot()
        => _values.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);

    public void Replace(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values.Clear();
        foreach (var entry in values) _values[entry.Key] = entry.Value.Clone();
    }

    public PlatformResult Save()
        => _files.WriteAllTextAtomically(_path, JsonSerializer.Serialize(_values, SerializerOptions));

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A settings key is required.", nameof(key));
    }
}

public sealed record PortableCustomPostProcessingEndpoint(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("endpointURL")] string EndpointUrl,
    [property: JsonPropertyName("modelName")] string ModelName);
