using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using HyperWhisper.Localization;

namespace HyperWhisper.Linux.Localization;

/// <summary>
/// Observable Avalonia-facing bridge over the portable localization catalog.
/// Create it on the UI thread so culture notifications are marshalled back to
/// that thread when a background settings operation changes the culture.
/// </summary>
public sealed class AvaloniaLocalizationBridge : INotifyPropertyChanged, IDisposable
{
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
                return _localizer.Get(key);
            }
        }
    }

    public IReadOnlyList<CultureInfo> SupportedCultures => PortableLocalizer.SupportedCultures;

    public string GetRequired(string key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _localizer.Get(_localizer.Key(key));
        }
    }

    public string Format(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _localizer.Format(_localizer.Key(key), arguments);
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
