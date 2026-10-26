using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PdfSplitter;

/// <summary>
/// A wrapping grid of fixed-size items that only creates containers for the rows on screen (plus one row
/// above and below), so a 5,000-page document costs about as much as a 50-page one. Scrolls vertically.
/// </summary>
public class VirtualizingGridPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingGridPanel),
        new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingGridPanel),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>How many rows above and below the visible ones keep their containers (the viewer's "preload N pages").</summary>
    public static readonly DependencyProperty CacheRowsProperty = DependencyProperty.Register(
        nameof(CacheRows), typeof(int), typeof(VirtualizingGridPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Upper limit for the number of columns (1 gives a single scrolling column of pages).</summary>
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(VirtualizingGridPanel),
        new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Pixels per mouse-wheel notch; 0 means half an item.</summary>
    public static readonly DependencyProperty ScrollStepProperty = DependencyProperty.Register(
        nameof(ScrollStep), typeof(double), typeof(VirtualizingGridPanel), new PropertyMetadata(0.0));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public int CacheRows { get => (int)GetValue(CacheRowsProperty); set => SetValue(CacheRowsProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double ScrollStep { get => (double)GetValue(ScrollStepProperty); set => SetValue(ScrollStepProperty, value); }

    Size _extent, _viewport;
    double _offsetY;

    int Columns(double width) => Math.Clamp((int)(width / ItemWidth), 1, Math.Max(1, MaxColumns));
    double WheelStep => ScrollStep > 0 ? ScrollStep : ItemHeight / 2;

    // ---------- layout ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = InternalChildren;   // the item container generator only exists once this has been touched
        var owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;

        double width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 800 : availableSize.Height;
        int cols = Columns(width);
        int rows = (count + cols - 1) / cols;
        UpdateScrollInfo(new Size(width, rows * ItemHeight), new Size(width, height));

        int cache = Math.Max(0, CacheRows);
        int firstRow = Math.Max(0, (int)(_offsetY / ItemHeight) - cache);
        int lastRow = (int)((_offsetY + height) / ItemHeight) + cache;
        int first = Math.Min(count, firstRow * cols);
        int last = Math.Min(count - 1, (lastRow + 1) * cols - 1);

        Realize(first, last);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(ItemWidth, ItemHeight));

        return new Size(width, height);
    }

    void Realize(int first, int last)
    {
        var generator = ItemContainerGenerator;
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool isNew);
                    if (!isNew) continue;
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
            }
        }

        // Recycle everything that scrolled out of range.
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex < first || itemIndex > last)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Columns(finalSize.Width);
        double pad = Math.Max(0, (finalSize.Width - cols * ItemWidth) / 2);   // centre the grid
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            int row = index / cols, col = index % cols;
            InternalChildren[i].Arrange(new Rect(pad + col * ItemWidth, row * ItemHeight - _offsetY, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                _offsetY = 0;
                break;
        }
        InvalidateMeasure();
    }

    // ---------- IScrollInfo ----------

    void UpdateScrollInfo(Size extent, Size viewport)
    {
        bool changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        double max = Math.Max(0, extent.Height - viewport.Height);
        if (_offsetY > max) { _offsetY = max; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offsetY;

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offsetY) < 0.01) return;
        _offsetY = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offsetY - 40);
    public void LineDown() => SetVerticalOffset(_offsetY + 40);
    public void PageUp() => SetVerticalOffset(_offsetY - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offsetY + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offsetY - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offsetY + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>Lets ListBox.ScrollIntoView work: scroll just far enough to show the item's row.</summary>
    protected override void BringIndexIntoView(int index)
    {
        int cols = Columns(_viewport.Width);
        double top = index / cols * ItemHeight;
        if (top < _offsetY) SetVerticalOffset(top);
        else if (top + ItemHeight > _offsetY + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        int child = InternalChildren.IndexOf(visual as UIElement);
        if (child < 0) return rectangle;
        int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(child, 0));
        int cols = Columns(_viewport.Width);
        double top = index / cols * ItemHeight;
        if (top < _offsetY) SetVerticalOffset(top);
        else if (top + ItemHeight > _offsetY + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
        return rectangle;
    }
}
