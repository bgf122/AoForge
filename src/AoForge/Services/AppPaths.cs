using System;
using System.IO;
using System.Reflection;

namespace AoForge.Services;

public static class AppPaths
{
    // Where the addon list is published. Overridable with --index <path or url> for testing.
    public const string IndexUrl = "https://raw.githubusercontent.com/bgf122/AoForge/main/index.json";

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AoForge");

    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
}
