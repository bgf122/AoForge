using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using AoForge.Models;

namespace AoForge.Services;

public enum AddonState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    ManualInstall, // present, but installed by hand: AoForge can take it over
    NeedsRepair,   // a file AoForge installed is missing or was replaced
}

public sealed class AddonStatus
{
    public AddonState State { get; init; }
    public string InstalledTag { get; init; }
    public string DetectedVersion { get; init; } // hand installs: from the file's version resource, if it has one
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

// Applies index recipes to one client folder and records every change in the manifest.
// No network and no UI: the caller downloads the release zip and reports progress.
public sealed class InstallEngine
{
    public const string DataDirName = "AoForge";
    static readonly StringComparer PathCmp = StringComparer.OrdinalIgnoreCase;

    readonly string _client;

    public InstallEngine(string clientDir)
    {
        _client = Path.GetFullPath(clientDir).TrimEnd('\\', '/');
        Manifest = File.Exists(ManifestPath) ? Json.ReadFile<Manifest>(ManifestPath) : new Manifest();
    }

    public string ClientDir => _client;
    public Manifest Manifest { get; }
    string DataDir => Path.Combine(_client, DataDirName);
    string ManifestPath => Path.Combine(DataDir, "installed.json");

    public InstalledAddon Find(string id) => Manifest.AddonList.FirstOrDefault(a => a.Id == id);

    // --- status ---------------------------------------------------------------------------------

    public AddonStatus GetStatus(AddonDef def, string latestTag)
    {
        var rec = Find(def.Id);
        if (rec == null) {
            return IsDetected(def)
                ? new AddonStatus { State = AddonState.ManualInstall, DetectedVersion = DetectedVersion(def) }
                : new AddonStatus { State = AddonState.NotInstalled };
        }

        var problems = new List<string>();
        foreach (var f in rec.FileList) {
            string path = Resolve(f.Path);
            if (!File.Exists(path))
                problems.Add($"{f.Path} is missing.");
            else if (!PathCmp.Equals(Sha256(path), f.Sha256))
                problems.Add($"{f.Path} was replaced by another version (the game's launcher may have restored it).");
        }
        foreach (var p in rec.PreservedList)
            if (!File.Exists(Resolve(p.As)))
                problems.Add($"{p.As} (the game's original {p.File}) is missing.");

        AddonState state = problems.Count > 0 ? AddonState.NeedsRepair
            : latestTag != null && latestTag != rec.Tag ? AddonState.UpdateAvailable
            : AddonState.Installed;
        return new AddonStatus { State = state, InstalledTag = rec.Tag, Problems = problems };
    }

    public bool IsDetected(AddonDef def) =>
        def.DetectList.Any(r => {
            string path = Resolve(r.File);
            return File.Exists(path) && (r.ProductName == null || ProductName(path) == r.ProductName);
        });

    // The version of a hand-installed copy, when a detect rule names a file with a version resource
    // (ProductName set). Null when unknown, e.g. ao-vk's randy31.dll has no version resource.
    public string DetectedVersion(AddonDef def)
    {
        foreach (var r in def.DetectList.Where(r => r.ProductName != null)) {
            string path = Resolve(r.File);
            if (!File.Exists(path))
                continue;
            try {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (info.ProductName == r.ProductName && !string.IsNullOrWhiteSpace(info.ProductVersion))
                    return info.ProductVersion.Split('+')[0].Trim(); // drop "+commit" build metadata
            } catch (Exception) {
                // unreadable: unknown version
            }
        }
        return null;
    }

    // Warnings to show before installing: files another addon owns, and files that aren't the addon's.
    public List<string> FindConflicts(AddonDef def, ICollection<string> options, IEnumerable<AddonDef> allDefs)
    {
        var names = allDefs.ToDictionary(d => d.Id, d => d.Name);
        var rec = Find(def.Id);
        bool takeOver = rec == null && IsDetected(def);
        var preservedFiles = PreserveSteps(def).Select(s => s.File).ToList();
        var result = new List<string>();

        foreach (var step in CopySteps(def, options)) {
            string rel = Normalize(step.To);
            var owner = Manifest.AddonList.FirstOrDefault(a => a.Id != def.Id && a.FileList.Any(f => PathCmp.Equals(f.Path, rel)));
            if (owner != null) {
                string ownerName = names.TryGetValue(owner.Id, out var n) ? n : owner.Id;
                result.Add($"{rel} is also installed by {ownerName}. Installing {def.Name} replaces it, and {ownerName} will then need repair.");
                continue;
            }
            bool mine = rec != null && rec.FileList.Any(f => PathCmp.Equals(f.Path, rel));
            if (File.Exists(Resolve(rel)) && !mine && !takeOver && !preservedFiles.Any(p => PathCmp.Equals(Normalize(p), rel)))
                result.Add($"{rel} is already in the client folder and isn't from {def.Name}. It will be backed up and put back when you uninstall {def.Name}.");
        }
        return result;
    }

    // --- install / update / repair / take over ------------------------------------------------

    public void Install(AddonDef def, string tag, string zipPath, ICollection<string> options, Action<string> log)
    {
        var prev = Find(def.Id);
        bool takeOver = prev == null && IsDetected(def);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = MapEntries(zip);

        // Check everything before the first change.
        var copies = new List<(string Rel, string Target, ZipArchiveEntry Entry)>();
        foreach (var step in CopySteps(def, options)) {
            if (!entries.TryGetValue(Normalize(step.From), out var entry))
                throw new InvalidDataException($"The {def.Name} download has no '{step.From}'. The addon's release may have changed; AoForge needs an index update.");
            string rel = Normalize(step.To);
            copies.Add((rel, Resolve(rel), entry));
        }
        var preserves = PreserveSteps(def).Select(s => (Step: s, File: Resolve(s.File), As: Resolve(s.As))).ToList();
        EnsureWritable(copies.Select(c => c.Target).Concat(preserves.SelectMany(p => new[] { p.File, p.As })));
        var newHash = copies.ToDictionary(c => c.Target, c => Sha256(c.Entry), PathCmp);

        foreach (var p in preserves) {
            if (File.Exists(p.As))
                continue;
            if (!File.Exists(p.File))
                throw new InvalidOperationException($"{p.Step.File} is missing from the client folder. Let the game's launcher restore it, then try again.");
            string cur = Sha256(p.File);
            bool isAddonFile = newHash.TryGetValue(p.File, out var h) && PathCmp.Equals(cur, h)
                || prev != null && prev.FileList.Any(f => PathCmp.Equals(f.Path, Normalize(p.Step.File)) && PathCmp.Equals(f.Sha256, cur));
            if (isAddonFile)
                throw new InvalidOperationException($"{p.Step.As} (the game's original {p.Step.File}) is missing, and {p.Step.File} is {def.Name}'s. Let the game's launcher restore {p.Step.File}, then repair {def.Name}.");
        }

        var rec = prev ?? new InstalledAddon { Id = def.Id };
        string backupDir = Path.Combine(DataDir, "backup", def.Id, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var undo = new Stack<Action>();
        try {
            // 1. The game's originals.
            foreach (var p in preserves) {
                if (!File.Exists(p.As)) {
                    File.Move(p.File, p.As);
                    undo.Push(() => File.Move(p.As, p.File));
                    log($"Renamed the game's {p.Step.File} to {p.Step.As}.");
                } else if (prev != null && File.Exists(p.File)) {
                    // Ours before, but now different and not the new version either: the game (or its
                    // launcher) put its own file back. That file is the newer original.
                    var recorded = prev.FileList.FirstOrDefault(f => PathCmp.Equals(f.Path, Normalize(p.Step.File)));
                    string cur = Sha256(p.File);
                    if (recorded != null && !PathCmp.Equals(cur, recorded.Sha256) && !PathCmp.Equals(cur, newHash.TryGetValue(p.File, out var h) ? h : null)) {
                        string parked = Path.Combine(backupDir, "replaced-original", Normalize(p.Step.As));
                        Directory.CreateDirectory(Path.GetDirectoryName(parked));
                        File.Move(p.As, parked);
                        File.Move(p.File, p.As);
                        log($"The game replaced {p.Step.File}; kept the new one as {p.Step.As} (the old one is in {Relative(parked)}).");
                    }
                }
                if (!rec.PreservedList.Any(x => PathCmp.Equals(x.File, p.Step.File)))
                    rec.PreservedList.Add(new PreservedFile { File = Normalize(p.Step.File), As = Normalize(p.Step.As) });
            }

            // 2. The addon's files.
            foreach (var c in copies) {
                bool mine = rec.FileList.Any(f => PathCmp.Equals(f.Path, c.Rel));
                bool preservedTarget = preserves.Any(p => PathCmp.Equals(p.File, c.Target));
                bool backedUp = rec.BackupList.Any(b => PathCmp.Equals(b.Path, c.Rel));
                if (File.Exists(c.Target) && !mine && !takeOver && !preservedTarget && !backedUp) {
                    string dest = Path.Combine(backupDir, c.Rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(c.Target, dest, true);
                    rec.BackupList.Add(new BackupFile { Path = c.Rel, Backup = Relative(dest) });
                    log($"Backed up the existing {c.Rel}.");
                }
                Extract(c.Entry, c.Target);
                rec.FileList.RemoveAll(f => PathCmp.Equals(f.Path, c.Rel));
                rec.FileList.Add(new InstalledFile { Path = c.Rel, Sha256 = newHash[c.Target] });
                undo.Clear(); // from here on, a failure leaves a recorded, repairable install
                log($"Installed {c.Rel}.");
            }

            // 3. Files of the previous install that this one doesn't have (e.g. an option turned off).
            foreach (var f in rec.FileList.ToList())
                if (!copies.Any(c => PathCmp.Equals(c.Rel, f.Path)))
                    RemoveOwnedFile(rec, f, log);

            rec.Tag = tag;
            rec.Options = options.ToList();
            rec.InstalledUtc = DateTime.UtcNow.ToString("o");
        } catch {
            while (undo.Count > 0) {
                try { undo.Pop()(); } catch (Exception e) { log($"Could not undo a change: {e.Message}"); }
            }
            throw;
        } finally {
            if (!Manifest.AddonList.Contains(rec) && rec.FileList.Count > 0)
                Manifest.AddonList.Add(rec);
            Save();
        }
    }

    // --- uninstall ------------------------------------------------------------------------------

    public void Uninstall(AddonDef def, bool removeUserFiles, Action<string> log)
    {
        var rec = Find(def.Id) ?? throw new InvalidOperationException($"{def.Name} wasn't installed by AoForge.");
        EnsureWritable(rec.FileList.Select(f => Resolve(f.Path))
            .Concat(rec.PreservedList.SelectMany(p => new[] { Resolve(p.File), Resolve(p.As) })));

        foreach (var f in rec.FileList.ToList())
            RemoveOwnedFile(rec, f, log);

        foreach (var p in rec.PreservedList) {
            string file = Resolve(p.File), orig = Resolve(p.As);
            if (!File.Exists(orig))
                continue;
            if (File.Exists(file)) {
                // The game already put its own file back; keep that one and park the old original.
                string parked = Path.Combine(DataDir, "backup", def.Id, "replaced-original", DateTime.Now.ToString("yyyyMMdd-HHmmss"), p.As);
                Directory.CreateDirectory(Path.GetDirectoryName(parked));
                File.Move(orig, parked);
                log($"Kept the game's current {p.File}; moved the old {p.As} to {Relative(parked)}.");
            } else {
                File.Move(orig, file);
                log($"Restored the game's {p.File}.");
            }
        }

        if (removeUserFiles) {
            foreach (string pattern in def.UserFileList) {
                // Wildcards only in the file name; the folder part goes through Resolve like any path.
                string n = Normalize(pattern);
                int slash = n.LastIndexOf('\\');
                string dir = slash < 0 ? _client : Resolve(n.Substring(0, slash));
                string name = n.Substring(slash + 1);
                if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name.Contains(".."))
                    throw new InvalidDataException($"Refusing unsafe user-file pattern '{pattern}'.");
                if (!Directory.Exists(dir))
                    continue;
                foreach (string path in Directory.GetFiles(dir, name, SearchOption.TopDirectoryOnly)) {
                    File.Delete(path);
                    log($"Removed {Relative(path)}.");
                }
            }
        }

        Manifest.AddonList.Remove(rec);
        Save();
        DeleteEmptyDirs(Path.Combine(DataDir, "backup", def.Id));
    }

    void RemoveOwnedFile(InstalledAddon rec, InstalledFile f, Action<string> log)
    {
        string path = Resolve(f.Path);
        if (File.Exists(path)) {
            if (PathCmp.Equals(Sha256(path), f.Sha256)) {
                File.Delete(path);
                log($"Removed {f.Path}.");
            } else {
                log($"Left {f.Path} in place: something else replaced it after AoForge installed it.");
            }
        }
        rec.FileList.Remove(f);

        var backup = rec.BackupList.FirstOrDefault(b => PathCmp.Equals(b.Path, f.Path));
        if (backup != null) {
            string src = Resolve(backup.Backup);
            if (File.Exists(src) && !File.Exists(path)) {
                File.Move(src, path);
                log($"Put back the earlier {f.Path}.");
            }
            rec.BackupList.Remove(backup);
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    static IEnumerable<RecipeStep> CopySteps(AddonDef def, ICollection<string> options) =>
        def.StepList.Where(s => s.Action == StepAction.Copy && (s.Option == null || options.Contains(s.Option)));

    static IEnumerable<RecipeStep> PreserveSteps(AddonDef def) =>
        def.StepList.Where(s => s.Action == StepAction.PreserveOriginal);

    static string Normalize(string rel) => (rel ?? "").Replace('/', '\\').Trim('\\');

    // A path from the index or manifest, resolved inside the client folder. Anything that could
    // point outside it (rooted, drive letters, "..") is refused.
    string Resolve(string rel)
    {
        string n = Normalize(rel);
        if (n.Length == 0 || Path.IsPathRooted(n) || n.IndexOf(':') >= 0 || n.Split('\\').Any(s => s == ".." || s == "."))
            throw new InvalidDataException($"Refusing unsafe path '{rel}'.");
        string full = Path.GetFullPath(Path.Combine(_client, n));
        if (!full.StartsWith(_client + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing path outside the client folder: '{rel}'.");
        return full;
    }

    string Relative(string full) => full.Substring(_client.Length + 1);

    // Zip entries by path, with the release's single top-level folder (e.g. "ao-vk-0.7.0/") stripped.
    static Dictionary<string, ZipArchiveEntry> MapEntries(ZipArchive zip)
    {
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        string Top(ZipArchiveEntry e)
        {
            string n = e.FullName.Replace('\\', '/');
            int i = n.IndexOf('/');
            return i < 0 ? null : n.Substring(0, i + 1);
        }
        string prefix = files.Count > 0 ? Top(files[0]) : null;
        if (prefix == null || files.Any(e => Top(e) != prefix))
            prefix = "";
        var map = new Dictionary<string, ZipArchiveEntry>(PathCmp);
        foreach (var e in files)
            map[Normalize(e.FullName.Replace('\\', '/').Substring(prefix.Length))] = e;
        return map;
    }

    static void Extract(ZipArchiveEntry entry, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        string tmp = target + ".aoforge-new";
        entry.ExtractToFile(tmp, true);
        if (File.Exists(target))
            File.Delete(target);
        File.Move(tmp, target);
    }

    // Files the game holds open can't be replaced; fail before changing anything.
    static void EnsureWritable(IEnumerable<string> paths)
    {
        foreach (string path in paths.Distinct(PathCmp)) {
            if (!File.Exists(path))
                continue;
            try {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                throw new IOException($"{Path.GetFileName(path)} is in use. Close the game and try again.");
            }
        }
    }

    static void DeleteEmptyDirs(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string sub in Directory.GetDirectories(dir))
            DeleteEmptyDirs(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    void Save() => Json.WriteFile(ManifestPath, Manifest);

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Hex(s);
    }

    static string Sha256(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        return Hex(s);
    }

    static string Hex(Stream s)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }

    public static string ProductName(string path)
    {
        try {
            return FileVersionInfo.GetVersionInfo(path).ProductName;
        } catch (Exception) {
            return null;
        }
    }
}
