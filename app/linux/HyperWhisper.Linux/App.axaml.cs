using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace HyperWhisper.Linux;

public partial class App : Application
{
    private LinuxDesktopServices? _platformServices;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _platformServices = new LinuxDesktopServices();
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
            desktop.Exit += (_, _) => _platformServices.Dispose();

            if (Program.IsSmokeTest)
            {
                window.Opened += async (_, _) =>
                {
                    var exitCode = await window.RunSmokeTestAsync();
                    desktop.Shutdown(exitCode);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
