using System.Diagnostics;
using System.Runtime.InteropServices;
using WinMusic.Audio.Interop;
using WinMusic.Audio.Wasapi;

namespace WinMusic.Audio.Platform;

/// <summary>An app the user can pick as a music source (grouped by executable name).</summary>
public sealed record AppInfo(string ExeName, string DisplayName, int RootPid, string? ExePath, bool HasAudioSession, bool IsPlaying);

public static class Apps
{
    private sealed record Proc(int Pid, int ParentPid, string Exe);

    /// <summary>Lists apps that currently have an audio session; optionally also every app with a window.</summary>
    public static List<AppInfo> List(bool includeAllWindowed)
    {
        var procs = Snapshot();
        var byPid = procs.ToDictionary(p => p.Pid);
        int self = Environment.ProcessId;
        var sessions = GetSessionPids(); // pid -> playing

        var result = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, playing) in sessions)
        {
            if (!byPid.TryGetValue(pid, out var proc) || pid == self || pid == 0) continue;
            if (IsExcluded(proc.Exe)) continue;
            int root = FindRoot(proc, byPid);
            if (root == self) continue;
            if (result.TryGetValue(proc.Exe, out var existing))
            {
                if (playing && !existing.IsPlaying) result[proc.Exe] = existing with { IsPlaying = true };
                continue;
            }
            var path = GetPath(root) ?? GetPath(pid);
            result[proc.Exe] = new AppInfo(proc.Exe, Describe(proc.Exe, path), root, path, true, playing);
        }

        if (includeAllWindowed)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || p.Id == self) continue;
                    if (!byPid.TryGetValue(p.Id, out var proc) || IsExcluded(proc.Exe)) continue;
                    if (result.ContainsKey(proc.Exe)) continue;
                    int root = FindRoot(proc, byPid);
                    var path = GetPath(root) ?? GetPath(p.Id);
                    result[proc.Exe] = new AppInfo(proc.Exe, Describe(proc.Exe, path), root, path, false, false);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        return result.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenByDescending(a => a.HasAudioSession)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds the top-most running process for an exe name (so we capture its whole tree), or null.</summary>
    public static int? FindRootPid(string exeName)
    {
        var procs = Snapshot();
        var byPid = procs.ToDictionary(p => p.Pid);
        var candidates = procs.Where(p => string.Equals(p.Exe, exeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;
        // prefer the process whose parent is not the same exe
        var roots = candidates.Select(c => FindRoot(c, byPid)).Distinct().ToList();
        return roots.OrderBy(r => r).First();
    }

    private static bool IsExcluded(string exe) =>
        exe.Equals("audiodg.exe", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("WinMusic.exe", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);

    private static int FindRoot(Proc p, Dictionary<int, Proc> byPid)
    {
        var cur = p;
        var seen = new HashSet<int>();
        while (byPid.TryGetValue(cur.ParentPid, out var parent) && seen.Add(parent.Pid) &&
               string.Equals(parent.Exe, cur.Exe, StringComparison.OrdinalIgnoreCase))
            cur = parent;
        return cur.Pid;
    }

    private static string Describe(string exe, string? path)
    {
        try
        {
            if (path != null)
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription!.Trim();
                if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName!.Trim();
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>pid -> true if the session is currently active (playing).</summary>
    private static Dictionary<int, bool> GetSessionPids() => Devices.Mta(() =>
    {
        var map = new Dictionary<int, bool>();
        var en = Devices.CreateEnumerator();
        if (en.EnumAudioEndpoints(EDataFlow.Render, Native.DEVICE_STATE_ACTIVE, out var col) != 0) return map;
        col.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            if (col.Item(i, out var dev) != 0) continue;
            try
            {
                var iid = Native.IID_IAudioSessionManager2;
                if (dev.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out object o) != 0) continue;
                var mgr = (IAudioSessionManager2)o;
                if (mgr.GetSessionEnumerator(out var sessions) == 0)
                {
                    sessions.GetCount(out int count);
                    for (int s = 0; s < count; s++)
                    {
                        if (sessions.GetSession(s, out object so) != 0) continue;
                        if (so is IAudioSessionControl2 ctl)
                        {
                            if (ctl.IsSystemSoundsSession() != 0 && ctl.GetProcessId(out uint pid) == 0 && pid != 0)
                            {
                                ctl.GetState(out int state);
                                if (state == 2) continue; // expired
                                map[(int)pid] = (map.TryGetValue((int)pid, out var prev) && prev) || state == 1;
                            }
                        }
                        Marshal.ReleaseComObject(so);
                    }
                    Marshal.ReleaseComObject(sessions);
                }
                Marshal.ReleaseComObject(mgr);
            }
            finally { Marshal.ReleaseComObject(dev); }
        }
        return map;
    });

    // ---- toolhelp / process path ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, char[] buffer, ref uint size);

    private static List<Proc> Snapshot()
    {
        var list = new List<Proc>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return list;
            do list.Add(new Proc((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            while (Process32NextW(snap, ref e));
        }
        finally { Native.CloseHandle(snap); }
        return list;
    }

    public static string? GetPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally { Native.CloseHandle(h); }
    }
}
