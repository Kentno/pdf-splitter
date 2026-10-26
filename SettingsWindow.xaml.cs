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
