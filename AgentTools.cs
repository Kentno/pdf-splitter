using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;
using UglyToad.PdfPig;
using PigDoc = UglyToad.PdfPig.PdfDocument;

namespace PdfSplitter;

/// <summary>The tools the AI can call. Runs off the UI thread; touches app state via the dispatcher.</summary>
public class AgentTools
{
    readonly MainWindow _w;
    public AgentTools(MainWindow w) => _w = w;

    const int MaxResultChars = 16000;
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    T UI<T>(Func<T> f) => _w.Dispatcher.Invoke(f);
    List<SourceDoc> Docs() => UI(() => _w.Sources.ToList());
    List<SplitItem> SplitList() => UI(() => _w.Splits.ToList());

    // ---------- tool definitions ----------

    static object S(string d) => new { type = "string", description = d };
    static object I(string d) => new { type = "integer", description = d };
    static object B(string d) => new { type = "boolean", description = d };

    static Dictionary<string, JsonElement> ToDict(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    static Tool Def(string name, string desc, object props, params string[] required) => new()
    {
        Name = name,
        Description = desc,
        InputSchema = new() { Properties = ToDict(props), Required = required.Length > 0 ? required.ToList() : null },
    };

    /// <summary>The in-app assistant (API key) gets these.</summary>
    public List<ToolUnion> Definitions() => BaseTools().Select(t => (ToolUnion)t).ToList();

    /// <summary>External clients (Claude Desktop over MCP) get the same tools plus two that only make sense there.</summary>
    public static List<Tool> ExternalTools()
    {
        var tools = BaseTools();
        tools.Add(Def("open_files",
            "Load PDF or image files from disk into PDF Splitter by full path (e.g. C:\\Users\\me\\Downloads\\book.pdf). Use this when list_documents is empty or the user names a file. Loading the same path twice is harmless. Returns the document ids to use with the other tools.",
            new { paths = new { type = "array", description = "Full file paths (max 20)", items = new { type = "string" } } }, "paths"));
        tools.Add(Def("get_app_state",
            "One-call overview of the app: project name, output folder, loaded documents and the current splits.",
            new { }));
        return tools;
    }

    static List<Tool> BaseTools() => new()
    {
        Def("list_documents",
            "List the loaded files with page counts and text/OCR readiness. Call this first.",
            new { }),
        Def("get_document_info",
            "PDF metadata (title, author, ...) and the bookmark/outline tree with page numbers if the PDF has one. Bookmarks are often the quickest way to find where books or chapters start.",
            new { doc = I("Document id from list_documents") }, "doc"),
        Def("get_page_text",
            "Full text of specific pages (max 10 pages per call, each truncated to ~2500 chars). Scanned pages are OCR'd on demand.",
            new { doc = I("Document id"), pages = S("1-based page list, e.g. '5' or '3-6, 20'") }, "doc", "pages"),
        Def("search_text",
            "Search page text across one or all documents. Returns matching page numbers with short snippets. Useful markers: 'Copyright', 'ISBN', 'Chapter 1', 'Table of Contents', 'All rights reserved', a known book title. Only searches pages whose text is available (OCR may still be running on scans; the result says how many pages are not searchable yet).",
            new
            {
                query = S("Text or regex to find"),
                doc = I("Optional document id; omit to search all"),
                regex = B("Treat query as a regular expression (default false)"),
                case_sensitive = B("Default false"),
                max_results = I("Default 40, max 100"),
            }, "query"),
        Def("scan_pages",
            "Survey a page range cheaply: for each sampled page returns the page's largest/title-like text (if any), the font size, and the first ~100 characters. Use step>1 to sample long documents, then zoom in around candidate boundaries. Title pages typically show large text.",
            new
            {
                doc = I("Document id"),
                from = I("First page (1-based, default 1)"),
                to = I("Last page (default: last page)"),
                step = I("Sample every Nth page (default 1)"),
                ocr = B("OCR scanned pages in range (default true; at most 30 pages OCR'd per call)"),
            }, "doc"),
        Def("ocr_pages",
            "Force OCR on scanned pages so they become searchable (max 40 pages per call).",
            new { doc = I("Document id"), pages = S("1-based page list, e.g. '1-40'") }, "doc", "pages"),
        Def("list_splits",
            "List the current splits (each becomes one output PDF) with their page summaries. Splits are numbered from 1.",
            new { }),
        Def("create_splits",
            "Create one or more new splits from page ranges of a document. Each split becomes one output PDF.",
            new
            {
                doc = I("Document id"),
                splits = new
                {
                    type = "array",
                    description = "Splits to create",
                    items = new
                    {
                        type = "object",
                        properties = new { name = S("Output file name without extension"), pages = S("1-based pages, e.g. '1-120' or '1-5, 9'") },
                        required = new[] { "name", "pages" },
                    },
                },
            }, "doc", "splits"),
        Def("add_pages_to_split",
            "Append pages from a document to an existing split.",
            new { split = I("Split number from list_splits"), doc = I("Document id"), pages = S("1-based pages") }, "split", "doc", "pages"),
        Def("rename_split", "Rename a split.", new { split = I("Split number"), name = S("New name") }, "split", "name"),
        Def("delete_split", "Delete a split (the pages remain in the source document).", new { split = I("Split number") }, "split"),
    };

    // ---------- argument helpers ----------

    static string? Str(IReadOnlyDictionary<string, JsonElement> a, string k) =>
        a.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(IReadOnlyDictionary<string, JsonElement> a, string k)
    {
        if (!a.TryGetValue(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
        return null;
    }

    static bool Bool(IReadOnlyDictionary<string, JsonElement> a, string k) =>
        a.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.True;

    SourceDoc GetDoc(IReadOnlyDictionary<string, JsonElement> a)
    {
        int? id = Int(a, "doc");
        var docs = Docs();
        var doc = id == null ? (docs.Count == 1 ? docs[0] : null) : docs.FirstOrDefault(d => d.Id == id);
        return doc ?? throw new ToolError(id == null ? "Missing 'doc'." : $"No document with id {id}. Call list_documents.");
    }

    /// <summary>"1-5, 9" -> zero-based indices (order preserved).</summary>
    static List<int> ParsePages(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ToolError("Missing page list.");
        var result = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(part, @"^(\d+)\s*(?:-\s*(\d+))?$");
            if (!m.Success) throw new ToolError($"Can't read page range '{part}'.");
            int a = int.Parse(m.Groups[1].Value), b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
            if (a < 1 || b < a || b > pageCount) throw new ToolError($"Range '{part}' is outside 1-{pageCount}.");
            for (int i = a; i <= b; i++) result.Add(i - 1);
        }
        return result;
    }

    class ToolError : Exception { public ToolError(string m) : base(m) { } }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------- execution ----------

    public async Task<(string Text, bool IsError)> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> a, CancellationToken ct)
    {
        try
        {
            object result = name switch
            {
                "list_documents" => ListDocuments(),
                "get_document_info" => GetInfo(GetDoc(a)),
                "get_page_text" => await GetPageText(GetDoc(a), Str(a, "pages")),
                "search_text" => await Search(a, ct),
                "scan_pages" => await Scan(a),
                "ocr_pages" => await OcrPages(GetDoc(a), Str(a, "pages")),
                "list_splits" => ListSplits(),
                "create_splits" => CreateSplits(a),
                "add_pages_to_split" => AddPages(a),
                "rename_split" => Rename(a),
                "delete_split" => Delete(a),
                "open_files" => await OpenFiles(a),
                "get_app_state" => GetAppState(),
                _ => throw new ToolError($"Unknown tool '{name}'."),
            };
            return (Clip(JsonSerializer.Serialize(result, JsonOpts), MaxResultChars), false);
        }
        catch (ToolError e) { return (e.Message, true); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return ($"{e.GetType().Name}: {e.Message}", true); }
    }

    async Task<object> OpenFiles(IReadOnlyDictionary<string, JsonElement> a)
    {
        if (!a.TryGetValue("paths", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new ToolError("Missing 'paths' array.");
        var paths = arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(20).ToList();
        if (paths.Count == 0) throw new ToolError("No paths given.");

        var (loaded, failed) = await await _w.Dispatcher.InvokeAsync(() => _w.LoadFilesQuietAsync(paths));
        return new
        {
            loaded = loaded.Select(d => new { doc = d.Id, name = d.Name, pages = d.PageCount }),
            failed,
            note = "Text is read in the background; scanned pages are OCR'd automatically.",
        };
    }

    object GetAppState() => UI<object>(() => new
    {
        project = _w.ProjectName,
        output_folder = string.IsNullOrWhiteSpace(_w.OutputFolderText) ? "(next to the first loaded file)" : _w.OutputFolderText,
        documents = _w.Sources.Select(d => new { doc = d.Id, name = d.Name, pages = d.PageCount }).ToList(),
        splits = _w.Splits.Select((s, i) => new { split = i + 1, name = s.Name, pages = s.Pages.Count, summary = s.Summary }).ToList(),
    });

    object ListDocuments() => new
    {
        ocr_available = OcrService.Available,
        ocr_language = OcrService.Language,
        documents = Docs().Select(d => new
        {
            doc = d.Id,
            name = d.Name,
            pages = d.PageCount,
            is_image = d.IsImage,
            text_pages_read = d.Data.Count(p => p.TextDone),
            scanned_pages_needing_ocr = d.Data.Count(p => p.NeedsOcr),
            scanned_pages_ocr_done = d.Data.Count(p => p.NeedsOcr && p.OcrDone),
        }),
    };

    object GetInfo(SourceDoc doc)
    {
        if (doc.IsImage) return new { name = doc.Name, note = "Image file, 1 page." };
        using var pig = PigDoc.Open(doc.Path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var info = pig.Information;
        var marks = new List<object>();
        int total = 0;
        if (pig.TryGetBookmarks(out var bm))
        {
            foreach (var node in bm.GetNodes())
            {
                total++;
                if (marks.Count >= 400) continue;
                int? page = (node as UglyToad.PdfPig.Outline.DocumentBookmarkNode)?.PageNumber;
                marks.Add(new { level = node.Level, title = Clip(node.Title, 100), page });
            }
        }
        return new
        {
            name = doc.Name,
            pages = doc.PageCount,
            title = info.Title,
            author = info.Author,
            subject = info.Subject,
            creator = info.Creator,
            producer = info.Producer,
            created = info.CreationDate,
            bookmark_count = total,
            bookmarks = marks,
            bookmark_note = total == 0 ? "No bookmarks in this PDF." : (total > marks.Count ? "Truncated to first 400." : null),
        };
    }

    async Task<object> GetPageText(SourceDoc doc, string? pagesText)
    {
        await doc.TextTask;
        var idx = ParsePages(pagesText, doc.PageCount);
        if (idx.Count > 10) throw new ToolError("At most 10 pages per call.");
        var pages = new List<object>();
        foreach (var i in idx)
        {
            var d = doc.Data[i];
            if (d.NeedsOcr && !d.OcrDone) await OcrService.OcrPageAsync(doc, i);
            pages.Add(new { page = i + 1, text = Clip(d.Text.Trim(), 2500), ocr = d.NeedsOcr });
        }
        return new { doc = doc.Id, pages };
    }

    async Task<object> Search(IReadOnlyDictionary<string, JsonElement> a, CancellationToken ct)
    {
        var q = Str(a, "query");
        if (string.IsNullOrEmpty(q)) throw new ToolError("Missing query.");
        bool useRegex = Bool(a, "regex"), cs = Bool(a, "case_sensitive");
        int max = Math.Clamp(Int(a, "max_results") ?? 40, 1, 100);

        var docs = Int(a, "doc") != null ? new List<SourceDoc> { GetDoc(a) } : Docs();
        foreach (var d in docs) await d.TextTask;

        Regex? rx = null;
        try { if (useRegex) rx = new Regex(q, (cs ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException e) { throw new ToolError("Bad regex: " + e.Message); }
        var flatQ = Indexer.Flatten(q);
        var cmp = cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var hits = new List<object>();
        int totalPages = 0, notSearchable = 0;
        foreach (var doc in docs)
        {
            for (int i = 0; i < doc.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var d = doc.Data[i];
                if (d.NeedsOcr && !d.OcrDone) { notSearchable++; continue; }
                int pos, len;
                if (rx != null)
                {
                    var m = rx.Match(d.Flat);
                    if (!m.Success) continue;
                    pos = m.Index; len = m.Length;
                }
                else
                {
                    pos = d.Flat.IndexOf(flatQ, cmp);
                    if (pos < 0) continue;
                    len = flatQ.Length;
                }
                totalPages++;
                if (hits.Count < max)
                {
                    int s = Math.Max(0, pos - 60), e = Math.Min(d.Flat.Length, pos + len + 60);
                    hits.Add(new { doc = doc.Id, page = i + 1, snippet = d.Flat[s..e] });
                }
            }
        }
        return new
        {
            matching_pages = totalPages,
            shown = hits.Count,
            pages_not_searchable_yet = notSearchable,
            note = notSearchable > 0 ? "Some scanned pages have not been OCR'd yet; use ocr_pages/scan_pages on the relevant range, or wait for background OCR." : null,
            hits,
        };
    }

    async Task<object> Scan(IReadOnlyDictionary<string, JsonElement> a)
    {
        var doc = GetDoc(a);
        await doc.TextTask;
        int from = Math.Clamp(Int(a, "from") ?? 1, 1, doc.PageCount);
        int to = Math.Clamp(Int(a, "to") ?? doc.PageCount, from, doc.PageCount);
        int step = Math.Max(1, Int(a, "step") ?? 1);
        bool ocr = !a.ContainsKey("ocr") || Bool(a, "ocr");
        const int maxPages = 80;

        var rows = new List<object>();
        int ocrBudget = 30;
        for (int p = from; p <= to && rows.Count < maxPages; p += step)
        {
            var d = doc.Data[p - 1];
            bool pending = false;
            if (d.NeedsOcr && !d.OcrDone)
            {
                if (ocr && ocrBudget > 0) { ocrBudget--; await OcrService.OcrPageAsync(doc, p - 1); }
                else pending = true;
            }
            rows.Add(new
            {
                page = p,
                big = d.Big.Length > 0 ? Clip(d.Big, 90) : null,
                size = d.MaxFont,
                start = Clip(d.Flat, 100),
                ocr_pending = pending ? true : (bool?)null,
            });
        }
        int last = rows.Count > 0 ? from + (rows.Count - 1) * step : from;
        return new
        {
            doc = doc.Id,
            pages_returned = rows.Count,
            truncated_at_page = last < to ? last : (int?)null,
            rows,
        };
    }

    async Task<object> OcrPages(SourceDoc doc, string? pagesText)
    {
        await doc.TextTask;
        if (!OcrService.Available) throw new ToolError("No Windows OCR language is installed on this PC.");
        var idx = ParsePages(pagesText, doc.PageCount).Where(i => doc.Data[i].NeedsOcr && !doc.Data[i].OcrDone).Take(40).ToList();
        foreach (var i in idx) await OcrService.OcrPageAsync(doc, i);
        return new
        {
            ocr_done_now = idx.Count,
            total_scanned_pages = doc.Data.Count(p => p.NeedsOcr),
            total_ocr_done = doc.Data.Count(p => p.NeedsOcr && p.OcrDone),
        };
    }

    // ---------- split editing ----------

    static string SplitInfoText(SplitItem s) => $"{s.Name} — {s.CountText} — {s.Summary}";

    object ListSplits() => UI<object>(() => new
    {
        splits = _w.Splits.Select((s, i) => new { split = i + 1, name = s.Name, pages = s.Pages.Count, summary = s.Summary }).ToList(),
    });

    object CreateSplits(IReadOnlyDictionary<string, JsonElement> a)
    {
        var doc = GetDoc(a);
        if (!a.TryGetValue("splits", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new ToolError("Missing 'splits' array.");

        var planned = new List<(string Name, List<int> Pages)>();
        foreach (var item in arr.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var pages = item.TryGetProperty("pages", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError("Every split needs a name.");
            planned.Add((name.Trim(), ParsePages(pages, doc.PageCount)));
        }

        return UI<object>(() =>
        {
            var created = new List<object>();
            foreach (var (name, pages) in planned)
            {
                var s = _w.AddSplit(name, pages.Select(i => new PageRef(doc, i)));
                created.Add(new { split = _w.Splits.IndexOf(s) + 1, name = s.Name, pages = s.Pages.Count, summary = s.Summary });
            }
            return new { created };
        });
    }

    object AddPages(IReadOnlyDictionary<string, JsonElement> a)
    {
        var doc = GetDoc(a);
        var pages = ParsePages(Str(a, "pages"), doc.PageCount);
        return UI<object>(() =>
        {
            var s = FindSplit(a);
            foreach (var i in pages) s.Pages.Add(new PageRef(doc, i));
            return new { split = _w.Splits.IndexOf(s) + 1, name = s.Name, pages = s.Pages.Count, summary = s.Summary };
        });
    }

    object Rename(IReadOnlyDictionary<string, JsonElement> a)
    {
        var name = Str(a, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ToolError("Missing name.");
        return UI<object>(() =>
        {
            var s = FindSplit(a);
            s.Name = name.Trim();
            return new { split = _w.Splits.IndexOf(s) + 1, name = s.Name };
        });
    }

    object Delete(IReadOnlyDictionary<string, JsonElement> a) => UI<object>(() =>
    {
        var s = FindSplit(a);
        _w.Splits.Remove(s);
        return new { deleted = s.Name, remaining = _w.Splits.Count };
    });

    SplitItem FindSplit(IReadOnlyDictionary<string, JsonElement> a)
    {
        int n = Int(a, "split") ?? throw new ToolError("Missing 'split'.");
        if (n < 1 || n > _w.Splits.Count) throw new ToolError($"No split {n}; there are {_w.Splits.Count}.");
        return _w.Splits[n - 1];
    }
}
