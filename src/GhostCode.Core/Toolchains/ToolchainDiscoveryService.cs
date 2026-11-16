using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using GhostCode.Core.Models;
using Microsoft.Win32;

namespace GhostCode.Core.Toolchains;

public interface IToolchainEnvironmentProbe
{
    bool FileExists(string path);
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption);
    IEnumerable<string> EnumerateDirectories(string directory);
    IReadOnlyCollection<string> GetRunningProcessNames();
    string? GetFileVersion(string path);
    string? ReadRegistryString(string fullKeyPath, string valueName);
    IEnumerable<string> ReadRegistryUninstallDisplayNames();
    (int ExitCode, string Stdout, string Stderr) RunProcess(
        string executable,
        IReadOnlyList<string> args,
        string? stdinText = null,
        int timeoutMs = 4000);
}

public sealed class WindowsToolchainEnvironmentProbe : IToolchainEnvironmentProbe
{
    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption)
    {
        if (!Directory.Exists(directory))
            return Enumerable.Empty<string>();
        try { return Directory.EnumerateFiles(directory, searchPattern, searchOption).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (!Directory.Exists(directory))
            return Enumerable.Empty<string>();
        try { return Directory.EnumerateDirectories(directory).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    public IReadOnlyCollection<string> GetRunningProcessNames()
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

    public string? GetFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    public string? ReadRegistryString(string fullKeyPath, string valueName)
    {
        try { return Registry.GetValue(fullKeyPath, valueName, null) as string; }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    public IEnumerable<string> ReadRegistryUninstallDisplayNames()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows()) return results;

        RegistryKey[] roots = { Registry.CurrentUser, Registry.LocalMachine };
        string[] subKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var root in roots)
        {
            foreach (var sub in subKeys)
            {
                try
                {
                    using var key = root.OpenSubKey(sub);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var appKey = key.OpenSubKey(name);
                        var dn = appKey?.GetValue("DisplayName") as string;
                        var loc = appKey?.GetValue("InstallLocation") as string;
                        var ico = appKey?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(dn))
                            results.Add($"{dn}|{loc}|{ico}");
                    }
                }
                catch { }
            }
        }
        return results;
    }

    public (int ExitCode, string Stdout, string Stderr) RunProcess(
        string executable,
        IReadOnlyList<string> args,
        string? stdinText = null,
        int timeoutMs = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdinText is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, string.Empty, "Failed to start process");

            if (stdinText is not null)
            {
                proc.StandardInput.Write(stdinText);
                proc.StandardInput.Close();
            }

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-2, string.Empty, "Probe timed out");
            }

            return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}

/// <summary>
/// Discovers installed IDEs (Dev-C++, VS Code, PyCharm, Visual Studio) and validates
/// C/C++/Python toolchains (GCC, G++, Clang++, MSVC, Python 3) without hardcoded user paths.
/// </summary>
public sealed class ToolchainDiscoveryService
{
    private readonly IToolchainEnvironmentProbe _probe;
    private readonly object _cacheLock = new();
    private string? _cacheKey;
    private List<ToolchainInfo>? _cachedToolchains;

    public ToolchainDiscoveryService(IToolchainEnvironmentProbe? probe = null)
    {
        _probe = probe ?? new WindowsToolchainEnvironmentProbe();
    }

    public (List<IdeStatusInfo> Ides, List<ToolchainInfo> Toolchains) DiscoverAll(GhostCodeConfig config)
    {
        var toolchains = DiscoverToolchains(config);
        var ides = DiscoverIdes(config, toolchains);
        return (ides, toolchains);
    }

    public List<IdeStatusInfo> DiscoverIdes(GhostCodeConfig config, IReadOnlyList<ToolchainInfo>? toolchains = null)
    {
        toolchains ??= DiscoverToolchains(config);
        var running = _probe.GetRunningProcessNames();
        var uninstallEntries = _probe.ReadRegistryUninstallDisplayNames().ToList();

        var hasC = toolchains.Any(t => t.Kind == "C" && t.IsAvailable);
        var hasCpp = toolchains.Any(t => t.Kind == "C++" && t.IsAvailable);
        var hasPy = toolchains.Any(t => t.Kind == "Python" && t.IsAvailable);

        var cDesc = hasC ? toolchains.First(t => t.Kind == "C" && t.IsAvailable).DisplayName : "None";
        var cppDesc = hasCpp ? toolchains.First(t => t.Kind == "C++" && t.IsAvailable).DisplayName : "None";
        var pyDesc = hasPy ? toolchains.First(t => t.Kind == "Python" && t.IsAvailable).DisplayName : "None";

        var list = new List<IdeStatusInfo>
        {
            ProbeDevCpp(config, running, $"{cDesc} / {cppDesc}"),
            ProbeVsCode(config, running, $"{cppDesc}, {pyDesc}"),
            ProbePyCharm(config, running, uninstallEntries, pyDesc),
            ProbeVisualStudio(config, running, cppDesc)
        };

        return list;
    }

    public List<ToolchainInfo> DiscoverToolchains(GhostCodeConfig config) => DiscoverToolchains(config, forceRescan: false);

    public List<ToolchainInfo> DiscoverToolchains(GhostCodeConfig config, bool forceRescan)
    {
        var key = $"{config.CCompilerPath}|{config.CppCompilerPath}|{config.PythonInterpreterPath}|{config.CStandard}|{config.CppStandard}";
        lock (_cacheLock)
        {
            if (!forceRescan && _cachedToolchains is not null && string.Equals(_cacheKey, key, StringComparison.Ordinal))
                return _cachedToolchains.ToList();
        }

        var fresh = new List<ToolchainInfo>
        {
            ProbeCCompilerCore(config),
            ProbeCppCompilerCore(config),
            ProbePythonCore(config)
        };

        lock (_cacheLock)
        {
            _cacheKey = key;
            _cachedToolchains = fresh;
            return fresh.ToList();
        }
    }

    public ToolchainInfo DiscoverCCompiler(GhostCodeConfig config) =>
        DiscoverToolchains(config).First(t => t.Kind == "C");

    public ToolchainInfo DiscoverCppCompiler(GhostCodeConfig config) =>
        DiscoverToolchains(config).First(t => t.Kind == "C++");

    public ToolchainInfo DiscoverPython(GhostCodeConfig config) =>
        DiscoverToolchains(config).First(t => t.Kind == "Python");

    private IdeStatusInfo ProbeDevCpp(GhostCodeConfig config, IReadOnlyCollection<string> running, string tcSummary)
    {
        var candidates = new List<string>();
        if (config.CustomIdePaths.TryGetValue("Dev-C++", out var custom)) candidates.Add(custom);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Dev-Cpp", "devcpp.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dev-Cpp", "devcpp.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Embarcadero", "Dev-Cpp", "devcpp.exe"));

        var exe = FirstExisting(candidates);
        var ver = exe is not null ? _probe.GetFileVersion(exe) : null;
        return new IdeStatusInfo(
            Name: "Dev-C++",
            IsInstalled: exe is not null,
            IsRunning: running.Contains("devcpp"),
            ExecutablePath: exe,
            Version: ver,
            SupportedLanguages: "C, C++",
            ToolchainSummary: tcSummary);
    }

    private IdeStatusInfo ProbeVsCode(GhostCodeConfig config, IReadOnlyCollection<string> running, string tcSummary)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        if (config.CustomIdePaths.TryGetValue("VS Code", out var custom)) candidates.Add(custom);
        candidates.Add(Path.Combine(localApp, "Programs", "Microsoft VS Code", "Code.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"));

        var exe = FirstExisting(candidates);
        var ver = exe is not null ? _probe.GetFileVersion(exe) : null;
        return new IdeStatusInfo(
            Name: "Visual Studio Code",
            IsInstalled: exe is not null,
            IsRunning: running.Contains("Code"),
            ExecutablePath: exe,
            Version: ver,
            SupportedLanguages: "C, C++, Python",
            ToolchainSummary: tcSummary);
    }

    private IdeStatusInfo ProbePyCharm(
        GhostCodeConfig config,
        IReadOnlyCollection<string> running,
        IReadOnlyList<string> uninstallEntries,
        string pySummary)
    {
        var candidates = new List<string>();
        if (config.CustomIdePaths.TryGetValue("PyCharm", out var custom)) candidates.Add(custom);

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var root in new[]
        {
            Path.Combine(pf, "JetBrains"),
            Path.Combine(localApp, "Programs"),
            Path.Combine(localApp, "JetBrains", "Toolbox", "apps", "PyCharm-P"),
            Path.Combine(localApp, "JetBrains", "Toolbox", "apps", "PyCharm-C")
        })
        {
            foreach (var dir in _probe.EnumerateDirectories(root))
            {
                if (Path.GetFileName(dir).Contains("PyCharm", StringComparison.OrdinalIgnoreCase) || root.Contains("PyCharm", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(dir, "bin", "pycharm64.exe"));
                    candidates.Add(Path.Combine(dir, "ch-0", "bin", "pycharm64.exe"));
                }
            }
        }

        foreach (var entry in uninstallEntries)
        {
            var parts = entry.Split('|');
            if (parts.Length >= 2 && parts[0].Contains("PyCharm", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(parts[1]))
                    candidates.Add(Path.Combine(parts[1].Trim('"'), "bin", "pycharm64.exe"));
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                    candidates.Add(parts[2].Split(',')[0].Trim('"'));
            }
        }

        var exe = FirstExisting(candidates);
        var ver = exe is not null ? _probe.GetFileVersion(exe) : null;
        return new IdeStatusInfo(
            Name: "PyCharm",
            IsInstalled: exe is not null,
            IsRunning: running.Contains("pycharm64") || running.Contains("pycharm"),
            ExecutablePath: exe,
            Version: ver,
            SupportedLanguages: "Python",
            ToolchainSummary: exe is not null ? pySummary : $"Not installed (Python runtime: {pySummary})");
    }

    private IdeStatusInfo ProbeVisualStudio(GhostCodeConfig config, IReadOnlyCollection<string> running, string cppSummary)
    {
        var candidates = new List<string>();
        if (config.CustomIdePaths.TryGetValue("Visual Studio", out var custom)) candidates.Add(custom);

        foreach (var baseDir in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio")
        })
        {
            foreach (var yr in _probe.EnumerateDirectories(baseDir))
            {
                foreach (var ed in _probe.EnumerateDirectories(yr))
                    candidates.Add(Path.Combine(ed, "Common7", "IDE", "devenv.exe"));
            }
        }

        var exe = FirstExisting(candidates);
        var ver = exe is not null ? _probe.GetFileVersion(exe) : null;
        return new IdeStatusInfo(
            Name: "Visual Studio",
            IsInstalled: exe is not null,
            IsRunning: running.Contains("devenv"),
            ExecutablePath: exe,
            Version: ver,
            SupportedLanguages: "C, C++, C#, Python",
            ToolchainSummary: cppSummary);
    }

    private ToolchainInfo ProbeCCompilerCore(GhostCodeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CCompilerPath))
        {
            if (!_probe.FileExists(config.CCompilerPath))
                return new ToolchainInfo("C", "C Compiler (Configured Missing)", false, config.CCompilerPath, null, "", $"Configured compiler path not found: {config.CCompilerPath}");
            var verCfg = ProbeGccLikeVersion(config.CCompilerPath);
            if (verCfg is null)
                return new ToolchainInfo("C", "C Compiler (Invalid)", false, config.CCompilerPath, null, "", $"Configured compiler failed validation: {config.CCompilerPath}");
            var stdCfg = string.IsNullOrWhiteSpace(config.CStandard) ? "c11" : config.CStandard;
            return new ToolchainInfo("C", $"GCC {verCfg}", true, config.CCompilerPath, verCfg, $"-std={stdCfg} -O2", $"Configured: {config.CCompilerPath}");
        }

        foreach (var candidate in EnumerateCompilerCandidates("gcc.exe", "clang.exe"))
        {
            var ver = ProbeGccLikeVersion(candidate);
            if (ver is not null)
            {
                var std = string.IsNullOrWhiteSpace(config.CStandard) ? "c11" : config.CStandard;
                return new ToolchainInfo("C", $"GCC ({ver})", true, candidate, ver, $"-std={std} -O2", candidate);
            }
        }

        return new ToolchainInfo("C", "C Compiler (Not Installed)", false, null, null, "", "Install MinGW-w64 GCC or Clang, or configure path in Settings -> Development Tools.");
    }

    private ToolchainInfo ProbeCppCompilerCore(GhostCodeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CppCompilerPath))
        {
            if (!_probe.FileExists(config.CppCompilerPath))
                return new ToolchainInfo("C++", "C++ Compiler (Configured Missing)", false, config.CppCompilerPath, null, "", $"Configured compiler path not found: {config.CppCompilerPath}");
            var verCfg = ProbeGccLikeVersion(config.CppCompilerPath);
            if (verCfg is null)
                return new ToolchainInfo("C++", "C++ Compiler (Invalid)", false, config.CppCompilerPath, null, "", $"Configured compiler failed validation: {config.CppCompilerPath}");
            var stdCfg = ResolveBestCppStandard(config.CppCompilerPath, config.CppStandard);
            return new ToolchainInfo("C++", $"G++ ({verCfg})", true, config.CppCompilerPath, verCfg, $"-std={stdCfg} -O2", $"Configured: {config.CppCompilerPath}");
        }

        foreach (var candidate in EnumerateCompilerCandidates("g++.exe", "clang++.exe"))
        {
            var ver = ProbeGccLikeVersion(candidate);
            if (ver is not null)
            {
                var std = ResolveBestCppStandard(candidate, config.CppStandard);
                return new ToolchainInfo("C++", $"G++ ({ver})", true, candidate, ver, $"-std={std} -O2", candidate);
            }
        }

        return new ToolchainInfo("C++", "C++ Compiler (Not Installed)", false, null, null, "", "Install MinGW-w64 G++ or Clang++, or configure path in Settings -> Development Tools.");
    }

    private ToolchainInfo ProbePythonCore(GhostCodeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.PythonInterpreterPath))
        {
            if (!_probe.FileExists(config.PythonInterpreterPath))
                return new ToolchainInfo("Python", "Python (Configured Missing)", false, config.PythonInterpreterPath, null, "", $"Configured interpreter not found: {config.PythonInterpreterPath}");
            var (okCfg, exeCfg, verCfg) = ProbePythonExecutable(config.PythonInterpreterPath);
            if (!okCfg)
                return new ToolchainInfo("Python", "Python (Invalid)", false, config.PythonInterpreterPath, null, "", $"Configured Python interpreter failed validation: {config.PythonInterpreterPath}");
            return new ToolchainInfo("Python", $"Python {verCfg}", true, exeCfg, verCfg, "-u", $"Configured: {exeCfg}");
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        foreach (var pyRoot in new[]
        {
            Path.Combine(localApp, "Python"),
            Path.Combine(localApp, "Programs", "Python"),
            @"C:\Python"
        })
        {
            foreach (var f in _probe.EnumerateFiles(pyRoot, "python.exe", SearchOption.AllDirectories))
                candidates.Add(f);
        }

        candidates.AddRange(EnumerateOnPath("python.exe"));
        candidates.AddRange(EnumerateOnPath("python3.exe"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (ok, resolvedExe, ver) = ProbePythonExecutable(candidate);
            if (ok)
                return new ToolchainInfo("Python", $"Python {ver}", true, resolvedExe, ver, "-u", resolvedExe);
        }

        return new ToolchainInfo("Python", "Python 3 (Not Installed)", false, null, null, "", "Python is required to execute .py files. Download from https://www.python.org/downloads/ or select python.exe.");
    }

    private string ResolveBestCppStandard(string compilerPath, string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && !preferred.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return preferred;

        foreach (var std in new[] { "c++17", "c++14", "c++11" })
        {
            var (code, _, _) = _probe.RunProcess(compilerPath, new[] { $"-std={std}", "-x", "c++", "-", "-fsyntax-only" }, "int main(){return 0;}", 4000);
            if (code == 0)
                return std;
        }
        return "c++11";
    }

    private string? ProbeGccLikeVersion(string exePath)
    {
        var (code, stdout, stderr) = _probe.RunProcess(exePath, new[] { "--version" });
        if (code != 0)
            return null;
        var first = (stdout + "\n" + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(first) ? "Available" : first;
    }

    private (bool Ok, string ResolvedExe, string Version) ProbePythonExecutable(string exePath)
    {
        var (code, stdout, _) = _probe.RunProcess(exePath, new[] { "-c", "import sys; print(sys.executable); print(sys.version.split()[0])" });
        if (code != 0)
            return (false, exePath, string.Empty);

        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var realExe = lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0]) ? lines[0].Trim() : exePath;
        var ver = lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1].Trim() : "3";
        return (true, realExe, ver);
    }

    private IEnumerable<string> EnumerateCompilerCandidates(params string[] exeNames)
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] knownDirs =
        {
            Path.Combine(pf86, "Dev-Cpp", "MinGW64", "bin"),
            Path.Combine(pf, "Dev-Cpp", "MinGW64", "bin"),
            @"C:\msys64\mingw64\bin",
            @"C:\msys64\ucrt64\bin",
            @"C:\MinGW\bin",
            @"C:\Program Files\LLVM\bin"
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in exeNames)
        {
            foreach (var dir in knownDirs)
            {
                var p = Path.Combine(dir, exe);
                if (seen.Add(p) && _probe.FileExists(p))
                    yield return p;
            }
            foreach (var p in EnumerateOnPath(exe))
            {
                if (seen.Add(p) && _probe.FileExists(p))
                    yield return p;
            }
        }
    }

    private IEnumerable<string> EnumerateOnPath(string exeName)
    {
        var raw = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim().Trim('"');
            if (string.IsNullOrEmpty(trimmed)) continue;
            var candidate = Path.Combine(trimmed, exeName);
            if (_probe.FileExists(candidate))
                yield return candidate;
        }
    }

    private string? FirstExisting(IEnumerable<string> paths) =>
        paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && _probe.FileExists(p));
}
