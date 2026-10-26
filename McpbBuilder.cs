using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfSplitter;

/// <summary>
/// Builds the Claude Desktop extension (.mcpb): a zip with manifest.json and a copy of this exe, started as "PdfSplitter.exe --mcp".
/// The copy is a snapshot: build the extension again after updating the app.
/// </summary>
public static class McpbBuilder
{
    public const string Version = "1.0.0";
    const string ServerPath = "server/PdfSplitter.exe";

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ManifestJson()
    {
        var tools = new JsonArray();
        foreach (var t in AgentTools.ExternalTools())
            tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = FirstSentence(t.Description ?? "") });

        var manifest = new JsonObject
        {
            ["manifest_version"] = "0.3",
            ["name"] = "pdf-splitter",
            ["display_name"] = "PDF Splitter",
            ["version"] = Version,
            ["description"] = "Let Claude find where books or chapters start in large PDFs and set up the splits in PDF Splitter.",
            ["long_description"] = "Connects Claude to the PDF Splitter desktop app on this PC. Claude can read bookmarks, search and OCR text, survey pages for title pages, "
                + "and create, rename and edit the splits you then export from the app. Your files stay on your computer; only the text Claude asks for is sent to Claude. "
                + "The app opens automatically if it is closed.",
            ["author"] = new JsonObject { ["name"] = "PDF Splitter" },
            ["keywords"] = new JsonArray("pdf", "split", "ocr", "books"),
            ["server"] = new JsonObject
            {
                ["type"] = "binary",
                ["entry_point"] = ServerPath,
                ["mcp_config"] = new JsonObject
                {
                    ["command"] = "${__dirname}/" + ServerPath,
                    ["args"] = new JsonArray("--mcp"),
                },
            },
            ["tools"] = tools,
            ["tools_generated"] = false,
            ["compatibility"] = new JsonObject { ["platforms"] = new JsonArray("win32") },
        };
        return manifest.ToJsonString(Pretty);
    }

    static string FirstSentence(string text)
    {
        int dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? text[..(dot + 1)] : text;
    }

    /// <summary>Writes the extension file. Throws if the exe cannot be read or the target cannot be written.</summary>
    public static void Create(string outPath)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the running program.");
        if (File.Exists(outPath)) File.Delete(outPath);

        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var w = new StreamWriter(manifest.Open(), new System.Text.UTF8Encoding(false))) w.Write(ManifestJson());
        zip.CreateEntryFromFile(exe, ServerPath, CompressionLevel.Fastest);   // already compressed: don't spend time on it
    }

    /// <summary>For people who prefer editing claude_desktop_config.json by hand: runs the exe where it is, no copy.</summary>
    public static string ConfigSnippet()
    {
        var exe = Environment.ProcessPath ?? "PdfSplitter.exe";
        var node = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["pdf-splitter"] = new JsonObject { ["command"] = exe, ["args"] = new JsonArray("--mcp") },
            },
        };
        return node.ToJsonString(Pretty);
    }
}
