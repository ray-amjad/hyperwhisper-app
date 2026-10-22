using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Collections;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Data.Converters;
using Avalonia.Data;
using Avalonia;
using HyperWhisper.Localization;

namespace HyperWhisper.Linux.Localization;

/// <summary>
/// Observable Avalonia-facing bridge over the portable localization catalog.
/// Create it on the UI thread so culture notifications are marshalled back to
/// that thread when a background settings operation changes the culture.
/// </summary>
public sealed class AvaloniaLocalizationBridge : INotifyPropertyChanged, IDisposable
{
    private const string LinuxBaseName = "HyperWhisper.Linux.Localization.Resources.LinuxStrings";
    private static readonly ResourceManager LinuxResources = new(LinuxBaseName, typeof(AvaloniaLocalizationBridge).Assembly);
    private static readonly IReadOnlySet<string> LinuxKeys = LoadLinuxKeys();
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]*)?\}(?!\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly SynchronizationContext? _notificationContext;
    private PortableLocalizer _localizer;
    private bool _disposed;

    public AvaloniaLocalizationBridge(
        CultureInfo? culture = null,
        SynchronizationContext? notificationContext = null)
    {
        _localizer = new PortableLocalizer(culture);
        _notificationContext = notificationContext ?? SynchronizationContext.Current;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? CultureChanged;

    public CultureInfo Culture
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _localizer.Culture;
            }
        }
    }

    public FlowDirection FlowDirection
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _localizer.IsRightToLeft
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
            }
        }
    }

    /// <summary>
    /// Dynamic binding surface. Unknown keys deliberately render their key so
    /// an incomplete Linux localization is visible rather than a blank control.
    /// </summary>
    public string this[string key]
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return LinuxKeys.Contains(key) ? GetLinux(key) : _localizer.Get(key);
            }
        }
    }

    public IReadOnlyList<CultureInfo> SupportedCultures => PortableLocalizer.SupportedCultures;
    public static IReadOnlySet<string> LinuxCatalogKeys => LinuxKeys;

    public static IReadOnlySet<string> LinuxTranslatedKeys(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var set = LinuxResources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    public static CultureInfo ResolveStartupCulture(string? requestedCulture = null)
    {
        CultureInfo requested;
        try
        {
            requested = string.IsNullOrWhiteSpace(requestedCulture)
                ? CultureInfo.CurrentUICulture
                : CultureInfo.GetCultureInfo(requestedCulture.Trim());
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en");
        }
        var match = PortableLocalizer.SupportedCultures.FirstOrDefault(culture =>
            string.Equals(culture.Name, requested.Name, StringComparison.OrdinalIgnoreCase))
            ?? PortableLocalizer.SupportedCultures.FirstOrDefault(culture =>
                string.Equals(culture.TwoLetterISOLanguageName, requested.TwoLetterISOLanguageName,
                    StringComparison.OrdinalIgnoreCase));
        return match ?? CultureInfo.GetCultureInfo("en");
    }

    public string GetRequired(string key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return LinuxKeys.Contains(key) ? GetLinux(key) : _localizer.Get(_localizer.Key(key));
        }
    }

    public string Format(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!LinuxKeys.Contains(key))
            {
                return _localizer.Format(_localizer.Key(key), arguments);
            }

            var invariant = LinuxResources.GetString(key, CultureInfo.InvariantCulture)
                ?? throw new KeyNotFoundException($"Unknown Linux localization key '{key}'.");
            var localized = GetLinux(key);
            var expected = PlaceholderIndexes(invariant);
            var actual = PlaceholderIndexes(localized);
            if (!expected.SequenceEqual(actual))
            {
                throw new LocalizationFormatException(
                    $"Linux localization key '{key}' has placeholders that do not match the base catalog.");
            }
            var required = expected.Count == 0 ? 0 : expected[^1] + 1;
            if (arguments.Length != required)
            {
                throw new LocalizationFormatException(
                    $"Linux localization key '{key}' requires {required} formatting argument(s), but {arguments.Length} were supplied.");
            }
            try { return string.Format(_localizer.Culture, localized, arguments); }
            catch (FormatException exception)
            {
                throw new LocalizationFormatException(
                    $"Linux localization key '{key}' contains an invalid composite format: {exception.Message}");
            }
        }
    }

    public LocalizedResource Bind(string key) => new(this, key, null);

    public LocalizedResource BindFormat(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new LocalizedResource(this, key, arguments);
    }

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.Equals(_localizer.Culture.Name, culture.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _localizer = new PortableLocalizer(culture);
        }

        PublishCultureChanged();
    }

    public static string ProviderIdentifier(string identifier) =>
        PortableLocalizer.PreserveIdentifier(LocalizationIdentifierKind.Provider, identifier);

    public static string ModelIdentifier(string identifier) =>
        PortableLocalizer.PreserveIdentifier(LocalizationIdentifierKind.Model, identifier);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CultureChanged = null;
            PropertyChanged = null;
        }
    }

    private void PublishCultureChanged()
    {
        void Publish(object? _)
        {
            EventHandler? cultureChanged;
            PropertyChangedEventHandler? propertyChanged;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                cultureChanged = CultureChanged;
                propertyChanged = PropertyChanged;
            }

            propertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
            propertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
            propertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            cultureChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_notificationContext is not null
            && !ReferenceEquals(SynchronizationContext.Current, _notificationContext))
        {
            _notificationContext.Post(Publish, null);
        }
        else
        {
            Publish(null);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private string GetLinux(string key) =>
        LinuxResources.GetString(key, _localizer.Culture)
        ?? LinuxResources.GetString(key, CultureInfo.InvariantCulture)
        ?? key;

    private static IReadOnlySet<string> LoadLinuxKeys()
    {
        var set = LinuxResources.GetResourceSet(CultureInfo.InvariantCulture, true, false)
            ?? throw new MissingManifestResourceException("The Linux localization catalog is missing.");
        return set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<int> PlaceholderIndexes(string value) =>
        Placeholder.Matches(value).Select(match => int.Parse(
            match.Groups["index"].Value, CultureInfo.InvariantCulture)).Order().ToArray();
}

/// <summary>
/// Bind <see cref="Value"/> from XAML or code. The resource re-evaluates when
/// the bridge culture changes and must be disposed with its owning view.
/// </summary>
public sealed class LocalizedResource : INotifyPropertyChanged, IDisposable
{
    private AvaloniaLocalizationBridge? _bridge;
    private readonly string _key;
    private object?[]? _arguments;
    private bool _disposed;

    internal LocalizedResource(
        AvaloniaLocalizationBridge bridge,
        string key,
        object?[]? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _bridge = bridge;
        _key = key;
        _arguments = arguments is null ? null : (object?[])arguments.Clone();

        // Validate the key and argument shape immediately, rather than waiting
        // for the binding engine to evaluate the first value.
        _ = Value;
        _bridge.CultureChanged += OnCultureChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bridge = _bridge ?? throw new ObjectDisposedException(nameof(LocalizedResource));
            return _arguments is null
                ? bridge.GetRequired(_key)
                : bridge.Format(_key, _arguments);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var bridge = _bridge;
        _bridge = null;
        if (bridge is not null)
        {
            bridge.CultureChanged -= OnCultureChanged;
        }
        if (_arguments is not null)
        {
            Array.Clear(_arguments);
            _arguments = null;
        }

        PropertyChanged = null;
    }

    private void OnCultureChanged(object? sender, EventArgs args)
    {
        if (!_disposed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}

/// <summary>Formats a single bound value with a catalog key.</summary>
public sealed class LocalizedFormatConverter(AvaloniaLocalizationBridge bridge) : IValueConverter
{
    private readonly AvaloniaLocalizationBridge _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = culture;
        if (ReferenceEquals(value, AvaloniaProperty.UnsetValue) || parameter is not string key) return string.Empty;
        return _bridge.Format(key, value);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
