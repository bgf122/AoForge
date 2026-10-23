using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AoForge.Services;

// A file AoForge needs to replace is held open by a running process.
public sealed class FileInUseException : IOException
{
    public FileInUseException(IReadOnlyList<string> paths)
        : base($"{string.Join(", ", paths.Select(Path.GetFileName))} {(paths.Count == 1 ? "is" : "are")} in use. Close the game and try again.")
    {
        Paths = paths;
    }

    public IReadOnlyList<string> Paths { get; }
}

// Which processes hold files open, through Windows' Restart Manager (the API installers use for
// "files in use"). Typical culprit: the game's browser helpers (cef_helper.exe) load the client
// folder's version.dll and can linger after the game has closed.
public static class FileLocks
{
    public sealed class Holder
    {
        public int Id { get; init; }
        public string Name { get; init; }
        public string ExePath { get; init; } // null if it couldn't be read
    }

    public static List<Holder> WhoHolds(IEnumerable<string> paths)
    {
        var result = new List<Holder>();
        string[] files = paths.ToArray();
        if (files.Length == 0 || RmStartSession(out uint session, 0, new StringBuilder(64)) != 0)
            return result;
        try {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
                return result;
            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc != ErrorMoreData || needed == 0)
                return result;
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, ref reasons) != 0)
                return result;
            for (int i = 0; i < count; ++i) {
                int pid = info[i].Process.ProcessId;
                string exe = GameGuard.ImagePath(pid);
                result.Add(new Holder {
                    Id = pid,
                    Name = exe != null ? Path.GetFileName(exe) : info[i].AppName,
                    ExePath = exe,
                });
            }
        } finally {
            RmEndSession(session);
        }
        return result;
    }

    // Ends the given processes and waits for them to go. Returns false if any is still running.
    public static bool End(IEnumerable<Holder> holders)
    {
        bool ok = true;
        foreach (var h in holders) {
            try {
                using var p = Process.GetProcessById(h.Id);
                p.Kill();
                ok &= p.WaitForExit(5000);
            } catch (ArgumentException) {
                // already gone
            } catch (Exception) {
                ok = false;
            }
        }
        return ok;
    }

    const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps,
                                          RmUniqueProcess[] apps, uint nServices, string[] services);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] info, ref uint reasons);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);
}
