using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfSplitter;

public class UsageTotals
{
    public long AllInput { get; set; }
    public long AllOutput { get; set; }
    public double AllCost { get; set; }
    public int AllRequests { get; set; }
    public string MonthKey { get; set; } = "";
    public long MonthInput { get; set; }
    public long MonthOutput { get; set; }
    public double MonthCost { get; set; }
}

public class AppSettings
{
    public string? EncryptedApiKey { get; set; }
    public string Model { get; set; } = "claude-opus-5-5";
    public double MonthlyBudgetUsd { get; set; }
    public bool BackgroundOcr { get; set; } = true;
    public UsageTotals Usage { get; set; } = new();
    public string? LastProjectPath { get; set; }
    public List<string> RecentProjects { get; set; } = new();
    /// <summary>Full-screen viewer: how many pages before and after the visible ones stay rendered.</summary>
    public int ViewerPreload { get; set; } = 3;
    /// <summary>User dismissed the "use Claude Desktop instead" tip in the chat panel.</summary>
    public bool HideDesktopTip { get; set; }
}

public record ModelOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public static class SettingsStore
{
    public static readonly ModelOption[] Models =
    {
        new("claude-opus-5-5", "Claude Opus 5.5 \u2013 most capable (default)"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5 \u2013 cheaper, fast"),
        new("claude-haiku-5-5", "Claude Haiku 5.5 \u2013 cheapest"),
    };

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfSplitter");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly object Lock = new();

    public static AppSettings Current { get; private set; } = Load();
    public static event Action? Changed;

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt file: start fresh */ }
        return new();
    }

    public static void Save()
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        Changed?.Invoke();
    }

    /// <summary>The key saved in this app (encrypted per Windows user), else ANTHROPIC_API_KEY.</summary>
    public static string? GetApiKey()
    {
        if (!string.IsNullOrEmpty(Current.EncryptedApiKey))
        {
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(Current.EncryptedApiKey), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { /* different user/machine: fall through */ }
        }
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public static bool HasStoredKey => !string.IsNullOrEmpty(Current.EncryptedApiKey);

    public static void SetApiKey(string? key)
    {
        Current.EncryptedApiKey = string.IsNullOrWhiteSpace(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        Save();
    }

    public static string DescribeKey()
    {
        var k = GetApiKey();
        if (k == null) return "No API key saved.";
        var masked = k.Length > 12 ? k[..7] + "\u2026" + k[^4..] : "\u2026";
        return HasStoredKey
            ? $"Saved key: {masked}  (encrypted for your Windows account)"
            : $"Using ANTHROPIC_API_KEY environment variable: {masked}";
    }
}

public static class Pricing
{
    // USD per million tokens: input, output, cache read. Cache writes are 1.25x input.
    static readonly Dictionary<string, (double In, double Out, double CacheRead)> Table = new()
    {
        ["claude-opus-5-5"] = (4.00, 20.00, 0.20),
        ["claude-sonnet-5-5"] = (2.00, 10.00, 0.20),
        ["claude-haiku-5-5"] = (0.10, 0.50, 0.01),
    };

    public static double Cost(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        var p = Table.TryGetValue(model, out var v) ? v : Table["claude-opus-5-5"];
        return (input * p.In + output * p.Out + cacheRead * p.CacheRead + cacheWrite * p.In * 1.25) / 1_000_000.0;
    }
}

public static class UsageTracker
{
    public static long SessionInput, SessionOutput, SessionRequests;
    public static double SessionCost;

    /// <summary>Lifetime usage of the open project; swapped when a project is opened.</summary>
    public static UsageTotals ProjectUsage { get; set; } = new();

    public static void Record(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        long totalIn = input + cacheRead + cacheWrite;
        double cost = Pricing.Cost(model, input, output, cacheRead, cacheWrite);

        SessionInput += totalIn; SessionOutput += output; SessionCost += cost; SessionRequests++;

        var u = SettingsStore.Current.Usage;
        var month = DateTime.Now.ToString("yyyy-MM");
        if (u.MonthKey != month) { u.MonthKey = month; u.MonthInput = 0; u.MonthOutput = 0; u.MonthCost = 0; }
        u.AllInput += totalIn; u.AllOutput += output; u.AllCost += cost; u.AllRequests++;
        u.MonthInput += totalIn; u.MonthOutput += output; u.MonthCost += cost;

        var pu = ProjectUsage;
        pu.AllInput += totalIn; pu.AllOutput += output; pu.AllCost += cost; pu.AllRequests++;
        SettingsStore.Save();
    }

    public static void ResetStored()
    {
        SettingsStore.Current.Usage = new UsageTotals { MonthKey = DateTime.Now.ToString("yyyy-MM") };
        SessionInput = SessionOutput = SessionRequests = 0; SessionCost = 0;
        SettingsStore.Save();
    }

    static string Tok(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:F2}M" : n >= 1000 ? $"{n / 1000.0:F1}k" : n.ToString();

    public static string Describe()
    {
        var u = SettingsStore.Current.Usage;
        var month = DateTime.Now.ToString("yyyy-MM");
        double mCost = u.MonthKey == month ? u.MonthCost : 0;
        long mIn = u.MonthKey == month ? u.MonthInput : 0, mOut = u.MonthKey == month ? u.MonthOutput : 0;
        return $"This session:  {Tok(SessionInput)} in / {Tok(SessionOutput)} out  \u2248 ${SessionCost:F3}  ({SessionRequests} requests)\n" +
               $"This project:  {Tok(ProjectUsage.AllInput)} in / {Tok(ProjectUsage.AllOutput)} out  ≈ ${ProjectUsage.AllCost:F2}  ({ProjectUsage.AllRequests} requests)\n" +
               $"This month ({month}):  {Tok(mIn)} in / {Tok(mOut)} out  \u2248 ${mCost:F2}\n" +
               $"All time:  {Tok(u.AllInput)} in / {Tok(u.AllOutput)} out  \u2248 ${u.AllCost:F2}  ({u.AllRequests} requests)";
    }

    public static double MonthCost
    {
        get
        {
            var u = SettingsStore.Current.Usage;
            return u.MonthKey == DateTime.Now.ToString("yyyy-MM") ? u.MonthCost : 0;
        }
    }
}
