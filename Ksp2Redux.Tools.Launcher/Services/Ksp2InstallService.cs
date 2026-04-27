using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IKsp2InstallService
{
    Ksp2Install? Ksp2 { get; }
    Ksp2InstallEntry? ActiveEntry { get; }
    IReadOnlyList<Ksp2InstallEntry> Entries { get; }
    event EventHandler? ActiveInstallChanged;
    event EventHandler? InstallsChanged;

    void TryLoadKsp2Install();

    /// <summary>
    /// Returns the active install the first time it's requested per session, null otherwise.
    /// </summary>
    Ksp2Install? ClaimActiveInstallForFirstCheckThisSession();

    Ksp2InstallEntry AddInstall(string exePath, string? name = null);
    void RemoveInstall(Guid id);
    void RenameInstall(Guid id, string newName);
    void SetActiveInstall(Guid id);
    void UpdateActiveReleaseChannel(string channel);
    void UpdateActiveLastInstalledVersion(GameVersion? version);
    void UpdateInstallReleaseChannel(Guid id, string channel);
    void UpdateInstallExePath(Guid id, string newExePath);
    void UpdateInstallDisableGraphicsJobs(Guid id, bool value);
    void NotifyInstallChanged(Guid id);

    void ApplyActiveInstallBootConfig();
}

public class Ksp2InstallService(ILauncherConfigService launcherConfigService, IFileSystem fileSystem, IModuleDefinitionService moduleDefinitionService) : IKsp2InstallService
{
    private readonly HashSet<Guid> _checkedThisSession = new();

    public Ksp2Install? Ksp2 { get; private set; }

    public Ksp2InstallEntry? ActiveEntry =>
        launcherConfigService.Config.ActiveKsp2InstallId is { } id
            ? launcherConfigService.Config.Ksp2Installs.FirstOrDefault(e => e.Id == id)
            : null;

    public IReadOnlyList<Ksp2InstallEntry> Entries => launcherConfigService.Config.Ksp2Installs;

    public event EventHandler? ActiveInstallChanged;
    public event EventHandler? InstallsChanged;

    public void TryLoadKsp2Install()
    {
        var path = ActiveEntry?.ExePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            Ksp2 = new(fileSystem, moduleDefinitionService, path);
        }
        else
        {
            Ksp2 = null;
        }
    }

    public Ksp2Install? ClaimActiveInstallForFirstCheckThisSession()
    {
        if (ActiveEntry is not { } entry) return null;
        if (!_checkedThisSession.Add(entry.Id)) return null;
        TryLoadKsp2Install();
        return Ksp2;
    }

    public Ksp2InstallEntry AddInstall(string exePath, string? name = null)
    {
        var entry = new Ksp2InstallEntry
        {
            ExePath = exePath,
            Name = string.IsNullOrWhiteSpace(name) ? LauncherConfigService.DeriveDefaultInstallName(exePath) : name!,
            ReleaseChannel = launcherConfigService.Config.Ksp2Installs.Count > 0
                ? (ActiveEntry?.ReleaseChannel ?? "beta")
                : (string.IsNullOrEmpty(launcherConfigService.Config.ReleaseChannel) ? "beta" : launcherConfigService.Config.ReleaseChannel),
        };
        launcherConfigService.Config.Ksp2Installs.Add(entry);

        var becomesActive = launcherConfigService.Config.Ksp2Installs.Count == 1;
        if (becomesActive)
        {
            launcherConfigService.Config.ActiveKsp2InstallId = entry.Id;
        }
        launcherConfigService.Save();

        InstallsChanged?.Invoke(this, EventArgs.Empty);
        if (becomesActive)
        {
            TryLoadKsp2Install();
            ApplyActiveInstallBootConfig();
            ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
        }
        return entry;
    }

    public void RemoveInstall(Guid id)
    {
        var entry = launcherConfigService.Config.Ksp2Installs.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;

        var wasActive = launcherConfigService.Config.ActiveKsp2InstallId == id;
        launcherConfigService.Config.Ksp2Installs.Remove(entry);

        if (wasActive)
        {
            launcherConfigService.Config.ActiveKsp2InstallId =
                launcherConfigService.Config.Ksp2Installs.Count > 0
                    ? launcherConfigService.Config.Ksp2Installs[0].Id
                    : null;
        }
        launcherConfigService.Save();

        InstallsChanged?.Invoke(this, EventArgs.Empty);
        if (wasActive)
        {
            TryLoadKsp2Install();
            ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RenameInstall(Guid id, string newName)
    {
        var entry = launcherConfigService.Config.Ksp2Installs.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;
        if (entry.Name == newName) return;
        entry.Name = newName;
        launcherConfigService.Save();
        InstallsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetActiveInstall(Guid id)
    {
        if (launcherConfigService.Config.ActiveKsp2InstallId == id) return;
        if (launcherConfigService.Config.Ksp2Installs.All(e => e.Id != id)) return;
        launcherConfigService.Config.ActiveKsp2InstallId = id;
        launcherConfigService.Save();
        TryLoadKsp2Install();
        ApplyActiveInstallBootConfig();
        ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateActiveReleaseChannel(string channel)
    {
        if (ActiveEntry is not { } entry) return;
        if (entry.ReleaseChannel == channel) return;
        entry.ReleaseChannel = channel;
        launcherConfigService.Save();
        ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateActiveLastInstalledVersion(GameVersion? version)
    {
        if (ActiveEntry is not { } entry) return;
        entry.LastInstalledVersion = version;
        launcherConfigService.Save();
    }

    public void UpdateInstallReleaseChannel(Guid id, string channel)
    {
        var entry = launcherConfigService.Config.Ksp2Installs.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;
        if (entry.ReleaseChannel == channel) return;
        entry.ReleaseChannel = channel;
        launcherConfigService.Save();
        if (id == launcherConfigService.Config.ActiveKsp2InstallId)
            ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
        else
            InstallsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyInstallChanged(Guid id)
    {
        if (launcherConfigService.Config.Ksp2Installs.All(e => e.Id != id)) return;
        launcherConfigService.Save();
        if (id == launcherConfigService.Config.ActiveKsp2InstallId)
            ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
        else
            InstallsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateInstallExePath(Guid id, string newExePath)
    {
        var entry = launcherConfigService.Config.Ksp2Installs.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;
        if (entry.ExePath == newExePath) return;
        entry.ExePath = newExePath;
        launcherConfigService.Save();
        if (id == launcherConfigService.Config.ActiveKsp2InstallId)
        {
            TryLoadKsp2Install();
            ApplyActiveInstallBootConfig();
            ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            InstallsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UpdateInstallDisableGraphicsJobs(Guid id, bool value)
    {
        var entry = launcherConfigService.Config.Ksp2Installs.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;
        if (entry.DisableGraphicsJobs == value) return;
        entry.DisableGraphicsJobs = value;
        launcherConfigService.Save();
        if (id == launcherConfigService.Config.ActiveKsp2InstallId)
        {
            ApplyActiveInstallBootConfig();
            ActiveInstallChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            InstallsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyActiveInstallBootConfig()
    {
        if (ActiveEntry is not { } entry) return;
        if (Ksp2 is not { IsValid: true } ksp2) return;
        var bootConfigPath = fileSystem.Path.Combine(ksp2.InstallDir, "KSP2_x64_Data", "boot.config");

        const string Key = "gfx-enable-gfx-jobs";
        var desiredLine = $"{Key}={(entry.DisableGraphicsJobs ? "0" : "1")}";

        try
        {
            if (!fileSystem.File.Exists(bootConfigPath)) return;
            var lines = fileSystem.File.ReadAllLines(bootConfigPath).ToList();
            var idx = lines.FindIndex(l => l.StartsWith(Key + "=", StringComparison.Ordinal));
            if (idx >= 0)
            {
                if (lines[idx] == desiredLine) return;
                lines[idx] = desiredLine;
            }
            else
            {
                lines.Add(desiredLine);
            }
            fileSystem.File.WriteAllLines(bootConfigPath, lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
