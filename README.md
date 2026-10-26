# PDF Splitter

A Windows desktop app for splitting large PDFs into several smaller ones. Load a file, group pages into named splits, and export one PDF per split. An optional AI assistant can find the boundaries for you, for example in a file that is really several books combined into one.

Everything runs locally. Your PDFs are never uploaded unless you use the AI assistant, and then only the text it reads is sent.

**[Download the latest release](https://github.com/Kentno/pdf-splitter/releases/latest)** (a single `PdfSplitter.exe`, see [Install](#install)).

## Features

- **Load PDFs and images.** Drag files onto the window or use Add files. Images become single-page PDFs. Several files can be open at once.
- **Page grid.** Thumbnails scroll smoothly even in files with thousands of pages. Select pages with click, Shift-click and Ctrl-click.
- **Splits.** Each split is a named list of pages that exports as one PDF. Create a split from the current selection, from page ranges such as `1-10; 25-40`, or by dragging pages onto the splits list.
- **Drag to insert.** Drop pages on the top half of a split card to insert them at the start, or the bottom half to add them at the end. Drop them into a split's page list to insert at an exact position.
- **Full-screen viewer.** Open the grid or a single split full screen and scroll through the pages at reading size. Only the pages near the one you are looking at are loaded.
- **Projects.** Save your files, splits, chat history and usage to a project file and pick up where you left off. The app also autosaves.
- **Export.** Export all splits or only the selected one to a folder.

## AI assistant

You can tell the assistant what you want, for example "this is several books combined, split them by book". It reads bookmarks and metadata, searches page text, scans pages for title-like text, and runs Windows OCR on scanned pages. It then creates the splits for you to review. It never exports. You do that yourself.

There are two ways to use it.

### Claude Desktop (recommended)

PDF Splitter can be installed as an extension in Claude Desktop. You chat in Claude Desktop, Claude uses the app's tools, and the usage comes out of your Claude plan. No API key is needed.

1. Get the app from the [latest release](https://github.com/Kentno/pdf-splitter/releases/latest). In PDF Splitter open AI settings and choose Create extension file.
2. In Claude Desktop go to Settings, then Extensions, then Install Extension, and pick the file.
3. Ask Claude to work on a file, for example "Split C:\path\to\my.pdf by book".

A green Claude Desktop indicator shows in the app while it is connected. See [docs/claude-desktop.md](docs/claude-desktop.md) for how it works and what to watch for.

### In-app chat (API key)

The chat panel in the app calls the Claude API with your own Anthropic API key, so it is billed per use. The key is stored encrypted for your Windows user only. The panel shows estimated token usage and cost, and you can set a monthly spending limit. Use this if you do not have Claude Desktop.

## Install

1. Download `PdfSplitter.exe` from the [latest release](https://github.com/Kentno/pdf-splitter/releases/latest).
2. Put it anywhere you like and run it. There is no installer.

It is a single self-contained file of about 77 MB, so nothing else needs to be installed. It needs 64-bit Windows 10 (version 2004) or later.

The exe is not code-signed, so Windows SmartScreen may warn you the first time you run it. Choose More info, then Run anyway. Each release lists the file's SHA-256 hash so you can check your download. You can also build it yourself, see below.

## Build

You need the .NET 10 SDK.

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 -o publish
```

The publish step produces the single-file `publish\PdfSplitter.exe`. Build the Claude Desktop extension from a published exe, not from a plain build, because the extension includes a copy of the exe.

## Files and privacy

- Settings and the encrypted API key are stored in `%APPDATA%\PdfSplitter`.
- Extracted text and OCR results are cached there too, so large files are only read once.
- Project files (`.pdfsplit`) contain file paths and chat text. Do not commit them to a public repository. The `.gitignore` in this repo already excludes them, along with PDFs and local settings.

## Built with

- WPF on .NET 10
- [PDFsharp](https://github.com/empira/PDFsharp) for writing PDFs
- [PdfPig](https://github.com/UglyToad/PdfPig) for reading text
- Windows.Data.Pdf for rendering and Windows.Media.Ocr for OCR
- The [Anthropic C# SDK](https://github.com/anthropics/anthropic-sdk-csharp) for the in-app assistant
- The Model Context Protocol for the Claude Desktop extension

## Project layout

| File | What it does |
|---|---|
| `MainWindow.*.cs` | The main window, split into the grid, projects, viewer, chat, drag handling and the Desktop bridge |
| `Rendering.cs`, `VirtualizingGridPanel.cs` | Thumbnail scheduling and the virtualized page grid |
| `AgentTools.cs`, `ChatAgent.cs` | The tools the assistant can use and the loop that runs them |
| `McpServer.cs`, `Bridge.cs`, `McpbBuilder.cs` | The Claude Desktop extension: MCP server, link to the running app, packaging |
| `Project.cs`, `Settings.cs`, `DocCache.cs` | Saving projects, settings and the text cache |

The `samples` folder has two generated PDFs for trying the app.
