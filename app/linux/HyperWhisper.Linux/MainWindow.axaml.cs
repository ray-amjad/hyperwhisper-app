using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace HyperWhisper.Linux;

public partial class MainWindow : Window
{
    private readonly LinuxDesktopServices _platformServices;

    private static readonly IReadOnlyDictionary<string, (string Heading, string Description)> Pages =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["home"] = ("Linux application shell is running", "Platform services are connected here as each parity milestone lands."),
            ["modes"] = ("Modes", "Mode selection and context-aware switching share the portable HyperWhisper core."),
            ["history"] = ("History", "Recordings and transcripts use Linux XDG data and state directories."),
            ["vocabulary"] = ("Vocabulary", "Custom terms and replacements remain compatible with Windows and macOS backups."),
            ["settings"] = ("Settings", "Hotkeys, audio, models, privacy, local API, and desktop integration are configured here."),
        };

    public MainWindow()
        : this(new LinuxDesktopServices())
    {
        Closed += (_, _) => _platformServices.Dispose();
    }

    internal MainWindow(LinuxDesktopServices platformServices)
    {
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        InitializeComponent();
        PlatformStatusText.Text = $"Linux platform ready · {_platformServices.Paths.DataDirectory}";
    }

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not ListBoxItem { Tag: string pageId })
        {
            return;
        }

        ShowPage(pageId);
    }

    private void ShowPage(string pageId)
    {
        if (!Pages.TryGetValue(pageId, out var page))
        {
            throw new ArgumentException("Unknown navigation page.", nameof(pageId));
        }

        PageTitle.Text = char.ToUpperInvariant(pageId[0]) + pageId[1..];
        PageHeading.Text = page.Heading;
        PageDescription.Text = page.Description;
        StatusText.Text = $"{PageTitle.Text} ready";
    }

    internal async Task<int> RunSmokeTestAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            if (Bounds.Width <= 0 || Bounds.Height <= 0 || !IsVisible)
            {
                return 2;
            }

            if (!Path.IsPathFullyQualified(_platformServices.Paths.DataDirectory)
                || _platformServices.PrivateFiles is null
                || _platformServices.GlobalShortcuts is null)
            {
                return 4;
            }

            foreach (var pageId in Pages.Keys)
            {
                ShowPage(pageId);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

                if (!string.Equals(StatusText.Text, $"{PageTitle.Text} ready", StringComparison.Ordinal))
                {
                    return 3;
                }
            }

            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
