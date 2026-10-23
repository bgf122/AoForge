using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
    readonly string _profile;

    // profileDir: the game's profile folder for this client; computed from the client folder by default.
    public InstallEngine(string clientDir, string profileDir = null)
    {
        _client = Path.GetFullPath(clientDir).TrimEnd('\\', '/');
        _profile = Path.GetFullPath(profileDir ?? GameProfile.DirFor(_client)).TrimEnd('\\', '/');
        Manifest = File.Exists(ManifestPath) ? Json.ReadFile<Manifest>(ManifestPath) : new Manifest();
    }

    public string ClientDir => _client;
    public string ProfileDir => _profile;
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

    public bool IsDetected(AddonDef def) => def.DetectList.Any(RuleMatches);

    bool RuleMatches(DetectRule r)
    {
        string path = Resolve(r.File);
        if (!File.Exists(path))
            return false;
        if (r.ProductName != null && ProductName(path) != r.ProductName)
            return false;
        return r.Contains == null || FileContains(path, r.Contains);
    }

    // The version of a hand-installed copy, from a detect rule's version resource (ProductName set)
    // or text (VersionPattern on the file's text). Null when unknown, e.g. ao-vk's randy31.dll.
    public string DetectedVersion(AddonDef def)
    {
        foreach (var r in def.DetectList.Where(RuleMatches)) {
            string path = Resolve(r.File);
            try {
                if (r.VersionPattern != null) {
                    if (new FileInfo(path).Length > 64 * 1024)
                        continue;
                    var m = Regex.Match(File.ReadAllText(path), r.VersionPattern);
                    if (m.Success && m.Groups.Count > 1)
                        return m.Groups[1].Value;
                } else if (r.ProductName != null) {
                    string v = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Split('+')[0].Trim(); // drop "+commit" build metadata
                }
            } catch (Exception) {
                // unreadable: unknown version
            }
        }
        return null;
    }

    static bool FileContains(string path, string text)
    {
        try {
            byte[] data = File.ReadAllBytes(path);
            byte[] needle = System.Text.Encoding.ASCII.GetBytes(text);
            for (int i = 0; i + needle.Length <= data.Length; ++i) {
                int k = 0;
                while (k < needle.Length && data[i + k] == needle[k])
                    ++k;
                if (k == needle.Length)
                    return true;
            }
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
        return false;
    }

    // The addon (other than exceptId) whose install recorded this file, if any.
    InstalledAddon OwnerOf(string rel, string exceptId) =>
        Manifest.AddonList.FirstOrDefault(a => a.Id != exceptId && a.FileList.Any(f => PathCmp.Equals(f.Path, rel)));

    // The option of an addon's recipe that installs this file, if any (e.g. ao-vk's "aoreloaded").
    static AddonOption OptionFor(AddonDef def, string rel)
    {
        var step = def?.StepList.FirstOrDefault(s => s.Action == StepAction.Copy && s.Option != null && PathCmp.Equals(Normalize(s.To), rel));
        return step == null ? null : def.OptionList.FirstOrDefault(o => o.Id == step.Option);
    }

    // Warnings to show before installing: files another addon owns, and files that aren't the addon's.
    public List<string> FindConflicts(AddonDef def, ICollection<string> options, IEnumerable<AddonDef> allDefs)
    {
        var defs = allDefs.ToList();
        var rec = Find(def.Id);
        bool takeOver = rec == null && IsDetected(def);
        var preservedFiles = PreserveSteps(def).Select(s => s.File).ToList();
        var result = new List<string>();

        foreach (var step in CopySteps(def, options)) {
            string rel = Normalize(step.To);
            var owner = OwnerOf(rel, def.Id);
            if (owner != null) {
                var ownerDef = defs.FirstOrDefault(d => d.Id == owner.Id);
                string ownerName = ownerDef?.Name ?? owner.Id;
                var option = OptionFor(ownerDef, rel);
                result.Add(option != null
                    ? $"{rel} is {ownerName}'s (\"{option.Label}\"). {def.Name} takes it over: {ownerName} stays installed with that option turned off."
                    : $"{rel} is {ownerName}'s. {def.Name} takes it over, and {ownerName} carries on without it.");
                continue;
            }
            bool mine = rec != null && rec.FileList.Any(f => PathCmp.Equals(f.Path, rel));
            if (File.Exists(Resolve(rel)) && !mine && !takeOver && !preservedFiles.Any(p => PathCmp.Equals(Normalize(p), rel)))
                result.Add($"{rel} is already in the client folder and isn't from {def.Name}. It will be backed up and put back when you uninstall {def.Name}.");
        }
        return result;
    }

    // --- install / update / repair / take over ------------------------------------------------

    // allDefs: the index, to turn off another addon's option when this one takes over its file.
    public void Install(AddonDef def, string tag, string zipPath, ICollection<string> options, Action<string> log,
                        IEnumerable<AddonDef> allDefs = null)
    {
        var unknown = def.StepList.FirstOrDefault(s => s.Action != StepAction.Copy && s.Action != StepAction.CopyTree && s.Action != StepAction.PreserveOriginal);
        if (unknown != null)
            throw new InvalidDataException($"{def.Name} needs a newer AoForge (it uses the install step '{unknown.Action}').");

        var prev = Find(def.Id);
        bool takeOver = prev == null && IsDetected(def);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = MapEntries(zip);

        // Check everything before the first change. Single files first, so an explicit copy of a
        // file wins over the same file in a copied folder.
        var copies = new List<(string Rel, string Target, ZipArchiveEntry Entry)>();
        var seen = new HashSet<string>(PathCmp);
        foreach (var step in CopySteps(def, options)) {
            if (!entries.TryGetValue(Normalize(step.From), out var entry))
                throw new InvalidDataException($"The {def.Name} download has no '{step.From}'. The addon's release may have changed; AoForge needs an index update.");
            string rel = Normalize(step.To);
            if (seen.Add(rel))
                copies.Add((rel, Resolve(rel), entry));
        }
        foreach (var step in TreeSteps(def, options)) {
            string from = Normalize(step.From), to = Normalize(step.To);
            string prefix = from.Length == 0 ? "" : from + "\\";
            int found = 0;
            foreach (var kv in entries.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Key)) {
                string sub = kv.Key.Substring(prefix.Length);
                string rel = to.Length == 0 ? sub : to + "\\" + sub;
                ++found;
                if (seen.Add(rel))
                    copies.Add((rel, Resolve(rel), kv.Value));
            }
            if (found == 0)
                throw new InvalidDataException($"The {def.Name} download has no folder '{step.From}'. The addon's release may have changed; AoForge needs an index update.");
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
            // 0. The game's settings as they are before the addon first runs. Not when taking over a
            // hand install: the addon has already changed them.
            if (prev == null && !takeOver && def.Profile != null && def.Profile.SnapshotList.Count > 0) {
                TakeProfileSnapshot(def, rec, log);
                undo.Push(() => DeleteDir(Path.Combine(DataDir, "profile-backup", def.Id)));
            }

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
            var defs = allDefs?.ToList() ?? new List<AddonDef>();
            foreach (var c in copies) {
                // Another addon's file (e.g. ao-vk's AOReloaded version.dll): this addon takes it
                // over. The other stops claiming it, with the option that installs it turned off,
                // so it neither needs repair nor puts the file back on its next update.
                bool handedOver = false;
                if (OwnerOf(c.Rel, def.Id) is InstalledAddon owner) {
                    var ownerDef = defs.FirstOrDefault(d => d.Id == owner.Id);
                    owner.FileList.RemoveAll(f => PathCmp.Equals(f.Path, c.Rel));
                    var option = OptionFor(ownerDef, c.Rel);
                    if (option != null)
                        owner.OptionList.Remove(option.Id);
                    if (!rec.TakenOverList.Any(t => PathCmp.Equals(t.Path, c.Rel)))
                        rec.TakenOverList.Add(new TakenFile { Path = c.Rel, From = owner.Id, Option = option?.Id });
                    log($"Took over {c.Rel} from {ownerDef?.Name ?? owner.Id}" + (option != null ? $" (its option \"{option.Label}\" is now off)." : "."));
                    handedOver = true;
                }
                bool mine = rec.FileList.Any(f => PathCmp.Equals(f.Path, c.Rel));
                bool preservedTarget = preserves.Any(p => PathCmp.Equals(p.File, c.Target));
                bool backedUp = rec.BackupList.Any(b => PathCmp.Equals(b.Path, c.Rel));
                if (File.Exists(c.Target) && !mine && !takeOver && !preservedTarget && !backedUp && !handedOver) {
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

    // Returns the addons whose files this one had taken over: their options are switched back on
    // here, and the caller reinstalls them so the files come back (e.g. ao-vk's AOReloaded after
    // MerkUI is uninstalled).
    // restoreProfile: put back the profile folders saved at install (ProfileSnapshot) first.
    public List<string> Uninstall(AddonDef def, bool removeUserFiles, Action<string> log, bool restoreProfile = false)
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

        if (restoreProfile && rec.ProfileSnapshot != null)
            RestoreProfileSnapshot(rec.ProfileSnapshot, log);
        if (def.Profile != null) {
            foreach (string rel in def.Profile.RemoveList) {
                string dir = ResolveIn(_profile, rel);
                if (Directory.Exists(dir)) {
                    DeleteDir(dir);
                    log($"Removed {Normalize(rel)} from the game's profile folder.");
                }
            }
        }
        if (rec.ProfileSnapshot != null)
            DeleteDir(Resolve(rec.ProfileSnapshot.Dir));

        // Give taken-over files back: the other addon's option is on again and it gets reinstalled.
        var giveBack = new List<string>();
        foreach (var t in rec.TakenOverList) {
            var owner = Find(t.From);
            if (owner == null)
                continue; // uninstalled meanwhile
            if (t.Option != null && !owner.OptionList.Contains(t.Option))
                owner.OptionList.Add(t.Option);
            if (!giveBack.Contains(owner.Id))
                giveBack.Add(owner.Id);
            log($"{t.Path} goes back to {owner.Id}.");
        }

        Manifest.AddonList.Remove(rec);
        Save();
        DeleteEmptyDirs(Path.Combine(DataDir, "backup", def.Id));
        DeleteEmptyDirs(Path.Combine(DataDir, "profile-backup"));
        return giveBack;
    }

    // --- the game's profile folder --------------------------------------------------------------

    void TakeProfileSnapshot(AddonDef def, InstalledAddon rec, Action<string> log)
    {
        string dest = Path.Combine(DataDir, "profile-backup", def.Id);
        DeleteDir(dest); // left over from an install that wasn't recorded
        var snap = new ProfileSnapshot { Dir = Relative(dest), TakenUtc = DateTime.UtcNow.ToString("o") };
        foreach (string rel in def.Profile.SnapshotList) {
            string src = ResolveIn(_profile, rel);
            if (!Directory.Exists(src))
                continue;
            CopyDir(src, Path.Combine(dest, Normalize(rel)));
            snap.FolderList.Add(Normalize(rel));
        }
        if (snap.FolderList.Count == 0) {
            log("The game has no settings for this client folder yet; nothing to back up.");
            return;
        }
        rec.ProfileSnapshot = snap;
        log($"Backed up the game's settings ({string.Join(", ", snap.FolderList)}) to {snap.Dir}.");
    }

    // Each folder is copied next to the current one, then swapped in, so a failure halfway leaves
    // the current settings in place.
    void RestoreProfileSnapshot(ProfileSnapshot snap, Action<string> log)
    {
        string root = Resolve(snap.Dir);
        foreach (string rel in snap.FolderList) {
            string src = ResolveIn(root, rel);
            if (!Directory.Exists(src)) {
                log($"The backup of {rel} is missing; left the current one in place.");
                continue;
            }
            string target = ResolveIn(_profile, rel);
            string staged = target + ".aoforge-restore", old = target + ".aoforge-old";
            DeleteDir(staged);
            DeleteDir(old);
            CopyDir(src, staged);
            if (Directory.Exists(target))
                Directory.Move(target, old);
            Directory.Move(staged, target);
            DeleteDir(old);
            log($"Restored the game's {rel} from before the install.");
        }
    }

    static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    static void DeleteDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal); // read-only files would stop Directory.Delete
        Directory.Delete(dir, true);
    }

    void RemoveOwnedFile(InstalledAddon rec, InstalledFile f, Action<string> log)
    {
        string path = Resolve(f.Path);
        if (File.Exists(path)) {
            if (PathCmp.Equals(Sha256(path), f.Sha256)) {
                File.Delete(path);
                log($"Removed {f.Path}.");
                PruneEmptyFolders(Path.GetDirectoryName(path));
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

    // Folders a removed file leaves empty (e.g. an addon's skin folder), up to the client folder.
    void PruneEmptyFolders(string dir)
    {
        while (dir != null && dir.StartsWith(_client + "\\", StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    static IEnumerable<RecipeStep> TreeSteps(AddonDef def, ICollection<string> options) =>
        def.StepList.Where(s => s.Action == StepAction.CopyTree && (s.Option == null || options.Contains(s.Option)));

    static IEnumerable<RecipeStep> PreserveSteps(AddonDef def) =>
        def.StepList.Where(s => s.Action == StepAction.PreserveOriginal);

    static string Normalize(string rel) => (rel ?? "").Replace('/', '\\').Trim('\\');

    // A path from the index or manifest, resolved inside the client folder. Anything that could
    // point outside it (rooted, drive letters, "..") is refused.
    string Resolve(string rel) => ResolveIn(_client, rel);

    static string ResolveIn(string root, string rel)
    {
        string n = Normalize(rel);
        if (n.Length == 0 || Path.IsPathRooted(n) || n.IndexOf(':') >= 0 || n.Split('\\').Any(s => s == ".." || s == "."))
            throw new InvalidDataException($"Refusing unsafe path '{rel}'.");
        string full = Path.GetFullPath(Path.Combine(root, n));
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing path outside {root}: '{rel}'.");
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
        var locked = new List<string>();
        foreach (string path in paths.Distinct(PathCmp)) {
            if (!File.Exists(path))
                continue;
            try {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                locked.Add(path);
            }
        }
        if (locked.Count > 0)
            throw new FileInUseException(locked); // the UI finds out who holds them
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
