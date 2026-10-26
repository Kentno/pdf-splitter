using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SharpPdf = PdfSharp.Pdf.PdfDocument;

namespace PdfSplitter;

public partial class MainWindow : Window
{
    static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    static readonly SemaphoreSlim RenderGate = new(2);

    readonly ObservableCollection<SourceDoc> _sources = new();
    readonly ObservableCollection<PageRef> _pool = new();
    readonly ObservableCollection<SplitItem> _splits = new();

    int _nextDocId;
    internal ObservableCollection<SourceDoc> Sources => _sources;
    internal ObservableCollection<SplitItem> Splits => _splits;

    public MainWindow()
    {
        InitializeComponent();
        SourcesList.ItemsSource = _sources;
        PoolList.ItemsSource = _pool;
        SplitsList.ItemsSource = _splits;
        InitChat();
        InitProject();
        InitRedesign();
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ---------- loading files ----------

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await AddFilesAsync(files);
    }

    async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "PDFs and images|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|PDF files|*.pdf|All files|*.*"
        };
        if (dlg.ShowDialog(this) == true) await AddFilesAsync(dlg.FileNames);
    }

    async Task AddFilesAsync(IEnumerable<string> paths)
    {
        paths = paths.ToList();
        var project = paths.FirstOrDefault(p => p.EndsWith(ProjectExt, StringComparison.OrdinalIgnoreCase));
        if (project != null) { await OpenProjectInteractiveAsync(project); return; }

        var skipped = new List<string>();
        int added = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".pdf" && !ImageExts.Contains(ext))
            {
                skipped.Add(Path.GetFileName(path) + " (unsupported type)");
                continue;
            }
            try
            {
                SetStatus($"Loading {Path.GetFileName(path)}...");
                await LoadDocAsync(path);
                added++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(path)} ({ex.Message.Trim()})");
            }
        }

        SetStatus($"Loaded {added} file(s)." + (skipped.Count > 0 ? "  Skipped: " + string.Join("; ", skipped) : ""));
        if (skipped.Count > 0)
            MessageBox.Show(this, "Could not load:\n\n" + string.Join("\n", skipped) +
                "\n\nPassword-protected PDFs are not supported.", "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Loads one PDF/image as a source (throws on failure). <paramref name="forcedId"/> restores a project's doc id.</summary>
    async Task<SourceDoc> LoadDocAsync(string path, int? forcedId = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        SourceDoc doc;
        if (ext == ".pdf")
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            doc = new SourceDoc { Path = path, PageCount = (int)pdf.PageCount, Pdf = pdf };
        }
        else
        {
            doc = new SourceDoc { Path = path, IsImage = true, PageCount = 1 };
        }

        doc.Id = forcedId ?? ++_nextDocId;
        if (doc.Id > _nextDocId) _nextDocId = doc.Id;
        Indexer.Start(doc);
        _sources.Add(doc);
        for (int i = 0; i < doc.PageCount; i++) _pool.Add(new PageRef(doc, i));
        SourcesList.SelectedItem = doc;
        return doc;
    }

    void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceDoc doc) return;
        for (int i = _pool.Count - 1; i >= 0; i--)
            if (_pool[i].Doc == doc) _pool.RemoveAt(i);
        foreach (var s in _splits)
            for (int i = s.Pages.Count - 1; i >= 0; i--)
                if (s.Pages[i].Doc == doc) s.Pages.RemoveAt(i);
        doc.Cts.Cancel();
        _sources.Remove(doc);
        SetStatus($"Removed {doc.Name}.");
    }

    // ---------- thumbnails ----------

    async void Thumb_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageRef p } && p.Thumb == null)
        {
            try { p.Thumb = await RenderAsync(p, 200); } catch { /* leave blank */ }
        }
    }

    void Thumb_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageRef p }) p.Thumb = null;
    }

    static async Task<ImageSource?> RenderAsync(PageRef p, uint width)
    {
        if (p.Doc.IsImage)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(p.Doc.Path);
            bmp.DecodePixelWidth = (int)width;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        await RenderGate.WaitAsync();
        try
        {
            using var page = p.Doc.Pdf!.GetPage((uint)p.Index);
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width });
            ms.Seek(0);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms.AsStreamForRead();
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        finally { RenderGate.Release(); }
    }

    // ---------- creating splits ----------

    internal SplitItem AddSplit(string name, IEnumerable<PageRef> pages)
    {
        var s = new SplitItem(name);
        foreach (var p in pages) s.Pages.Add(new PageRef(p.Doc, p.Index));
        _splits.Add(s);
        SplitsList.SelectedItem = s;
        return s;
    }

    List<PageRef> SelectedPoolPages() =>
        PoolList.SelectedItems.Cast<PageRef>().OrderBy(p => _pool.IndexOf(p)).ToList();

    static string DefaultName(IReadOnlyList<PageRef> pages, int fallbackNumber)
    {
        if (pages.Count == 0) return $"Split {fallbackNumber}";
        var doc = pages[0].Doc;
        if (pages.All(p => p.Doc == doc))
        {
            int a = pages.Min(p => p.Number), b = pages.Max(p => p.Number);
            return doc.IsImage ? doc.BaseName : $"{doc.BaseName} p{a}-{b}";
        }
        return $"Split {fallbackNumber}";
    }

    void NewSplitFromSelection_Click(object sender, RoutedEventArgs e)
    {
        var pages = SelectedPoolPages();
        if (pages.Count == 0) { SetStatus("Select one or more pages on the left first."); return; }
        AddSplit(DefaultName(pages, _splits.Count + 1), pages);
        SetStatus($"Created split with {pages.Count} page(s).");
    }

    void AddSelectionToSplit_Click(object sender, RoutedEventArgs e)
    {
        var pages = SelectedPoolPages();
        if (pages.Count == 0) { SetStatus("Select one or more pages on the left first."); return; }
        if (SplitsList.SelectedItem is not SplitItem s) { SetStatus("Select a split on the right first."); return; }
        foreach (var p in pages) s.Pages.Add(new PageRef(p.Doc, p.Index));
        SetStatus($"Added {pages.Count} page(s) to \"{s.Name}\".");
    }

    /// <summary>"1-5, 8; 10-12" -> groups of (doc, zero-based index) lists, one group per split.</summary>
    bool TryParseRanges(out List<(string Text, List<PageRef> Pages)> groups)
    {
        groups = new();
        if (SourcesList.SelectedItem is not SourceDoc doc)
        {
            SetStatus("Select a file in 'Loaded files' to take the page ranges from.");
            return false;
        }
        var text = RangeBox.Text.Trim();
        if (text.Length == 0) { SetStatus("Type page ranges first, e.g. 1-10; 11-20."); return false; }

        foreach (var groupText in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pages = new List<PageRef>();
            foreach (var part in groupText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var m = Regex.Match(part, @"^(\d+)\s*(?:-\s*(\d+))?$");
                if (!m.Success) { SetStatus($"Can't read \"{part}\". Use forms like 5 or 3-9."); return false; }
                int a = int.Parse(m.Groups[1].Value);
                int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
                if (a < 1 || b < a || b > doc.PageCount)
                {
                    SetStatus($"\"{part}\" is out of range; {doc.Name} has {doc.PageCount} page(s).");
                    return false;
                }
                for (int i = a; i <= b; i++) pages.Add(new PageRef(doc, i - 1));
            }
            if (pages.Count > 0) groups.Add((groupText, pages));
        }
        return groups.Count > 0;
    }

    void NewSplitFromRange_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseRanges(out var groups)) return;
        foreach (var (text, pages) in groups)
            AddSplit($"{pages[0].Doc.BaseName} p{text.Replace(" ", "")}", pages);
        SetStatus($"Created {groups.Count} split(s).");
    }

    void AddRangeToSplit_Click(object sender, RoutedEventArgs e)
    {
        if (SplitsList.SelectedItem is not SplitItem s) { SetStatus("Select a split on the right first."); return; }
        if (!TryParseRanges(out var groups)) return;
        int n = 0;
        foreach (var (_, pages) in groups)
            foreach (var p in pages) { s.Pages.Add(p); n++; }
        SetStatus($"Added {n} page(s) to \"{s.Name}\".");
    }

    // ---------- managing splits ----------

    void NewEmptySplit_Click(object sender, RoutedEventArgs e) =>
        AddSplit($"Split {_splits.Count + 1}", Array.Empty<PageRef>());

    void DeleteSplit_Click(object sender, RoutedEventArgs e)
    {
        if (SplitsList.SelectedItem is SplitItem s) _splits.Remove(s);
    }

    void MoveSplitUp_Click(object sender, RoutedEventArgs e) => MoveSplit(-1);
    void MoveSplitDown_Click(object sender, RoutedEventArgs e) => MoveSplit(1);

    void MoveSplit(int delta)
    {
        if (SplitsList.SelectedItem is not SplitItem s) return;
        int i = _splits.IndexOf(s), j = i + delta;
        if (j < 0 || j >= _splits.Count) return;
        _splits.Move(i, j);
    }

    void PageUp_Click(object sender, RoutedEventArgs e) => MovePages(-1);
    void PageDown_Click(object sender, RoutedEventArgs e) => MovePages(1);

    void MovePages(int delta)
    {
        if (SplitsList.SelectedItem is not SplitItem s) return;
        var sel = SplitPagesList.SelectedItems.Cast<PageRef>().ToList();
        var idx = sel.Select(p => s.Pages.IndexOf(p)).OrderBy(i => i).ToList();
        if (idx.Count == 0) return;
        if (delta < 0)
        {
            if (idx[0] == 0) return;
            foreach (var i in idx) s.Pages.Move(i, i - 1);
        }
        else
        {
            if (idx[^1] == s.Pages.Count - 1) return;
            foreach (var i in Enumerable.Reverse(idx)) s.Pages.Move(i, i + 1);
        }
        foreach (var p in sel) SplitPagesList.SelectedItems.Add(p);
    }

    void RemovePages_Click(object sender, RoutedEventArgs e)
    {
        if (SplitsList.SelectedItem is not SplitItem s) return;
        foreach (var p in SplitPagesList.SelectedItems.Cast<PageRef>().ToList()) s.Pages.Remove(p);
    }

    // ---------- export ----------

    void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose output folder" };
        if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FolderName;
    }

    async void ExportAll_Click(object sender, RoutedEventArgs e) => await ExportAsync(_splits.ToList());

    async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SplitsList.SelectedItem is SplitItem s) await ExportAsync(new() { s });
        else SetStatus("Select a split on the right first.");
    }

    record ExportJob(string Name, List<(string Path, bool IsImage, int Index)> Pages);

    async Task ExportAsync(List<SplitItem> items)
    {
        var jobs = items.Where(s => s.Pages.Count > 0)
            .Select(s => new ExportJob(s.Name, s.Pages.Select(p => (p.Doc.Path, p.Doc.IsImage, p.Index)).ToList()))
            .ToList();
        if (jobs.Count == 0) { SetStatus("Nothing to export: create splits with at least one page."); return; }

        var folder = OutputBox.Text.Trim();
        if (folder.Length == 0)
        {
            if (_sources.Count == 0) { SetStatus("Choose an output folder."); return; }
            folder = Path.GetDirectoryName(_sources[0].Path)!;
        }

        IsEnabled = false;
        var written = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var cache = new Dictionary<string, SharpPdf>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (int n = 0; n < jobs.Count; n++)
                    {
                        var job = jobs[n];
                        Dispatcher.Invoke(() => SetStatus($"Exporting {n + 1}/{jobs.Count}: {job.Name}..."));
                        var outPath = UniquePath(folder, job.Name);
                        using var outDoc = new SharpPdf();
                        foreach (var (path, isImage, index) in job.Pages)
                        {
                            if (isImage) AddImagePage(outDoc, path);
                            else
                            {
                                if (!cache.TryGetValue(path, out var src))
                                    cache[path] = src = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                                outDoc.AddPage(src.Pages[index]);
                            }
                        }
                        outDoc.Save(outPath);
                        written.Add(outPath);
                    }
                }
                finally { foreach (var d in cache.Values) d.Dispose(); }
            });
            SetStatus($"Done: wrote {written.Count} PDF(s) to {folder}");
            if (written.Count > 0)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{written[0]}\"");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
            MessageBox.Show(this, "Export failed:\n\n" + ex.Message, "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    static void AddImagePage(SharpPdf doc, string path)
    {
        using var img = XImage.FromFile(path);
        double w = img.PointWidth, h = img.PointHeight;
        // Fit within A4 (in the image's orientation), never upscale.
        double maxW = w >= h ? 842 : 595, maxH = w >= h ? 595 : 842;
        double scale = Math.Min(1.0, Math.Min(maxW / w, maxH / h));
        w *= scale; h *= scale;

        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(w);
        page.Height = XUnit.FromPoint(h);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(img, 0, 0, w, h);
    }

    static string UniquePath(string folder, string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        if (name.Length == 0) name = "split";
        var path = Path.Combine(folder, name + ".pdf");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}).pdf");
        return path;
    }
}
