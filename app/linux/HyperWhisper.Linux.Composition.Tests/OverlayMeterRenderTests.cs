using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Overlay;

// PR #2711. Ray asked for the Linux recording pill to look like the macOS one, so its meter is now a
// port of macOS's WaveformCAView: 25 white bars, 2 px wide on a 4 px pitch, under a center-peaked
// envelope, that scale with a smoothed audio level and animate on a ~30 fps timer. This opens the real
// overlay window in the Recording and Streaming states and checks the waveform: its 25 bars sit inside
// the pill at their 2 px width, they are drawn (25 lit columns in a render of the window), they are
// taller at level 1 than at silence, the middle bar outgrows the edge bars, and the real timer moves
// them. Then it checks the timer stops once the waveform is hidden (Transcribing) and the
// Transcribing spinner's stops once the pill is hidden; that a face or pill shown again animates again
// from silence; that an hour-long timer narrows the row evenly instead of clipping it; and that normal
// speech (-22 dBFS) through the controller's macOS-style dB mapping gives a mid-to-tall wave.
// Set HW_OVERLAY_SHOTS_DIR to also write the pill's states as PNGs.
static class OverlayMeterRenderTests
{
    public const string ProbeArgument = "--probe-overlay-meter";
    private const double Frame = 1.0 / 30;

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the overlay waveform probe", "en", "1280x800");

    /// <summary>The child half. Exit 0 only if the waveform has the macOS shape, stays in the pill and animates only while shown.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var shots = Environment.GetEnvironmentVariable("HW_OVERLAY_SHOTS_DIR");
            var viewModel = new LinuxRecordingOverlayViewModel();
            var window = new LinuxRecordingOverlayWindow(viewModel, new MemoryPlacementStore());
            var failures = new List<string>();
            var waveforms = new List<LinuxOverlayWaveform>();
            foreach (var state in new[] { LinuxRecordingOverlayState.Recording, LinuxRecordingOverlayState.Streaming })
            {
                var waveform = CheckWaveform(window, viewModel, state, failures);
                waveforms.Add(waveform);
                if (shots is not null)
                {
                    Apply(viewModel, state, 0.5);
                    Pump(window);
                    Settle(waveform, 3);
                    Pump(window);
                    Save(window, Path.Combine(shots, $"pill-{state.ToString().ToLowerInvariant()}.png"));
                }
            }

            CheckRoom(window, viewModel, failures);

            viewModel.Apply(new(LinuxRecordingOverlayState.Transcribing, true, "Transcribing", "Default", "00:03"));
            Pump(window);
            RunLoop(TimeSpan.FromMilliseconds(250));
            var hiddenFrames = waveforms.Select(waveform => waveform.FrameCount).ToArray();
            RunLoop(TimeSpan.FromMilliseconds(250));
            for (var index = 0; index < waveforms.Count; index++)
            {
                if (waveforms[index].IsFrameTimerRunning) failures.Add($"waveform {index}'s frame timer still runs while the Transcribing face hides it");
                if (waveforms[index].FrameCount != hiddenFrames[index]) failures.Add($"waveform {index} ran {waveforms[index].FrameCount - hiddenFrames[index]} frames while hidden");
            }
            var spinner = window.GetVisualDescendants().OfType<LinuxOverlaySpinner>().Single(candidate => candidate.IsEffectivelyVisible);
            var pill = Pill(window);
            var spinnerBox = Box(spinner, pill);
            Console.WriteLine($"Transcribing: spinner {Format(spinnerBox)}, animating {spinner.IsFrameTimerRunning}");
            if (Math.Abs(spinnerBox.Width - LinuxOverlaySpinner.Diameter) > 0.5 || Math.Abs(spinnerBox.Height - LinuxOverlaySpinner.Diameter) > 0.5)
                failures.Add($"Transcribing: the spinner is {spinnerBox.Width:F1}x{spinnerBox.Height:F1}, not {LinuxOverlaySpinner.Diameter}");
            if (!Contains(Inner(pill), spinnerBox)) failures.Add($"Transcribing: the spinner {Format(spinnerBox)} leaves the pill");
            if (!spinner.IsFrameTimerRunning) failures.Add("Transcribing: the spinner's timer did not start once it was shown");
            if (shots is not null) Save(window, Path.Combine(shots, "pill-transcribing.png"));

            window.HideBestEffort();
            RunLoop(TimeSpan.FromMilliseconds(250));
            var spinnerFrames = spinner.FrameCount;
            RunLoop(TimeSpan.FromMilliseconds(250));
            if (spinner.IsFrameTimerRunning) failures.Add("the spinner's frame timer still runs after the pill was hidden");
            if (spinner.FrameCount != spinnerFrames) failures.Add($"the spinner ran {spinner.FrameCount - spinnerFrames} frames while the pill was hidden");

            CheckReshow(window, viewModel, waveforms[0], failures);
            CheckNormalSpeech(window, viewModel, waveforms[0], failures);

            if (shots is not null)
            {
                viewModel.Apply(new(LinuxRecordingOverlayState.Error, true, "Transcription failed", string.Empty, "00:03"));
                window.ShowBestEffort();
                Pump(window);
                Save(window, Path.Combine(shots, "pill-failed.png"));
                foreach (var (name, snapshot) in new (string, LinuxRecordingOverlaySnapshot)[]
                {
                    ("cancel-confirm", new(LinuxRecordingOverlayState.CancelConfirmation, true, "Cancel recording?", "Default", "00:03")),
                    ("streaming-reconnecting", new(LinuxRecordingOverlayState.Streaming, true, "Live transcription reconnecting…", "Default", "00:03", 0.3, LinuxStreamingOverlayConnectionState.Reconnecting)),
                    ("streaming-error", new(LinuxRecordingOverlayState.Streaming, true, "Live transcription connection lost", "Default", "01:12", 0, LinuxStreamingOverlayConnectionState.Error)),
                    ("recording-long-mode", new(LinuxRecordingOverlayState.Recording, true, "Recording", "A very long custom mode name", "12:34", 0.9)),
                    ("recording-silence", new(LinuxRecordingOverlayState.Recording, true, "Recording", "Default", "00:01")),
                    ("recording-normal-speech", new(LinuxRecordingOverlayState.Recording, true, "Recording", "Default", "00:07", LinuxRecordingOverlayController.WaveformLevel(NormalSpeech))),
                    ("recording-hour", new(LinuxRecordingOverlayState.Recording, true, "Recording", new string('W', 64), "01:00:00", LinuxRecordingOverlayController.WaveformLevel(NormalSpeech))),
                    ("streaming-hour", new(LinuxRecordingOverlayState.Streaming, true, "Live transcription connection lost", "Default", "01:00:00", 0.6, LinuxStreamingOverlayConnectionState.Error)),
                })
                {
                    viewModel.Apply(snapshot);
                    Pump(window);
                    foreach (var waveform in window.GetVisualDescendants().OfType<LinuxOverlayWaveform>()) Settle(waveform, 3);
                    Pump(window);
                    Save(window, Path.Combine(shots, $"pill-{name}.png"));
                }
            }
            window.Close();
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    private static LinuxOverlayWaveform CheckWaveform(LinuxRecordingOverlayWindow window, LinuxRecordingOverlayViewModel viewModel,
        LinuxRecordingOverlayState state, List<string> failures)
    {
        Apply(viewModel, state, 0);
        window.ShowBestEffort();
        Pump(window);
        var waveform = window.GetVisualDescendants().OfType<LinuxOverlayWaveform>().SingleOrDefault(candidate => candidate.IsEffectivelyVisible)
            ?? throw new InvalidOperationException($"{state}: the overlay shows no waveform");
        var pill = Pill(window);
        var inner = Inner(pill);
        var origin = waveform.TranslatePoint(default, pill) ?? throw new InvalidOperationException($"{state}: the waveform is not in the pill");

        // Silence, then a loud voice: the smoothed level settles and the bars grow.
        var quiet = Settle(waveform, 2);
        Apply(viewModel, state, 1);
        Pump(window);
        var loud = Settle(waveform, 2);
        var bars = waveform.BarRects().ToArray();
        if (bars.Length != 25) failures.Add($"{state}: the waveform has {bars.Length} bars, not 25");
        for (var index = 0; index < bars.Length; index++)
        {
            var box = bars[index].Translate(origin);
            var label = $"{state} bar {index}";
            if (Math.Abs(box.Width - 2) > 0.01) failures.Add($"{label} is {box.Width:F1} px wide, not 2");
            if (index > 0 && Math.Abs(bars[index].X - bars[index - 1].X - 4) > 0.01)
                failures.Add($"{label} starts {bars[index].X - bars[index - 1].X:F1} px after the previous bar, not 4");
            if (box.Height > 24.01) failures.Add($"{label} is {box.Height:F1} px tall, more than the waveform's 24");
            if (!Contains(inner, box)) failures.Add($"{label} {Format(box)} leaves the pill's border {Format(inner)}");
            if (!(loud[index] > quiet[index] * 1.5)) failures.Add($"{label}: its mean height at level 1 is {loud[index]:F2} px, not taller than {quiet[index]:F2} px at silence");
        }
        Console.WriteLine($"{state}: waveform {Format(new Rect(origin, waveform.Bounds.Size))} in pill {Format(inner)}");
        Console.WriteLine($"{state}: mean bar heights at silence  {string.Join(' ', quiet.Select(height => height.ToString("F1")))}");
        Console.WriteLine($"{state}: mean bar heights at level 1 {string.Join(' ', loud.Select(height => height.ToString("F1")))}");

        // A center-peaked envelope: rising from each edge to the middle, symmetric about it.
        var peak = Array.IndexOf(loud, loud.Max());
        if (peak is < 10 or > 14) failures.Add($"{state}: the tallest bar on average is bar {peak}, not one in the middle");
        if (!(loud[12] > loud[6] * 1.2 && loud[6] > loud[0] * 1.2 && loud[12] > loud[18] * 1.2 && loud[18] > loud[24] * 1.2))
            failures.Add($"{state}: the bars do not rise to the middle (0 {loud[0]:F1}, 6 {loud[6]:F1}, 12 {loud[12]:F1}, 18 {loud[18]:F1}, 24 {loud[24]:F1})");

        // The bars are really drawn: 25 lit columns across the waveform in a render of the window.
        Pump(window);
        var columns = LitColumns(window, new Rect(origin, waveform.Bounds.Size));
        Console.WriteLine($"{state}: {columns} lit column runs in the rendered waveform");
        if (columns != 25) failures.Add($"{state}: a render of the waveform shows {columns} bars, not 25");

        // The real ~30 fps timer moves the bars while the waveform is shown. The first loop lets the
        // window's first frames render, which is what starts the timer.
        RunLoop(TimeSpan.FromMilliseconds(300));
        var before = waveform.BarRects().ToArray();
        var framesBefore = waveform.FrameCount;
        RunLoop(TimeSpan.FromMilliseconds(300));
        var after = waveform.BarRects().ToArray();
        Console.WriteLine($"{state}: {waveform.FrameCount - framesBefore} timer frames in 300 ms");
        if (waveform.FrameCount - framesBefore < 3) failures.Add($"{state}: only {waveform.FrameCount - framesBefore} timer frames ran in 300 ms");
        if (!waveform.IsFrameTimerRunning) failures.Add($"{state}: the waveform's frame timer is not running while it is shown");
        if (before.Zip(after).All(pair => Math.Abs(pair.First.Height - pair.Second.Height) < 0.01))
            failures.Add($"{state}: the bars did not move in 300 ms of the real timer");
        return waveform;
    }

    /// <summary>
    /// A long mode name or connection caption trims or wraps; it never squeezes the waveform's 25 bars.
    /// An hour-long timer ("01:00:00") beside them leaves less than the row's 98 px: the row then narrows
    /// evenly inside the waveform, every bar still drawn, none left of it or past its right edge.
    /// </summary>
    private static void CheckRoom(Window window, LinuxRecordingOverlayViewModel viewModel, List<string> failures)
    {
        var longMode = new string('W', 64);
        foreach (var snapshot in new LinuxRecordingOverlaySnapshot[]
        {
            new(LinuxRecordingOverlayState.Recording, true, "Recording", longMode, "59:59", 0.5),
            new(LinuxRecordingOverlayState.Streaming, true, "Live transcription connection lost", "Default", "59:59", 0.5, LinuxStreamingOverlayConnectionState.Error),
            new(LinuxRecordingOverlayState.Streaming, true, "Live transcription reconnecting…", "Default", "59:59", 0.5, LinuxStreamingOverlayConnectionState.Reconnecting),
            new(LinuxRecordingOverlayState.Recording, true, "Recording", longMode, "99:59:59", 0.5),
            new(LinuxRecordingOverlayState.Streaming, true, "Live transcription connection lost", "Default", "01:00:00", 0.5, LinuxStreamingOverlayConnectionState.Error),
        })
        {
            viewModel.Apply(snapshot);
            Pump(window);
            var waveform = window.GetVisualDescendants().OfType<LinuxOverlayWaveform>().Single(candidate => candidate.IsEffectivelyVisible);
            var pill = Pill(window);
            var box = Box(waveform, pill);
            var label = $"{snapshot.State} {snapshot.DurationText} \"{snapshot.StatusText}\" / \"{snapshot.ModeText}\"";
            var hours = snapshot.DurationText.Length > 5;
            var bars = waveform.BarRects().ToArray();
            Console.WriteLine($"{label}: waveform {Format(box)}, bars {bars[0].X:F1}..{bars[^1].Right:F1} at {bars[0].Width:F2} px");
            if (!hours && box.Width < LinuxOverlayWaveform.RowWidth - 0.5)
                failures.Add($"{label}: the waveform has {box.Width:F1} px, less than its {LinuxOverlayWaveform.RowWidth} px of bars");
            if (bars.Length != LinuxOverlayWaveform.BarCount) failures.Add($"{label}: {bars.Length} bars");
            if (bars[0].X < -0.01 || bars[^1].Right > waveform.Bounds.Width + 0.01)
                failures.Add($"{label}: the bars run {bars[0].X:F1}..{bars[^1].Right:F1}, outside the waveform's 0..{waveform.Bounds.Width:F1}");
            if (Math.Abs(bars[0].X - (waveform.Bounds.Width - bars[^1].Right)) > 1.01)
                failures.Add($"{label}: the bars are not centered ({bars[0].X:F1} px left, {waveform.Bounds.Width - bars[^1].Right:F1} px right)");
            if (bars.Any(bar => bar.Width < 1.5)) failures.Add($"{label}: the bars narrowed under 1.5 px");
            if (!Contains(Inner(pill), box)) failures.Add($"{label}: the waveform {Format(box)} leaves the pill");
            foreach (var text in pill.GetVisualDescendants().OfType<TextBlock>().Where(candidate => candidate.IsEffectivelyVisible))
                if (!Contains(Inner(pill), Box(text, pill))) failures.Add($"{label}: \"{text.Text}\" {Format(Box(text, pill))} leaves the pill");
        }
    }

    /// <summary>
    /// Show, hide, show again: the second showing of the face (and of the pill) must animate again, and
    /// start from silence rather than the last recording's level.
    /// </summary>
    private static void CheckReshow(LinuxRecordingOverlayWindow window, LinuxRecordingOverlayViewModel viewModel,
        LinuxOverlayWaveform waveform, List<string> failures)
    {
        void ExpectMoving(string label)
        {
            var before = waveform.BarRects().ToArray();
            var frames = waveform.FrameCount;
            RunLoop(TimeSpan.FromMilliseconds(300));
            var after = waveform.BarRects().ToArray();
            Console.WriteLine($"{label}: {waveform.FrameCount - frames} frames in 300 ms, timer {waveform.IsFrameTimerRunning}");
            if (!waveform.IsFrameTimerRunning || waveform.FrameCount - frames < 3)
                failures.Add($"{label}: the waveform's timer ran {waveform.FrameCount - frames} frames in 300 ms");
            if (before.Zip(after).All(pair => Math.Abs(pair.First.Height - pair.Second.Height) < 0.01))
                failures.Add($"{label}: the bars did not move");
        }
        void ExpectStopped(string label)
        {
            RunLoop(TimeSpan.FromMilliseconds(150));
            var frames = waveform.FrameCount;
            RunLoop(TimeSpan.FromMilliseconds(200));
            if (waveform.IsFrameTimerRunning || waveform.FrameCount != frames)
                failures.Add($"{label}: the waveform's timer still runs ({waveform.FrameCount - frames} frames)");
        }

        // First recording, loud.
        Apply(viewModel, LinuxRecordingOverlayState.Recording, 1);
        window.ShowBestEffort();
        Pump(window);
        RunLoop(TimeSpan.FromMilliseconds(600));
        var loudLevel = waveform.SmoothedLevel;
        ExpectMoving("first showing");

        // Another face covers it (the pill stays up), then the recording face comes back.
        viewModel.Apply(new(LinuxRecordingOverlayState.Transcribing, true, "Transcribing", "Default", "00:03"));
        Pump(window);
        ExpectStopped("face hidden");
        Apply(viewModel, LinuxRecordingOverlayState.Recording, 0);
        Pump(window);
        Console.WriteLine($"face shown again: smoothed level {waveform.SmoothedLevel:F2} (was {loudLevel:F2})");
        if (!(loudLevel > 0.3 && waveform.SmoothedLevel < 0.05))
            failures.Add($"face shown again: the waveform began at level {waveform.SmoothedLevel:F2}, the last recording's {loudLevel:F2}, not silence");
        Apply(viewModel, LinuxRecordingOverlayState.Recording, 1);
        ExpectMoving("face shown again");

        // The pill hides between recordings, then the next recording shows it again.
        window.HideBestEffort();
        ExpectStopped("pill hidden");
        Apply(viewModel, LinuxRecordingOverlayState.Recording, 0.6);
        window.ShowBestEffort();
        Pump(window);
        ExpectMoving("pill shown again");
    }

    /// <summary>
    /// Normal speech, about -22 dBFS RMS, through the controller's level mapping, must give a lively wave
    /// like macOS's (whose -60..0 dB mapping reads it as about 0.63), not a flat one.
    /// </summary>
    private static void CheckNormalSpeech(LinuxRecordingOverlayWindow window, LinuxRecordingOverlayViewModel viewModel,
        LinuxOverlayWaveform waveform, List<string> failures)
    {
        var level = LinuxRecordingOverlayController.WaveformLevel(NormalSpeech);
        Apply(viewModel, LinuxRecordingOverlayState.Recording, level);
        Pump(window);
        var heights = Settle(waveform, 2);
        var tallest = waveform.BarRects().ToArray().Max(bar => bar.Height);
        Console.WriteLine($"normal speech (-22 dBFS): level {level:F2}, middle bar mean {heights[12]:F1} px, tallest now {tallest:F1} px of 24");
        if (!(heights[12] >= 10)) failures.Add($"normal speech: the middle bar averages {heights[12]:F1} px of 24, a flat wave");
    }

    /// <summary>A capture level (mean |sample|) for speech at about -22 dBFS.</summary>
    internal const float NormalSpeech = 0.0794f;

    /// <summary>Advances <paramref name="seconds"/> of 30 fps frames and returns each bar's mean height over the last second.</summary>
    private static double[] Settle(LinuxOverlayWaveform waveform, double seconds)
    {
        var total = (int)Math.Round(seconds / Frame);
        var sampled = (int)Math.Round(1 / Frame);
        var sums = new double[LinuxOverlayWaveform.BarCount];
        for (var frame = 0; frame < total; frame++)
        {
            waveform.Advance(TimeSpan.FromSeconds(Frame));
            if (frame < total - sampled) continue;
            var bars = waveform.BarRects().ToArray();
            for (var index = 0; index < bars.Length && index < sums.Length; index++) sums[index] += bars[index].Height;
        }
        return sums.Select(sum => sum / sampled).ToArray();
    }

    private static void Apply(LinuxRecordingOverlayViewModel viewModel, LinuxRecordingOverlayState state, double level) =>
        viewModel.Apply(new(state, true, state == LinuxRecordingOverlayState.Streaming ? "Streaming" : "Recording", "Default", "00:03", level,
            state == LinuxRecordingOverlayState.Streaming ? LinuxStreamingOverlayConnectionState.Connected : null));

    /// <summary>Counts runs of columns inside <paramref name="area"/> that hold a bright pixel in a render of the window.</summary>
    private static int LitColumns(Window window, Rect area)
    {
        using var bitmap = Render(window);
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var buffer = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, pixels.Length, stride);
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        var pillOrigin = Pill(window).TranslatePoint(default, window) ?? default;
        var box = area.Translate(pillOrigin);
        var runs = 0;
        var lit = false;
        for (var x = (int)Math.Floor(box.X); x < (int)Math.Ceiling(box.Right) && x < size.Width; x++)
        {
            var column = false;
            for (var y = Math.Max(0, (int)Math.Floor(box.Y)); y < (int)Math.Ceiling(box.Bottom) && y < size.Height; y++)
                if (pixels[y * stride + x * 4 + 2] > 48) { column = true; break; }
            if (column && !lit) runs++;
            lit = column;
        }
        return runs;
    }

    private static RenderTargetBitmap Render(Window window)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        bitmap.Render(window);
        return bitmap;
    }

    private static void Save(Window window, string path)
    {
        using var bitmap = Render(window);
        bitmap.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {path}");
    }

    private static Border Pill(Window window) =>
        window.Content as Border ?? throw new InvalidOperationException("the overlay's content is not its pill Border");

    private static Rect Inner(Border pill) => new Rect(pill.Bounds.Size).Deflate(pill.BorderThickness);

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

    /// <summary>Runs the real dispatcher loop, timers included, for <paramref name="duration"/>.</summary>
    private static void RunLoop(TimeSpan duration)
    {
        using var stop = new CancellationTokenSource(duration);
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    private sealed class MemoryPlacementStore : ILinuxOverlayPlacementStore
    {
        private LinuxOverlayPlacement? _placement;
        public LinuxOverlayPlacement? Load() => _placement;
        public void Save(LinuxOverlayPlacement placement) => _placement = placement;
    }
}
