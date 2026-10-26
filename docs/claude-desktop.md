# Using PDF Splitter from Claude Desktop

PDF Splitter can be installed in Claude Desktop (or Claude Code) as an extension. Claude then uses the app's tools
(read bookmarks, search and OCR text, scan pages for title pages, create and edit splits) while you chat in Claude Desktop.
The conversation runs on your Claude plan; no API key is involved. The in-app assistant (which does use an API key)
keeps working and is unaffected.

## Install

1. In PDF Splitter open **AI settings**, then **Create extension file...** and save `PDF Splitter.mcpb` (about 70 MB).
2. In Claude Desktop: **Settings > Extensions > Advanced settings > Install Extension...** and pick that file.
3. Chat, e.g. *"Split C:\path\to\my.pdf by book."* PDF Splitter opens by itself if it is closed.

While Claude Desktop is connected, a green **Claude Desktop** indicator shows in the app's top bar (with the tool Claude
is running), each call is listed in the AI assistant panel, and **Undo AI changes** reverts Claude's changes to the splits.

Prefer editing config by hand? **Copy manual config** in AI settings gives a `claude_desktop_config.json` snippet that runs
the installed exe in place (no copy), so it follows app updates automatically.

After updating PDF Splitter, create and install the extension again: the extension contains a copy of the exe.

## How it works

```
Claude Desktop --stdio/MCP--> PdfSplitter.exe --mcp  --named pipe-->  running PdfSplitter app
                              (headless, no window)                    (same tools as the in-app assistant)
```

* `PdfSplitter.exe --mcp` is a small MCP server (`McpServer.cs`). It keeps no state; every tool call goes to the app.
* The app serves a local named pipe (`Bridge.cs`) that only the current Windows user can open, and runs the calls with the
  same code as the in-app assistant (`AgentTools.cs`).
* If the app is closed when Claude calls a tool, the MCP server starts it (the path is remembered in
  `%APPDATA%\PdfSplitter\app-path.txt`; set `PDFSPLITTER_NO_LAUNCH=1` to disable) and waits up to 25 seconds.
* `PdfSplitter.exe --make-mcpb <file>` writes the extension without opening the app.

## Tools Claude gets

The in-app assistant's tools plus `open_files` (load PDFs/images by full path) and `get_app_state`. Exporting is not offered:
you export from the app.

## Things to know

* `open_files` lets Claude load any PDF or image your Windows account can read, and Claude can then read text from it.
  Claude Desktop asks you to approve tools; review `open_files` calls.
* Text that Claude reads (search hits, page text) is sent to Claude as part of your conversation.
* Long scans use your plan's usage window; tool results are size-limited to keep that in check.
* Anthropic's terms say plan sign-in is for Claude's own apps: this feature never touches your sign-in. Claude Desktop starts
  the extension and does the talking; PDF Splitter only answers tool calls.
