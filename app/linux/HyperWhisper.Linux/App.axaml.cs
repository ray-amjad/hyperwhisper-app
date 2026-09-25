using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HyperWhisper.Telemetry;
using HyperWhisper.Linux.Localization;

namespace HyperWhisper.Linux;

public partial class App : Application
{
    private LinuxDesktopServices? _platformServices;
    private readonly LinuxSentryService _telemetry = new();
    public AvaloniaLocalizationBridge Localization { get; } = new(
        AvaloniaLocalizationBridge.ResolveStartupCulture(
            Environment.GetEnvironmentVariable("HYPERWHISPER_UI_CULTURE")));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["Localization"] = Localization;
        Resources["LocalizedFormatConverter"] = new LocalizedFormatConverter(Localization);
        Resources["LocalTimeConverter"] = new LocalTimeConverter();
        Resources["ShortDurationConverter"] = new ShortDurationConverter();
        Resources["IconKeyConverter"] = new IconKeyConverter();
        Resources["ProviderLogoConverter"] = new ProviderLogoConverter();
        Resources["LanguageDisplayNameConverter"] = new LanguageDisplayNameConverter(Localization);
        Resources["OptionLabelConverter"] = new OptionLabelConverter(Localization);
        Resources["CloudPostProcessingLabelConverter"] = new CloudPostProcessingLabelConverter();
        Resources["LocalModelLabelConverter"] = new LocalModelLabelConverter();
        Resources["CloudSttModelLabelConverter"] = new CloudSttModelLabelConverter();
        Resources["CloudVendorLabelConverter"] = new CloudVendorLabelConverter();
        Resources["CloudTierModelLabelConverter"] = new CloudTierModelLabelConverter();
        Resources["CloudSttTierLabelConverter"] = new CloudSttTierLabelConverter();
        Resources["ModeProviderLineConverter"] = new ModeProviderLineConverter();
        Resources["ModePostProcessingConverter"] = new ModePostProcessingConverter();
        Resources["StatusBarModelConverter"] = new StatusBarModelConverter();
        Resources["HistoryGroupHeaderConverter"] = new HistoryGroupHeaderConverter(Localization);
        Resources["TranscriptStatusConverter"] = new TranscriptStatusConverter();
        Resources["TranscriptFontConverter"] = new TranscriptFontConverter();
        Resources["VocabularySourceConverter"] = new VocabularySourceConverter(Localization);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SubscribeUnhandledExceptions();
            _platformServices = new LinuxDesktopServices(_telemetry);
            var acquired = _platformServices.SingleInstance.TryAcquire();
            if (acquired.IsFailure)
            {
                Console.Error.WriteLine($"HyperWhisper single-instance startup failed: {acquired.Error!.Code}");
                _platformServices.Dispose();
                ShutdownFromMainLoop(desktop, 1);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (acquired.IsSuccess && acquired.Value == false)
            {
                _ = _platformServices.SingleInstance.SignalExistingInstance();
                ShutdownFromMainLoop(desktop, 0);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var window = new MainWindow(_platformServices);
            desktop.MainWindow = window;
            _platformServices.SingleInstance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                window.Show();
                window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Activate();
            });
            desktop.Exit += (_, _) =>
            {
                UnsubscribeUnhandledExceptions();
                _platformServices.Dispose();
                _telemetry.Dispose();
            };

            if (Program.IsSmokeTest)
            {
                window.Opened += async (_, _) =>
                {
                    var exitCode = await window.RunSmokeTestAsync();
                    Console.Error.WriteLine($"Smoke result: {exitCode}");
                    // Opened can raise before the dispatcher enters its main loop. Shutting down
                    // from here then aborts the process with "Dispatcher shut down" and loses the
                    // result, so hand the shutdown back to the loop.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode));
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The dispatcher has not entered its main loop yet while OnFrameworkInitializationCompleted
    // runs. Shutting down here makes MainLoop throw "Dispatcher shut down" and the process aborts
    // with SIGABRT (exit 134, #956), so post the shutdown and let the loop run it. No window is
    // open on these paths, so nothing else ends the loop first.
    private static void ShutdownFromMainLoop(IClassicDesktopStyleApplicationLifetime desktop, int exitCode) =>
        Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode));

    private void SubscribeUnhandledExceptions()
    {
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnsubscribeUnhandledExceptions()
    {
        Dispatcher.UIThread.UnhandledException -= OnUiUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs args) =>
        _telemetry.Capture(args.Exception, "Unhandled UI exception");

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            _telemetry.Capture(exception, "Unhandled application exception");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        _telemetry.Capture(args.Exception, "Unobserved task exception");
        args.SetObserved();
    }
}
