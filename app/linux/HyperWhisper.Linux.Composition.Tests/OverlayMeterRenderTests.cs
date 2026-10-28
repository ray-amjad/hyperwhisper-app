using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Overlay;

// PR #2711. The recording overlay's audio meter is a row of thin vertical ProgressBars, 3 px wide and
// 10 to 26 px tall. Fluent's ProgressBar theme gives a vertical bar MinHeight 200 and MinWidth 4 (from a
// :vertical trigger, which outranks a plain Style setter), and a min size beats an explicit Height, so
// each bar laid out 4x200: grey stripes that ran the whole height of the 52 px pill and through its
// rounded border. The theme's horizontal MinWidth 200 stretched the 28 px Transcribing bar to 200 and
// pushed its caption out of the pill. This opens the real overlay window in the Recording and in the
// Streaming state at AudioLevel 0 and 1, reads every meter bar's Bounds and its fill's Bounds, then the
// Transcribing bar's.
static class OverlayMeterRenderTests
{
    public const string ProbeArgument = "--probe-overlay-meter";
    private const double TallestBar = 26;

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the overlay audio meter probe", "en", "1280x800");

    /// <summary>The child half. Exit 0 only if every bar is short, inside the pill, and fills with the level.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var viewModel = new LinuxRecordingOverlayViewModel();
            var window = new LinuxRecordingOverlayWindow(viewModel, new MemoryPlacementStore());
            var failures = new List<string>();
            foreach (var state in new[] { LinuxRecordingOverlayState.Recording, LinuxRecordingOverlayState.Streaming })
            {
                var fill = new double[2][];
                foreach (var level in new[] { 0.0, 1.0 })
                {
                    viewModel.Apply(new(state, true, state.ToString(), "Default", "00:03", level,
                        state == LinuxRecordingOverlayState.Streaming ? LinuxStreamingOverlayConnectionState.Connected : null));
                    window.ShowBestEffort();
                    Pump(window);
                    fill[(int)level] = Measure(window, state, level, failures);
                }
                for (var index = 0; index < fill[0].Length && index < fill[1].Length; index++)
                    if (!(fill[1][index] > fill[0][index]))
                        failures.Add($"{state} bar {index}: the fill at level 1 is {fill[1][index]:F1} px, not taller than {fill[0][index]:F1} px at level 0");
            }
            viewModel.Apply(new(LinuxRecordingOverlayState.Transcribing, true, "Transcribing", "Default", "00:03"));
            Pump(window);
            CheckTranscribingBar(window, failures);
            window.Close();
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    /// <summary>Checks every bar of the visible meter and returns each bar's fill height.</summary>
    private static double[] Measure(Window window, LinuxRecordingOverlayState state, double level, List<string> failures)
    {
        var pill = window.Content as Border ?? throw new InvalidOperationException("the overlay's content is not its pill Border");
        var bars = MeterBars(window);
        if (bars.Count == 0) throw new InvalidOperationException($"{state}: the overlay shows no meter bars");
        var inner = new Rect(pill.Bounds.Size).Deflate(pill.BorderThickness);
        var heights = new double[bars.Count];
        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            var box = Box(bar, pill);
            var indicator = bar.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "PART_Indicator")
                ?? throw new InvalidOperationException($"{state} bar {index} has no PART_Indicator fill");
            var fillBox = Box(indicator, pill);
            heights[index] = indicator.IsEffectivelyVisible ? fillBox.Height : 0;
            Console.WriteLine($"{state} level {level}: bar {index} {Format(box)} (Height {bar.Height}, MinHeight {bar.MinHeight}, MinWidth {bar.MinWidth}), fill {Format(fillBox)}");

            var label = $"{state} level {level} bar {index}";
            if (box.Height > TallestBar + 0.5) failures.Add($"{label} is {box.Height:F1} px tall, more than {TallestBar}");
            if (!Contains(inner, box)) failures.Add($"{label} {Format(box)} leaves the pill's border {Format(inner)}");
            if (Math.Abs(box.Height - bar.Height) > 0.5) failures.Add($"{label} is {box.Height:F1} px tall, not its designed {bar.Height}");
            if (Math.Abs(box.Width - bar.Width) > 0.5) failures.Add($"{label} is {box.Width:F1} px wide, not its designed {bar.Width}");
            if (heights[index] > 0 && !Contains(box, fillBox)) failures.Add($"{label}: its fill {Format(fillBox)} leaves the bar {Format(box)}");
        }
        return heights;
    }

    /// <summary>The Transcribing state's indeterminate bar keeps its 28x4 inside the pill.</summary>
    private static void CheckTranscribingBar(Window window, List<string> failures)
    {
        var pill = window.Content as Border ?? throw new InvalidOperationException("the overlay's content is not its pill Border");
        var bar = window.GetVisualDescendants().OfType<ProgressBar>().Single(candidate => candidate.IsEffectivelyVisible);
        var box = Box(bar, pill);
        Console.WriteLine($"Transcribing: bar {Format(box)} (Width {bar.Width}, Height {bar.Height})");
        if (Math.Abs(box.Width - bar.Width) > 0.5 || Math.Abs(box.Height - bar.Height) > 0.5)
            failures.Add($"Transcribing: the bar is {box.Width:F1}x{box.Height:F1}, not its designed {bar.Width}x{bar.Height}");
        if (!Contains(new Rect(pill.Bounds.Size).Deflate(pill.BorderThickness), box))
            failures.Add($"Transcribing: the bar {Format(box)} leaves the pill");
    }

    /// <summary>The bars of the visible state's meter: the children of column 2's StackPanel.</summary>
    private static List<Control> MeterBars(Window window)
    {
        var grid = window.GetVisualDescendants().OfType<Grid>()
            .Single(candidate => candidate.ColumnDefinitions.Count == 4 && candidate.IsEffectivelyVisible);
        var meter = grid.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 2);
        return meter.Children.OfType<Control>().ToList();
    }

    private static Rect Box(Visual visual, Visual relativeTo)
    {
        var origin = visual.TranslatePoint(default, relativeTo) ?? throw new InvalidOperationException($"{visual} is not in the pill");
        return new Rect(origin, visual.Bounds.Size);
    }

    private static bool Contains(Rect outer, Rect inner) =>
        inner.X >= outer.X - 0.5 && inner.Y >= outer.Y - 0.5 && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    private static string Format(Rect rect) => $"x {rect.X:F1} y {rect.Y:F1} {rect.Width:F1}x{rect.Height:F1}";

    // Layout and the bindings settle synchronously; no fixed sleep (#2188).
    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class MemoryPlacementStore : ILinuxOverlayPlacementStore
    {
        private LinuxOverlayPlacement? _placement;
        public LinuxOverlayPlacement? Load() => _placement;
        public void Save(LinuxOverlayPlacement placement) => _placement = placement;
    }
}
