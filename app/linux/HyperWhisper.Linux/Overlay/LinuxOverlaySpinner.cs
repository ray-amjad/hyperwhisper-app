using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace HyperWhisper.Linux.Overlay;

/// <summary>
/// The Transcribing pill's small white activity spinner, standing in for macOS's
/// <c>ProgressView().progressViewStyle(.circular).controlSize(.small)</c>: spokes around a
/// center whose brightest spoke steps round once every 0.8 s. Like the waveform it animates
/// only while it is shown.
/// </summary>
internal sealed class LinuxOverlaySpinner : LinuxOverlayAnimatedControl
{
    public const double Diameter = 16;
    private const int Spokes = 8;
    private const double Period = 0.8;
    private static readonly IPen[] Pens = BuildPens();
    private double _time;

    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter);

    internal override void Advance(TimeSpan elapsed) =>
        _time = (_time + Math.Clamp(elapsed.TotalSeconds, 0, 0.5)) % Period;

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var head = (int)(_time / Period * Spokes) % Spokes;
        for (var spoke = 0; spoke < Spokes; spoke++)
        {
            var angle = spoke * 2 * Math.PI / Spokes - Math.PI / 2;
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            var age = (head - spoke + Spokes) % Spokes;
            context.DrawLine(Pens[age], center + direction * radius * 0.45, center + direction * radius * 0.9);
        }
        base.Render(context);
    }

    private static IPen[] BuildPens()
    {
        var pens = new IPen[Spokes];
        for (var age = 0; age < Spokes; age++)
        {
            var alpha = (byte)Math.Round(255 * (1 - 0.8 * age / Spokes));
            pens[age] = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)),
                1.75, lineCap: PenLineCap.Round);
        }
        return pens;
    }
}
