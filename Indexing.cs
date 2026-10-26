using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using PigDoc = UglyToad.PdfPig.PdfDocument;

namespace PdfSplitter;

public class PageData
{
    public string Text = "";
    public string Flat = "";      // whitespace-collapsed, used for search/snippets
    public string Big = "";       // text noticeably larger than the page's body text (titles)
    public double MaxFont;
    public bool TextDone, NeedsOcr, OcrDone;
}

/// <summary>Background text extraction (PdfPig) plus OCR for pages that have no text layer.</summary>
public static class Indexer
{
    static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    public static string Flatten(string s) => Ws.Replace(s, " ").Trim();

    public static void Start(SourceDoc doc)
    {
        doc.Data = Enumerable.Range(0, doc.PageCount).Select(_ => new PageData()).ToArray();
        doc.TextTask = Task.Run(() => BuildText(doc));
        _ = doc.TextTask.ContinueWith(_ => BackgroundOcrAsync(doc), TaskScheduler.Default).Unwrap();
    }

    static void BuildText(SourceDoc doc)
    {
        if (doc.IsImage) { doc.Data[0].NeedsOcr = true; doc.Data[0].TextDone = true; return; }
        try
        {
            using var pig = PigDoc.Open(doc.Path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            int n = pig.NumberOfPages;
            for (int i = 0; i < doc.PageCount; i++)
            {
                if (doc.Cts.IsCancellationRequested) return;
                var d = doc.Data[i];
                try { if (i < n) Fill(d, pig.GetPage(i + 1)); } catch { /* leave empty -> OCR */ }
                d.NeedsOcr = d.Flat.Length < 25;
                d.TextDone = true;
            }
        }
        catch
        {
            foreach (var d in doc.Data) { d.NeedsOcr = true; d.TextDone = true; }
        }
    }

    static void Fill(PageData d, UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().ToList();
        d.Text = words.Count > 0 ? string.Join(" ", words.Select(w => w.Text)) : page.Text;
        d.Flat = Flatten(d.Text);
        if (words.Count == 0) return;

        var sized = words.Select(w => (w, size: w.Letters.Count > 0 ? w.Letters.Max(l => l.PointSize) : 0.0)).ToList();
        var sizes = sized.Select(x => x.size).OrderBy(x => x).ToList();
        double max = sizes[^1], median = sizes[sizes.Count / 2];
        d.MaxFont = Math.Round(max, 1);
        if (max >= median * 1.2)
        {
            d.Big = string.Join(" ", sized.Where(x => x.size >= max * 0.85)
                .OrderByDescending(x => x.w.BoundingBox.Bottom).ThenBy(x => x.w.BoundingBox.Left)
                .Select(x => x.w.Text));
            if (d.Big.Length > 200) d.Big = d.Big[..200];
        }
    }

    static async Task BackgroundOcrAsync(SourceDoc doc)
    {
        if (!OcrService.Available) return;
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (doc.Cts.IsCancellationRequested || !SettingsStore.Current.BackgroundOcr) return;
            var d = doc.Data[i];
            if (d.NeedsOcr && !d.OcrDone) await OcrService.OcrPageAsync(doc, i);
        }
    }

    public static string Status(IEnumerable<SourceDoc> docs)
    {
        int text = 0, textTotal = 0, ocr = 0, ocrTotal = 0;
        foreach (var doc in docs)
        {
            foreach (var d in doc.Data)
            {
                textTotal++;
                if (d.TextDone) text++;
                if (d.NeedsOcr) { ocrTotal++; if (d.OcrDone) ocr++; }
            }
        }
        if (textTotal == 0) return "";
        var s = text < textTotal ? $"Reading text: {text}/{textTotal} pages" : "Text indexed";
        if (ocrTotal > 0)
            s += OcrService.Available ? $"  •  OCR: {ocr}/{ocrTotal} scanned pages" : "  •  OCR unavailable (no Windows OCR language installed)";
        return s;
    }
}

public static class OcrService
{
    static readonly OcrEngine? Engine = TryCreate();
    static readonly SemaphoreSlim Gate = new(1, 1);

    static OcrEngine? TryCreate()
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages(); } catch { return null; }
    }

    public static bool Available => Engine != null;
    public static string Language => Engine?.RecognizerLanguage.DisplayName ?? "none";

    public static async Task OcrPageAsync(SourceDoc doc, int index)
    {
        var d = doc.Data[index];
        if (d.OcrDone || Engine == null) return;
        await Gate.WaitAsync();
        try
        {
            if (d.OcrDone) return;
            using var bmp = await LoadAsync(doc, index);
            var res = await Engine.RecognizeAsync(bmp);
            d.Text = res.Text;
            d.Flat = Indexer.Flatten(res.Text);

            var lines = res.Lines.Select(l => (l.Text, h: l.Words.Count > 0 ? l.Words.Max(w => w.BoundingRect.Height) : 0.0)).ToList();
            if (lines.Count > 0)
            {
                var hs = lines.Select(l => l.h).OrderBy(x => x).ToList();
                double max = hs[^1], baseline = hs[(hs.Count - 1) / 3];
                d.MaxFont = Math.Round(max / bmp.PixelHeight * 842, 1);
                d.Big = max >= baseline * 1.5
                    ? string.Join(" ", lines.Where(l => l.h >= max * 0.85).Select(l => l.Text))
                    : "";
                if (d.Big.Length > 200) d.Big = d.Big[..200];
            }
        }
        catch { /* leave empty */ }
        finally
        {
            d.OcrDone = true;
            Gate.Release();
        }
    }

    static async Task<SoftwareBitmap> LoadAsync(SourceDoc doc, int index)
    {
        uint maxDim = Math.Min(OcrEngine.MaxImageDimension, 2400u);
        if (doc.IsImage)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(doc.Path);
            using var s = await file.OpenReadAsync();
            var dec = await BitmapDecoder.CreateAsync(s);
            double scale = Math.Min(1.0, (double)maxDim / Math.Max(dec.PixelWidth, dec.PixelHeight));
            var tr = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, dec.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, dec.PixelHeight * scale)
            };
            return await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }

        using var page = doc.Pdf!.GetPage((uint)index);
        double sc = maxDim / Math.Max(page.Size.Width, page.Size.Height);
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * sc),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * sc)
        });
        var dec2 = await BitmapDecoder.CreateAsync(ms);
        return await dec2.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
