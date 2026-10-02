using System.Runtime.InteropServices;
using Avalonia;

namespace HyperWhisper.Linux;

internal static class Program
{
    public static bool IsSmokeTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        CapMallocArenas();
        IsSmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Freed Whisper buffers stay in per-thread glibc arenas: https://github.com/ray-amjad/hyperwhisper-app/issues/1088
    private static void CapMallocArenas()
    {
        if (!OperatingSystem.IsLinux()) return;
        // mallopt beats the environment, so leave a cap the user or the launcher already set.
        if (Environment.GetEnvironmentVariable("MALLOC_ARENA_MAX") is not null
            || Environment.GetEnvironmentVariable("GLIBC_TUNABLES")?.Contains("glibc.malloc.arena_max", StringComparison.Ordinal) == true) return;
        try { _ = Mallopt(MArenaMax, 2); }
        catch (EntryPointNotFoundException) { } // non-glibc libc
        catch (DllNotFoundException) { }
    }

    private const int MArenaMax = -8;

    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int Mallopt(int param, int value);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .WithInterFont()
        .LogToTrace();
}
