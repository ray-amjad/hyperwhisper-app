namespace HyperWhisper.Services;

// Stand-in for the app's LoggingService (it writes to %LOCALAPPDATA%), so the
// linked MicrophoneKeepWarmService.cs compiles here without the WPF project.
public static class LoggingService
{
    public static void Debug(string message) { }
    public static void Info(string message) { }
    public static void Warn(string message) { }
}
