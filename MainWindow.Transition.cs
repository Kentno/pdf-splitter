using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PdfSplitter;

/// <summary>
/// Open/close transitions for the full-screen viewer. Copies of the visible page tiles ("ghosts") fly on a layer above
/// everything: from their grid positions into the viewer's single column (the grid straightens out, the clicked page
/// grows to fill the window), or from a split card that expands to the whole window. Closing plays it backwards.
/// The real viewer and grid are never animated, so nothing about their layout or virtualization changes.
/// </summary>
public partial class MainWindow
{
    const int TransitionMs = 368;       // flight time; every other timing below is scaled to match
    static readonly IEasingFunction TransitionEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

    bool _animating;
    static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Lets a corner radius be animated (Border.CornerRadius is not a double).</summary>
    public static readonly DependencyProperty GhostRadiusProperty = DependencyProperty.RegisterAttached(
        "GhostRadius", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => { if (d is Border b) b.CornerRadius = new CornerRadius((double)e.NewValue); }));
    public static double GetGhostRadius(DependencyObject d) => (double)d.GetValue(GhostRadiusProperty);
    public static void SetGhostRadius(DependencyObject d, double v) => d.SetValue(GhostRadiusProperty, v);

    sealed record GhostSpec(int Page, Rect Start, ImageSource? Image);
    sealed class OpenPlan { public Rect Backdrop; public bool FromCard; public List<GhostSpec> Ghosts = new(); }
    sealed record CloseGhost(int Page, Rect From, Rect To, ImageSource? Image, bool Lands);
    sealed class ClosePlan { public Rect BackdropFrom, BackdropTo; public bool ToCard; public List<CloseGhost> Ghosts = new(); public int Current; }

    // ---------- geometry helpers ----------

    Rect BoundsOf(FrameworkElement e) =>
        e.TransformToVisual(TransitionLayer).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    Rect WindowRect => new(0, 0, TransitionLayer.ActualWidth, TransitionLayer.ActualHeight);

    /// <summary>A small stand-in rectangle at the left of a split card, where its pages appear from / vanish into.</summary>
    static Rect CardMini(Rect card) => new(card.X + 16, card.Y + card.Height / 2 - 20, 30, 40);

    /// <summary>Realised grid tiles of one document (the tiles on screen plus the preload rows) with their thumbnail rectangles.</summary>
    IEnumerable<(PageRef Page, Rect Rect)> RealizedTiles(SourceDoc doc)
    {
        var panel = FindDescendant<VirtualizingGridPanel>(PoolList);
        if (panel == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is not FrameworkElement tile || tile.DataContext is not PageRef p || p.Doc != doc) continue;
            if (FindDescendant<Image>(tile) is not { ActualWidth: > 0 } img) continue;
            yield return (p, BoundsOf(img));
        }
    }

    /// <summary>Where the page image of viewer slot <paramref name="index"/> sits on screen once the viewer is laid out.</summary>
    Rect ViewerPageImageRect(int index)
    {
        ViewerList.UpdateLayout();
        if (ViewerList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement slot &&
            FindDescendant<Image>(slot) is { ActualWidth: > 0 } img)
            return BoundsOf(img);

        var list = BoundsOf(ViewerList);   // not realised yet: assume it is centred
        double w = ViewerBaseWidth * _vzoom, h = w / Math.Clamp(_vaspect, 0.3, 3.0);
        return new Rect(list.X + (list.Width - w) / 2, list.Y + (list.Height - h) / 2, w, h);
    }

    // ---------- ghosts ----------

    Border MakeGhost(ImageSource? image, Rect r)
    {
        var ghost = new Border
        {
            Width = r.Width,
            Height = r.Height,
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
            Child = new Image { Source = image, Stretch = Stretch.Uniform },
        };
        ghost.SetResourceReference(Border.BackgroundProperty, "ControlAltFillColorSecondaryBrush");
        Canvas.SetLeft(ghost, r.X);
        Canvas.SetTop(ghost, r.Y);
        TransitionLayer.Children.Add(ghost);
        return ghost;
    }

    /// <summary>The card-coloured rectangle that grows to the whole window (or shrinks back to its card).</summary>
    Border MakeBackdrop(Rect r, double radius)
    {
        var b = new Border { Width = r.Width, Height = r.Height, BorderThickness = new Thickness(1), IsHitTestVisible = false };
        // The real cards use a translucent brush; this must be opaque or the old UI shows through while it grows.
        b.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        SetGhostRadius(b, radius);
        Canvas.SetLeft(b, r.X);
        Canvas.SetTop(b, r.Y);
        TransitionLayer.Children.Add(b);
        return b;
    }

    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    static void Fly(FrameworkElement g, Rect to, double delayMs, double durationMs)
    {
        void Animate(DependencyProperty p, double value) =>
            g.BeginAnimation(p, new DoubleAnimation(value, Ms(durationMs)) { BeginTime = Ms(delayMs), EasingFunction = TransitionEase });
        Animate(Canvas.LeftProperty, to.X);
        Animate(Canvas.TopProperty, to.Y);
        Animate(WidthProperty, to.Width);
        Animate(HeightProperty, to.Height);
    }

    enum FadeOut { None, AtEnd, Early }

    /// <summary>Optional fade in at the start and/or fade out of a ghost during its flight (Early = gone by about 70%).</summary>
    static void FadePlan(UIElement g, bool fadeIn, FadeOut fadeOut, double delayMs, double durationMs)
    {
        if (!fadeIn && fadeOut == FadeOut.None) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = Ms(delayMs + durationMs), FillBehavior = FillBehavior.HoldEnd };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(fadeIn ? 0 : 1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (fadeIn) a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(delayMs + 120))));
        if (fadeOut != FadeOut.None)
        {
            double from = fadeOut == FadeOut.Early ? 0.25 : 1 - 185 / durationMs;
            double to = fadeOut == FadeOut.Early ? 0.7 : 1.0;
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(delayMs + durationMs * from))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ms(delayMs + durationMs * to))));
        }
        g.BeginAnimation(OpacityProperty, a);
    }

    static FadeOut FadeFor(int rank) => rank >= 3 ? FadeOut.Early : rank == 2 ? FadeOut.AtEnd : FadeOut.None;

    /// <summary>Pages of a split have no thumbnail yet; render a small one and drop it into the ghost when ready.</summary>
    void FillGhost(Border ghost, PageRef page) =>
        Renderer.Request(page, ThumbWidth, () => (int)RenderPriority.ViewerCurrent, 0, img => { if (ghost.Child is Image im) im.Source = img; });

    // ---------- open: grid (or split card) -> full-screen viewer ----------

    /// <summary>Captures where things are on screen right now, before the viewer appears. Null = no suitable source, open instantly.</summary>
    OpenPlan? PlanOpen(ViewerSource src)
    {
        try
        {
            var plan = new OpenPlan();
            if (src.Doc != null)
            {
                plan.Backdrop = BoundsOf(PagesCard);
                var viewport = BoundsOf(PoolList);
                foreach (var (page, rect) in RealizedTiles(src.Doc).Where(t => viewport.Contains(t.Rect)))   // fully visible tiles only
                    plan.Ghosts.Add(new GhostSpec(page.Index, rect, page.Thumb));
            }
            else if (src.Split != null)
            {
                if (SplitsList.ItemContainerGenerator.ContainerFromItem(src.Split) is not FrameworkElement card) return null;
                plan.Backdrop = BoundsOf(card);
                plan.FromCard = true;
                for (int i = 0; i < Math.Min(6, src.Split.Pages.Count); i++)
                    plan.Ghosts.Add(new GhostSpec(i, CardMini(plan.Backdrop), null));
            }
            else return null;
            return plan;
        }
        catch { return null; }
    }

    async Task PlayOpenAsync(OpenPlan plan, ViewerSource source, int current)
    {
        _animating = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ViewerOverlay.UpdateLayout();
            var reference = ViewerPageImageRect(current);     // where page `current` ends up; the other pages stack above and below it
            double dur = TransitionMs;
            Renderer.Trace($"open start (flight {dur} ms)");

            var backdrop = MakeBackdrop(plan.Backdrop, 10);
            Fly(backdrop, WindowRect, 0, dur);
            backdrop.BeginAnimation(GhostRadiusProperty, new DoubleAnimation(0, Ms(dur)) { EasingFunction = TransitionEase });

            // Farthest pages first so the page you opened is drawn on top.
            foreach (var g in plan.Ghosts.OrderByDescending(x => Math.Abs(x.Page - current)))
            {
                int rank = Math.Abs(g.Page - current);
                double delay = Math.Min(112, rank * 11);
                var ghost = MakeGhost(g.Image, g.Start);
                var to = new Rect(reference.X, reference.Y + (g.Page - current) * ViewerItemHeight, reference.Width, reference.Height);
                Fly(ghost, to, delay, dur);
                FadePlan(ghost, plan.FromCard, FadeFor(rank), delay, dur);
                if (g.Image == null && source.Split != null && g.Page < source.Split.Pages.Count)
                    FillGhost(ghost, source.Split.Pages[g.Page]);
            }

            ViewerOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(190)) { BeginTime = Ms(dur - 160) });
            await Task.Delay((int)dur + 20);

            // The real viewer is now underneath. Keep the copies until it has its sharp render of the page we landed on
            // (they only show a thumbnail), then dissolve them so the reveal is a crisp page, not a blur.
            var landed = _vpages[current];
            Renderer.Trace($"open landed at {clock.ElapsedMilliseconds} ms, sharp={landed.IsSharp}");
            for (int waited = 0; !landed.IsSharp && waited < 500; waited += 10) await Task.Delay(10);
            Renderer.Trace($"open sharp at {clock.ElapsedMilliseconds} ms, sharp={landed.IsSharp}");
            // Fade the whole layer as one group: faded separately, the grey backdrop would show over the white page mid-fade.
            TransitionLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(90)));
            await Task.Delay(100);
            Renderer.Trace($"open done at {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            TransitionLayer.Children.Clear();
            TransitionLayer.BeginAnimation(OpacityProperty, null);
            TransitionLayer.Opacity = 1;
            ViewerOverlay.BeginAnimation(OpacityProperty, null);
            ViewerOverlay.Opacity = 1;
            ViewerOverlay.IsHitTestVisible = true;
            _animating = false;
        }
    }

    // ---------- close: full-screen viewer -> grid (or split card) ----------

    ClosePlan? PlanClose()
    {
        try
        {
            var src = _vsource;
            if (src == null || _vpages.Count == 0) return null;
            int c = Math.Clamp(_vcurrent, 0, _vpages.Count - 1);
            var plan = new ClosePlan { BackdropFrom = WindowRect, Current = c };

            // The viewer slots on screen right now become ghosts.
            var starts = new List<(int Page, Rect Rect, ImageSource? Image)>();
            for (int j = Math.Max(0, c - 2); j <= Math.Min(_vpages.Count - 1, c + 2); j++)
            {
                if (ViewerList.ItemContainerGenerator.ContainerFromIndex(j) is not FrameworkElement slot) continue;
                if (FindDescendant<Image>(slot) is not { ActualWidth: > 0 } img) continue;
                var r = BoundsOf(img);
                if (r.IntersectsWith(plan.BackdropFrom)) starts.Add((j, r, _vpages[j].Image));
            }
            if (starts.Count == 0) return null;

            var tiles = new Dictionary<int, Rect>();
            if (src.Doc != null)
            {
                PoolList.ScrollIntoView(_vpages[c].Ref);   // bring the tile you stopped on into view first
                PoolList.UpdateLayout();
                plan.BackdropTo = BoundsOf(PagesCard);
                var viewport = BoundsOf(PoolList);
                foreach (var (page, rect) in RealizedTiles(src.Doc).Where(t => viewport.Contains(t.Rect))) tiles[page.Index] = rect;
            }
            else if (src.Split != null)
            {
                SplitsList.ScrollIntoView(src.Split);
                SplitsList.UpdateLayout();
                if (SplitsList.ItemContainerGenerator.ContainerFromItem(src.Split) is not FrameworkElement card) return null;
                plan.BackdropTo = BoundsOf(card);
                plan.ToCard = true;
            }
            else return null;

            foreach (var (page, rect, image) in starts)
            {
                if (plan.ToCard) plan.Ghosts.Add(new CloseGhost(page, rect, CardMini(plan.BackdropTo), image, false));
                else if (tiles.TryGetValue(page, out var tile)) plan.Ghosts.Add(new CloseGhost(page, rect, tile, image, true));
                else if (tiles.TryGetValue(c, out var home)) plan.Ghosts.Add(new CloseGhost(page, rect, home, image, false));   // no tile on screen: shrink into the current one
                else plan.Ghosts.Add(new CloseGhost(page, rect, new Rect(plan.BackdropTo.X + plan.BackdropTo.Width / 2 - 15, plan.BackdropTo.Y + plan.BackdropTo.Height / 2 - 20, 30, 40), image, false));
            }
            return plan;
        }
        catch { return null; }
    }

    async Task PlayCloseAsync(ClosePlan plan)
    {
        _animating = true;
        ViewerOverlay.IsHitTestVisible = false;
        try
        {
            double dur = TransitionMs - 56;

            var backdrop = MakeBackdrop(plan.BackdropFrom, 0);
            Fly(backdrop, plan.BackdropTo, 0, dur);
            backdrop.BeginAnimation(GhostRadiusProperty, new DoubleAnimation(10, Ms(dur)) { EasingFunction = TransitionEase });

            foreach (var g in plan.Ghosts.OrderByDescending(x => Math.Abs(x.Page - plan.Current)))
            {
                var ghost = MakeGhost(g.Image, g.From);
                Fly(ghost, g.To, 0, dur);
                FadePlan(ghost, false, g.Lands ? FadeOut.None : FadeOut.AtEnd, 0, dur);
            }
            ViewerOverlay.Opacity = 0;   // the backdrop and copies already cover it; the real viewer can go
            await Task.Delay((int)dur + 20);
        }
        finally
        {
            CloseViewerNow();
            TransitionLayer.Children.Clear();
            ViewerOverlay.Opacity = 1;
            ViewerOverlay.IsHitTestVisible = true;
            _animating = false;
        }
    }
}
