using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PdfSplitter;

/// <summary>
/// Local link between the running app and the MCP server process that Claude Desktop starts (the same exe run with --mcp).
/// One JSON object per line over a named pipe that only the current Windows user can open:
///   {"op":"hello"}                                    -> {"id":0,"ok":true}
///   {"op":"call","id":7,"tool":"...","args":{...}}    -> {"id":7,"ok":true,"text":"...","isError":false}
/// </summary>
public static class Bridge
{
    /// <summary>One per Windows user. PDFSPLITTER_PIPE overrides the name so tests can run beside a real instance without touching it.</summary>
    public static string PipeName => Environment.GetEnvironmentVariable("PDFSPLITTER_PIPE") is { Length: > 0 } custom
        ? custom
        : "PdfSplitter-bridge-" + Environment.UserName.Replace('\\', '_').Replace('/', '_');

    static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfSplitter");

    /// <summary>The app records where it runs from, so the MCP side can start the same app when Claude asks and it is closed.</summary>
    public static string AppPathFile => Path.Combine(AppData, "app-path.txt");

    public static void RememberAppPath()
    {
        try
        {
            Directory.CreateDirectory(AppData);
            if (Environment.ProcessPath is { } p) File.WriteAllText(AppPathFile, p);
        }
        catch { }
    }

    public static string? LastAppPath()
    {
        try { return File.Exists(AppPathFile) ? File.ReadAllText(AppPathFile).Trim() : null; } catch { return null; }
    }
}

public sealed record BridgeCall(string Tool, string Summary, bool Done, bool IsError);

/// <summary>Runs inside the app: executes the tools Claude Desktop asks for, using the same code as the in-app assistant.</summary>
public sealed class BridgeServer : IDisposable
{
    readonly AgentTools _tools;
    readonly CancellationTokenSource _cts = new();
    int _clients;

    public BridgeServer(AgentTools tools) => _tools = tools;

    public int ClientCount => Volatile.Read(ref _clients);
    public event Action? ClientsChanged;

    /// <summary>Raised on a worker thread before (Done=false) and after (Done=true) each tool call.</summary>
    public event Action<BridgeCall>? ToolCalled;

    public void Start()
    {
        // Another instance of the app already owns the bridge: leave it alone.
        if (BridgeClient.Probe()) return;
        _ = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Bridge.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                var connected = pipe;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch { pipe?.Dispose(); await Task.Delay(1000); }
        }
    }

    async Task HandleAsync(NamedPipeServerStream pipe)
    {
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(pipe, utf8);
        using var writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new SemaphoreSlim(1);
        bool counted = false;

        async Task Reply(object message)
        {
            await writeLock.WaitAsync();
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(message)); }
            catch { /* client went away */ }
            finally { writeLock.Release(); }
        }

        try
        {
            string? line;
            while (!_cts.IsCancellationRequested && (line = await reader.ReadLineAsync(_cts.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement msg;
                try { msg = JsonDocument.Parse(line).RootElement.Clone(); } catch { continue; }
                var op = msg.TryGetProperty("op", out var o) ? o.GetString() : null;
                int id = msg.TryGetProperty("id", out var i) && i.TryGetInt32(out var n) ? n : 0;

                switch (op)
                {
                    case "hello":
                        if (!counted) { counted = true; Interlocked.Increment(ref _clients); ClientsChanged?.Invoke(); }
                        await Reply(new { id, ok = true });
                        break;
                    case "ping":
                        await Reply(new { id, ok = true });
                        break;
                    case "call":
                        _ = Task.Run(async () =>
                        {
                            var (text, isError) = await RunToolAsync(msg);
                            await Reply(new { id, ok = true, text, isError });
                        });
                        break;
                }
            }
        }
        catch { /* connection ended */ }
        finally
        {
            if (counted) { Interlocked.Decrement(ref _clients); ClientsChanged?.Invoke(); }
            pipe.Dispose();
        }
    }

    async Task<(string Text, bool IsError)> RunToolAsync(JsonElement msg)
    {
        var tool = msg.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
        var args = new Dictionary<string, JsonElement>();
        if (msg.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject()) args[p.Name] = p.Value.Clone();

        try { ToolCalled?.Invoke(new BridgeCall(tool, Summarize(args), false, false)); } catch { }
        (string Text, bool IsError) result;
        try { result = await _tools.ExecuteAsync(tool, args, _cts.Token); }
        catch (Exception ex) { result = ($"{ex.GetType().Name}: {ex.Message}", true); }
        try { ToolCalled?.Invoke(new BridgeCall(tool, "", true, result.IsError)); } catch { }
        return result;
    }

    static string Summarize(Dictionary<string, JsonElement> args)
    {
        var s = string.Join(", ", args.Select(kv =>
        {
            var v = kv.Value.ToString();
            return $"{kv.Key}={(v.Length > 40 ? v[..40] + "…" : v)}";
        }));
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Runs in the MCP server process: talks to the app, starting it first if Claude asks while it is closed.</summary>
public sealed class BridgeClient : IDisposable
{
    NamedPipeClientStream? _pipe;
    StreamWriter? _writer;
    readonly SemaphoreSlim _connectLock = new(1);
    readonly SemaphoreSlim _writeLock = new(1);
    readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    int _nextId;

    public bool Connected => _pipe is { IsConnected: true };

    /// <summary>Is an app instance serving the bridge right now? (Connects and disconnects.)</summary>
    public static bool Probe()
    {
        try
        {
            using var p = new NamedPipeClientStream(".", Bridge.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            p.Connect(150);
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> TryConnectAsync(int timeoutMs)
    {
        await _connectLock.WaitAsync();
        try
        {
            if (Connected) return true;
            var pipe = new NamedPipeClientStream(".", Bridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(timeoutMs); }
            catch { pipe.Dispose(); return false; }

            var utf8 = new UTF8Encoding(false);
            _pipe = pipe;
            _writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };
            _ = Task.Run(() => ReadLoopAsync(pipe, new StreamReader(pipe, utf8)));
            await SendAsync(new { op = "hello", id = 0 });
            return true;
        }
        finally { _connectLock.Release(); }
    }

    async Task ReadLoopAsync(NamedPipeClientStream pipe, StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                JsonElement msg;
                try { msg = JsonDocument.Parse(line).RootElement.Clone(); } catch { continue; }
                if (!msg.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                TaskCompletionSource<JsonElement>? tcs;
                lock (_pending) { _pending.Remove(id, out tcs); }
                tcs?.TrySetResult(msg);
            }
        }
        catch { }
        finally
        {
            pipe.Dispose();
            List<TaskCompletionSource<JsonElement>> orphans;
            lock (_pending) { orphans = _pending.Values.ToList(); _pending.Clear(); }
            foreach (var t in orphans) t.TrySetException(new IOException("PDF Splitter closed the connection."));
        }
    }

    async Task SendAsync(object message)
    {
        await _writeLock.WaitAsync();
        try { await _writer!.WriteLineAsync(JsonSerializer.Serialize(message)); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Keeps (re)connecting in the background so the app's "connected" indicator comes on as soon as the app is open.</summary>
    public async Task KeepConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Connected) await TryConnectAsync(250);
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Makes sure the app is reachable, starting it if needed. Returns an explanation if it could not be.</summary>
    public async Task<string?> EnsureAppAsync()
    {
        if (Connected || await TryConnectAsync(500)) return null;

        if (Environment.GetEnvironmentVariable("PDFSPLITTER_NO_LAUNCH") == "1")
            return "PDF Splitter is not running. Ask the user to open it.";

        var exe = Bridge.LastAppPath();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) exe = Environment.ProcessPath;
        try { Process.Start(new ProcessStartInfo(exe!) { UseShellExecute = true }); }
        catch (Exception ex) { return $"PDF Splitter is not running and could not be started ({ex.Message}). Ask the user to open it."; }

        for (int waited = 0; waited < 25000; waited += 500)
        {
            await Task.Delay(500);
            if (await TryConnectAsync(300)) return null;
        }
        return "PDF Splitter was started but did not become ready in time. Ask the user to check the app and try again.";
    }

    public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonElement? args, CancellationToken ct)
    {
        var problem = await EnsureAppAsync();
        if (problem != null) return (problem, true);

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        try
        {
            await SendAsync(new { op = "call", id, tool, args = args ?? JsonDocument.Parse("{}").RootElement });
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var reply = await tcs.Task;
            var text = reply.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var isError = reply.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            return (text, isError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_pending) _pending.Remove(id);
            return ($"Lost the connection to PDF Splitter ({ex.Message}). Ask the user to check that the app is open, then try again.", true);
        }
    }

    public void Dispose() { try { _pipe?.Dispose(); } catch { } }
}
