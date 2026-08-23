using Avalonia;

namespace HyperWhisper.Linux;

internal static class Program
{
    public static bool IsSmokeTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        IsSmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .WithInterFont()
        .LogToTrace();
}
