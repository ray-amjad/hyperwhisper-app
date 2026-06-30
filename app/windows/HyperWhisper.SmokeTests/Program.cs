using System.IO;
using System.Windows;
using HyperWhisper.Data;
using HyperWhisper.Services;
using HyperWhisper.Views.Pages.Settings;

namespace HyperWhisper.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HyperWhisper.SmokeTests",
            Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, tempRoot);

        try
        {
            Directory.CreateDirectory(tempRoot);
            DatabaseInitializer.InitializeAsync().GetAwaiter().GetResult();

            var application = new Application();
            LoadApplicationResources(application);

            var page = new BackupExportSettingsPage();
            if (!page.IsInitialized)
                throw new InvalidOperationException("BackupExportSettingsPage did not finish WPF initialization.");

            application.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("BackupExportSettingsPage smoke test failed:");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable, null);

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup; a failed delete must not mask the smoke result.
            }
        }
    }

    private static void LoadApplicationResources(Application application)
    {
        AddResourceDictionary(application, "Themes/LightColors.xaml");
        AddResourceDictionary(application, "Themes/Brushes.xaml");
        AddResourceDictionary(application, "Themes/Generic.xaml");
    }

    private static void AddResourceDictionary(Application application, string resourcePath)
    {
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/HyperWhisper;component/{resourcePath}", UriKind.Absolute)
        });
    }
}
