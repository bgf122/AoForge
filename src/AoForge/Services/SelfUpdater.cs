using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace AoForge.Services;

// AoForge updates itself from its own GitHub releases. A running exe can't be overwritten, but it
// can be renamed: the current exe becomes AoForge.exe.old, the new one takes its name, starts,
// and deletes the .old file.
public static class SelfUpdater
{
    public const string Repo = "bgf122/AoForge";
    public const string AssetName = "AoForge.exe";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

    public static string ExePath => Assembly.GetExecutingAssembly().Location;

    // "v0.2.0" -> 0.2.0; null if the tag isn't a version.
    public static Version ParseTag(string tag)
    {
        string t = (tag ?? "").Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(t.IndexOf('.') >= 0 ? t : t + ".0", out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(string tag, Version current)
    {
        var v = ParseTag(tag);
        return v != null && v > Normalize(current);
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // False when the exe's folder isn't writable (e.g. Program Files without admin rights).
    public static bool CanReplace(string exePath)
    {
        try {
            string probe = Path.Combine(Path.GetDirectoryName(exePath), $".aoforge-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        } catch (Exception) {
            return false;
        }
    }

    // The downloaded file must really be AoForge, at the version the release says.
    public static void Verify(string downloaded, string tag)
    {
        var info = FileVersionInfo.GetVersionInfo(downloaded);
        var expected = ParseTag(tag);
        var actual = ParseTag(info.ProductVersion);
        if (info.ProductName != "AoForge" || expected == null || actual != expected)
            throw new InvalidDataException($"The downloaded {AssetName} isn't AoForge {tag} (it says {info.ProductName} {info.ProductVersion}). Nothing was changed.");
    }

    // Puts the new exe in place of the running one; on failure the old exe is put back.
    public static void Apply(string downloaded, string exePath)
    {
        string staged = exePath + ".new";
        string old = exePath + ".old";
        File.Copy(downloaded, staged, true); // same folder as the exe, so the renames below can't fail halfway across drives
        if (File.Exists(old))
            File.Delete(old); // an earlier update's leftover; not in use any more
        File.Move(exePath, old);
        try {
            File.Move(staged, exePath);
        } catch {
            File.Move(old, exePath);
            TryDelete(staged);
            throw;
        }
    }

    // Called at startup: removes the previous exe once its process has exited.
    public static void CleanupOld(string exePath)
    {
        string old = exePath + ".old";
        for (int i = 0; i < 20 && File.Exists(old); i++) {
            if (TryDelete(old))
                return;
            Thread.Sleep(250);
        }
    }

    static bool TryDelete(string path)
    {
        try {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        } catch (Exception) {
            return false;
        }
    }

    // Starts the new exe with the same arguments, minus our own update flag.
    public static void Restart(string exePath)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--updated").Select(Quote);
        Process.Start(new ProcessStartInfo(exePath, string.Join(" ", args.Append("--updated"))) { UseShellExecute = false });
    }

    static string Quote(string a) => a.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
}
