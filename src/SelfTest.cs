using System.Text.Json;

namespace VerseMirror;

static class SelfTest
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "VerseMirrorTests-" + Guid.NewGuid().ToString("N"));
        var results = new List<string>();
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "Project"), cache = Path.Combine(root, "Cache"), mirror = Path.Combine(root, "Mirror");
            Directory.CreateDirectory(Path.Combine(source, "Content", "Nested")); Directory.CreateDirectory(Path.Combine(source, "Content", "BinaryOnly")); Directory.CreateDirectory(Path.Combine(cache, "BuiltIn")); Directory.CreateDirectory(mirror);
            File.WriteAllText(Path.Combine(source, "Content", "Nested", "device.verse"), "hello := class {}\r\n");
            File.WriteAllBytes(Path.Combine(source, "Content", "BinaryOnly", "asset.uasset"), [0, 1, 2, 3]);
            File.WriteAllText(Path.Combine(cache, "BuiltIn", "Verse.digest.verse"), "# digest\n");
            var p = new Profile { Name = "Fixture", Source = source, Digests = cache, Mirror = mirror, Repository = "example/test-fixture" };
            void Check(bool value, string label) { if (!value) throw new Exception("FAILED: " + label); results.Add("PASS: " + label); }
            void Reject(Action action, string label) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, label); }
            Check(new Settings().MirrorRoot == Path.Combine(Settings.Home, "mirrors"), "New mirror default stays in local application data");
            var snapshot = SyncEngine.Snapshot(p);
            Check(snapshot.ContainsKey("Content/Nested/device.verse") && snapshot.ContainsKey("Digests/BuiltIn/Verse.digest.verse"), "Source and digest relative hierarchy");
            Check(!snapshot.Keys.Any(k => k.EndsWith(".uasset")) && System.Text.Encoding.UTF8.GetString(snapshot["FOLDERS.md"]).Contains("Content/BinaryOnly/"), "Binary exclusion and binary-only folder index");
            SyncEngine.Apply(p, snapshot);
            Check(File.ReadAllBytes(Path.Combine(mirror, "Content", "Nested", "device.verse")).SequenceEqual(File.ReadAllBytes(Path.Combine(source, "Content", "Nested", "device.verse"))), "Exact source bytes and CRLF preservation");
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(mirror, "Content", "Nested", "device.verse")); SyncEngine.Apply(p, SyncEngine.Snapshot(p));
            Check(stamp == File.GetLastWriteTimeUtc(Path.Combine(mirror, "Content", "Nested", "device.verse")), "Unchanged sync avoids writes");
            File.Move(Path.Combine(source, "Content", "Nested", "device.verse"), Path.Combine(source, "Content", "renamed.verse"));
            File.WriteAllText(Path.Combine(source, "Content", "renamed.verse"), "changed := class {}\n"); SyncEngine.Apply(p, SyncEngine.Snapshot(p));
            Check(!File.Exists(Path.Combine(mirror, "Content", "Nested", "device.verse")) && File.ReadAllText(Path.Combine(mirror, "Content", "renamed.verse")).StartsWith("changed"), "Renames, deletions and edits propagate");
            string savedCache = p.Digests; p.Digests = Path.Combine(root, "Missing"); Reject(() => SyncEngine.Snapshot(p), "Missing digest root stops before deletion"); p.Digests = savedCache;
            File.WriteAllBytes(Path.Combine(source, "Content", "binary.verse"), [1, 0, 2]); Reject(() => SyncEngine.Snapshot(p), "Binary disguised as Verse is rejected"); File.Delete(Path.Combine(source, "Content", "binary.verse"));
            string savedMirror = p.Mirror; p.Mirror = Path.Combine(source, "BadMirror"); Reject(() => SyncEngine.Snapshot(p), "Mirror nested in source is rejected"); p.Mirror = savedMirror;
            File.WriteAllText(Path.Combine(mirror, "mirror-manifest.json"), "{\"files\":[\"../outside.verse\"]}"); Reject(() => SyncEngine.Apply(p, SyncEngine.Snapshot(p)), "Manifest path traversal rejected before mutation");
            File.WriteAllText(Path.Combine(mirror, "mirror-manifest.json"), "{\"files\":[]}");
            string largeText = string.Concat(Enumerable.Range(1, 24000).Select(i => $"# Digest declaration line {i:00000}\n"));
            string largePath = Path.Combine(cache, "BuiltIn", "Large.digest.verse"); File.WriteAllText(largePath, largeText);
            var largeSnapshot = SyncEngine.Snapshot(p);
            var parts = largeSnapshot.Where(kv => kv.Key.StartsWith("Readable/Digests/BuiltIn/Large.digest.verse.parts/")).OrderBy(kv => kv.Key).ToArray();
            Check(parts.Length > 1 && parts.All(kv => kv.Value.Length < 500000), "Large digests split into connector-readable parts");
            string reconstructed = string.Concat(parts.Select(kv => string.Join("\n", System.Text.Encoding.UTF8.GetString(kv.Value).Split('\n').Skip(3))));
            Check(reconstructed.TrimEnd('\n') == largeText.TrimEnd('\n'), "Reading parts retain every original line in order");
            SyncEngine.Apply(p, largeSnapshot); File.Delete(largePath); SyncEngine.Apply(p, SyncEngine.Snapshot(p));
            Check(parts.All(kv => !File.Exists(Path.Combine(mirror, kv.Key))), "Stale reading parts removed with their original digest");
            File.Delete(Path.Combine(source, "Content", "renamed.verse")); Reject(() => SyncEngine.Snapshot(p), "Empty source refuses to erase mirror");
            Check(!SyncEngine.Allowed("Content/asset.uasset") && !SyncEngine.Allowed("credentials.json"), "Git staging allowlist rejects assets and arbitrary metadata");
            Directory.CreateDirectory(Settings.Home); File.WriteAllText(Path.Combine(Settings.Home, "self-test.json"), JsonSerializer.Serialize(new { passed = true, results }, Settings.Json)); return 0;
        }
        catch (Exception e) { Directory.CreateDirectory(Settings.Home); File.WriteAllText(Path.Combine(Settings.Home, "self-test.json"), JsonSerializer.Serialize(new { passed = false, results, error = e.ToString(), fixture = root }, Settings.Json)); return 1; }
        // Fixture is intentionally retained for inspection; never deletes outside its unique temporary directory.
    }
}
