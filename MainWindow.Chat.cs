using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PdfSplitter;

public class ChatMessage : Notifier
{
    public ChatMessage(string role, string text) { Role = role; _text = text; }
    public string Role { get; }
    string _text;
    public string Text { get => _text; set { _text = value; Raise(); } }
}

public partial class MainWindow
{
    readonly ObservableCollection<ChatMessage> _chat = new();
    ChatAgent? _agent;
    CancellationTokenSource? _chatCts;
    bool _chatBusy;
    List<(string Name, List<PageRef> Pages)>? _undoSnapshot;
    bool _chatOpen;

    void InitChat()
    {
        ChatItems.ItemsSource = _chat;
        _agent = new ChatAgent(new AgentTools(this));
        SettingsStore.Changed += () => Dispatcher.BeginInvoke(RefreshUsage);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => IndexText.Text = Indexer.Status(_sources);
        timer.Start();
        RefreshUsage();
        AddWelcome();
    }

    void AddWelcome()
    {
        _chat.Add(new ChatMessage("system",
            "Load your PDFs, then tell me what to do, for example:\n" +
            "• \"This is several books combined — split them by book.\"\n" +
            "• \"Split at every chapter and name the files after the chapter titles.\"\n" +
            "I read and OCR pages to find boundaries, and create the splits for you to review. I never export; you do that."));
    }

    // ---------- panel / settings ----------

    void ToggleChat_Click(object sender, RoutedEventArgs e) => SetChatOpen(!_chatOpen);

    void SetChatOpen(bool open)
    {
        _chatOpen = open;
        ChatPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChatSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChatSplitCol.Width = new GridLength(open ? 12 : 0);
        ChatCol.Width = new GridLength(open ? 430 : 0);
        ChatToggleBtn.Style = (Style)FindResource(open ? "AccentButton" : "OutlineButton");
        var wanted = Math.Min(1700, SystemParameters.WorkArea.Width);
        if (open && Width < wanted && WindowState == WindowState.Normal) Width = wanted;
        if (open) ChatInput.Focus();
    }

    void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        RefreshUsage();
    }

    void RefreshUsage()
    {
        var s = SettingsStore.Current;
        string model = SettingsStore.Models.FirstOrDefault(m => m.Id == s.Model)?.Label.Split('–')[0].Trim() ?? s.Model;
        string key = SettingsStore.GetApiKey() == null ? "No API key – click AI settings." : "";
        UsageText.Text = $"{model}\n{UsageTracker.Describe()}" + (key.Length > 0 ? "\n" + key : "");
        if (s.MonthlyBudgetUsd > 0)
        {
            BudgetBar.Visibility = Visibility.Visible;
            BudgetBar.Value = Math.Min(100, UsageTracker.MonthCost / s.MonthlyBudgetUsd * 100);
            BudgetBar.Foreground = BudgetBar.Value >= 90 ? Brushes.Firebrick : BudgetBar.Value >= 70 ? Brushes.DarkOrange : Brushes.SteelBlue;
            BudgetBar.ToolTip = $"${UsageTracker.MonthCost:F2} of ${s.MonthlyBudgetUsd:F2} monthly limit (estimated)";
        }
        else BudgetBar.Visibility = Visibility.Collapsed;
    }

    // ---------- chat ----------

    void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (!_chatBusy) SendChat();
        }
    }

    void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_chatBusy) _chatCts?.Cancel();
        else SendChat();
    }

    void AddChat(string role, string text)
    {
        _chat.Add(new ChatMessage(role, text));
        ChatScroll.ScrollToEnd();
    }

    async void SendChat()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0 || _agent == null) return;

        if (SettingsStore.GetApiKey() == null)
        {
            AddChat("error", "Add your Anthropic API key first (AI settings...).");
            AiSettings_Click(this, new RoutedEventArgs());
            if (SettingsStore.GetApiKey() == null) return;
        }
        if (_sources.Count == 0)
        {
            AddChat("error", "Load a PDF first (drop it on the window).");
            return;
        }

        ChatInput.Clear();
        AddChat("user", text);
        _undoSnapshot = SnapshotSplits();
        UndoBtn.IsEnabled = true;
        SetBusy(true);
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;
        var agent = _agent;

        await Task.Run(() => agent.RunAsync(text,
            t => Dispatcher.Invoke(() => AddChat("assistant", t)),
            t => Dispatcher.Invoke(() => AddChat("tool", "→ " + t)),
            t => Dispatcher.Invoke(() => AddChat("error", t)),
            ct));

        SetBusy(false);
        RefreshUsage();
        MarkDirty();   // persist the finished conversation
    }

    void SetBusy(bool busy)
    {
        _chatBusy = busy;
        SendBtn.Content = busy ? "Stop" : "Send";
        ChatInput.IsEnabled = !busy;
        if (!busy) ChatInput.Focus();
    }

    void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_chatBusy) return;
        _agent?.Reset();
        _chat.Clear();
        AddWelcome();
    }

    // ---------- undo ----------

    List<(string Name, List<PageRef> Pages)> SnapshotSplits() =>
        _splits.Select(s => (s.Name, s.Pages.Select(p => new PageRef(p.Doc, p.Index)).ToList())).ToList();

    void UndoAi_Click(object sender, RoutedEventArgs e)
    {
        if (_chatBusy || _undoSnapshot == null) return;
        _splits.Clear();
        foreach (var (name, pages) in _undoSnapshot)
        {
            var s = new SplitItem(name);
            foreach (var p in pages.Where(p => _sources.Contains(p.Doc))) s.Pages.Add(new PageRef(p.Doc, p.Index));
            _splits.Add(s);
        }
        _undoSnapshot = null;
        UndoBtn.IsEnabled = false;
        AddChat("system", "Restored the splits to how they were before your last AI request.");
    }
}
