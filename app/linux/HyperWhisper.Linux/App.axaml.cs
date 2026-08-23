using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
            var window = new MainWindow(_platformServices);
            desktop.MainWindow = window;
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
