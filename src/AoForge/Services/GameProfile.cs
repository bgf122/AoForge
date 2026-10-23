using System;
using System.IO;
using System.Text;

namespace AoForge.Services;

// The game's per-install profile folder: %LOCALAPPDATA%\Funcom\Anarchy Online\<hash>\client, with
// Prefs (settings, window layouts, the GUI skin choice) and Gui (skins copied there).
public static class GameProfile
{
    public static string DirFor(string clientDir) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Funcom", "Anarchy Online", HashFor(clientDir), "client");

    // The game's name for the folder: the client folder's parent as the ANSI path the game sees
    // ('/' separators, no trailing slash), hashed with boost hash_combine over signed chars and
    // printed with %x. "C:\Project Rubi-Ka\client" -> 2e333565.
    public static string HashFor(string clientDir)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(clientDir).TrimEnd('\\', '/'))
                        ?? throw new ArgumentException($"'{clientDir}' has no parent folder.");
        string path = parent.Replace('\\', '/').TrimEnd('/');
        uint h = 0;
        foreach (byte b in Encoding.Default.GetBytes(path))
            h ^= unchecked((uint)(sbyte)b + 0x9e3779b9u + (h << 6) + (h >> 2));
        return h.ToString("x");
    }
}
