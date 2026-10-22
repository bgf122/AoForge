using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace AoForge.Services;

// Finds Anarchy Online client folders. The PRK launcher keeps "ClientPath" in LauncherSettings.json
// next to PRK.Launcher.exe; there is no registry entry.
public static class ClientLocator
{
    [DataContract]
    sealed class LauncherSettings
    {
        [DataMember(Name = "ClientPath")] public string ClientPath { get; set; }
    }

    public static bool IsClientDir(string dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "AnarchyOnline.exe"));

    public static List<string> FindCandidates()
    {
        var bases = new List<string>();
        for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null && bases.Count < 4; d = d.Parent)
            bases.Add(d.FullName);
        bases.Add(@"C:\Project Rubi-Ka");
        foreach (var root in new[] {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 }) {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                continue;
            try {
                bases.AddRange(Directory.GetDirectories(root));
            } catch (UnauthorizedAccessException) {
            }
        }

        var found = new List<string>();
        foreach (string b in bases) {
            try {
                string settings = Path.Combine(b, "LauncherSettings.json");
                if (File.Exists(settings)) {
                    string client = Json.ReadFile<LauncherSettings>(settings).ClientPath;
                    if (IsClientDir(client))
                        found.Add(Path.GetFullPath(client));
                }
                if (IsClientDir(Path.Combine(b, "client")))
                    found.Add(Path.GetFullPath(Path.Combine(b, "client")));
                if (IsClientDir(b))
                    found.Add(Path.GetFullPath(b));
            } catch (Exception) {
                // unreadable folder or settings file: not a candidate
            }
        }
        return found.Select(p => p.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
