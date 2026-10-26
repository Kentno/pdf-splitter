using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace PdfSplitter;

/// <summary>Runs the Claude tool-use loop against the app's tools. Conversation history is kept between requests.</summary>
public class ChatAgent
{
    const int MaxSteps = 40;

    const string SystemPrompt = """
        You are the AI assistant inside a desktop app that splits large PDFs. The user loads PDFs/images, and "splits" are named lists of pages that each export as one PDF. You inspect the loaded documents with tools and create or edit splits to carry out the user's instructions (for example: "this is several books combined, split by book").

        Rules:
        - You cannot export. You only prepare splits; the user reviews them in the app and presses Export. Never delete or rename splits the user made unless asked.
        - Page numbers are 1-based positions in the PDF file, not printed page numbers.
        - Start with list_documents. For finding boundaries, prefer in this order: bookmarks/metadata (get_document_info); search_text for markers (e.g. "ISBN", "Copyright", "All rights reserved", "Chapter 1", "Table of Contents", a title you already found); scan_pages with a step to survey long documents for title-like large text, then zoom in around candidates; finally get_page_text on a few pages around each boundary to confirm. Never read whole documents; keep calls small and targeted.
        - Scanned pages are OCR'd by Windows OCR, which can make mistakes (and may be slow); allow for typos when searching.
        - Make sure the splits you create cover every page exactly once when the user wants a full split (no gaps/overlaps), and name them after the actual book/section titles (plain file-name-safe text).
        - If instructions are ambiguous, ask one short question instead of guessing. When done, give a brief summary: each split with its page range, how confident you are, and any boundaries the user should double-check.
        """;

    readonly AgentTools _tools;
    // _history is what is sent to the API; _log mirrors it in a serialisable shape (same indices).
    readonly List<MessageParam> _history = new();
    readonly List<HistoryEntry> _log = new();
    readonly object _lock = new();

    public ChatAgent(AgentTools tools) => _tools = tools;

    void Add(MessageParam message, HistoryEntry entry)
    {
        lock (_lock) { _history.Add(message); _log.Add(entry); }
    }

    void RollbackTo(int count)
    {
        lock (_lock)
        {
            if (_history.Count <= count) return;
            _history.RemoveRange(count, _history.Count - count);
            _log.RemoveRange(count, _log.Count - count);
        }
    }

    public void Reset()
    {
        lock (_lock) { _history.Clear(); _log.Clear(); }
    }

    // ---------- persistence ----------

    /// <summary>The conversation up to the last complete exchange (never a dangling tool call).</summary>
    public List<HistoryEntry> Export()
    {
        lock (_lock)
        {
            var copy = _log.ToList();
            while (copy.Count > 0)
            {
                var last = copy[^1];
                bool completeAssistant = last.Role == "assistant" && last.Blocks?.Any(b => b.Type == "tool_use") != true;
                if (completeAssistant) break;
                copy.RemoveAt(copy.Count - 1);
            }
            return copy;
        }
    }

    public void Import(IEnumerable<HistoryEntry> entries)
    {
        lock (_lock)
        {
            _history.Clear(); _log.Clear();
            foreach (var e in entries) { _history.Add(ToParam(e)); _log.Add(e); }
        }
    }

    static MessageParam ToParam(HistoryEntry e)
    {
        var role = e.Role == "assistant" ? Role.Assistant : Role.User;
        if (e.Blocks == null) return new MessageParam { Role = role, Content = e.Text ?? "" };

        var list = new List<ContentBlockParam>();
        foreach (var b in e.Blocks)
        {
            switch (b.Type)
            {
                case "text":
                    list.Add(new TextBlockParam { Text = b.Text ?? "" });
                    break;
                case "thinking":
                    list.Add(new ThinkingBlockParam { Thinking = b.Text ?? "", Signature = b.Signature ?? "" });
                    break;
                case "redacted_thinking":
                    list.Add(new RedactedThinkingBlockParam { Data = b.Data ?? "" });
                    break;
                case "tool_use":
                    list.Add(new ToolUseBlockParam
                    {
                        ID = b.Id ?? "",
                        Name = b.Name ?? "",
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b.Input ?? "{}") ?? new(),
                    });
                    break;
                case "tool_result":
                    list.Add(new ToolResultBlockParam { ToolUseID = b.ToolUseId ?? "", Content = b.Text ?? "", IsError = b.IsError ? true : null });
                    break;
            }
        }
        return new MessageParam { Role = role, Content = list };
    }

    // ---------- the loop ----------

    public async Task RunAsync(string userText, Action<string> onText, Action<string> onTool, Action<string> onError, CancellationToken ct)
    {
        var key = SettingsStore.GetApiKey();
        if (key == null) { onError("No API key. Open AI settings and add your Anthropic API key."); return; }

        var client = new AnthropicClient { ApiKey = key };
        var model = SettingsStore.Current.Model;
        var toolDefs = _tools.Definitions();
        int rollbackTo;
        lock (_lock) rollbackTo = _history.Count;
        Add(new MessageParam { Role = Role.User, Content = userText }, new HistoryEntry { Role = "user", Text = userText });

        try
        {
            for (int step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                var s = SettingsStore.Current;
                if (s.MonthlyBudgetUsd > 0 && UsageTracker.MonthCost >= s.MonthlyBudgetUsd)
                {
                    onError($"Monthly spend limit reached (${UsageTracker.MonthCost:F2} of ${s.MonthlyBudgetUsd:F2} estimated). Raise it in AI settings to continue.");
                    return;
                }

                List<MessageParam> snapshot;
                lock (_lock) snapshot = _history.ToList();

                var resp = await client.Messages.Create(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = 16000,
                    System = SystemPrompt,
                    Tools = toolDefs,
                    Messages = snapshot,
                    OutputConfig = new OutputConfig { Effort = Effort.Medium },
                    CacheControl = new CacheControlEphemeral(),
                }, ct);

                UsageTracker.Record(model,
                    resp.Usage.InputTokens,
                    resp.Usage.OutputTokens,
                    resp.Usage.CacheReadInputTokens ?? 0,
                    resp.Usage.CacheCreationInputTokens ?? 0);

                var assistant = new List<ContentBlockParam>();
                var blocks = new List<BlockDto>();
                var toolUses = new List<ToolUseBlock>();
                foreach (ContentBlock block in resp.Content)
                {
                    if (block.TryPickText(out TextBlock? text))
                    {
                        assistant.Add(new TextBlockParam { Text = text.Text });
                        blocks.Add(new BlockDto { Type = "text", Text = text.Text });
                        if (!string.IsNullOrWhiteSpace(text.Text)) onText(text.Text);
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? thinking))
                    {
                        assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                        blocks.Add(new BlockDto { Type = "thinking", Text = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? red))
                    {
                        assistant.Add(new RedactedThinkingBlockParam { Data = red.Data });
                        blocks.Add(new BlockDto { Type = "redacted_thinking", Data = red.Data });
                    }
                    else if (block.TryPickToolUse(out ToolUseBlock? tu))
                    {
                        assistant.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                        blocks.Add(new BlockDto { Type = "tool_use", Id = tu.ID, Name = tu.Name, Input = JsonSerializer.Serialize(tu.Input) });
                        toolUses.Add(tu);
                    }
                }
                if (assistant.Count == 0)
                {
                    assistant.Add(new TextBlockParam { Text = "(no response)" });
                    blocks.Add(new BlockDto { Type = "text", Text = "(no response)" });
                }
                Add(new MessageParam { Role = Role.Assistant, Content = assistant }, new HistoryEntry { Role = "assistant", Blocks = blocks });

                if (resp.StopReason == "refusal") { onError("The model declined this request."); return; }
                if (toolUses.Count == 0)
                {
                    if (resp.StopReason == "max_tokens") onError("The reply was cut off (output limit).");
                    return;
                }

                var results = new List<ContentBlockParam>();
                var resultBlocks = new List<BlockDto>();
                foreach (var tu in toolUses)
                {
                    onTool(Describe(tu));
                    var (text, isError) = await _tools.ExecuteAsync(tu.Name, tu.Input, ct);
                    results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = text, IsError = isError ? true : null });
                    resultBlocks.Add(new BlockDto { Type = "tool_result", ToolUseId = tu.ID, Text = text, IsError = isError });
                }
                Add(new MessageParam { Role = Role.User, Content = results }, new HistoryEntry { Role = "user", Blocks = resultBlocks });
            }
            onError($"Stopped after {MaxSteps} steps. Ask me to continue if needed.");
        }
        catch (OperationCanceledException)
        {
            RollbackTo(rollbackTo);
            onError("Stopped.");
        }
        catch (Exception ex)
        {
            RollbackTo(rollbackTo);
            var msg = ex.Message;
            if (msg.Contains("401") || msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                msg += "\n(Check your API key in AI settings.)";
            onError(msg);
        }
    }

    static string Describe(ToolUseBlock tu)
    {
        var args = string.Join(", ", tu.Input.Select(kv =>
        {
            var v = kv.Value.ToString();
            return $"{kv.Key}={(v.Length > 60 ? v[..60] + "…" : v)}";
        }));
        return $"{tu.Name}({args})";
    }
}
