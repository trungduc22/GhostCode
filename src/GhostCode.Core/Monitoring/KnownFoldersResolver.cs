using System.Runtime.InteropServices;
using GhostCode.Core.Models;

namespace GhostCode.Core.Monitoring;

public static class KnownFoldersResolver
{
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    public static string? TryGetKnownFolder(Guid folderId)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        IntPtr ptr = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out ptr);
            if (hr == 0 && ptr != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(ptr);
            }
        }
        catch
        {
            // Fall back to Environment.GetFolderPath
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(ptr);
        }
        return null;
    }

    public static bool IsDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Discovers real Desktop and Documents directories (including OneDrive-redirected paths)
    /// without hardcoding any username.
    /// </summary>
    public static List<WatchedFolderConfig> DiscoverDefaultWatchedFolders(string? userProfileOverride = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WatchedFolderConfig>();

        void TryAdd(string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }
            if (IsDriveRoot(full) || !Directory.Exists(full))
                return;
            if (seen.Add(full))
            {
                result.Add(new WatchedFolderConfig(full, Enabled: true, Recursive: true, Label: label));
            }
        }

        if (string.IsNullOrWhiteSpace(userProfileOverride))
        {
            TryAdd(TryGetKnownFolder(FolderIdDesktop) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop");
            TryAdd(TryGetKnownFolder(FolderIdDocuments) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                TryAdd(Path.Combine(profile, "Desktop"), "Local Desktop");
                TryAdd(Path.Combine(profile, "Documents"), "Local Documents");
                TryAdd(Path.Combine(profile, "OneDrive", "Desktop"), "OneDrive Desktop");
                TryAdd(Path.Combine(profile, "OneDrive", "Documents"), "OneDrive Documents");
            }
        }
        else
        {
            TryAdd(Path.Combine(userProfileOverride, "OneDrive", "Desktop"), "OneDrive Desktop");
            TryAdd(Path.Combine(userProfileOverride, "Desktop"), "Desktop");
            TryAdd(Path.Combine(userProfileOverride, "OneDrive", "Documents"), "OneDrive Documents");
            TryAdd(Path.Combine(userProfileOverride, "Documents"), "Documents");
        }

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(userProfileOverride))
        {
            result.Add(new WatchedFolderConfig(Path.Combine(userProfileOverride, "Documents"), Enabled: true, Recursive: true, Label: "Documents"));
        }

        return result;
    }

    public static string ResolveDefaultStorageBase(string? userProfileOverride = null)
    {
        if (string.IsNullOrWhiteSpace(userProfileOverride))
        {
            var docs = TryGetKnownFolder(FolderIdDocuments);
            if (!string.IsNullOrWhiteSpace(docs))
                return docs;
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, "Documents");
        }
        var oneDriveDocs = Path.Combine(userProfileOverride, "OneDrive", "Documents");
        if (Directory.Exists(oneDriveDocs))
            return oneDriveDocs;
        return Path.Combine(userProfileOverride, "Documents");
    }

    public static string ResolveAppDataDir(string? localAppDataOverride = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(localAppDataOverride)
            ? localAppDataOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.GetTempPath();
        return Path.Combine(baseDir, "GhostCode");
    }
}
