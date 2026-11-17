using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GhostCode.Core.Monitoring;

/// <summary>
/// Fast, low-allocation Windows process-name lookup via Toolhelp32 snapshot.
/// Avoids allocating hundreds of Process objects and kernel process handles on every scan.
/// </summary>
public static class WindowsProcessSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static HashSet<string> GetRunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return set;

        IntPtr snap = InvalidHandle;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == InvalidHandle || snap == IntPtr.Zero)
                return FallbackProcessNames();

            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    var name = Path.GetFileNameWithoutExtension(entry.szExeFile);
                    if (!string.IsNullOrWhiteSpace(name))
                        set.Add(name);
                } while (Process32NextW(snap, ref entry));
            }
            return set;
        }
        catch
        {
            return FallbackProcessNames();
        }
        finally
        {
            if (snap != InvalidHandle && snap != IntPtr.Zero)
                CloseHandle(snap);
        }
    }

    public static string? TryGetProcessNameByPid(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0)
            return null;

        IntPtr snap = InvalidHandle;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == InvalidHandle || snap == IntPtr.Zero)
                return null;

            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    if ((int)entry.th32ProcessID == pid)
                        return Path.GetFileNameWithoutExtension(entry.szExeFile);
                } while (Process32NextW(snap, ref entry));
            }
        }
        catch { }
        finally
        {
            if (snap != InvalidHandle && snap != IntPtr.Zero)
                CloseHandle(snap);
        }
        return null;
    }

    private static HashSet<string> FallbackProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return set;
    }
}
