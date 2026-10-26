using System.Windows.Media;

namespace PdfSplitter;

/// <summary>What the full-screen viewer can show: one loaded file, or one split (which may mix files).</summary>
public class ViewerSource
{
    public string Title { get; init; } = "";
    public SourceDoc? Doc { get; init; }
    public SplitItem? Split { get; init; }
    public override string ToString() => Title;
}

/// <summary>One page slot in the viewer. The image exists only while the slot is inside the preload window.</summary>
public class ViewerPage : Notifier
{
    public ViewerPage(PageRef page, int position, bool showSourceInLabel)
    {
        Ref = page;
        Position = position;
        Label = showSourceInLabel ? $"{position}  ·  {page.Doc.Name}  p.{page.Number}" : $"Page {position}";
    }

    public PageRef Ref { get; }
    public int Position { get; }          // 1-based position within the current view
    public string Label { get; }

    /// <summary>Bumped on every (re)load and on unload so stale renders can tell they are no longer wanted.</summary>
    public int Token;

    ImageSource? _image;
    public ImageSource? Image { get => _image; set { _image = value; Raise(); } }

    bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set { if (_isCurrent == value) return; _isCurrent = value; Raise(); } }

    bool _inRange;
    public bool InRange { get => _inRange; set { if (_inRange == value) return; _inRange = value; Raise(); } }

    string _marker = "";
    public string Marker { get => _marker; set { if (_marker == value) return; _marker = value; Raise(); } }
}
