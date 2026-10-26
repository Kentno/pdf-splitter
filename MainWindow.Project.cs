using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PdfSplitter;

public partial class MainWindow
{
    const string ProjectExt = ".pdfsplit";
    const string AppTitle = "PDF Splitter";

    static string ScratchPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfSplitter", "untitled" + ProjectExt);

    /// <summary>Path of the saved project, or null while working in the unsaved "Untitled" project.</summary>
    string? _projectPath;
    bool _suspendDirty;
    bool _autosavePaused;
    DispatcherTimer? _saveTimer;

    void InitProject()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        _sources.CollectionChanged += (_, _) => MarkDirty();
        _chat.CollectionChanged += (_, _) => MarkDirty();
        _splits.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (SplitItem s in e.NewItems) s.PropertyChanged += (_, _) => MarkDirty();
            MarkDirty();
        };
        OutputBox.TextChanged += (_, _) => MarkDirty();
        SettingsStore.Changed += () => Dispatcher.BeginInvoke(MarkDirty);   // per-project usage rides on settings saves

        Loaded += async (_, _) => await StartupOpenAsync();
        Closing += (_, _) => SaveNow();
        UpdateTitle();
    }

    // ---------- dirty tracking / saving ----------

    void MarkDirty()
    {
        if (_suspendDirty || _autosavePaused || _saveTimer == null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    string ProjectDisplayName => _projectPath == null ? "Untitled" : Path.GetFileNameWithoutExtension(_projectPath);

    void UpdateTitle() => Title = $"{ProjectDisplayName}{(_autosavePaused ? " (autosave paused)" : "")} — {AppTitle}";

    bool IsEmptyProject => _sources.Count == 0 && _splits.Count == 0 && _chat.All(m => m.Role == "system");

    ProjectFile Capture(string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        var pf = new ProjectFile
        {
            OutputFolder = OutputBox.Text,
            NextDocId = _nextDocId,
            ChatOpen = _chatOpen,
            Usage = UsageTracker.ProjectUsage,
            ApiHistory = _agent?.Export() ?? new(),
        };
        foreach (var d in _sources)
        {
            long size = 0; DateTime mod = default;
            try { var fi = new FileInfo(d.Path); size = fi.Length; mod = fi.LastWriteTimeUtc; } catch { }
            string? rel = null;
            try { rel = Path.GetRelativePath(dir, d.Path); } catch { }
            pf.Sources.Add(new ProjectSource { Id = d.Id, Path = d.Path, RelativePath = rel, Size = size, ModifiedUtc = mod, PageCount = d.PageCount });
        }
        foreach (var s in _splits)
            pf.Splits.Add(new ProjectSplit { Name = s.Name, Pages = ProjectIO.EncodeRuns(s.Pages) });
        foreach (var m in _chat)
            pf.Chat.Add(new ProjectChat { Role = m.Role, Text = m.Text });
        return pf;
    }

    void SaveNow()
    {
        if (_suspendDirty || _autosavePaused) return;
        _saveTimer?.Stop();
        var path = _projectPath ?? ScratchPath;
        if (_projectPath == null && IsEmptyProject) return;   // don't write an empty scratch file
        try { ProjectIO.Write(path, Capture(path)); }
        catch (Exception ex) { SetStatus("Autosave failed: " + ex.Message); }
    }

    // ---------- open / new / save as ----------

    async Task StartupOpenAsync()
    {
        var last = SettingsStore.Current.LastProjectPath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last)) { await OpenProjectAsync(last, scratch: false); return; }
        if (File.Exists(ScratchPath)) await OpenProjectAsync(ScratchPath, scratch: true);
    }

    /// <summary>Loads a project file into the app. Returns false (leaving the current project untouched) if it can't be read.</summary>
    async Task<bool> OpenProjectAsync(string path, bool scratch)
    {
        ProjectFile pf;
        try { pf = ProjectIO.Read(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the project:\n\n{ex.Message}", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            if (scratch) { try { File.Move(path, path + ".bad", true); } catch { } }
            return false;
        }

        _suspendDirty = true;
        try
        {
            ResetState();
            _projectPath = scratch ? null : path;
            OutputBox.Text = pf.OutputFolder;
            _nextDocId = Math.Max(0, pf.NextDocId);
            UsageTracker.ProjectUsage = pf.Usage ?? new UsageTotals();

            var projectDir = Path.GetDirectoryName(path)!;
            int missing = 0, changed = 0;
            foreach (var s in pf.Sources)
            {
                var found = ResolveSource(s, projectDir) ?? AskToLocate(s);
                if (found == null) { missing++; continue; }
                try
                {
                    SetStatus($"Loading {Path.GetFileName(found)}...");
                    var doc = await LoadDocAsync(found, s.Id);
                    if (doc.PageCount != s.PageCount) changed++;
                }
                catch { missing++; }
            }

            int dropped = 0;
            var byId = _sources.ToDictionary(d => d.Id);
            foreach (var ps in pf.Splits)
            {
                var split = new SplitItem(ps.Name);
                foreach (var (docId, index) in ProjectIO.DecodeRuns(ps.Pages))
                {
                    if (byId.TryGetValue(docId, out var doc) && index >= 0 && index < doc.PageCount)
                        split.Pages.Add(new PageRef(doc, index));
                    else dropped++;
                }
                _splits.Add(split);
            }

            foreach (var m in pf.Chat) _chat.Add(new ChatMessage(m.Role, m.Text));
            if (_chat.Count == 0) AddWelcome();
            _agent?.Import(pf.ApiHistory);
            SetChatOpen(pf.ChatOpen);

            _autosavePaused = missing > 0 || changed > 0 || dropped > 0;
            if (_autosavePaused)
            {
                var why = new List<string>();
                if (missing > 0) why.Add($"{missing} file(s) couldn't be found");
                if (changed > 0) why.Add($"{changed} file(s) have a different page count than when saved");
                if (dropped > 0) why.Add($"{dropped} split page(s) no longer exist");
                AddChat("system", "Opened with problems: " + string.Join("; ", why) +
                    ".\nAutosave is paused so your saved project isn't overwritten. Use Project ▾ Save as... to keep a corrected copy.");
            }
            else SetStatus($"Opened {ProjectDisplayName}.");

            if (!scratch) RememberProject(path);
        }
        finally
        {
            _suspendDirty = false;
            UpdateTitle();
        }
        return true;
    }

    static string? ResolveSource(ProjectSource s, string projectDir)
    {
        if (File.Exists(s.Path)) return s.Path;
        if (!string.IsNullOrEmpty(s.RelativePath))
        {
            try
            {
                var p = Path.GetFullPath(Path.Combine(projectDir, s.RelativePath));
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    string? AskToLocate(ProjectSource s)
    {
        var name = Path.GetFileName(s.Path);
        var r = MessageBox.Show(this, $"\"{name}\" was not found at:\n{s.Path}\n\nLocate it now?",
            AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return null;
        var dlg = new OpenFileDialog { Title = $"Locate {name}", FileName = name, Filter = "PDFs and images|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    /// <summary>Clears documents, splits, chat and usage so another project can be loaded.</summary>
    void ResetState()
    {
        foreach (var d in _sources) d.Cts.Cancel();
        _sources.Clear();
        _pool.Clear();
        _splits.Clear();
        _chat.Clear();
        _agent?.Reset();
        _undoSnapshot = null;
        UndoBtn.IsEnabled = false;
        OutputBox.Clear();
        RangeBox.Clear();
        _nextDocId = 0;
        _autosavePaused = false;
        UsageTracker.ProjectUsage = new UsageTotals();
        RefreshUsage();
    }

    void RememberProject(string path)
    {
        var s = SettingsStore.Current;
        s.LastProjectPath = path;
        s.RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        s.RecentProjects.Insert(0, path);
        if (s.RecentProjects.Count > 8) s.RecentProjects.RemoveRange(8, s.RecentProjects.Count - 8);
        SettingsStore.Save();
    }

    /// <summary>Asks what to do with unsaved "Untitled" work before it would be replaced. False = cancel.</summary>
    bool ConfirmLeaveUntitled()
    {
        if (_projectPath != null || IsEmptyProject) return true;
        var r = MessageBox.Show(this, "Save this untitled project first?\n\nYes = save it, No = discard it.",
            AppTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProjectAs();
        return true;
    }

    bool GuardIdle()
    {
        if (!_chatBusy) return true;
        SetStatus("Stop the AI assistant before switching projects.");
        return false;
    }

    async Task NewProjectAsync()
    {
        if (!GuardIdle() || !ConfirmLeaveUntitled()) return;
        SaveNow();
        _suspendDirty = true;
        try
        {
            ResetState();
            _projectPath = null;
            AddWelcome();
            try { File.Delete(ScratchPath); } catch { }
            SettingsStore.Current.LastProjectPath = null;
            SettingsStore.Save();
        }
        finally { _suspendDirty = false; UpdateTitle(); }
        SetStatus("New project. Drop PDFs or images to start.");
        await Task.CompletedTask;
    }

    async Task OpenProjectInteractiveAsync(string path)
    {
        if (!GuardIdle() || !ConfirmLeaveUntitled()) return;
        SaveNow();
        await OpenProjectAsync(path, scratch: false);
    }

    async Task OpenProjectDialogAsync()
    {
        var dlg = new OpenFileDialog { Title = "Open project", Filter = "PDF Splitter projects|*" + ProjectExt };
        if (dlg.ShowDialog(this) == true) await OpenProjectInteractiveAsync(dlg.FileName);
    }

    bool SaveProjectAs()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save project as",
            Filter = "PDF Splitter projects|*" + ProjectExt,
            FileName = _projectPath != null ? Path.GetFileName(_projectPath) : "My project",
            InitialDirectory = _projectPath != null ? Path.GetDirectoryName(_projectPath) : _sources.Count > 0 ? Path.GetDirectoryName(_sources[0].Path) : null,
            AddExtension = true,
            DefaultExt = ProjectExt,
        };
        if (dlg.ShowDialog(this) != true) return false;

        try { ProjectIO.Write(dlg.FileName, Capture(dlg.FileName)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't save:\n\n" + ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        if (_projectPath == null) { try { File.Delete(ScratchPath); } catch { } }
        _projectPath = dlg.FileName;
        _autosavePaused = false;
        RememberProject(dlg.FileName);
        UpdateTitle();
        SetStatus($"Saved {ProjectDisplayName}. Changes are saved automatically from now on.");
        return true;
    }

    // ---------- Project menu ----------

    void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProjectBtn };
        menu.Items.Add(MenuEntry("New project", () => _ = NewProjectAsync()));
        menu.Items.Add(MenuEntry("Open project...", () => _ = OpenProjectDialogAsync()));
        menu.Items.Add(MenuEntry("Save project as...", () => SaveProjectAs()));
        menu.Items.Add(new Separator());

        var recent = new MenuItem { Header = "Open recent" };
        var existing = SettingsStore.Current.RecentProjects.Where(File.Exists).ToList();
        if (existing.Count == 0) recent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        foreach (var p in existing)
            recent.Items.Add(MenuEntry(Path.GetFileNameWithoutExtension(p), () => _ = OpenProjectInteractiveAsync(p), p));
        menu.Items.Add(recent);

        if (_projectPath != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Show project file in Explorer", () =>
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_projectPath}\"")));
        }
        menu.IsOpen = true;
    }

    static MenuItem MenuEntry(string header, Action action, string? tooltip = null)
    {
        var item = new MenuItem { Header = header, ToolTip = tooltip };
        item.Click += (_, _) => action();
        return item;
    }
}
