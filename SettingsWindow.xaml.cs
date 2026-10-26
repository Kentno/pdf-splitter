using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Anthropic;

namespace PdfSplitter;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = SettingsStore.Current;
        ModelBox.ItemsSource = SettingsStore.Models;
        ModelBox.SelectedItem = SettingsStore.Models.FirstOrDefault(m => m.Id == s.Model) ?? SettingsStore.Models[0];
        BudgetBox.Text = s.MonthlyBudgetUsd.ToString("0.##", CultureInfo.InvariantCulture);
        OcrBox.IsChecked = s.BackgroundOcr;
        RefreshKey();
        RefreshUsage();

        // The owner is assigned after construction, so check the Claude Desktop connection once shown and keep it current.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshMcp();
        Loaded += (_, _) => { RefreshMcp(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    // ---------- Claude Desktop extension ----------

    void RefreshMcp()
    {
        bool connected = (Owner as MainWindow)?.ClaudeDesktopClients > 0;
        McpDot.Fill = connected ? System.Windows.Media.Brushes.MediumSeaGreen : System.Windows.Media.Brushes.Gray;
        McpStatus.Text = connected
            ? "Claude Desktop is connected."
            : "Not connected. Install the extension, then open Claude Desktop (it connects when it starts the extension).";
    }

    async void CreateMcpb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the Claude Desktop extension",
            FileName = "PDF Splitter.mcpb",
            Filter = "Claude Desktop extension|*.mcpb",
            DefaultExt = ".mcpb",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        McpResult.Foreground = System.Windows.Media.Brushes.Gray;
        McpResult.Text = "Creating the extension (about 70 MB)...";
        try
        {
            await Task.Run(() => McpbBuilder.Create(dlg.FileName));
            McpResult.Foreground = System.Windows.Media.Brushes.DarkGreen;
            McpResult.Text = "Saved. Now install it in Claude Desktop (see How to install).";
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex)
        {
            McpResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            McpResult.Text = "Could not create the extension: " + ex.Message;
        }
    }

    void HowToInstall_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "1. Click \"Create extension file...\" and save the .mcpb file.\n\n" +
            "2. In Claude Desktop open Settings > Extensions > Advanced settings > Install Extension... and choose that file.\n" +
            "   (Menu names can differ slightly between Claude Desktop versions.)\n\n" +
            "3. Start a chat in Claude Desktop and ask, for example: \"Split C:\\path\\to\\my.pdf by book.\" " +
            "PDF Splitter opens by itself if it is closed, and the \"Claude Desktop\" indicator lights up here while it is connected.\n\n" +
            "Claude Desktop may ask you to approve each tool the first time; \"Always allow\" for the read-only ones saves clicks.\n\n" +
            "After updating PDF Splitter, create and install the extension again so it runs the new version.",
            "Use Claude Desktop", MessageBoxButton.OK, MessageBoxImage.Information);

    void CopyConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(McpbBuilder.ConfigSnippet());
            McpResult.Foreground = System.Windows.Media.Brushes.DarkGreen;
            McpResult.Text = "Copied. In Claude Desktop: Settings > Developer > Edit Config, merge it into claude_desktop_config.json, then restart Claude Desktop.";
        }
        catch (Exception ex)
        {
            McpResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            McpResult.Text = "Could not copy: " + ex.Message;
        }
    }

    void RefreshKey() => KeyStatus.Text = SettingsStore.DescribeKey();
    void RefreshUsage() => UsageText.Text = UsageTracker.Describe();

    void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { TestResult.Text = "Paste your API key above first."; return; }
        if (!key.StartsWith("sk-ant-"))
        {
            var r = MessageBox.Show(this, "That doesn't look like an Anthropic API key (they start with \"sk-ant-\"). Save it anyway?",
                "API key", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        SettingsStore.SetApiKey(key);
        KeyBox.Clear();
        RefreshKey();
        TestResult.Text = "Key saved.";
    }

    async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) key = SettingsStore.GetApiKey() ?? "";
        if (key.Length == 0) { TestResult.Text = "No key to test."; return; }

        TestResult.Foreground = System.Windows.Media.Brushes.Gray;
        TestResult.Text = "Testing...";
        try
        {
            var client = new AnthropicClient { ApiKey = key };
            await client.Models.List();
            TestResult.Foreground = System.Windows.Media.Brushes.DarkGreen;
            TestResult.Text = "Key works.";
        }
        catch (Exception ex)
        {
            TestResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResult.Text = "Key test failed: " + ex.Message;
        }
    }

    void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.SetApiKey(null);
        RefreshKey();
        TestResult.Text = "Saved key removed.";
    }

    void OpenConsole_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://console.anthropic.com/settings/usage") { UseShellExecute = true });

    void ResetUsage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset the local usage counters (this month and all time)?", "Usage",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UsageTracker.ResetStored();
        RefreshUsage();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var s = SettingsStore.Current;
        if (ModelBox.SelectedItem is ModelOption m) s.Model = m.Id;
        if (double.TryParse(BudgetBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b >= 0)
            s.MonthlyBudgetUsd = b;
        s.BackgroundOcr = OcrBox.IsChecked == true;
        SettingsStore.Save();
        base.OnClosing(e);
    }
}
