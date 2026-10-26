using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;

namespace PdfSplitter;

public class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SourceDoc
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string BaseName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool IsImage { get; init; }
    public int PageCount { get; init; }
    public Windows.Data.Pdf.PdfDocument? Pdf { get; init; }

    // AI assistant support: stable id, per-page extracted/OCR text.
    public int Id { get; set; }
    public PageData[] Data { get; set; } = Array.Empty<PageData>();
    public Task TextTask { get; set; } = Task.CompletedTask;
    public CancellationTokenSource Cts { get; } = new();
    public string? Fingerprint { get; set; }
    public int CachePending;
    public override string ToString() => $"{Name}  ({PageCount} page{(PageCount == 1 ? "" : "s")})";
}

public class PageRef : Notifier
{
    public PageRef(SourceDoc doc, int index) { Doc = doc; Index = index; }
    public SourceDoc Doc { get; }
    public int Index { get; }
    public int Number => Index + 1;
    public string Caption => $"{Doc.Name}  \u2022  p.{Number}";

    ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set { _thumb = value; Raise(); } }
}

public class SplitItem : Notifier
{
    string _name;
    public SplitItem(string name)
    {
        _name = name;
        Pages.CollectionChanged += (_, _) => { Raise(nameof(Summary)); Raise(nameof(CountText)); };
    }

    public string Name { get => _name; set { _name = value; Raise(); } }
    public ObservableCollection<PageRef> Pages { get; } = new();
    public string CountText => $"{Pages.Count} page{(Pages.Count == 1 ? "" : "s")}";

    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < Pages.Count)
            {
                var doc = Pages[i].Doc;
                var runs = new List<string>();
                while (i < Pages.Count && Pages[i].Doc == doc)
                {
                    int start = Pages[i].Number, end = start;
                    i++;
                    while (i < Pages.Count && Pages[i].Doc == doc && Pages[i].Number == end + 1) { end++; i++; }
                    runs.Add(start == end ? $"{start}" : $"{start}\u2013{end}");
                }
                if (sb.Length > 0) sb.Append("  |  ");
                sb.Append(doc.Name).Append(": ").Append(string.Join(", ", runs));
            }
            return sb.Length == 0 ? "(empty)" : sb.ToString();
        }
    }
}
