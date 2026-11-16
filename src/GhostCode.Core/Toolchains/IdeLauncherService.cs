using System.Diagnostics;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;

namespace GhostCode.Core.Toolchains;

public sealed record IdeLaunchResult(
    bool Success,
    string? IdeName,
    string? ExecutablePath,
    string? OpenedFilePath,
    bool WasWorkingCopy,
    string Message);

public interface IIdeProcessStarter
{
    bool Start(string executablePath, IReadOnlyList<string> arguments);
}

public sealed class DefaultIdeProcessStarter : IIdeProcessStarter
{
    public bool Start(string executablePath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        return Process.Start(psi) is not null;
    }
}

/// <summary>
/// Opens original source files or isolated verified working copies of historical archive versions
/// inside discovered IDEs (Dev-C++, VS Code, PyCharm, Visual Studio) without risking immutable backups.
/// </summary>
public sealed class IdeLauncherService
{
    private readonly GhostCodeConfig _config;
    private readonly ToolchainDiscoveryService _discovery;
    private readonly LosslessArchiver _archiver;
    private readonly IIdeProcessStarter _starter;

    public IdeLauncherService(
        GhostCodeConfig config,
        ToolchainDiscoveryService discovery,
        LosslessArchiver archiver,
        IIdeProcessStarter? starter = null)
    {
        _config = config;
        _discovery = discovery;
        _archiver = archiver;
        _starter = starter ?? new DefaultIdeProcessStarter();
    }

    public IdeStatusInfo? ResolvePreferredIde(string fileNameOrPath, string? explicitIdeName = null)
    {
        var ides = _discovery.DiscoverIdes(_config);
        if (!string.IsNullOrWhiteSpace(explicitIdeName) && !explicitIdeName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var direct = ides.FirstOrDefault(i => i.IsInstalled && i.Name.Equals(explicitIdeName, StringComparison.OrdinalIgnoreCase));
            if (direct is not null)
                return direct;
        }

        var lang = SourceAnalyzer.LanguageFromPath(fileNameOrPath);
        string configured = lang switch
        {
            SourceLanguage.Python => _config.DefaultIdeForPython,
            SourceLanguage.C => _config.DefaultIdeForC,
            SourceLanguage.Cpp or SourceLanguage.Header => _config.DefaultIdeForCpp,
            _ => "Auto"
        };

        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var matched = ides.FirstOrDefault(i => i.IsInstalled && i.Name.Equals(configured, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
                return matched;
        }

        // Language-aware defaults among installed IDEs
        string[] order = lang == SourceLanguage.Python
            ? new[] { "PyCharm", "Visual Studio Code", "Visual Studio", "Dev-C++" }
            : new[] { "Visual Studio Code", "Dev-C++", "Visual Studio", "PyCharm" };

        foreach (var name in order)
        {
            var found = ides.FirstOrDefault(i => i.IsInstalled && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found;
        }

        return null;
    }

    public async Task<IdeLaunchResult> OpenVersionInIdeAsync(
        ArchiveVersionRecord version,
        bool preferOriginalSource = false,
        string? explicitIdeName = null,
        string? workingCopiesRootOverride = null,
        CancellationToken ct = default)
    {
        var ide = ResolvePreferredIde(version.FileName, explicitIdeName);
        if (ide is null || string.IsNullOrWhiteSpace(ide.ExecutablePath))
        {
            return new IdeLaunchResult(false, explicitIdeName, null, null, false,
                "No compatible installed IDE found (checked Dev-C++, VS Code, PyCharm, Visual Studio).");
        }

        string fileToOpen;
        bool isWorkingCopy;
        if (preferOriginalSource && File.Exists(version.SourcePath))
        {
            fileToOpen = version.SourcePath;
            isWorkingCopy = false;
        }
        else
        {
            if (!version.IsLocalAvailable)
            {
                return new IdeLaunchResult(false, ide.Name, ide.ExecutablePath, null, false,
                    "This version is Cloud-Only. Download it locally before opening in an IDE.");
            }

            var exactBytes = await _archiver.ReadAndVerifyVersionBytesAsync(version.Id, ct);
            var root = workingCopiesRootOverride ?? Path.Combine(KnownFoldersResolver.ResolveAppDataDir(), "WorkingCopies");
            Directory.CreateDirectory(root);

            var stem = Path.GetFileNameWithoutExtension(version.FileName);
            if (string.IsNullOrWhiteSpace(stem)) stem = "solution";
            var ext = Path.GetExtension(version.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".txt";
            var shortSha = version.Sha256.Length >= 8 ? version.Sha256[..8] : version.Sha256;
            fileToOpen = Path.Combine(root, $"{stem}_v{version.VersionNum}_{shortSha}_working{ext}");

            await LosslessArchiver.WriteReplaceableCopyAtomicallyAsync(fileToOpen, exactBytes, ct);
            isWorkingCopy = true;
        }

        try
        {
            bool started = _starter.Start(ide.ExecutablePath, new[] { fileToOpen });
            if (!started)
            {
                return new IdeLaunchResult(false, ide.Name, ide.ExecutablePath, fileToOpen, isWorkingCopy,
                    $"OS declined to launch {ide.Name}.");
            }

            return new IdeLaunchResult(true, ide.Name, ide.ExecutablePath, fileToOpen, isWorkingCopy,
                isWorkingCopy
                    ? $"Opened isolated working copy in {ide.Name}: {Path.GetFileName(fileToOpen)}"
                    : $"Opened original file in {ide.Name}: {fileToOpen}");
        }
        catch (Exception ex)
        {
            return new IdeLaunchResult(false, ide.Name, ide.ExecutablePath, fileToOpen, isWorkingCopy,
                $"Failed to launch {ide.Name}: {ex.Message}");
        }
    }
}
