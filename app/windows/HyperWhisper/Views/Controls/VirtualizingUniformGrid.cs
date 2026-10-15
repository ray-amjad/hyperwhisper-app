using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace HyperWhisper.Views.Controls;

/// <summary>
/// A <see cref="UniformGrid"/> look-alike that only builds the cells on screen.
///
/// Issue #1593: a backup import has no mode cap, and the Modes page laid every card
/// out in a plain UniformGrid, so 10,000 modes built 10,000 cards on the UI thread
/// and hung the window for about 100 s. WPF ships no virtualising grid, and
/// VirtualizingStackPanel can only stack, so this panel keeps the fixed-column card
/// layout while realising just the rows in view (plus one either side).
///
/// Every row is as tall as the tallest card built so far; the cards are uniform, so
/// that is the UniformGrid behaviour without measuring every item. It scrolls by
/// pixel through <see cref="IScrollInfo"/>, so its ItemsControl must host it in a
/// ScrollViewer with CanContentScroll="True".
/// </summary>
public class VirtualizingUniformGrid : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns), typeof(int), typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure),
            value => value is int columns && columns >= 1);

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    // Rows built beyond each edge of the viewport. One row covers the page margin
    // the ItemsPresenter keeps around the panel, so no card pops in at an edge.
    private const int CacheRows = 1;
    private const double LineDelta = 16;
    private const double WheelDelta = 48;

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        // InternalChildren must be read before the generator: it is what hooks the
        // generator up for a VirtualizingPanel.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var columns = Columns;
        var cellWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : availableSize.Width / columns;

        if (itemCount == 0)
        {
            RemoveItemsOutside(0, -1);
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(0, 0);
        }

        var rowCount = (itemCount + columns - 1) / columns;

        // A card taller than the row height so far moves every row below it, so the
        // visible range is worked out again with the new height. Two passes settle it:
        // the second one measures the same cards again and finds nothing taller.
        var widest = 0.0;
        for (var pass = 0; pass < 2; pass++)
        {
            var rowHeightBefore = _rowHeight;
            var firstRow = _rowHeight > 0 ? (int)Math.Floor(_offset.Y / _rowHeight) - CacheRows : 0;
            firstRow = Math.Clamp(firstRow, 0, rowCount - 1);
            var firstIndex = firstRow * columns;
            var lastIndex = firstIndex - 1;

            var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
            var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

            using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
            {
                for (var itemIndex = firstIndex; itemIndex < itemCount; itemIndex++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count)
                            AddInternalChild(child);
                        else
                            InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(cellWidth, double.PositiveInfinity));
                    _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
                    widest = Math.Max(widest, child.DesiredSize.Width);
                    lastIndex = itemIndex;

                    // Stop at the end of the first row that reaches past the viewport
                    // and its cache. An infinite height (no ScrollViewer) builds all.
                    if ((itemIndex + 1) % columns == 0 && _rowHeight > 0)
                    {
                        var rowsBuilt = (itemIndex + 1) / columns;
                        var bottom = rowsBuilt * _rowHeight;
                        if (bottom >= _offset.Y + availableSize.Height + CacheRows * _rowHeight)
                            break;
                    }
                }
            }

            RemoveItemsOutside(firstIndex, lastIndex);

            if (_rowHeight <= rowHeightBefore)
                break;
        }

        var width = double.IsInfinity(availableSize.Width) ? widest * columns : availableSize.Width;
        var extent = new Size(width, rowCount * _rowHeight);
        UpdateScrollInfo(availableSize, extent);

        return new Size(width, Math.Min(extent.Height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var columns = Columns;
        var cellWidth = finalSize.Width / columns;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0)
                continue;

            var row = itemIndex / columns;
            var column = itemIndex % columns;
            InternalChildren[i].Arrange(new Rect(
                column * cellWidth, row * _rowHeight - _offset.Y, cellWidth, _rowHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || _rowHeight <= 0)
            return;

        ScrollRowIntoView(index / Columns);
        UpdateLayout();
    }

    /// <summary>
    /// Drops the containers for items outside [first, last]. A card holding keyboard
    /// focus is kept, so scrolling it out of view does not throw focus away.
    /// </summary>
    private void RemoveItemsOutside(int first, int last)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;

        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= first && itemIndex <= last)
                continue;
            if (itemIndex >= 0 && children[i].IsKeyboardFocusWithin)
                continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void ScrollRowIntoView(int row)
    {
        var top = row * _rowHeight;
        var bottom = top + _rowHeight;

        if (top < _offset.Y)
            SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(bottom - _viewport.Height);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        if (double.IsInfinity(viewport.Height))
            viewport.Height = extent.Height;
        if (double.IsInfinity(viewport.Width))
            viewport.Width = extent.Width;

        // A shorter list (a reload after a delete, or a smaller window) must not
        // leave the offset past the new end.
        var offsetY = Math.Max(0, Math.Min(_offset.Y, extent.Height - viewport.Height));

        if (viewport == _viewport && extent == _extent && offsetY == _offset.Y)
            return;

        _viewport = viewport;
        _extent = extent;
        _offset.Y = offsetY;
        ScrollOwner?.InvalidateScrollInfo();
    }

    // =========================================================================
    // IScrollInfo — vertical, by pixel. The grid always fits its width.
    // =========================================================================

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - LineDelta);
    public void LineDown() => SetVerticalOffset(_offset.Y + LineDelta);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - WheelDelta);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + WheelDelta);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset == _offset.Y)
            return;

        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual is null || !IsAncestorOf(visual))
            return Rect.Empty;

        // Walk up to the container this panel owns, then scroll its row into view.
        DependencyObject current = visual;
        while (VisualTreeHelper.GetParent(current) is { } parent && !ReferenceEquals(parent, this))
            current = parent;

        if (current is UIElement child)
        {
            var childIndex = InternalChildren.IndexOf(child);
            var itemIndex = childIndex < 0
                ? -1
                : ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex >= 0 && _rowHeight > 0)
                ScrollRowIntoView(itemIndex / Columns);
        }

        return rectangle;
    }
}
