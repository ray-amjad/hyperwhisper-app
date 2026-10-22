using Avalonia;
using Avalonia.Controls;

namespace HyperWhisper.Linux;

/// <summary>
/// The row of segments inside a segmented control (Border.segmented). Windows lays its segments
/// out in a one-row UniformGrid, and so did Linux, but Inter is wider than Segoe UI: in the 550px
/// mode editor a third of the control is 154px and "HyperWhisper Cloud" needs about 173px with
/// its padding, so the label was cut to "HyperWhisper Clouc" (issue #635).
///
/// So the segments are equal while every label fits in an equal share, exactly as UniformGrid
/// drew them. When one does not, each segment gets its own label's width plus an equal share of
/// the space left over, so no label is clipped while the row still fills the control. Only a row
/// whose labels together are wider than the control is scaled down, in proportion.
/// </summary>
public sealed class SegmentedPanel : Panel
{
    private double[] _natural = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(child => child.IsVisible).ToList();
        if (children.Count == 0) return default;

        _natural = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _natural[i] = children[i].DesiredSize.Width;
        }

        var equal = _natural.Max() * children.Count;
        var width = double.IsInfinity(availableSize.Width) ? equal : Math.Min(equal, availableSize.Width);
        var widths = Distribute(width);
        var height = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children.Where(child => child.IsVisible).ToList();
        if (children.Count != _natural.Length) return finalSize;

        var widths = Distribute(finalSize.Width);
        var x = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
        }
        return finalSize;
    }

    private double[] Distribute(double width)
    {
        var count = _natural.Length;
        var sum = _natural.Sum();
        if (_natural.Max() * count <= width) return [.. _natural.Select(_ => width / count)];
        if (sum <= width) return [.. _natural.Select(natural => natural + (width - sum) / count)];
        return [.. _natural.Select(natural => sum > 0 ? natural * width / sum : width / count)];
    }
}
