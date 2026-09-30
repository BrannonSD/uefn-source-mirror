using System.Diagnostics;

namespace VerseMirror;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) return SelfTest.Run();
        if (args.Contains("--sync"))
        {
            using var cliMutex = new Mutex(true, "Local\\UEFNSourceMirror.Sync", out bool available);
            if (!available) return 2;
            var settings = Settings.Load(); var engine = new SyncEngine();
            try { foreach (var p in settings.Projects.Where(p => p.Enabled)) { p.Status = engine.Sync(p).GetAwaiter().GetResult(); p.LastSync = DateTime.Now; } settings.Save(); return 0; }
            catch (Exception e) { Directory.CreateDirectory(Settings.Home); File.AppendAllText(Path.Combine(Settings.Home, "sync.log"), DateTime.Now + " " + e.Message + "\n"); return 1; }
        }
        using var mutex = new Mutex(true, "Local\\UEFNSourceMirror.Sync", out bool first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\UEFNSourceMirror.Show");
        if (!first) { wake.Set(); return 0; }
        try { Application.Run(new MainWindow(Settings.Load(), args.Contains("--tray"), wake)); }
        catch (Exception e) { MessageBox.Show(e.Message, "UEFN Source Mirror", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        return 0;
    }
}

sealed class MainWindow : Form
{
    readonly Settings settings;
    readonly SyncEngine engine = new();
    readonly NotifyIcon tray;
    readonly EventWaitHandle wake;
    readonly ListView projects = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly TextBox activity = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly Label detail = new() { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 8) };
    readonly Button pause;
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    readonly Dictionary<Profile, DateTime> due = [];
    readonly List<FileSystemWatcher> watchers = [];
    bool exiting, running;
    readonly bool startHidden;
    public MainWindow(Settings config, bool hidden, EventWaitHandle showEvent)
    {
        settings = config; startHidden = hidden; wake = showEvent;
        Text = "UEFN Source Mirror"; Size = new Size(1100, 740); MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(17, 23, 34); ForeColor = Color.FromArgb(227, 233, 244);
        Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "mirror.ico"));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(22) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var heading = new Label { Text = "UEFN Source Mirror\nVerse on your PC. Context in your conversations.", Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 19), ForeColor = Color.FromArgb(112, 229, 194) };
        layout.Controls.Add(heading, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Sync now", async () => await SyncAll(true)));
        pause = Button(settings.Paused ? "Resume sync" : "Pause sync", TogglePause); buttons.Controls.Add(pause);
        buttons.Controls.Add(Button("Add project", async () => await AddProject()));
        buttons.Controls.Add(Button("Edit profile", EditProfile));
        buttons.Controls.Add(Button("Open GitHub", () => Open(Selected()?.Repository is string repo ? "https://github.com/" + repo : "https://github.com")));
        buttons.Controls.Add(Button("Open mirror", () => { if (Selected() is { } p) Open(p.Mirror); }));
        layout.Controls.Add(buttons, 0, 1);
        projects.BackColor = Color.FromArgb(25, 34, 48); projects.ForeColor = ForeColor;
        projects.OwnerDraw = true;
        projects.DrawColumnHeader += (_, e) =>
        {
            using var header = new SolidBrush(Color.FromArgb(32, 44, 62));
            e.Graphics.FillRectangle(header, e.Bounds);
            var bounds = e.Bounds; bounds.X += 7;
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Font, bounds, Color.FromArgb(156, 178, 202), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        projects.SmallImageList = new ImageList { ImageSize = new Size(1, 30) };
        projects.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            bool selected = (e.ItemState & ListViewItemStates.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(40, 66, 94) : projects.BackColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            var bounds = e.Bounds; bounds.X += 6; bounds.Width -= 8;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, projects.Font, bounds, e.Item.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        projects.Columns.Add("Project", 150); projects.Columns.Add("Status", 450); projects.Columns.Add("Last success", 200);
        projects.SelectedIndexChanged += (_, _) => Details(); layout.Controls.Add(projects, 0, 2);
        detail.BackColor = Color.FromArgb(25, 34, 48); layout.Controls.Add(detail, 0, 3);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var startup = new CheckBox { Text = "Start at sign-in", Checked = Startup.Enabled, AutoSize = true, Margin = new Padding(0, 4, 20, 0) };
        startup.CheckedChanged += (_, _) => { try { Startup.Set(startup.Checked); } catch (Exception e) { Log(e.Message); } }; options.Controls.Add(startup);
        options.Controls.Add(new Label { Text = "Scan every (seconds)", AutoSize = true, Margin = new Padding(0, 4, 8, 0) });
        var interval = new NumericUpDown { Minimum = 30, Maximum = 3600, Value = Math.Clamp(settings.IntervalSeconds, 30, 3600), Width = 80, BackColor = Color.FromArgb(32, 44, 62), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        interval.ValueChanged += (_, _) => { settings.IntervalSeconds = (int)interval.Value; settings.Save(); }; options.Controls.Add(interval);
        var logLink = new LinkLabel { Text = "Open logs", AutoSize = true, LinkColor = heading.ForeColor, Margin = new Padding(22, 4, 0, 0) };
        logLink.LinkClicked += (_, _) => Open(Settings.Home); options.Controls.Add(logLink); layout.Controls.Add(options, 0, 4);
        activity.BackColor = BackColor; activity.ForeColor = Color.FromArgb(166, 184, 207); activity.Font = new Font("Consolas", 9);
        var logPanel = new GroupBox { Text = "SYNC ACTIVITY", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(156, 178, 202), Padding = new Padding(12, 18, 12, 10) };
        logPanel.Controls.Add(activity); layout.Controls.Add(logPanel, 0, 5);
        Controls.Add(layout);
        var menu = new ContextMenuStrip(); menu.Items.Add("Open Source Mirror", null, (_, _) => Reveal());
        menu.Items.Add("Sync all now", null, async (_, _) => await SyncAll(true));
        menu.Items.Add("Pause / resume automatic sync", null, (_, _) => TogglePause());
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Exit", null, (_, _) => { if (running) { Log("A sync is running. Wait for it to finish before exiting."); Reveal(); return; } exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "UEFN Source Mirror", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Reveal();
        engine.Log = Log;
        FormClosing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } else { clock.Stop(); foreach (var w in watchers) w.Dispose(); tray.Dispose(); } };
        Shown += (_, _) => { if (startHidden) Hide(); };
        Rebuild();
        clock.Tick += async (_, _) => { if (wake.WaitOne(0)) Reveal(); await SyncAll(false); };
        clock.Start();
        Log("Watching saved .verse changes · 8-second settle time · timer fallback enabled. Close this window to keep syncing in the tray.");
    }
    Button Button(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 38, MinimumSize = new Size(115, 38), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(38, 59, 82), ForeColor = ForeColor, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 10, 0) };
        if (text == "Sync now") { b.BackColor = Color.FromArgb(112, 229, 194); b.ForeColor = Color.FromArgb(17, 23, 34); }
        b.FlatAppearance.BorderSize = 0; b.Click += (_, _) => action(); return b;
    }
    static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(e.Message); } }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    Profile? Selected() => projects.SelectedItems.Count > 0 ? projects.SelectedItems[0].Tag as Profile : settings.Projects.FirstOrDefault();
    void Details()
    {
        var p = Selected(); detail.Text = p == null ? "Add your first project to keep its Verse sources available in a private GitHub mirror." : $"Source:  {p.Source}\nMirror:  {p.Mirror}\nGitHub:  {p.Repository}   ·   {(!p.Enabled ? "Profile disabled" : settings.Paused ? "Automatic sync paused" : "Watching for saved changes")}";
    }
    void RefreshRows()
    {
        var selected = Selected(); projects.BeginUpdate(); projects.Items.Clear();
        foreach (var p in settings.Projects) { var row = new ListViewItem([p.Name, p.Status, p.LastSync?.ToString("MMM d · HH:mm:ss") ?? "—"]) { Tag = p, BackColor = projects.BackColor, ForeColor = p.Status.StartsWith("Needs attention") ? Color.FromArgb(255, 182, 122) : ForeColor }; projects.Items.Add(row); if (p == selected) row.Selected = true; }
        projects.EndUpdate(); Details(); tray.Text = settings.Paused ? "UEFN Source Mirror · paused" : "UEFN Source Mirror · watching";
    }
    void Rebuild()
    {
        foreach (var w in watchers) w.Dispose(); watchers.Clear(); due.Clear();
        foreach (var p in settings.Projects)
        {
            due[p] = DateTime.UtcNow.AddSeconds(2);
            foreach (string root in new[] { p.Source, p.Digests }.Where(Directory.Exists))
            {
                try
                {
                    var watcher = new FileSystemWatcher(root, "*.verse") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName, InternalBufferSize = 32768 };
                    void Changed(object? s, FileSystemEventArgs e) { if (IsHandleCreated) BeginInvoke((Action)(() => due[p] = DateTime.UtcNow.AddSeconds(8))); }
                    watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (s, e) => Changed(s, e);
                    watcher.Error += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)(() => { due[p] = DateTime.UtcNow.AddSeconds(8); Log(p.Name + ": watcher interrupted; timer scan will recover."); })); };
                    watcher.EnableRaisingEvents = true; watchers.Add(watcher);
                }
                catch (Exception e) { Log(p.Name + ": " + e.Message + "; timer scan remains active."); }
            }
        }
        RefreshRows();
    }
    void TogglePause() { settings.Paused = !settings.Paused; pause.Text = settings.Paused ? "Resume sync" : "Pause sync"; if (!settings.Paused) foreach (var p in settings.Projects) due[p] = DateTime.UtcNow; settings.Save(); RefreshRows(); Log(settings.Paused ? "Automatic sync paused. Manual Sync now remains available." : "Automatic sync resumed."); }
    async Task SyncAll(bool manual)
    {
        if (running || (!manual && settings.Paused)) return;
        var pending = settings.Projects.Where(p => p.Enabled && (manual || !due.TryGetValue(p, out var time) || time <= DateTime.UtcNow)).ToArray();
        if (pending.Length == 0) return;
        running = true;
        try
        {
            foreach (var p in pending)
            {
                string priorStatus = p.Status;
                p.Status = "Syncing…"; RefreshRows();
                // Schedule before the scan so watcher events during it remain pending.
                due[p] = DateTime.UtcNow.AddSeconds(settings.IntervalSeconds);
                try { p.Status = await Task.Run(() => engine.Sync(p)); p.LastSync = DateTime.Now; }
                catch (Exception e) { p.Status = "Needs attention: " + e.Message.Replace('\n', ' '); Log(p.Name + ": " + e.Message); if (p.Status != priorStatus) tray.ShowBalloonTip(4000, p.Name + " sync needs attention", e.Message, ToolTipIcon.Warning); }
                settings.Save(); RefreshRows();
            }
        }
        finally { running = false; }
    }
    void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke((Action)(() => Log(message))); return; }
        string line = $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
        activity.AppendText(line); if (activity.TextLength > 40000) activity.Text = activity.Text[^25000..];
        Directory.CreateDirectory(Settings.Home); string path = Path.Combine(Settings.Home, "sync.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".previous", true);
        File.AppendAllText(path, line);
    }
    async Task AddProject()
    {
        if (running) { Log("Wait for the current sync to finish before adding a project."); return; }
        string owner;
        try { owner = await Commands.Run("gh", AppContext.BaseDirectory, "api", "user", "--jq", ".login"); }
        catch (Exception e) { MessageBox.Show("Sign in with GitHub CLI before adding a project.\n\n" + e.Message, "GitHub sign-in required"); return; }
        using var dialog = new ProfileDialog(null, owner, settings.MirrorRoot);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var p = dialog.Result;
        if (settings.Projects.Any(x => SyncEngine.Normal(x.Source).Equals(SyncEngine.Normal(p.Source), StringComparison.OrdinalIgnoreCase) || SyncEngine.Normal(x.Mirror).Equals(SyncEngine.Normal(p.Mirror), StringComparison.OrdinalIgnoreCase) || x.Repository.Equals(p.Repository, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("That source, mirror or repo already has a profile."); return; }
        running = true;
        try { Log("Creating private mirror for " + p.Name); await Task.Run(() => engine.Provision(p)); settings.Projects.Add(p); settings.Save(); Rebuild(); }
        catch (Exception e) { MessageBox.Show(e.Message + "\n\nIf creation was interrupted, inspect the chosen mirror and GitHub repo before retrying.", "Could not add project"); }
        finally { running = false; }
        await SyncAll(true);
    }
    void EditProfile()
    {
        if (running) { Log("Wait for the current sync to finish before editing a profile."); return; }
        var p = Selected(); if (p == null) return;
        using var dialog = new ProfileDialog(p);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        p.Digests = dialog.Result.Digests; p.Enabled = dialog.Result.Enabled; settings.Save(); Rebuild();
    }
}

sealed class ProfileDialog : Form
{
    readonly string owner;
    readonly string mirrorRoot;
    readonly TextBox name = new(), source = new(), digests = new(), mirror = new(), repo = new();
    readonly CheckBox enabled = new() { Text = "Enable automatic sync", AutoSize = true };
    public Profile Result { get; private set; } = new();
    public ProfileDialog(Profile? existing, string githubOwner = "", string? defaultMirrorRoot = null)
    {
        owner = githubOwner; mirrorRoot = defaultMirrorRoot ?? Path.Combine(Settings.Home, "mirrors");
        Text = existing == null ? "Add UEFN project" : "Project settings"; Size = new Size(780, 480); MinimumSize = Size; Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(17, 23, 34); ForeColor = Color.FromArgb(227, 233, 244);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 3, RowCount = 8 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        void Row(int i, string label, TextBox box, bool browse)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, 0, i); box.Dock = DockStyle.Fill; box.BackColor = Color.FromArgb(32, 44, 62); box.ForeColor = ForeColor; box.BorderStyle = BorderStyle.FixedSingle; grid.Controls.Add(box, 1, i);
            if (browse) { var b = new Button { Text = "Browse", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(38, 59, 82), ForeColor = ForeColor }; b.FlatAppearance.BorderSize = 0; b.Click += (_, _) => { using var f = new FolderBrowserDialog(); if (f.ShowDialog(this) == DialogResult.OK) { box.Text = f.SelectedPath; if (box == source && existing == null) Guess(); } }; grid.Controls.Add(b, 2, i); }
        }
        Row(0, "Project name", name, false); Row(1, "UEFN folder", source, existing == null); Row(2, "Verse cache", digests, true); Row(3, "Mirror folder", mirror, existing == null); Row(4, "GitHub repo", repo, false);
        grid.Controls.Add(enabled, 1, 5);
        var note = new Label { Text = existing == null ? "Creates a NEW private repo using your existing GitHub CLI sign-in.\nVerse cache is the project folder under Saved/VerseProject; leave blank to mirror source only." : "Source, destination and repository are fixed for safety.\nDisable a profile to stop syncing it. Existing files and repository are retained.", Dock = DockStyle.Fill, AutoSize = true }; grid.Controls.Add(note, 0, 6); grid.SetColumnSpan(note, 3);
        var save = new Button { Text = existing == null ? "Create private mirror" : "Save settings", AutoSize = true, Height = 38, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(112, 229, 194), ForeColor = Color.FromArgb(17, 23, 34) };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => { Result = new Profile { Name = name.Text.Trim(), Source = source.Text.Trim(), Digests = digests.Text.Trim(), Mirror = mirror.Text.Trim(), Repository = repo.Text.Trim(), Enabled = enabled.Checked }; try { if (Result.Name.Length == 0) throw new Exception("Enter a project name."); SyncEngine.Validate(Result); DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(e.Message); } };
        grid.Controls.Add(save, 1, 7); AcceptButton = save; Controls.Add(grid);
        enabled.Checked = existing?.Enabled ?? true;
        if (existing != null) { name.Text = existing.Name; source.Text = existing.Source; digests.Text = existing.Digests; mirror.Text = existing.Mirror; repo.Text = existing.Repository; name.ReadOnly = source.ReadOnly = mirror.ReadOnly = repo.ReadOnly = true; }
    }
    void Guess()
    {
        name.Text = Path.GetFileName(source.Text.TrimEnd('\\', '/'));
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealEditorFortnite", "Saved", "VerseProject", name.Text);
        digests.Text = Directory.Exists(cache) ? cache : "";
        mirror.Text = Path.Combine(mirrorRoot, name.Text);
        repo.Text = owner + "/" + System.Text.RegularExpressions.Regex.Replace(name.Text.ToLowerInvariant(), "[^a-z0-9_-]", "-") + "-verse-mirror";
    }
}

static class Startup
{
    const string ValueName = "UEFN Source Mirror";
    public static bool Enabled { get { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue(ValueName) != null; } }
    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue(ValueName, false);
    }
}
