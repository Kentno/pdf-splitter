using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PdfSplitter;

/// <summary>
/// Per-file cache of extracted text and OCR results, shared by all projects and keyed by file content
/// (size + head + tail hash) so it survives renames and moves. Safe to delete at any time.
/// </summary>
public static class DocCache
{
    const int Version = 1;
    static readonly object Lock = new();

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfSplitter", "cache");
    static string FileFor(string fingerprint) => Path.Combine(Dir, fingerprint + ".json.gz");

    class CacheFile
    {
        public int Version { get; set; }
        public int PageCount { get; set; }
        public List<Entry> Pages { get; set; } = new();
    }

    class Entry
    {
        public string T { get; set; } = "";   // text
        public string B { get; set; } = "";   // big (title-like) text
        public double F { get; set; }         // max font size
        public bool N { get; set; }           // needs OCR
        public bool O { get; set; }           // OCR done
    }

    public static string? Fingerprint(string path)
    {
        try
        {
            const int chunk = 1 << 20;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = SHA256.Create();
            sha.TransformBlock(BitConverter.GetBytes(fs.Length), 0, 8, null, 0);
            var buf = new byte[chunk];
            int n = fs.Read(buf, 0, (int)Math.Min(chunk, fs.Length));
            sha.TransformBlock(buf, 0, n, null, 0);
            if (fs.Length > chunk)
            {
                fs.Seek(Math.Max(chunk, fs.Length - chunk), SeekOrigin.Begin);
                n = fs.Read(buf, 0, chunk);
                sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch { return null; }
    }

    public static bool TryLoad(SourceDoc doc)
    {
        if (doc.Fingerprint == null) return false;
        try
        {
            var file = FileFor(doc.Fingerprint);
            if (!File.Exists(file)) return false;
            CacheFile? cf;
            lock (Lock)
            {
                using var fs = File.OpenRead(file);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                cf = JsonSerializer.Deserialize<CacheFile>(gz);
            }
            if (cf == null || cf.Version != Version || cf.PageCount != doc.PageCount || cf.Pages.Count != doc.PageCount) return false;

            for (int i = 0; i < doc.PageCount; i++)
            {
                var e = cf.Pages[i];
                var d = doc.Data[i];
                d.Text = e.T;
                d.Flat = Indexer.Flatten(e.T);
                d.Big = e.B;
                d.MaxFont = e.F;
                d.NeedsOcr = e.N;
                d.OcrDone = e.O;
                d.TextDone = true;
            }
            return true;
        }
        catch { return false; }
    }

    public static void Save(SourceDoc doc)
    {
        if (doc.Fingerprint == null || doc.Data.Length == 0 || doc.Data.Any(d => !d.TextDone)) return;
        try
        {
            var cf = new CacheFile
            {
                Version = Version,
                PageCount = doc.PageCount,
                Pages = doc.Data.Select(d => new Entry { T = d.Text, B = d.Big, F = d.MaxFont, N = d.NeedsOcr, O = d.OcrDone }).ToList(),
            };
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                var file = FileFor(doc.Fingerprint);
                var tmp = file + ".tmp";
                using (var fs = File.Create(tmp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                    JsonSerializer.Serialize(gz, cf);
                File.Move(tmp, file, overwrite: true);
            }
        }
        catch { /* cache is best-effort */ }
    }

    /// <summary>Coalesces frequent OCR completions into one write every few seconds.</summary>
    public static void ScheduleSave(SourceDoc doc)
    {
        if (Interlocked.Exchange(ref doc.CachePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Interlocked.Exchange(ref doc.CachePending, 0);
            Save(doc);
        });
    }
}
