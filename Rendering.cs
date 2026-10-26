using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PdfSplitter;

/// <summary>Lower number = rendered sooner.</summary>
public enum RenderPriority { ViewerCurrent = 0, ViewerNear = 1, ViewerPreview = 2, Grid = 3, ViewerPreload = 4 }

/// <summary>A queued page render. Cancel it when whatever asked for it has scrolled away.</summary>
public sealed class RenderRequest
{
    internal PageRef Page = null!;
    internal uint Width;
    internal Func<int> Priority = null!;     // evaluated when a worker picks the next job, so it can follow the scroll position
    internal long Seq;                       // newest first within a priority
    internal long NotBefore;                 // Stopwatch timestamp: the "wait for scrolling to settle" delay
    internal Action<ImageSource> OnDone = null!;

    volatile bool _cancelled;
    public bool IsCancelled => _cancelled;
    public void Cancel()
    {
        if (_cancelled) return;
        _cancelled = true;
        Renderer.Stats.Cancelled();
    }
}

/// <summary>
/// Renders PDF pages and images on background threads.
///  - Settle: a request only becomes runnable after a short delay, and requests whose tile/page has scrolled
///    away are cancelled in the meantime, so dragging a scrollbar across thousands of pages renders almost nothing.
///  - Latest first: the newest request of the best priority runs next, never the oldest.
///  - Off the UI thread: the PDF is rendered AND the bitmap is decoded and frozen on the worker; the UI thread only
///    receives a finished image.
///  - Cached: finished images are kept in size-limited LRU caches, so scrolling back is instant.
/// </summary>
public static class Renderer
{
    static readonly object Gate = new();
    static readonly List<RenderRequest> Pending = new();
    static long _seq;

    static readonly ImageLru ThumbCache = new(96L << 20);    // small page previews (about 1,000 tiles)
    static readonly ImageLru PageCache = new(128L << 20);    // large viewer pages (about 20 pages)

    public static readonly RenderStats Stats = new();

    static Renderer()
    {
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (int i = 0; i < workers; i++)
            new Thread(WorkerLoop) { IsBackground = true, Name = "PageRender " + i, Priority = ThreadPriority.BelowNormal }.Start();

        var log = Environment.GetEnvironmentVariable("PDFSPLITTER_PERFLOG");
        if (!string.IsNullOrEmpty(log))
            _logTimer = new Timer(_ => { try { File.AppendAllText(log, Stats.Line(PendingCount) + Environment.NewLine); } catch { } }, null, 0, 500);
    }

    static readonly Timer? _logTimer;   // kept in a field so it is not garbage-collected

    static int PendingCount { get { lock (Gate) return Pending.Count; } }
    static ImageLru CacheFor(uint width) => width <= 400 ? ThumbCache : PageCache;

    public static ImageSource? TryGetCached(PageRef page, uint width) => CacheFor(width).TryGet(page.Doc, page.Index, width);

    /// <summary>Forget everything about a document (it was removed or the project changed).</summary>
    public static void Forget(SourceDoc doc)
    {
        ThumbCache.Forget(doc);
        PageCache.Forget(doc);
    }

    /// <summary>
    /// Asks for a render. If the image is already cached, <paramref name="onDone"/> runs immediately and null is returned.
    /// Otherwise it runs on the UI thread when the render finishes, unless the returned request was cancelled.
    /// </summary>
    public static RenderRequest? Request(PageRef page, uint width, Func<int> priority, int settleDelayMs, Action<ImageSource> onDone)
    {
        var hit = TryGetCached(page, width);
        if (hit != null) { Stats.CacheHit(); onDone(hit); return null; }

        var r = new RenderRequest
        {
            Page = page,
            Width = width,
            Priority = priority,
            OnDone = onDone,
            Seq = Interlocked.Increment(ref _seq),
            NotBefore = Stopwatch.GetTimestamp() + settleDelayMs * Stopwatch.Frequency / 1000,
        };
        Stats.Requested();
        lock (Gate)
        {
            Pending.Add(r);
            Monitor.PulseAll(Gate);
        }
        return r;
    }

    static void WorkerLoop()
    {
        while (true)
        {
            RenderRequest? job;
            lock (Gate)
            {
                while (true)
                {
                    int dropped = Pending.RemoveAll(r => r.IsCancelled || r.Page.Doc.Cts.IsCancellationRequested);
                    if (dropped > 0) Stats.DroppedBeforeStart(dropped);

                    long now = Stopwatch.GetTimestamp(), earliest = long.MaxValue;
                    RenderRequest? best = null;
                    int bestPriority = int.MaxValue;
                    foreach (var r in Pending)
                    {
                        if (r.NotBefore > now) { earliest = Math.Min(earliest, r.NotBefore); continue; }   // still settling
                        int p = r.Priority();
                        if (best == null || p < bestPriority || (p == bestPriority && r.Seq > best.Seq)) { best = r; bestPriority = p; }
                    }
                    if (best != null) { Pending.Remove(best); job = best; break; }

                    int wait = earliest == long.MaxValue ? Timeout.Infinite : (int)Math.Max(1, (earliest - now) * 1000 / Stopwatch.Frequency);
                    Monitor.Wait(Gate, wait);
                }
            }
            Execute(job);
        }
    }

    static void Execute(RenderRequest job)
    {
        var sw = Stopwatch.StartNew();
        ImageSource? image = null;
        try { image = RenderSync(job.Page, job.Width); } catch { /* unreadable page: leave blank */ }
        Stats.Rendered(sw.Elapsed.TotalMilliseconds);
        if (image == null) return;

        CacheFor(job.Width).Put(job.Page.Doc, job.Page.Index, job.Width, image);   // useful even if nobody wants it any more
        if (job.IsCancelled) { Stats.FinishedStale(); return; }

        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!job.IsCancelled) job.OnDone(image);
        });
    }

    /// <summary>Renders and decodes entirely on the calling (worker) thread; the result is frozen so any thread can use it.</summary>
    static ImageSource? RenderSync(PageRef p, uint width)
    {
        var bmp = new BitmapImage();
        if (p.Doc.IsImage)
        {
            bmp.BeginInit();
            bmp.UriSource = new Uri(p.Doc.Path);
            bmp.DecodePixelWidth = (int)width;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        using var page = p.Doc.Pdf!.GetPage((uint)p.Index);
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width }).AsTask().GetAwaiter().GetResult();
        ms.Seek(0);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms.AsStreamForRead();
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>Least-recently-used image cache limited by decoded size in bytes.</summary>
sealed class ImageLru
{
    readonly long _budget;
    long _bytes;
    readonly object _lock = new();
    readonly Dictionary<(SourceDoc, int, uint), LinkedListNode<Entry>> _map = new();
    readonly LinkedList<Entry> _order = new();

    record struct Entry((SourceDoc Doc, int Index, uint Width) Key, ImageSource Image, long Cost);

    public ImageLru(long budgetBytes) => _budget = budgetBytes;

    public ImageSource? TryGet(SourceDoc doc, int index, uint width)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue((doc, index, width), out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Image;
        }
    }

    public void Put(SourceDoc doc, int index, uint width, ImageSource image)
    {
        long cost = image is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 0;
        var key = (doc, index, width);
        lock (_lock)
        {
            if (_map.Remove(key, out var old)) { _order.Remove(old); _bytes -= old.Value.Cost; }
            _map[key] = _order.AddFirst(new Entry(key, image, cost));
            _bytes += cost;
            while (_bytes > _budget && _order.Count > 1)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Cost;
            }
        }
    }

    public void Forget(SourceDoc doc)
    {
        lock (_lock)
        {
            foreach (var node in _order.Where(n => n.Key.Doc == doc).ToList())
            {
                _order.Remove(node);
                _map.Remove(node.Key);
                _bytes -= node.Cost;
            }
        }
    }
}

/// <summary>Counters for diagnosing scrolling performance. Written to a file every 500 ms when PDFSPLITTER_PERFLOG is set.</summary>
public sealed class RenderStats
{
    long _requested, _cacheHits, _dropped, _rendered, _stale, _renderTicksMs, _cancelled;

    public void Cancelled() => Interlocked.Increment(ref _cancelled);
    public void Requested() => Interlocked.Increment(ref _requested);
    public void CacheHit() => Interlocked.Increment(ref _cacheHits);
    public void DroppedBeforeStart(int n) => Interlocked.Add(ref _dropped, n);
    public void FinishedStale() => Interlocked.Increment(ref _stale);
    public void Rendered(double ms) { Interlocked.Increment(ref _rendered); Interlocked.Add(ref _renderTicksMs, (long)(ms * 1000)); }

    public string Line(int pending) =>
        $"{DateTime.Now:HH:mm:ss.fff} requested={_requested} cancelled={_cancelled} cacheHits={_cacheHits} droppedBeforeStart={_dropped} rendered={_rendered} " +
        $"finishedStale={_stale} avgRenderMs={(_rendered == 0 ? 0 : _renderTicksMs / 1000.0 / _rendered):F1} pending={pending}";
}
