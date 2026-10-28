using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// The recording pill's waveform, a port of macOS's <c>WaveformNSView</c> (WaveformCAView.swift):
/// 25 bars, 2 px wide with 2 px gaps, white at 0.4 opacity, under a center-peaked quadratic envelope.
/// Each bar loops its own 10-keyframe height wave (1.5-1.9 s at speed 2), and the whole row scales
/// from 25% (silence) to 100% of the control's height with a smoothed audio level.
/// Its ~30 fps frame timer runs only while it is shown (<see cref="LinuxOverlayAnimatedControl"/>).
/// </summary>
internal sealed class LinuxOverlayWaveform : LinuxOverlayAnimatedControl
{
    public const int BarCount = 25;
    public const double BarWidth = 2;
    public const double BarSpacing = 2;
    public const double RowWidth = BarCount * (BarWidth + BarSpacing) - BarSpacing;
    private const int Keyframes = 10;
    private const double NoiseFloor = 0.08;
    // macOS smooths by 0.10 per level update at ~30 FPS; this applies the same per 1/30 s of real time.
    private const double SmoothingPerFrame = 0.10;
    private const double AnimationSpeed = 2.0;
    private static readonly IBrush BarBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LinuxOverlayWaveform, double>(nameof(Level));

    private static readonly double[] Envelope = BuildEnvelope();
    private static readonly double[][] Waves = BuildWaves();
    private double _time;
    private double _smoothedLevel;

    /// <summary>The audio level, 0 (silence) to 1.</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    internal double SmoothedLevel => _smoothedLevel;

    protected override Size MeasureOverride(Size availableSize) =>
        new(RowWidth, double.IsFinite(availableSize.Height) ? Math.Min(availableSize.Height, 24) : 24);

    /// <summary>Advances the wave and the level smoothing by <paramref name="elapsed"/> of animation time.</summary>
    internal override void Advance(TimeSpan elapsed)
    {
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.5);
        _time += seconds * AnimationSpeed;
        var level = double.IsFinite(Level) ? Math.Clamp(Level, 0, 1) : 0;
        var target = level < NoiseFloor ? 0 : level;
        var keep = Math.Pow(1 - SmoothingPerFrame, seconds / FrameInterval.TotalSeconds);
        _smoothedLevel = _smoothedLevel * keep + target * (1 - keep);
    }

    /// <summary>Each bar's rectangle in this control's coordinates, as Render draws them now.</summary>
    internal Rect[] BarRects()
    {
        var bounds = Bounds;
        var rects = new Rect[BarCount];
        var startX = Math.Round((bounds.Width - RowWidth) / 2); // whole pixels keep the 2 px bars crisp
        var rowScale = 0.25 + 0.75 * Math.Clamp(_smoothedLevel, 0, 1);
        for (var index = 0; index < BarCount; index++)
        {
            var height = bounds.Height * rowScale * BarScale(index, _time);
            rects[index] = new Rect(startX + index * (BarWidth + BarSpacing), (bounds.Height - height) / 2, BarWidth, height);
        }
        return rects;
    }

    public override void Render(DrawingContext context)
    {
        foreach (var rect in BarRects())
            context.DrawRectangle(BarBrush, null, rect, BarWidth / 2, BarWidth / 2);
        base.Render(context);
    }

    /// <summary>A bar's height as a fraction of the row, looping its keyframes linearly.</summary>
    private static double BarScale(int index, double time)
    {
        var duration = 1.5 + index % 5 * 0.1;
        var position = time % duration / duration * Keyframes;
        var frame = (int)position % Keyframes;
        var fraction = position - Math.Floor(position);
        var wave = Waves[index];
        return wave[frame] + (wave[(frame + 1) % Keyframes] - wave[frame]) * fraction;
    }

    private static double[] BuildEnvelope()
    {
        var center = (BarCount - 1) / 2.0;
        var envelope = new double[BarCount];
        for (var index = 0; index < BarCount; index++)
        {
            var value = 1 - Math.Abs(index - center) / Math.Max(center, 1);
            envelope[index] = value * value;
        }
        return envelope;
    }

    private static double[][] BuildWaves()
    {
        var waves = new double[BarCount][];
        for (var index = 0; index < BarCount; index++)
        {
            var phase = (double)index / BarCount * 2 * Math.PI;
            waves[index] = new double[Keyframes];
            for (var frame = 0; frame < Keyframes; frame++)
            {
                var wave = (Math.Sin((double)frame / Keyframes * 2 * Math.PI + phase) + 1) * 0.5;
                waves[index][frame] = 0.15 + Envelope[index] * 0.85 * (0.35 + 0.65 * wave);
            }
        }
        return waves;
    }
}
