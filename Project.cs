using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfSplitter;

/// <summary>On-disk project (*.pdfsplit): JSON that points at the original files rather than copying them.</summary>
public class ProjectFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string OutputFolder { get; set; } = "";
    public int NextDocId { get; set; }
    public bool ChatOpen { get; set; }
    public List<ProjectSource> Sources { get; set; } = new();
    public List<ProjectSplit> Splits { get; set; } = new();
    public List<ProjectChat> Chat { get; set; } = new();
    public List<HistoryEntry> ApiHistory { get; set; } = new();
    public UsageTotals Usage { get; set; } = new();
}

public class ProjectSource
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public string? RelativePath { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public int PageCount { get; set; }
}

public class ProjectSplit
{
    public string Name { get; set; } = "";
    /// <summary>Run-length encoded pages, e.g. "1:0-119" = doc 1, zero-based pages 0..119.</summary>
    public List<string> Pages { get; set; } = new();
}

public class ProjectChat
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>One message of the Claude conversation, in an SDK-independent shape.</summary>
public class HistoryEntry
{
    public string Role { get; set; } = "user";
    public string? Text { get; set; }
    public List<BlockDto>? Blocks { get; set; }
}

public class BlockDto
{
    public string Type { get; set; } = "";   // text | thinking | redacted_thinking | tool_use | tool_result
    public string? Text { get; set; }
    public string? Signature { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Input { get; set; }       // tool_use input as JSON
    public string? ToolUseId { get; set; }
    public bool IsError { get; set; }
    public string? Data { get; set; }
}

public static class ProjectIO
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ProjectFile Read(string path)
    {
        var pf = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Opts)
                 ?? throw new InvalidDataException("The project file is empty.");
        if (pf.Version > ProjectFile.CurrentVersion)
            throw new InvalidDataException("This project was saved by a newer version of PDF Splitter.");
        return pf;
    }

    /// <summary>Atomic write: temp file then replace, so a crash can't leave a half-written project.</summary>
    public static void Write(string path, ProjectFile pf)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(pf, Opts));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static List<string> EncodeRuns(IEnumerable<PageRef> pages)
    {
        var runs = new List<string>();
        int doc = -1, start = 0, end = 0;
        void Flush()
        {
            if (doc < 0) return;
            runs.Add(start == end ? $"{doc}:{start}" : $"{doc}:{start}-{end}");
        }
        foreach (var p in pages)
        {
            if (p.Doc.Id == doc && p.Index == end + 1) { end = p.Index; continue; }
            Flush();
            doc = p.Doc.Id; start = end = p.Index;
        }
        Flush();
        return runs;
    }

    public static IEnumerable<(int Doc, int Index)> DecodeRuns(IEnumerable<string> runs)
    {
        foreach (var run in runs)
        {
            var colon = run.IndexOf(':');
            if (colon < 0 || !int.TryParse(run[..colon], out var doc)) continue;
            var range = run[(colon + 1)..].Split('-');
            if (!int.TryParse(range[0], out var a)) continue;
            int b = range.Length > 1 && int.TryParse(range[1], out var bb) ? bb : a;
            for (int i = a; i <= b; i++) yield return (doc, i);
        }
    }
}
