using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VerseMirror;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Digests { get; set; } = "";
    public string Mirror { get; set; } = "";
    public string Repository { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = "Waiting for first sync";
    public DateTime? LastSync { get; set; }
}

public sealed class Settings
{
    public string MirrorRoot { get; set; } = Path.Combine(Home, "mirrors");
    public int IntervalSeconds { get; set; } = 120;
    public bool Paused { get; set; }
    public List<Profile> Projects { get; set; } = [];
    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEFNSourceMirror");
    public static readonly string FilePath = Path.Combine(Home, "settings.json");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static Settings Load() => File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
    public void Save() { Directory.CreateDirectory(Home); AtomicWrite(FilePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json))); }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".mirror-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
}

public static class Commands
{
    public static async Task<string> Run(string exe, string cwd, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "Never";
        using var process = Process.Start(info) ?? throw new Exception($"Could not start {exe}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new Exception($"{exe} timed out. Check network/authentication and retry."); }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new Exception($"{exe} {args.FirstOrDefault()}: {error.Trim()} {output.Trim()}");
        return output.TrimEnd('\r', '\n');
    }
}

public sealed class SyncEngine
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Action<string>? Log;
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".git", ".urc", ".codex", ".lore", "node_modules", "Intermediate", "Saved", "DerivedDataCache", "bin", "obj" };
    public static string Normal(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    static bool Within(string path, string root) => Normal(path).Equals(Normal(root), StringComparison.OrdinalIgnoreCase) || Normal(path).StartsWith(Normal(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void Validate(Profile p)
    {
        if (!Regex.IsMatch(p.Repository, @"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$")) throw new Exception("Repository must be owner/name.");
        if (!Directory.Exists(p.Source) || !Directory.Exists(Path.Combine(p.Source, "Content"))) throw new Exception("Source project/Content folder is unavailable. Nothing was deleted.");
        if (Within(p.Mirror, p.Source) || Within(p.Source, p.Mirror)) throw new Exception("Mirror must be separate from the source project.");
        if (p.Digests.Length > 0 && (Within(p.Mirror, p.Digests) || Within(p.Digests, p.Mirror))) throw new Exception("Digest and mirror folders must be separate.");
        if (p.Digests.Length > 0 && !Directory.Exists(p.Digests)) throw new Exception("UEFN digest cache is unavailable. Nothing was deleted. Open the project in UEFN or fix the digest path.");
        foreach (string root in new[] { p.Source, p.Mirror, p.Digests }.Where(s => s.Length > 0 && Directory.Exists(s)))
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new Exception("Root folders cannot be junctions or symlinks.");
    }
    public static IEnumerable<string> Walk(string root, bool directories = false)
    {
        foreach (string dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Excluded.Contains(Path.GetFileName(dir)) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            if (directories) yield return dir;
            foreach (string item in Walk(dir, directories)) yield return item;
        }
        if (!directories)
            foreach (string file in Directory.EnumerateFiles(root).Order(StringComparer.OrdinalIgnoreCase))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && file.EndsWith(".verse", StringComparison.OrdinalIgnoreCase)) yield return file;
    }
    static byte[] StableRead(string path)
    {
        var before = new FileInfo(path); long size = before.Length; DateTime stamp = before.LastWriteTimeUtc;
        byte[] bytes = File.ReadAllBytes(path);
        var after = new FileInfo(path);
        if (after.Length != size || after.LastWriteTimeUtc != stamp || bytes.Length != size) throw new IOException("File changed during sync; retrying on the next scan: " + Path.GetFileName(path));
        // UTF-8/UTF-16 text only, even if a binary has been renamed .verse.
        bool utf16 = bytes.Length >= 2 && ((bytes[0] == 255 && bytes[1] == 254) || (bytes[0] == 254 && bytes[1] == 255));
        if (!utf16) { if (bytes.Contains((byte)0)) throw new Exception("Binary content rejected: " + path); _ = new UTF8Encoding(false, true).GetString(bytes); }
        else { string decoded = new UnicodeEncoding(bytes[0] == 254, true, true).GetString(bytes, 2, bytes.Length - 2); if (decoded.Contains('\0')) throw new Exception("Binary content rejected: " + path); }
        return bytes;
    }
    public static Dictionary<string, byte[]> Snapshot(Profile p)
    {
        Validate(p);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Walk(p.Source)) files.Add(Path.GetRelativePath(p.Source, file).Replace('\\', '/'), StableRead(file));
        int sourceCount = files.Count;
        if (sourceCount == 0) throw new Exception("No source Verse files found. Refusing to empty the mirror.");
        if (p.Digests.Length > 0)
        {
            foreach (string file in Walk(p.Digests)) files.Add("Digests/" + Path.GetRelativePath(p.Digests, file).Replace('\\', '/'), StableRead(file));
            if (files.Count == sourceCount) throw new Exception("Digest cache contains no Verse files. Refusing to remove saved digests.");
        }
        string[] versePaths = files.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var readable = new List<string>();
        foreach (string original in versePaths.Where(f => files[f].Length > 500_000))
        {
            byte[] raw = files[original];
            string text = raw.Length >= 2 && raw[0] == 255 && raw[1] == 254 ? Encoding.Unicode.GetString(raw, 2, raw.Length - 2) : raw.Length >= 2 && raw[0] == 254 && raw[1] == 255 ? Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2) : Encoding.UTF8.GetString(raw).TrimStart('\uFEFF');
            readable.Add($"\n## {original}\n");
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int part = 0, begin = 0;
            while (begin < lines.Length)
            {
                int end = begin, count = 0;
                var content = new StringBuilder();
                while (end < lines.Length && end - begin < 800)
                {
                    int next = Encoding.UTF8.GetByteCount(lines[end]) + 1;
                    if (count + next > 160_000 && end > begin) break;
                    if (next > 400_000) throw new Exception("Verse line too large for a GitHub reading copy: " + original);
                    content.Append(lines[end]).Append('\n'); count += next; end++;
                }
                string path = $"Readable/{original}.parts/part-{++part:000}.verse";
                files[path] = Encoding.UTF8.GetBytes($"# Reading copy of {original}; original lines {begin + 1}-{end}\n# Continue with the next part listed in READABLE_INDEX.md.\n\n" + content);
                readable.Add($"- [Lines {begin + 1}–{end}](<{path}>)");
                begin = end;
            }
        }
        files["READABLE_INDEX.md"] = Encoding.UTF8.GetBytes("# Large-file reading copies\n\nThe complete, byte-identical files remain at their original mirror paths. Some GitHub connectors return empty content for files larger than 1 MB. These generated parts keep large Verse files readable through the contents API. Read parts in order; classes/modules can span parts. Line ranges refer to the original file.\n" + (readable.Count > 0 ? string.Join("\n", readable) : "\nNo large files need reading copies.") + "\n");
        string[] allVersePaths = files.Keys.Where(f => f.EndsWith(".verse", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string folders = "# Project folder hierarchy\n\nAll project directories, including directories whose binary assets are omitted. Generated caches, tool internals and symlinks are excluded. Verse files retain these relative paths; Git does not track empty directories.\n\n```text\n" + string.Join("\n", Walk(p.Source, true).Select(d => Path.GetRelativePath(p.Source, d).Replace('\\', '/') + "/")) + "\n```\n";
        files["FOLDERS.md"] = Encoding.UTF8.GetBytes(folders);
        files["SOURCE_INDEX.md"] = Encoding.UTF8.GetBytes("# Verse source index\n\nIf a large digest returns empty content, use its smaller parts in [READABLE_INDEX.md](READABLE_INDEX.md).\n\n" + string.Join("\n", versePaths.Select(f => $"- [{f}](<{f}>)")) + "\n");
        files["README.md"] = Encoding.UTF8.GetBytes($"# {p.Name} — UEFN source mirror\n\nPrivate, automatically maintained, one-way source mirror. The UEFN project on the source computer is authoritative. Do not edit mirrored files here: local changes replace them on the next sync.\n\n- Project Verse files keep their original relative paths.\n- `Digests/` preserves the relative hierarchy of this project's UEFN Verse cache, including generated asset and built-in API digests. These are the last files UEFN generated; a sync does not compile or regenerate them.\n- [Source index](SOURCE_INDEX.md) lists all Verse files.\n- [Folder hierarchy](FOLDERS.md) includes folders containing only binary assets.\n- No `.uasset`, `.umap`, textures, models, audio, credentials or editor caches are copied. Only text Verse files and generated Markdown/JSON metadata enter this mirror.\n\n## Reading with ChatGPT\n\nSelect `{p.Repository}` in your GitHub app's repository access if needed, then ask ChatGPT to read this repo, `SOURCE_INDEX.md`, and the relevant Verse paths. The source index provides direct paths when code search is not yet indexed.\n\n## Sync controls\n\nOpen **UEFN Source Mirror** from the desktop or Start menu. It watches saved Verse changes, waits 8 seconds for edits to settle, and also scans on a timer (default: 2 minutes). Use Sync now for a manual update; pause/resume from the window or tray menu. Closing the window keeps it running in the tray; Exit stops it. Windows sign-in starts it in the tray. Offline pushes are retried on subsequent scans, with errors shown in the app.\n\n## Local app\n\nThe companion app source, build instructions, configuration and logs are installed separately on the PC in the application folder and LocalAppData/UEFNSourceMirror. This repository is a source mirror, not a runnable UEFN project or a backup of binary assets.\n");
        files["README.md"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["README.md"]) + "\n## Large digest access\n\nIf the connector returns empty content for a large digest, read its indexed small parts in [READABLE_INDEX.md](READABLE_INDEX.md). `Readable/` is generated from the original text and refreshed/pruned with every sync; the full original digests remain in `Digests/`.\n");
        files[".gitignore"] = Encoding.UTF8.GetBytes("# Only Verse and generated text metadata may be added\n*\n!*/\n!*.verse\n!README.md\n!FOLDERS.md\n!SOURCE_INDEX.md\n!READABLE_INDEX.md\n!mirror-manifest.json\n!.gitignore\n!.gitattributes\n*.mirror-tmp\n");
        files[".gitattributes"] = Encoding.UTF8.GetBytes("# Preserve exact source bytes, including CRLF\n* -text\n");
        files["mirror-manifest.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { project = p.Name, sourceVerseFiles = sourceCount, digestVerseFiles = versePaths.Length - sourceCount, readingCopyFiles = allVersePaths.Length - versePaths.Length, files = allVersePaths }, Settings.Json) + "\n");
        return files;
    }
    public static bool Allowed(string path) => path.EndsWith(".verse", StringComparison.OrdinalIgnoreCase) || new[] { "README.md", "FOLDERS.md", "SOURCE_INDEX.md", "READABLE_INDEX.md", "mirror-manifest.json", ".gitignore", ".gitattributes" }.Contains(path);
    static string Target(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(path, root) || Normal(path) == Normal(root) || !Allowed(relative)) throw new Exception("Unsafe mirror path: " + relative);
        string? ancestor = path;
        while (ancestor != null && Within(ancestor, root))
        {
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new Exception("Mirror symlink/junction rejected: " + ancestor);
            ancestor = Path.GetDirectoryName(ancestor);
        }
        return path;
    }
    public static void Apply(Profile p, Dictionary<string, byte[]> files)
    {
        var previous = new List<string>();
        string manifest = Path.Combine(p.Mirror, "mirror-manifest.json");
        if (File.Exists(manifest))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            previous = json.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToList();
        }
        // Validate every destination before changing anything.
        foreach (string path in files.Keys.Concat(previous)) _ = Target(p.Mirror, path);
        foreach (var (relative, bytes) in files)
        {
            string dest = Target(p.Mirror, relative);
            if (!File.Exists(dest) || !File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) Settings.AtomicWrite(dest, bytes);
        }
        foreach (string old in previous.Where(f => !files.ContainsKey(f)))
        {
            string dest = Target(p.Mirror, old);
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
    public async Task Provision(Profile p)
    {
        Validate(p);
        Directory.CreateDirectory(p.Mirror);
        if (Directory.EnumerateFileSystemEntries(p.Mirror).Any()) throw new Exception("Choose an empty mirror folder for a new profile.");
        string login = await Commands.Run("gh", p.Mirror, "api", "user", "--jq", ".login");
        if (!p.Repository.StartsWith(login + "/", StringComparison.OrdinalIgnoreCase)) throw new Exception("New mirrors must belong to your signed-in personal GitHub account: " + login);
        await Commands.Run("git", p.Mirror, "init", "-b", "main");
        await Commands.Run("git", p.Mirror, "config", "core.autocrlf", "false");
        await Commands.Run("git", p.Mirror, "config", "credential.https://github.com.helper", "");
        await Commands.Run("git", p.Mirror, "config", "--add", "credential.https://github.com.helper", "!gh auth git-credential");
        await Commands.Run("gh", p.Mirror, "repo", "create", p.Repository, "--private", "--source", p.Mirror, "--remote", "origin", "--description", p.Name + " UEFN Verse and digest source mirror (no binary assets)");
    }
    public async Task<string> Sync(Profile p)
    {
        await gate.WaitAsync();
        try
        {
            var snapshot = Snapshot(p);
            if (!Directory.Exists(Path.Combine(p.Mirror, ".git"))) throw new Exception("Mirror repository is not initialized.");
            string top = await Commands.Run("git", p.Mirror, "rev-parse", "--show-toplevel");
            if (!Normal(top).Equals(Normal(p.Mirror), StringComparison.OrdinalIgnoreCase)) throw new Exception("Mirror must be its own Git repository.");
            string branch = await Commands.Run("git", p.Mirror, "branch", "--show-current");
            if (branch != "main") throw new Exception("Mirror must remain on main; switch it back before syncing.");
            string remote = await Commands.Run("git", p.Mirror, "remote", "get-url", "origin");
            if (!remote.Equals($"https://github.com/{p.Repository}.git", StringComparison.OrdinalIgnoreCase) && !remote.Equals($"git@github.com:{p.Repository}.git", StringComparison.OrdinalIgnoreCase)) throw new Exception("Origin does not match the configured GitHub repo.");
            string privacy = await Commands.Run("gh", p.Mirror, "repo", "view", p.Repository, "--json", "isPrivate", "--jq", ".isPrivate");
            if (privacy != "true") throw new Exception("Remote is not private. Sync stopped.");
            string tracked = await Commands.Run("git", p.Mirror, "-c", "core.quotepath=false", "ls-files", "-z");
            if (tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(f => !Allowed(f))) throw new Exception("Unexpected tracked file detected. Remove it before syncing; no binaries will be pushed.");
            var owned = snapshot.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string manifestPath = Target(p.Mirror, "mirror-manifest.json");
            if (File.Exists(manifestPath))
            {
                using var priorManifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (var item in priorManifest.RootElement.GetProperty("files").EnumerateArray()) { string old = item.GetString()!; _ = Target(p.Mirror, old); owned.Add(old); }
            }
            if (tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(f => !owned.Contains(f))) throw new Exception("Unmanaged tracked file found. Only snapshot/manifest-owned files may be pushed.");
            string staged = await Commands.Run("git", p.Mirror, "diff", "--cached", "--name-only", "-z");
            if (staged.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(f => !owned.Contains(f))) throw new Exception("Unmanaged staged changes found. Unstage them before syncing.");
            Apply(p, snapshot);
            string[] paths = snapshot.Keys.Concat(tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await Commands.Run("git", p.Mirror, new[] { "--literal-pathspecs", "add", "-A", "--" }.Concat(paths).ToArray());
            bool changed = (await Commands.Run("git", p.Mirror, "diff", "--cached", "--name-only")).Length > 0;
            if (changed) await Commands.Run("git", p.Mirror, "commit", "-m", "Sync UEFN Verse sources and digests");
            // Always push: an earlier offline commit may still be pending. Never force or merge remote edits.
            await Commands.Run("git", p.Mirror, "push", "--set-upstream", "origin", "main");
            int originals = snapshot.Keys.Count(f => f.EndsWith(".verse", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("Readable/"));
            string result = $"{originals} Verse files · " + (changed ? "changes pushed" : "up to date");
            Log?.Invoke($"{p.Name}: {result}");
            return result;
        }
        finally { gate.Release(); }
    }
}
