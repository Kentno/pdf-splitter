using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PdfSplitter;

/// <summary>The app's side of the Claude Desktop connection: serves the bridge and shows when Claude is connected and working.</summary>
public partial class MainWindow
{
    static readonly HashSet<string> MutatingTools = new() { "create_splits", "add_pages_to_split", "rename_split", "delete_split" };

    BridgeServer? _bridge;
    DispatcherTimer? _pillTimer;
    DateTime _lastBridgeSnapshot = DateTime.MinValue;

    internal string ProjectName => ProjectDisplayName;
    internal string OutputFolderText => OutputBox.Text;
    internal int ClaudeDesktopClients => _bridge?.ClientCount ?? 0;

    void InitBridge()
    {
        Bridge.RememberAppPath();

        _bridge = new BridgeServer(new AgentTools(this));
        _bridge.ClientsChanged += () => Dispatcher.BeginInvoke(OnBridgeClientsChanged);
        _bridge.ToolCalled += call => Dispatcher.Invoke(() => OnBridgeCall(call));   // synchronous: the undo snapshot must be taken before the change
        _bridge.Start();
        Closing += (_, _) => _bridge.Dispose();

        _pillTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _pillTimer.Tick += (_, _) => { _pillTimer.Stop(); McpPillText.Text = "Claude Desktop"; };
    }

    void OnBridgeClientsChanged()
    {
        bool connected = ClaudeDesktopClients > 0;
        McpPill.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        McpPillText.Text = "Claude Desktop";
        SetStatus(connected ? "Claude Desktop is connected. Ask it to work on your files; changes appear here as it goes."
                            : "Claude Desktop disconnected.");
    }

    void OnBridgeCall(BridgeCall call)
    {
        if (call.Done)
        {
            if (call.IsError) McpPillText.Text = "Claude Desktop · " + call.Tool + " failed";
            return;
        }

        // Make Claude's changes undoable with the same button as the in-app assistant: one snapshot per burst of work.
        if (MutatingTools.Contains(call.Tool) && (DateTime.UtcNow - _lastBridgeSnapshot).TotalSeconds > 20)
        {
            _undoSnapshot = SnapshotSplits();
            UndoBtn.IsEnabled = true;
        }
        if (MutatingTools.Contains(call.Tool)) _lastBridgeSnapshot = DateTime.UtcNow;

        AddChat("tool", $"← Claude Desktop: {call.Tool}({call.Summary})");
        McpPillText.Text = "Claude Desktop · " + call.Tool;
        _pillTimer?.Stop();
        _pillTimer?.Start();
    }

    void McpPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => AiSettings_Click(sender, e);
}
