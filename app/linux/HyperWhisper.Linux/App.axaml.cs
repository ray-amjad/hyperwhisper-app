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
                desktop.Shutdown(1);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (acquired.IsSuccess && acquired.Value == false)
            {
                _ = _platformServices.SingleInstance.SignalExistingInstance();
                desktop.Shutdown();
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
