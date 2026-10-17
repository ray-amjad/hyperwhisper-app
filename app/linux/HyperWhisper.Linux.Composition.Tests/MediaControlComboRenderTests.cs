using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;

// The Sound page's media-control ComboBox showed "Muffle Audi" closed: its 130 px column left the
// label less room than the text once the chevron took its share. This builds that row (the column
// width read from MainWindow.axaml) under the real App and font, selects each item in turn and
// fails when the closed box draws a label wider than the space it gave it. Once per culture whose
// labels are the longest in the catalog.
static class MediaControlComboRenderTests
{
    public const string ProbeArgument = "--probe-media-control-combo";
    private const string WidthVariable = "HYPERWHISPER_PROBE_COLUMN_WIDTH";
    private static readonly string[] Cultures = ["en", "bg", "de", "ru", "ja", "ar"];
    private static readonly string[] Keys =
        ["settings.sound.mediaControl.off", "settings.sound.mediaControl.muteAudio", "linux.settings.sound.mediaControl.muffleAudio"];

    public static async Task RunAsync()
    {
        var width = ColumnWidth();
        Environment.SetEnvironmentVariable(WidthVariable, width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var culture in Cultures)
            await XvfbProbe.RunAsync(ProbeArgument, "the media-control ComboBox probe", culture, "1440x900");
    }

    // The fixed column the row's Grid gives SettingsAudioEnvironmentPolicy.
    private static int ColumnWidth([CallerFilePath] string here = "")
    {
        var axaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "HyperWhisper.Linux", "MainWindow.axaml"));
        var match = Regex.Match(axaml, @"ColumnDefinitions=""\*,(\d+)"">(?:(?!</Grid>).)*?x:Name=""SettingsAudioEnvironmentPolicy""", RegexOptions.Singleline);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("MainWindow.axaml has no fixed column for SettingsAudioEnvironmentPolicy");
    }

    /// <summary>The child half, on the X server xvfb-run started. Exit 0 only if every label fits.</summary>
    public static Task<int> RunProbeAsync()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var localization = ((App)Application.Current!).Localization;
            var width = double.Parse(Environment.GetEnvironmentVariable(WidthVariable) ?? "130", System.Globalization.CultureInfo.InvariantCulture);
            var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            foreach (var key in Keys) combo.Items.Add(new ComboBoxItem { Content = localization[key] });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}") };
            Grid.SetColumn(combo, 1); grid.Children.Add(new TextBlock { Text = "row" }); grid.Children.Add(combo);
            var window = new Window { Width = 900, Height = 200, Content = grid, FlowDirection = localization.FlowDirection };
            window.Show();
            var failures = new List<string>();
            for (var index = 0; index < Keys.Length; index++)
            {
                combo.SelectedIndex = index;
                Pump(window);
                var label = combo.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Text == localization[Keys[index]]);
                if (label is null) { failures.Add($"{Keys[index]} draws no label"); continue; }
                var needed = label.TextLayout.Width; var given = label.Bounds.Width;
                Console.WriteLine($"{localization.Culture.Name}: '{label.Text}' needs {needed:F1} px, given {given:F1} px in a {width:F0} px column");
                if (needed > given + 0.5) failures.Add($"'{label.Text}' needs {needed:F1} px but the closed box gives it {given:F1} px, so it is clipped");
            }
            window.Close();
            Pump(window);
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return Task.FromResult(failures.Count == 0 ? 0 : 1);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return Task.FromResult(2); }
    }

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
