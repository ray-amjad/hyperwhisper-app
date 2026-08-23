using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace HyperWhisper.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

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
