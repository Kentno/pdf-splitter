using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PdfSplitter;

/// <summary>UI behaviour for the card layout: empty states, selection info, and dragging pages onto splits.</summary>
public partial class MainWindow
{
    const string PagesFormat = "PdfSplitter.Pages";

    /// <summary>
    /// True while pages are being dragged. The editable split-name boxes ignore the mouse during a drag:
    /// a TextBox's built-in drag handling would otherwise veto the drop onto the card underneath it.
    /// </summary>
    public static readonly DependencyProperty IsDraggingPagesProperty =
        DependencyProperty.Register(nameof(IsDraggingPages), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsDraggingPages
    {
        get => (bool)GetValue(IsDraggingPagesProperty);
        set => SetValue(IsDraggingPagesProperty, value);
    }

    bool _poolMouseDown, _collapseSelectionOnUp;
    Point _dragStart;
    ListBoxItem? _dragItem;

    void InitRedesign()
    {
        _pool.CollectionChanged += (_, _) => UpdateEmptyStates();
        _splits.CollectionChanged += (_, _) => UpdateEmptyStates();
        _sources.CollectionChanged += (_, _) => UpdateEmptyStates();
        UpdateEmptyStates();
    }

    void UpdateEmptyStates()
    {
        PoolEmpty.Visibility = _pool.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SplitsEmpty.Visibility = _splits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourcesList.Visibility = _sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionInfo();
        SplitsCountText.Text = _splits.Count == 0 ? "" : $"{_splits.Count} split{(_splits.Count == 1 ? "" : "s")}";
    }

    void UpdateSelectionInfo()
    {
        PoolInfoText.Text = _pool.Count == 0
            ? ""
            : $"{_pool.Count:N0} page{(_pool.Count == 1 ? "" : "s")}" +
              (PoolList.SelectedItems.Count > 0 ? $"  ·  {PoolList.SelectedItems.Count:N0} selected" : "");
    }

    void PoolList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionInfo();

    // ---------- small card actions ----------

    void RemoveChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceDoc doc })
        {
            SourcesList.SelectedItem = doc;
            RemoveSource_Click(sender, e);
        }
        e.Handled = true;
    }

    void DeleteSplitCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SplitItem split }) _splits.Remove(split);
        e.Handled = true;
    }

    void SplitName_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SplitItem split }) SplitsList.SelectedItem = split;
    }

    // ---------- dragging pages out of the grid ----------

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void Pool_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _poolMouseDown = false;
        _collapseSelectionOnUp = false;
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;

        _poolMouseDown = true;
        _dragStart = e.GetPosition(null);
        _dragItem = item;

        // Pressing on an already-selected page of a multi-selection must not collapse the selection,
        // otherwise you could never drag several pages. Collapse on mouse-up if no drag happened.
        if (item.IsSelected && Keyboard.Modifiers == ModifierKeys.None && PoolList.SelectedItems.Count > 1)
        {
            _collapseSelectionOnUp = true;
            PoolList.Focus();
            e.Handled = true;
        }
    }

    void Pool_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_poolMouseDown || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _poolMouseDown = false;
        _collapseSelectionOnUp = false;
        var pages = SelectedPoolPages();
        if (pages.Count == 0) return;
        SetStatus($"Dragging {pages.Count} page(s): drop on a split, or on the empty area to make a new split.");
        IsDraggingPages = true;
        try { DragDrop.DoDragDrop(PoolList, new DataObject(PagesFormat, pages), DragDropEffects.Copy); }
        finally { IsDraggingPages = false; }
    }

    void Pool_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_collapseSelectionOnUp && _dragItem != null)
        {
            PoolList.SelectedItems.Clear();
            _dragItem.IsSelected = true;
        }
        _poolMouseDown = false;
        _collapseSelectionOnUp = false;
    }

    // ---------- dropping pages on splits ----------

    static List<PageRef>? DraggedPages(DragEventArgs e) =>
        e.Data.GetDataPresent(PagesFormat) ? e.Data.GetData(PagesFormat) as List<PageRef> : null;

    void SplitCard_DragOver(object sender, DragEventArgs e)
    {
        if (DraggedPages(e) == null) return;   // file drops fall through to the window handler
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (sender is ListBoxItem item) item.Tag = "drop";
    }

    void SplitCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBoxItem item) item.Tag = null;
    }

    void SplitCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SplitItem split } item) return;
        item.Tag = null;
        var pages = DraggedPages(e);
        if (pages == null) return;

        foreach (var p in pages) split.Pages.Add(new PageRef(p.Doc, p.Index));
        SplitsList.SelectedItem = split;
        SetStatus($"Added {pages.Count} page(s) to “{split.Name}”.");
        e.Handled = true;
    }

    void SplitsArea_DragOver(object sender, DragEventArgs e)
    {
        if (DraggedPages(e) == null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void SplitsArea_Drop(object sender, DragEventArgs e)
    {
        var pages = DraggedPages(e);
        if (pages == null) return;
        AddSplit(DefaultName(pages, _splits.Count + 1), pages);
        SetStatus($"Created a new split from {pages.Count} dragged page(s).");
        e.Handled = true;
    }
}
