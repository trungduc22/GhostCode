using System.Runtime.InteropServices;
using System.Text;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Hotkeys;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;

namespace GhostCode.Tests;

public sealed class ExpansionP1ProductivityTests : IDisposable
{
    private readonly string _root;

    public ExpansionP1ProductivityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_ExpP1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeToolchainProbe : IToolchainEnvironmentProbe
    {
        private readonly HashSet<string> _existingFiles;

        public FakeToolchainProbe(params string[] existingFiles)
        {
            _existingFiles = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        }

        public bool FileExists(string path) => _existingFiles.Contains(path);
        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption) => Array.Empty<string>();
        public IEnumerable<string> EnumerateDirectories(string directory) => Array.Empty<string>();
        public IReadOnlyCollection<string> GetRunningProcessNames() => Array.Empty<string>();
        public string? GetFileVersion(string path) => "1.0.0-test";
        public string? ReadRegistryString(string fullKeyPath, string valueName) => null;
        public IEnumerable<string> ReadRegistryUninstallDisplayNames() => Array.Empty<string>();
        public (int ExitCode, string Stdout, string Stderr) RunProcess(string executable, IReadOnlyList<string> args, string? stdinText = null, int timeoutMs = 4000) => (0, "", "");
    }

    private sealed class RecordingIdeStarter : IIdeProcessStarter
    {
        public List<(string Exe, IReadOnlyList<string> Args)> Launches { get; } = new();
        public bool ShouldSucceed { get; set; } = true;

        public bool Start(string executablePath, IReadOnlyList<string> arguments)
        {
            Launches.Add((executablePath, arguments.ToArray()));
            return ShouldSucceed;
        }
    }

    [Fact]
    public void SourceDiffEngine_ComputesSideBySideAndUnifiedDiff_Accurately()
    {
        var left = "int main() {\r\n    int a = 1;\r\n    int b = 2;\r\n    return a;\r\n}\r\n";
        var right = "int main() {\n    int a = 10;\n    int b = 2;\n    int c = 3;\n    return a + c;\n}\n";

        var diff = SourceDiffEngine.ComputeDiff(left, right, "v1.cpp", "v2.cpp");

        Assert.True(diff.ModifiedLines >= 1);
        Assert.True(diff.AddedLines >= 1);
        Assert.Contains("--- v1.cpp", diff.UnifiedDiffText);
        Assert.Contains("+++ v2.cpp", diff.UnifiedDiffText);
        Assert.Contains("-     int a = 1;", diff.UnifiedDiffText);
        Assert.Contains("+     int a = 10;", diff.UnifiedDiffText);
        Assert.Contains("+     int c = 3;", diff.UnifiedDiffText);
        Assert.Contains(diff.SideBySideLines, p => p.Kind == DiffLineKind.Modified);
    }

    [Fact]
    public void SourceDiffEngine_HandlesLargeFilesWithoutMatrixOverflow()
    {
        var sb1 = new StringBuilder();
        var sb2 = new StringBuilder();
        for (int i = 1; i <= 1700; i++)
        {
            sb1.AppendLine($"int val_{i} = {i};");
            sb2.AppendLine(i == 850 ? "int val_850 = 999999;" : $"int val_{i} = {i};");
        }
        sb2.AppendLine("// extra tail line");

        var diff = SourceDiffEngine.ComputeDiff(sb1.ToString(), sb2.ToString(), "big1.cpp", "big2.cpp");

        Assert.Equal(1, diff.ModifiedLines);
        Assert.Equal(1, diff.AddedLines);
        Assert.Equal(0, diff.RemovedLines);
        Assert.Contains("+ // extra tail line", diff.UnifiedDiffText);
    }

    [Fact]
    public async Task IdeLauncher_HistoricalVersion_WritesIsolatedWorkingCopy_NeverModifiesOriginal()
    {
        var watchDir = Path.Combine(_root, "Watched");
        var archiveDir = Path.Combine(_root, "Archive");
        var readyDir = Path.Combine(_root, "Ready");
        var workingCopies = Path.Combine(_root, "WorkingCopies");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var fakeVsCode = @"C:\Program Files\Fake VS Code\Code.exe";
        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db"),
            CustomIdePaths = new Dictionary<string, string> { ["VS Code"] = fakeVsCode }
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var sourceFile = Path.Combine(watchDir, "My Solution 1.cpp");
        const string v1Source = "#include <iostream>\nint main() { int x = 42; std::cout << x << \"\\n\"; return 0; }\n";
        const string v2Source = "#include <iostream>\nint main() { int x = 99; std::cout << (x * 2) << \"\\n\"; return 0; }\n";

        await File.WriteAllTextAsync(sourceFile, v1Source, Encoding.UTF8);
        var r1 = await archiver.ProcessFileSaveAsync(sourceFile, Encoding.UTF8.GetBytes(v1Source), null);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r1.Outcome);

        await File.WriteAllTextAsync(sourceFile, v2Source, Encoding.UTF8);
        var r2 = await archiver.ProcessFileSaveAsync(sourceFile, Encoding.UTF8.GetBytes(v2Source), null);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r2.Outcome);

        var starter = new RecordingIdeStarter();
        var discovery = new ToolchainDiscoveryService(new FakeToolchainProbe(fakeVsCode));
        var launcher = new IdeLauncherService(cfg, discovery, archiver, starter);

        // Open historical v1 -> must create an isolated working copy inside WorkingCopies
        var launchHistorical = await launcher.OpenVersionInIdeAsync(
            r1.Version!,
            preferOriginalSource: false,
            explicitIdeName: "Visual Studio Code",
            workingCopiesRootOverride: workingCopies);

        Assert.True(launchHistorical.Success);
        Assert.True(launchHistorical.WasWorkingCopy);
        Assert.NotNull(launchHistorical.OpenedFilePath);
        Assert.StartsWith(workingCopies, launchHistorical.OpenedFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(v1Source, await File.ReadAllTextAsync(launchHistorical.OpenedFilePath!));
        Assert.Equal(v2Source, await File.ReadAllTextAsync(sourceFile));
        Assert.Single(starter.Launches);
        Assert.Equal(fakeVsCode, starter.Launches[0].Exe);
        Assert.Equal(launchHistorical.OpenedFilePath, starter.Launches[0].Args[0]);

        // Open current version with preferOriginalSource: true -> opens original source file directly
        var launchOriginal = await launcher.OpenVersionInIdeAsync(
            r2.Version!,
            preferOriginalSource: true,
            explicitIdeName: "Visual Studio Code",
            workingCopiesRootOverride: workingCopies);

        Assert.True(launchOriginal.Success);
        Assert.False(launchOriginal.WasWorkingCopy);
        Assert.Equal(sourceFile, launchOriginal.OpenedFilePath);
    }

    [Fact]
    public async Task IdeLauncher_MissingIdeOrCloudOnlyVersion_ReturnsStructuredFailureWithoutThrowing()
    {
        var archiveDir = Path.Combine(_root, "Archive2");
        var readyDir = Path.Combine(_root, "Ready2");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghost.db")
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        var dummyRecord = new ArchiveVersionRecord(
            Id: "v_cloud",
            VersionNum: 1,
            SourcePath: @"C:\nonexistent\test.cpp",
            ArchivePath: Path.Combine(archiveDir, "missing.cpp"),
            ReadyPath: null,
            FileName: "test.cpp",
            Sha256: "abcdef0123456789",
            NormHash: "norm",
            SizeBytes: 120,
            ProblemCode: "J01001",
            ProblemTitle: "Sample",
            Platform: "Ptit",
            CreatedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsLocalAvailable: false,
            DriveFileId: "rem1");

        var starter = new RecordingIdeStarter();
        // 1. Missing IDE on host
        var noIdeLauncher = new IdeLauncherService(cfg, new ToolchainDiscoveryService(new FakeToolchainProbe()), archiver, starter);
        var noIdeResult = await noIdeLauncher.OpenVersionInIdeAsync(dummyRecord);
        Assert.False(noIdeResult.Success);
        Assert.Contains("No compatible installed IDE", noIdeResult.Message);
        Assert.Empty(starter.Launches);

        // 2. Installed IDE, but Cloud-Only version
        const string fakePyCharm = @"C:\Fake\pycharm64.exe";
        cfg.CustomIdePaths["PyCharm"] = fakePyCharm;
        var hasIdeLauncher = new IdeLauncherService(cfg, new ToolchainDiscoveryService(new FakeToolchainProbe(fakePyCharm)), archiver, starter);
        var cloudOnlyResult = await hasIdeLauncher.OpenVersionInIdeAsync(dummyRecord, explicitIdeName: "PyCharm");
        Assert.False(cloudOnlyResult.Success);
        Assert.Contains("Cloud-Only", cloudOnlyResult.Message);
        Assert.Empty(starter.Launches);
    }

    [Fact]
    public void GlobalHotkeyParser_ParsesValidChordsAndRejectsUnsafeInputs()
    {
        Assert.True(GlobalHotkeyParser.TryParse("Ctrl+Shift+F9", out var mods, out var vk));
        Assert.Equal(GlobalHotkeyParser.ModControl | GlobalHotkeyParser.ModShift, mods);
        Assert.Equal((uint)(0x70 + 8), vk); // VK_F9 = 0x78

        Assert.True(GlobalHotkeyParser.TryParse("Win+Alt+Space", out var mods2, out var vk2));
        Assert.Equal(GlobalHotkeyParser.ModWin | GlobalHotkeyParser.ModAlt, mods2);
        Assert.Equal(0x20u, vk2);

        Assert.True(GlobalHotkeyParser.TryParse("F11", out var modsF11, out var vkF11));
        Assert.Equal(0u, modsF11);
        Assert.Equal((uint)(0x70 + 10), vkF11);

        Assert.False(GlobalHotkeyParser.TryParse("", out _, out _));
        Assert.False(GlobalHotkeyParser.TryParse("A", out _, out _)); // unsafe bare printable key
        Assert.False(GlobalHotkeyParser.TryParse("Ctrl+A+B", out _, out _));
        Assert.False(GlobalHotkeyParser.TryParse("Ctrl+UnknownKey", out _, out _));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [Fact]
    public void Win32RegisterHotKey_RegistersDetectsDuplicateConflictAndUnregisters()
    {
        if (!OperatingSystem.IsWindows())
            return;

        uint mods = GlobalHotkeyParser.ModControl | GlobalHotkeyParser.ModShift | GlobalHotkeyParser.ModAlt | 0x4000u; // MOD_NOREPEAT
        uint chosenVk = 0;
        const int id1 = 0x7A01;
        const int id2 = 0x7A02;

        try
        {
            for (int f = 19; f <= 24; f++)
            {
                uint vk = (uint)(0x70 + f - 1);
                if (RegisterHotKey(IntPtr.Zero, id1, mods, vk))
                {
                    chosenVk = vk;
                    break;
                }
            }

            Assert.NotEqual(0u, chosenVk);

            bool second = RegisterHotKey(IntPtr.Zero, id2, mods, chosenVk);
            int err = Marshal.GetLastWin32Error();
            Assert.False(second);
            Assert.Equal(1409, err); // ERROR_HOTKEY_ALREADY_REGISTERED
        }
        finally
        {
            if (chosenVk != 0)
                Assert.True(UnregisterHotKey(IntPtr.Zero, id1));
            UnregisterHotKey(IntPtr.Zero, id2);
        }
    }
}
