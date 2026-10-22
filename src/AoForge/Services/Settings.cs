using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace AoForge.Services;

// %APPDATA%\AoForge\settings.json
[DataContract]
public sealed class Settings
{
    [DataMember(Name = "clients")] public List<string> Clients { get; set; }
    [DataMember(Name = "selected")] public string Selected { get; set; }

    public List<string> ClientList => Clients ??= new();

    static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static Settings Load()
    {
        try {
            if (File.Exists(FilePath))
                return Json.ReadFile<Settings>(FilePath);
        } catch (Exception) {
            // corrupt settings: start over
        }
        return new Settings();
    }

    public void Save() => Json.WriteFile(FilePath, this);
}
