using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PdfSplitter;

/// <summary>Dragging pages into the page list of the selected split: items nudge apart to open a gap at the drop position.</summary>
public partial class MainWindow
{
    const double NudgeGap = 30;
    static readonly Duration NudgeTime = new(TimeSpan.FromMilliseconds(130));

    int _insertIndex = -1;

    static void InsertPages(SplitItem split, int at, List<PageRef> pages)
    {
        at = Math.Clamp(at, 0, split.Pages.Count);
        foreach (var p in pages) split.Pages.Insert(at++, new PageRef(p.Doc, p.Index));
    }

    static TranslateTransform NudgeOf(ListBoxItem c)
    {
        if (c.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        t = new TranslateTransform();
        c.RenderTransform = t;
        return t;
    }

    /// <summary>Top of an item in list coordinates, ignoring the nudge currently applied to it.</summary>
    double LayoutTop(ListBoxItem c) => c.TranslatePoint(new Point(0, 0), SplitPagesList).Y - NudgeOf(c).Y;

    /// <summary>Where the dragged pages would go: before the item whose upper half the cursor is in, else after it.</summary>
    int InsertIndexAt(DragEventArgs e, SplitItem split)
    {
        double y = e.GetPosition(SplitPagesList).Y;
        int last = -1;
        for (int i = 0; i < split.Pages.Count; i++)
        {
            if (SplitPagesList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c)
            {
                if (last >= 0) break;   // past the realized (visible) items
                continue;
            }
            last = i;
            if (y < LayoutTop(c) + c.ActualHeight / 2) return i;
        }
        return last < 0 ? split.Pages.Count : last + 1;
    }

    void ApplyNudge(int index)
    {
        var split = SplitsList.SelectedItem as SplitItem;
        if (split == null) return;
        double? lineY = null;
        double lastBottom = 0;
        for (int i = 0; i < split.Pages.Count; i++)
        {
            if (SplitPagesList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c)
            {
                if (lastBottom > 0) break;
                continue;
            }
            double to = index >= 0 && i >= index ? NudgeGap : 0;
            if (c.Tag is not double current || current != to)
            {
                c.Tag = to;
                NudgeOf(c).BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(to, NudgeTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            if (i == index) lineY = LayoutTop(c);
            lastBottom = LayoutTop(c) + c.ActualHeight;
        }
        if (index < 0) { HideInsertLine(); return; }

        double boundary = lineY ?? lastBottom;
        double y = Math.Clamp(boundary + NudgeGap / 2 - 1.5, 0, Math.Max(0, SplitPagesList.ActualHeight - 3));
        var target = new Thickness(6, y, 6, 0);
        if (InsertLine.Visibility != Visibility.Visible)
        {
            InsertLine.Margin = target;
            InsertLine.Visibility = Visibility.Visible;
            InsertLine.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, NudgeTime));
        }
        else
            InsertLine.BeginAnimation(MarginProperty, new ThicknessAnimation(target, NudgeTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void HideInsertLine()
    {
        InsertLine.BeginAnimation(MarginProperty, null);
        InsertLine.BeginAnimation(OpacityProperty, null);
        InsertLine.Visibility = Visibility.Collapsed;
    }

    void ResetNudge(bool immediate)
    {
        _insertIndex = -1;
        foreach (var item in SplitPagesList.Items)
        {
            if (SplitPagesList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c || c.Tag is not double) continue;
            c.Tag = null;
            var t = NudgeOf(c);
            if (immediate) { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = 0; }
            else t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, NudgeTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        HideInsertLine();
    }

    void SplitPages_DragOver(object sender, DragEventArgs e)
    {
        if (DraggedPages(e) == null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // Scroll when the cursor is near the top or bottom edge of a long list.
        double y = e.GetPosition(SplitPagesList).Y;
        if (FindDescendant<ScrollViewer>(SplitPagesList) is { } sv)
        {
            if (y < 26) sv.LineUp();
            else if (y > SplitPagesList.ActualHeight - 26) sv.LineDown();
        }

        if (SplitsList.SelectedItem is not SplitItem split) return;   // dropping with no split selected makes a new one
        _insertIndex = InsertIndexAt(e, split);
        ApplyNudge(_insertIndex);
        SetStatus($"Drop to insert at position {_insertIndex + 1} of “{split.Name}”.");
    }

    void SplitPages_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(SplitPagesList);
        if (p.X >= 0 && p.Y >= 0 && p.X < SplitPagesList.ActualWidth && p.Y < SplitPagesList.ActualHeight) return;   // moved between child elements
        ResetNudge(immediate: false);
    }

    void SplitPages_Drop(object sender, DragEventArgs e)
    {
        var pages = DraggedPages(e);
        if (pages == null) return;
        e.Handled = true;

        if (SplitsList.SelectedItem is not SplitItem split)
        {
            AddSplit(DefaultName(pages, _splits.Count + 1), pages);
            SetStatus($"Created a new split from {pages.Count} dragged page(s).");
            return;
        }

        int at = _insertIndex >= 0 ? _insertIndex : InsertIndexAt(e, split);
        ResetNudge(immediate: true);
        InsertPages(split, at, pages);

        // Select what was inserted and bring it into view.
        SplitPagesList.SelectedItems.Clear();
        for (int i = 0; i < pages.Count && at + i < split.Pages.Count; i++) SplitPagesList.SelectedItems.Add(split.Pages[at + i]);
        SplitPagesList.ScrollIntoView(split.Pages[Math.Min(at, split.Pages.Count - 1)]);
        SetStatus($"Inserted {pages.Count} page(s) at position {at + 1} of “{split.Name}”.");
    }
}
