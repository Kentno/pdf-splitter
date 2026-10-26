using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfSplitter;

/// <summary>
/// "PdfSplitter.exe --mcp": a Model Context Protocol server over stdio, which is what Claude Desktop (or Claude Code) starts.
/// It holds no state of its own; every tool call is forwarded to the running app through the bridge, so Claude's work
/// shows up live in the app and the conversation runs on the user's Claude plan.
/// </summary>
public static class McpServer
{
    const string ServerName = "pdf-splitter";
    const string ServerVersion = "1.0.0";
    const string FallbackProtocol = "2025-06-18";

    static readonly HashSet<string> ReadOnly = new()
    {
        "list_documents", "get_document_info", "get_page_text", "search_text", "scan_pages", "list_splits", "get_app_state",
    };

    static readonly Dictionary<string, string> Titles = new()
    {
        ["list_documents"] = "List loaded documents",
        ["get_document_info"] = "Document info and bookmarks",
        ["get_page_text"] = "Read page text",
        ["search_text"] = "Search text",
        ["scan_pages"] = "Scan pages for titles",
        ["ocr_pages"] = "OCR scanned pages",
        ["list_splits"] = "List splits",
        ["create_splits"] = "Create splits",
        ["add_pages_to_split"] = "Add pages to a split",
        ["rename_split"] = "Rename a split",
        ["delete_split"] = "Delete a split",
        ["open_files"] = "Open PDF or image files",
        ["get_app_state"] = "App overview",
    };

    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync()
    {
        var utf8 = new UTF8Encoding(false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var outLock = new object();
        using var cts = new CancellationTokenSource();
        using var bridge = new BridgeClient();

        void Send(JsonObject message)
        {
            lock (outLock) stdout.WriteLine(message.ToJsonString(Json));
        }
        static void Log(string text) { try { Console.Error.WriteLine("[pdf-splitter mcp] " + text); } catch { } }

        _ = bridge.KeepConnectedAsync(cts.Token);
        Log("started");

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { Send(Error(null, -32700, "Parse error")); continue; }
            if (node is not JsonObject msg) continue;

            var method = msg["method"]?.GetValue<string>();
            var id = msg["id"]?.DeepClone();
            if (method == null) continue;      // a response to something we never asked, or malformed
            if (id == null) continue;          // notifications (initialized, cancelled, ...) need no reply

            switch (method)
            {
                case "initialize":
                    var requested = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? FallbackProtocol;
                    Send(Result(id, new JsonObject
                    {
                        ["protocolVersion"] = requested,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "PDF Splitter", ["version"] = ServerVersion },
                        ["instructions"] = AssistantGuidance.McpInstructions,
                    }));
                    break;

                case "ping":
                    Send(Result(id, new JsonObject()));
                    break;

                case "tools/list":
                    Send(Result(id, new JsonObject { ["tools"] = ToolList() }));
                    break;

                case "tools/call":
                    var callId = id;
                    var name = msg["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = msg["params"]?["arguments"];
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            JsonElement? argEl = args == null ? null : JsonDocument.Parse(args.ToJsonString()).RootElement.Clone();
                            var (text, isError) = await bridge.CallAsync(name, argEl, cts.Token);
                            Send(Result(callId, ToolResult(text, isError)));
                        }
                        catch (Exception ex)
                        {
                            Log("tool call failed: " + ex.Message);
                            Send(Result(callId, ToolResult($"{ex.GetType().Name}: {ex.Message}", true)));
                        }
                    });
                    break;

                case "resources/list":
                    Send(Result(id, new JsonObject { ["resources"] = new JsonArray() }));
                    break;
                case "prompts/list":
                    Send(Result(id, new JsonObject { ["prompts"] = new JsonArray() }));
                    break;

                default:
                    Send(Error(id, -32601, "Method not found: " + method));
                    break;
            }
        }

        cts.Cancel();
        Log("stdin closed, exiting");
        return 0;
    }

    static JsonArray ToolList()
    {
        var list = new JsonArray();
        foreach (var t in AgentTools.ExternalTools())
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = JsonSerializer.SerializeToNode(t.InputSchema.Properties ?? new Dictionary<string, JsonElement>()),
            };
            if (t.InputSchema.Required is { Count: > 0 } req) schema["required"] = JsonSerializer.SerializeToNode(req);

            list.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["title"] = Titles.GetValueOrDefault(t.Name, t.Name),
                ["description"] = t.Description,
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = ReadOnly.Contains(t.Name),
                    ["destructiveHint"] = t.Name == "delete_split",
                    ["openWorldHint"] = false,
                },
            });
        }
        return list;
    }

    static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
