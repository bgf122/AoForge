using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AoForge.Services;

// Addon files are locked while the game runs, and replacing them under it would crash it.
public static class GameGuard
{
    public static bool IsGameRunning(string clientDir)
    {
        foreach (var p in Process.GetProcessesByName("AnarchyOnline")) {
            using (p) {
                string exe = ImagePath(p.Id);
                // Unknown path: assume it's this client, to be safe.
                if (exe == null || clientDir == null || exe.StartsWith(clientDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // Works across 32/64-bit, unlike Process.MainModule.
    public static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        } finally {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}
