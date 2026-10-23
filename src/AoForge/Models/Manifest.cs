using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AoForge.Models;

// <client>\AoForge\installed.json: what AoForge did to the client folder, so it can be undone exactly.
[DataContract]
public sealed class Manifest
{
    [DataMember(Name = "addons")] public List<InstalledAddon> Addons { get; set; } = new();

    public List<InstalledAddon> AddonList => Addons ??= new();
}

[DataContract]
public sealed class InstalledAddon
{
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "tag")] public string Tag { get; set; }
    [DataMember(Name = "installedUtc")] public string InstalledUtc { get; set; }
    [DataMember(Name = "options")] public List<string> Options { get; set; }
    [DataMember(Name = "files")] public List<InstalledFile> Files { get; set; }
    [DataMember(Name = "preserved")] public List<PreservedFile> Preserved { get; set; }
    [DataMember(Name = "backups")] public List<BackupFile> Backups { get; set; }
    [DataMember(Name = "takenOver")] public List<TakenFile> TakenOver { get; set; }

    public List<string> OptionList => Options ??= new();
    public List<InstalledFile> FileList => Files ??= new();
    public List<PreservedFile> PreservedList => Preserved ??= new();
    public List<BackupFile> BackupList => Backups ??= new();
    public List<TakenFile> TakenOverList => TakenOver ??= new();
}

// A file this addon took over from another installed addon (e.g. MerkUI's version.dll from ao-vk's
// AOReloaded option); given back when this addon is uninstalled.
[DataContract]
public sealed class TakenFile
{
    [DataMember(Name = "path")] public string Path { get; set; }
    [DataMember(Name = "from")] public string From { get; set; }     // the other addon's id
    [DataMember(Name = "option")] public string Option { get; set; } // its option that installs the file, if any
}

[DataContract]
public sealed class InstalledFile
{
    [DataMember(Name = "path")] public string Path { get; set; } // relative to the client folder
    [DataMember(Name = "sha256")] public string Sha256 { get; set; }
}

// The game's own file, renamed so the addon can sit in its place (e.g. randy31.dll -> randy31_orig.dll).
[DataContract]
public sealed class PreservedFile
{
    [DataMember(Name = "file")] public string File { get; set; }
    [DataMember(Name = "as")] public string As { get; set; }
}

// A file that wasn't the addon's, copied away before it was overwritten; put back on uninstall.
[DataContract]
public sealed class BackupFile
{
    [DataMember(Name = "path")] public string Path { get; set; }
    [DataMember(Name = "backup")] public string Backup { get; set; } // relative to the client folder
}
