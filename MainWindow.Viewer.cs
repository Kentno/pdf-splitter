using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PdfSplitter;

/// <summary>
/// Full-screen page viewer. One scrolling column of fixed-size page slots (so jumping anywhere is instant);
/// only the slots on screen plus <see cref="ViewerCacheRows"/> before and after them are realised and rendered,
/// everything else is released.
/// </summary>
public partial class MainWindow
{
    const double ViewerBaseWidth = 720;   // page width at 100% zoom, in device-independent pixels
    const double ViewerPad = 24;          // space around the page inside its slot
    const double ViewerCaption = 28;      // caption row under each page

    public static readonly DependencyProperty ViewerItemWidthProperty =
        DependencyProperty.Register(nameof(ViewerItemWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(744.0));
    public static readonly DependencyProperty ViewerItemHeightProperty =
        DependencyProperty.Register(nameof(ViewerItemHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(1100.0));
    public static readonly DependencyProperty ViewerCacheRowsProperty =
        DependencyProperty.Register(nameof(ViewerCacheRows), typeof(int), typeof(MainWindow), new PropertyMetadata(3));

    public double ViewerItemWidth { get => (double)GetValue(ViewerItemWidthProperty); set => SetValue(ViewerItemWidthProperty, value); }
    public double ViewerItemHeight { get => (double)GetValue(ViewerItemHeightProperty); set => SetValue(ViewerItemHeightProperty, value); }
    public int ViewerCacheRows { get => (int)GetValue(ViewerCacheRowsProperty); set => SetValue(ViewerCacheRowsProperty, value); }

    ViewerSource? _vsource;
    List<ViewerPage> _vpages = new();
    readonly HashSet<ViewerPage> _vloaded = new();   // slots currently inside the realised window
    readonly List<ViewerPage> _vflagged = new();     // slots currently showing range highlight/markers
    double _vzoom = 1.0, _vaspect = 0.707;
    int _vcurrent = -1;                              // 0-based page in the middle of the screen
    int _markA = -1, _markB = -1;                    // 0-based pages marked with S and E
    ScrollViewer? _vscroll;
    bool _vswitching;

    void InitViewer()
    {
        PreviewKeyDown += Viewer_PreviewKeyDown;
        ViewerList.PreviewMouseWheel += Viewer_PreviewMouseWheel;

        ViewerPreloadBox.ItemsSource = new[] { 1, 2, 3, 5, 8 };
        var preload = SettingsStore.Current.ViewerPreload;
        ViewerPreloadBox.SelectedItem = new[] { 1, 2, 3, 5, 8 }.Contains(preload) ? preload : 3;
        ViewerCacheRows = (int)ViewerPreloadBox.SelectedItem;
        ViewerTargetBox.ItemsSource = _splits;
    }

    // ---------- opening and closing ----------

    void ViewPages_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPoolPages();
        if (selected.Count > 0) OpenViewer(selected[0].Doc, null, selected[0].Index);
        else if (SourcesList.SelectedItem is SourceDoc d) OpenViewer(d, null, 0);
        else if (_sources.Count > 0) OpenViewer(_sources[0], null, 0);
        else SetStatus("Add a file first, then you can view its pages full screen.");
    }

    void ViewSplit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SplitItem split }) OpenViewer(null, split, 0);
        e.Handled = true;
    }

    void Pool_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not PageRef page) return;
        _poolMouseDown = false;
        _collapseSelectionOnUp = false;
        OpenViewer(page.Doc, null, page.Index);
        e.Handled = true;
    }

    void OpenViewer(SourceDoc? doc, SplitItem? split, int pageIndex)
    {
        if (_sources.Count == 0 && _splits.Count == 0) { SetStatus("Nothing to view yet."); return; }

        var items = BuildViewerSources();
        var target = items.FirstOrDefault(i => (doc != null && i.Doc == doc) || (split != null && i.Split == split)) ?? items[0];

        _vswitching = true;
        ViewerSourceBox.ItemsSource = items;
        ViewerSourceBox.SelectedItem = target;
        _vswitching = false;
        ViewerTargetBox.SelectedItem = SplitsList.SelectedItem ?? (_splits.Count > 0 ? _splits[^1] : null);

        // Where the tiles/card are on screen must be captured before the viewer covers them.
        var plan = AnimationsEnabled && !_animating && ViewerOverlay.Visibility != Visibility.Visible ? PlanOpen(target) : null;

        if (plan != null) { ViewerOverlay.Opacity = 0; ViewerOverlay.IsHitTestVisible = false; }
        ViewerOverlay.Visibility = Visibility.Visible;
        ViewerOverlay.Focus();
        ViewerOverlay.UpdateLayout();   // so the list has a real height to fit the page into
        _vaspect = PageAspectOfSource(target);
        _vzoom = FitZoom();
        LoadView(target, pageIndex);

        if (plan != null && _vpages.Count > 0) _ = PlayOpenAsync(plan, target, Math.Clamp(pageIndex, 0, _vpages.Count - 1));
        else { ViewerOverlay.Opacity = 1; ViewerOverlay.IsHitTestVisible = true; }
    }

    List<ViewerSource> BuildViewerSources()
    {
        var items = new List<ViewerSource>();
        foreach (var d in _sources)
            items.Add(new ViewerSource { Title = $"File · {d.Name} ({d.PageCount:N0} page{(d.PageCount == 1 ? "" : "s")})", Doc = d });
        foreach (var s in _splits)
            items.Add(new ViewerSource { Title = $"Split · {s.Name} ({s.Pages.Count:N0} page{(s.Pages.Count == 1 ? "" : "s")})", Split = s });
        return items;
    }

    /// <summary>Rebuilds the "what to view" list (new splits, changed page counts) and keeps the current selection.</summary>
    void RefreshViewerSources()
    {
        var items = BuildViewerSources();
        var current = _vsource;
        var match = items.FirstOrDefault(i => i.Doc == current?.Doc && i.Split == current?.Split);
        _vswitching = true;
        ViewerSourceBox.ItemsSource = items;
        ViewerSourceBox.SelectedItem = match;
        _vswitching = false;
        _vsource = match ?? _vsource;
    }

    static double PageAspectOfSource(ViewerSource src)
    {
        var first = src.Doc != null ? new PageRef(src.Doc, 0) : src.Split?.Pages.FirstOrDefault();
        return first != null ? PageAspect(first) : 0.707;
    }

    /// <summary>Zoom at which one whole page (plus caption) just fits the height of the viewer.</summary>
    double FitZoom()
    {
        double available = ViewerList.ActualHeight;
        if (available < 200) return 1.0;
        double pageHeight = Math.Max(100, available - ViewerPad - ViewerCaption - 8);
        double width = pageHeight * Math.Clamp(_vaspect, 0.3, 3.0);
        return Math.Clamp(width / ViewerBaseWidth, 0.4, 2.5);
    }

    void ViewerFit_Click(object sender, RoutedEventArgs e)
    {
        if (_vpages.Count == 0) return;
        ViewerZoom(FitZoom() / _vzoom);
    }

    void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseViewer();

    void CloseViewer()
    {
        if (ViewerOverlay.Visibility != Visibility.Visible || _animating) return;
        var plan = AnimationsEnabled ? PlanClose() : null;
        if (plan != null) _ = PlayCloseAsync(plan);
        else CloseViewerNow();
    }

    void CloseViewerNow()
    {
        if (ViewerOverlay.Visibility != Visibility.Visible) return;

        // Bring the page grid to where the reader stopped, without changing the user's selection.
        PageRef? back = _vsource?.Doc != null && _vcurrent >= 0 && _vcurrent < _vpages.Count ? _vpages[_vcurrent].Ref : null;

        ViewerOverlay.Visibility = Visibility.Collapsed;
        ViewerList.ItemsSource = null;   // releases every slot and its bitmap
        _vpages = new List<ViewerPage>();
        _vloaded.Clear();
        _vflagged.Clear();
        _vsource = null;
        _vcurrent = -1;
        _markA = _markB = -1;

        if (back != null) PoolList.ScrollIntoView(back);
        PoolList.Focus();
    }

    void ViewerSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vswitching || ViewerSourceBox.SelectedItem is not ViewerSource src) return;
        LoadView(src, 0);
        ViewerOverlay.Focus();
    }

    // ---------- building the view ----------

    void LoadView(ViewerSource src, int pageIndex)
    {
        foreach (var p in _vloaded) CancelViewerRequests(p);   // pending renders for the old view are no longer wanted
        _vloaded.Clear();
        _vflagged.Clear();
        _markA = _markB = -1;
        _vsource = src;

        var refs = src.Doc != null ? _pool.Where(p => p.Doc == src.Doc).ToList() : src.Split!.Pages.ToList();
        bool isSplit = src.Split != null;
        _vpages = refs.Select((r, i) => new ViewerPage(r, i + 1, isSplit)).ToList();
        _vaspect = refs.Count > 0 ? PageAspect(refs[0]) : 0.707;
        _vcurrent = -1;

        ApplyGeometry();
        ViewerList.ItemsSource = _vpages;
        ViewerEmpty.Visibility = _vpages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewerPageCount.Text = $"/ {_vpages.Count:N0}";
        ViewerPageBox.Text = _vpages.Count > 0 ? "1" : "";
        UpdateViewerTools();

        ViewerList.UpdateLayout();
        EnsureScroll();
        ViewerGoTo(pageIndex);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ViewerGoTo(pageIndex));   // once the extent is final
    }

    static double PageAspect(PageRef r)
    {
        try
        {
            if (r.Doc.IsImage)
            {
                var frame = BitmapFrame.Create(new Uri(r.Doc.Path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return frame.PixelWidth / (double)frame.PixelHeight;
            }
            using var page = r.Doc.Pdf!.GetPage((uint)r.Index);
            return page.Size.Width / page.Size.Height;
        }
        catch { return 0.707; }
    }

    void ApplyGeometry()
    {
        double w = ViewerBaseWidth * _vzoom;
        double h = w / Math.Clamp(_vaspect, 0.3, 3.0);
        ViewerItemWidth = w + ViewerPad;
        ViewerItemHeight = h + ViewerPad + ViewerCaption;
        ViewerZoomText.Text = $"{_vzoom * 100:0}%";
    }

    void EnsureScroll()
    {
        if (_vscroll != null) return;
        ViewerList.ApplyTemplate();
        _vscroll = FindDescendant<ScrollViewer>(ViewerList);
        if (_vscroll != null) _vscroll.ScrollChanged += Viewer_ScrollChanged;
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ---------- rendering window ----------

    uint ViewerTargetWidth()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (uint)Math.Min(2600, Math.Max(200, ViewerBaseWidth * _vzoom * dpi));
    }

    // As with grid tiles, the slot is remembered in Tag because DataContext is already cleared when Unloaded fires.
    void ViewerImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ViewerPage vp } fe) return;
        fe.Tag = vp;
        _vloaded.Add(vp);
        LoadViewerPage(vp);
    }

    void ViewerImage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ViewerPage vp } fe) return;
        fe.Tag = null;
        _vloaded.Remove(vp);
        CancelViewerRequests(vp);
        vp.Image = null;   // outside the preload window: free the bitmap (it stays in the renderer's cache)
    }

    static void CancelViewerRequests(ViewerPage vp)
    {
        vp.PreviewRequest?.Cancel();
        vp.SharpRequest?.Cancel();
        vp.PreviewRequest = vp.SharpRequest = null;
    }

    /// <summary>The page being read renders first, its neighbours next, the rest of the preload window last.</summary>
    int ViewerPriority(ViewerPage vp)
    {
        int distance = Math.Abs(vp.Position - 1 - _vcurrent);
        return distance == 0 ? (int)RenderPriority.ViewerCurrent : distance == 1 ? (int)RenderPriority.ViewerNear : (int)RenderPriority.ViewerPreload;
    }

    const uint ViewerPreviewWidth = 280;

    /// <summary>
    /// Shows something at once (cached image, the grid's thumbnail, or a quick small render), then upgrades it to a
    /// sharp render. Both wait briefly to see whether the page is still wanted; the page you are on waits least.
    /// </summary>
    void LoadViewerPage(ViewerPage vp)
    {
        CancelViewerRequests(vp);

        bool near = Math.Abs(vp.Position - 1 - _vcurrent) <= 1;
        vp.IsSharp = false;
        vp.SharpRequest = Renderer.Request(vp.Ref, ViewerTargetWidth(), () => ViewerPriority(vp), near ? 50 : 220, img => { vp.Image = img; vp.IsSharp = true; });

        if (vp.Image != null) return;   // the sharp image came straight from the cache, or an older image is still showing
        vp.Image = vp.Ref.Thumb;
        if (vp.Image != null) return;
        vp.PreviewRequest = Renderer.Request(vp.Ref, ViewerPreviewWidth, () => (int)RenderPriority.ViewerPreview, 25,
            img => { if (vp.Image == null) vp.Image = img; });
    }

    // ---------- navigation ----------

    void Viewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vscroll == null || _vpages.Count == 0) return;
        int idx = (int)((_vscroll.VerticalOffset + _vscroll.ViewportHeight / 2) / ViewerItemHeight);
        SetCurrent(Math.Clamp(idx, 0, _vpages.Count - 1));
    }

    void SetCurrent(int index)
    {
        if (index == _vcurrent) return;
        if (_vcurrent >= 0 && _vcurrent < _vpages.Count) _vpages[_vcurrent].IsCurrent = false;
        _vcurrent = index;
        if (index >= 0 && index < _vpages.Count) _vpages[index].IsCurrent = true;
        if (!ViewerPageBox.IsKeyboardFocusWithin) ViewerPageBox.Text = (index + 1).ToString();
    }

    void ViewerGoTo(int index)
    {
        if (_vpages.Count == 0) return;
        index = Math.Clamp(index, 0, _vpages.Count - 1);
        EnsureScroll();
        _vscroll?.ScrollToVerticalOffset(index * ViewerItemHeight);
        SetCurrent(index);
    }

    void ViewerPrev_Click(object sender, RoutedEventArgs e) => ViewerGoTo(_vcurrent - 1);
    void ViewerNext_Click(object sender, RoutedEventArgs e) => ViewerGoTo(_vcurrent + 1);

    void ViewerPageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(ViewerPageBox.Text.Trim(), out var n)) ViewerGoTo(n - 1);
        ViewerOverlay.Focus();
        e.Handled = true;
    }

    void ViewerZoomIn_Click(object sender, RoutedEventArgs e) => ViewerZoom(1.2);
    void ViewerZoomOut_Click(object sender, RoutedEventArgs e) => ViewerZoom(1 / 1.2);

    void ViewerZoom(double factor)
    {
        if (_vpages.Count == 0) return;
        int cur = Math.Max(0, _vcurrent);
        _vzoom = Math.Clamp(_vzoom * factor, 0.4, 2.5);
        ApplyGeometry();
        foreach (var vp in _vloaded.ToList()) LoadViewerPage(vp);   // re-render at the new size, old image shows meanwhile
        ViewerList.UpdateLayout();
        _vscroll?.ScrollToVerticalOffset(cur * ViewerItemHeight);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _vscroll?.ScrollToVerticalOffset(cur * ViewerItemHeight));
    }

    void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ViewerZoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    void ViewerPreload_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewerPreloadBox.SelectedItem is not int n) return;
        ViewerCacheRows = n;
        if (SettingsStore.Current.ViewerPreload == n) return;   // initial selection at startup: nothing to save
        SettingsStore.Current.ViewerPreload = n;
        SettingsStore.Save();
    }

    void Viewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewerOverlay.Visibility != Visibility.Visible) return;
        if (_animating) { e.Handled = true; return; }   // ignore keys for the half second a transition takes
        if (e.Key == Key.Escape) { CloseViewer(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;   // typing a page number etc.

        switch (e.Key)
        {
            case Key.Right: case Key.PageDown: case Key.Space: ViewerGoTo(_vcurrent + 1); break;
            case Key.Left: case Key.PageUp: ViewerGoTo(_vcurrent - 1); break;
            case Key.Home: ViewerGoTo(0); break;
            case Key.End: ViewerGoTo(_vpages.Count - 1); break;
            case Key.Down: _vscroll?.ScrollToVerticalOffset(_vscroll.VerticalOffset + 160); break;
            case Key.Up: _vscroll?.ScrollToVerticalOffset(_vscroll.VerticalOffset - 160); break;
            case Key.S: MarkStart(); break;
            case Key.E: MarkEnd(); break;
            case Key.OemPlus: case Key.Add: ViewerZoom(1.2); break;
            case Key.OemMinus: case Key.Subtract: ViewerZoom(1 / 1.2); break;
            case Key.G: ViewerPageBox.Focus(); ViewerPageBox.SelectAll(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- marking a range ----------

    void ViewerMarkStart_Click(object sender, RoutedEventArgs e) => MarkStart();
    void ViewerMarkEnd_Click(object sender, RoutedEventArgs e) => MarkEnd();
    void ViewerClearMarks_Click(object sender, RoutedEventArgs e) => ClearMarks();

    void MarkStart() { if (_vcurrent >= 0) { _markA = _vcurrent; RefreshRange(); } }
    void MarkEnd() { if (_vcurrent >= 0) { _markB = _vcurrent; RefreshRange(); } }

    void ClearMarks()
    {
        _markA = _markB = -1;
        RefreshRange();
    }

    bool TryRange(out int lo, out int hi)
    {
        lo = hi = -1;
        if (_markA < 0 || _markB < 0) return false;
        lo = Math.Min(_markA, _markB);
        hi = Math.Max(_markA, _markB);
        return true;
    }

    void RefreshRange()
    {
        foreach (var p in _vflagged) { p.InRange = false; p.Marker = ""; }
        _vflagged.Clear();

        if (TryRange(out var lo, out var hi))
        {
            for (int i = lo; i <= hi; i++) { _vpages[i].InRange = true; _vflagged.Add(_vpages[i]); }
            _vpages[lo].Marker = lo == hi ? "START · END" : "START";
            if (hi != lo) _vpages[hi].Marker = "END";
        }
        else if (_markA >= 0) { _vpages[_markA].Marker = "START"; _vflagged.Add(_vpages[_markA]); }
        else if (_markB >= 0) { _vpages[_markB].Marker = "END"; _vflagged.Add(_vpages[_markB]); }

        UpdateViewerTools();
    }

    void UpdateViewerTools()
    {
        bool isSplit = _vsource?.Split != null;
        bool hasRange = TryRange(out var lo, out var hi);

        ViewerRangeText.Text = hasRange ? $"Range: pages {lo + 1:N0}–{hi + 1:N0}  ·  {hi - lo + 1:N0} page{(hi == lo ? "" : "s")}"
            : _markA >= 0 ? $"Start marked at page {_markA + 1:N0}. Scroll to the last page and press E."
            : _markB >= 0 ? $"End marked at page {_markB + 1:N0}. Scroll to the first page and press S."
            : "Scroll to a page and press S to mark where a range starts, E where it ends.";

        ViewerFileTools.Visibility = isSplit ? Visibility.Collapsed : Visibility.Visible;
        ViewerSplitTools.Visibility = isSplit ? Visibility.Visible : Visibility.Collapsed;
        ViewerNewSplitBtn.IsEnabled = hasRange;
        ViewerAddBtn.IsEnabled = hasRange && ViewerTargetBox.SelectedItem != null;
        ViewerRemoveRangeBtn.IsEnabled = hasRange;
        ViewerRemovePageBtn.IsEnabled = _vpages.Count > 0;
        ViewerClearBtn.IsEnabled = _markA >= 0 || _markB >= 0;
    }

    void ViewerTarget_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateViewerTools();

    void ViewerMessage(string text) => ViewerRangeText.Text = text;

    // ---------- acting on the range ----------

    void ViewerNewSplit_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRange(out var lo, out var hi)) return;
        var refs = Enumerable.Range(lo, hi - lo + 1).Select(i => _vpages[i].Ref).ToList();
        var split = AddSplit(DefaultName(refs, _splits.Count + 1), refs);
        ViewerTargetBox.SelectedItem = split;
        SetStatus($"Created “{split.Name}” from {refs.Count:N0} page(s).");
        RefreshViewerSources();
        ClearMarks();
        ViewerMessage($"Created “{split.Name}” with {refs.Count:N0} page{(refs.Count == 1 ? "" : "s")}. Keep going, or press Esc to go back.");
    }

    void ViewerAddToSplit_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRange(out var lo, out var hi) || ViewerTargetBox.SelectedItem is not SplitItem split) return;
        int n = hi - lo + 1;
        for (int i = lo; i <= hi; i++) split.Pages.Add(new PageRef(_vpages[i].Ref.Doc, _vpages[i].Ref.Index));
        SetStatus($"Added {n:N0} page(s) to “{split.Name}”.");
        RefreshViewerSources();
        ClearMarks();
        ViewerMessage($"Added {n:N0} page{(n == 1 ? "" : "s")} to “{split.Name}”.");
    }

    void ViewerRemoveRange_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRange(out var lo, out var hi)) return;
        RemoveFromSplitView(lo, hi);
    }

    void ViewerRemovePage_Click(object sender, RoutedEventArgs e)
    {
        if (_vcurrent >= 0) RemoveFromSplitView(_vcurrent, _vcurrent);
    }

    void RemoveFromSplitView(int lo, int hi)
    {
        if (_vsource?.Split is not { } split) return;
        var doomed = Enumerable.Range(lo, hi - lo + 1).Select(i => _vpages[i].Ref).ToList();
        foreach (var p in doomed) split.Pages.Remove(p);
        SetStatus($"Removed {doomed.Count:N0} page(s) from “{split.Name}”.");

        int keep = Math.Min(lo, Math.Max(0, split.Pages.Count - 1));
        RefreshViewerSources();
        LoadView(_vsource!, keep);
        ViewerMessage($"Removed {doomed.Count:N0} page{(doomed.Count == 1 ? "" : "s")} from “{split.Name}”.");
    }
}
