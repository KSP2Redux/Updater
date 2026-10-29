using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public interface IInstallPlanService
{
    void Describe(InstallPlan installPlan, Action<LogMessage> log);

    Task ApplyToFolder(InstallPlan installPlan, string install, Action<LogMessage> log,
        Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct);
}

public class InstallPlanService(IFileSystem fileSystem, ICacheService cacheService, IEnvironmentProvider environmentProvider,
    IAssemblyService assemblyService, IModuleDefinitionService moduleDefinitionService, IZipFileService zipFileService,
    IDiskSpaceService diskSpaceService, IPatchDownloadService patchDownloadService,
    ILauncherConfigService launcherConfigService, IBundleConversionService? bundleConversionService = null,
    IInstallFileLockService? installFileLockService = null) : IInstallPlanService
{
    private const string EPIC_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.epic-prepatch.patch";
    private const string STEAM_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.steam-prepatch.patch";
    private const string PORTABLE_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.portable-prepatch.patch";
    private const long DISK_SAFETY_BYTES = 512L * 1024 * 1024;

    // Extra headroom on top of the raw estimate below, since patch application briefly holds both the
    // old and new copy of a changed file, and the download itself needs to sit on disk before it's applied.
    private const double RequiredSpaceSafetyMargin = 1.2;

    // The plan's lines reach the install log from instance and static members alike, so they read the shared service.
    private static ILocalizationService Loc => LocalizationService.Default;

    public void Describe(InstallPlan installPlan, Action<LogMessage> log)
    {
        log(Loc.Message("Install.PlanMade", installPlan.Steps.Count));
        for (var i = 0; i < installPlan.Steps.Count; i++)
        {
            switch (installPlan.Steps[i].Action)
            {
                case InstallPlanAction.Uninstall:
                    log(Loc.Message("Install.Step.Uninstall", i + 1));
                    break;
                case InstallPlanAction.RevertToStock:
                    log(Loc.Message("Install.Step.RevertToStock", i + 1));
                    break;
                case InstallPlanAction.Prepatch:
                    log(Loc.Message("Install.Step.Prepatch", i + 1));
                    break;
                case InstallPlanAction.ApplyPatchFile:
                    log(Loc.Message("Install.Step.ApplyPatchFile", i + 1, installPlan.Steps[i].ArgumentDescription));
                    break;
                case InstallPlanAction.CopyFrom:
                    log(LogMessage.Literal($"Step {i + 1}: (This should be impossible rn)"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public async Task ApplyToFolder(InstallPlan installPlan, string install, Action<LogMessage> log,
        Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct)
    {
        // Ksp2Patch reports plain text, which is never translated.
        Action<string> patchLog = text => log(LogMessage.Literal(text));

        // Patching, rollback and bundle conversion rewrite files in place. A file another program holds open (a
        // running KSP2, or a Unity editor that loaded the game's bundles) fails partway through and leaves old and
        // new files mixed, so nothing starts while any is held.
        if (installFileLockService is not null)
        {
            log(Loc.Message("Install.CheckingFiles"));
            await Task.Run(() => installFileLockService.EnsureNoFilesInUse(install), ct);
        }

        EnsureEnoughDiskSpace(installPlan, install, log);
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var requestSteps = installPlan.Steps
            .Select((step, index) => (step, index))
            .Where(item => item.step.DownloadRequest is not null)
            .ToList();
        var downloadTasks = patchDownloadService.EnqueueAll(
            requestSteps.Select(item => item.step.DownloadRequest!).ToList(),
            launcherConfigService.Config.PatchDownloadSource,
            launcherConfigService.Config.MaxConcurrentChunkDownloads,
            log,
            downloadProgress,
            downloadCts.Token);
        var downloadByStep = requestSteps
            .Select((item, index) => (item.index, task: downloadTasks[index]))
            .ToDictionary(item => item.index, item => item.task);

        var i = 0;
        bool mutationStarted = false;
        foreach (var step in installPlan.Steps)
        {
            try
            {
                switch (step.Action)
                {
                case InstallPlanAction.Uninstall:
                    log(Loc.Message("Install.Uninstalling"));
                    // The bundle folder is not in the snapshot, and the restore deletes the journal, so undo
                    // the bundle conversion first. A bundle it could not put back would stay converted for good
                    // once the journal is gone, so a failed revert stops here with the Redux install intact.
                    await RevertBundleConversion(install, log, ct);
                    mutationStarted = true;
                    cacheService.RecursivelyRestoreCache(install);
                    break;
                case InstallPlanAction.RevertToStock:
                    // When patching follows, the conversion after it brings the bundles up to the new shader
                    // manifest, and the journal survives the repatch, so only a plain revert undoes it here.
                    if (!installPlan.Steps.Any(planned =>
                            planned.Action is InstallPlanAction.Prepatch or InstallPlanAction.ApplyPatchFile))
                    {
                        await RevertBundleConversion(install, log, ct);
                    }

                    if (fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
                    {
                        log(Loc.Message("Install.RevertingForRepatch"));
                        mutationStarted = true;
                        cacheService.RecursivelyRestoreCache(install, true);
                    }
                    else
                    {
                        log(Loc.Message("Install.NoRevertNeeded"));
                    }
                    break;
                case InstallPlanAction.Prepatch:
                {
                    log(Loc.Message("Install.ApplyingPrepatch"));
                    mutationStarted = true;
                    if (fileSystem.File.Exists(fileSystem.Path.Combine(install, "winhttp.dll")))
                    {
                        log(Loc.Message("Install.DeletingModloader"));
                        fileSystem.File.Delete(fileSystem.Path.Combine(install, "winhttp.dll"));
                    }
                    
                    if (!fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
                    {
                        cacheService.RecursivelyCreateCache(install);
                    }

                    var patchFile = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), fileSystem.Path.GetRandomFileName());
                    var exe = fileSystem.Path.Combine(install, Ksp2Install.KSP2_EXE_NAME);
                    var ksp2Install = new Ksp2Install(fileSystem, moduleDefinitionService, exe);
                    switch (ksp2Install.Distribution)
                    {
                        case Distribution.Portable:
                        {
                            log(Loc.Message("Install.Prepatch.Portable"));
                            await using var stream = assemblyService.GetManifestResourceStream(PORTABLE_PREPATCH_NAME);
                            await using var fstream =
                                fileSystem.FileStream.New(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Steam:
                        {
                            log(Loc.Message("Install.Prepatch.Steam"));
                            await using var stream = assemblyService.GetManifestResourceStream(STEAM_PREPATCH_NAME);
                            await using var fstream =
                                fileSystem.FileStream.New(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Epic:
                        {
                            log(Loc.Message("Install.Prepatch.Epic"));
                            await using var stream = assemblyService.GetManifestResourceStream(EPIC_PREPATCH_NAME);
                            await using var fstream =
                                fileSystem.FileStream.New(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Redux:
                            throw Loc.Message("Install.Error.PrepatchOnRedux").ToException(message => new Exception(message));
                        case Distribution.Prepatched:
                            // Shouldn't happen, but *could*
                            goto delete_patch;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    using (var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchFile))
                    {
                        await patch.AsyncApply(environmentProvider, install, install, patchLog, patchLog);
                    }

                    delete_patch:
                    fileSystem.File.Delete(patchFile);
                    break;
                }

                case InstallPlanAction.ApplyPatchFile:
                {
                    string patchPath;
                    if (downloadByStep.TryGetValue(i, out var downloadTask))
                    {
                        patchPath = await downloadTask;
                    }
                    else
                    {
                        patchPath = await step.Argument!(log, downloadProgress, ct);
                    }

                    mutationStarted = true;
                    using (var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchPath))
                    {
                        await patch.AsyncApply(environmentProvider, install, install, patchLog, patchLog);
                    }
                    if (step.DeleteAfter && fileSystem.File.Exists(patchPath))
                    {
                        log(Loc.Message("Install.CleaningPatch", patchPath));
                        try { fileSystem.File.Delete(patchPath); }
                        catch (Exception ex) { log(Loc.Message("Install.PatchDeleteFailed", patchPath, ex.ToLogMessage())); }
                    }
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception ex)
            {
                downloadCts.Cancel();
                if (!mutationStarted)
                {
                    throw;
                }
                RollbackOrRethrow(install, log, ex);
            }
            stepsProgress(++i, installPlan.Steps.Count);
            if (i < installPlan.Steps.Count)
            {
                try
                {
                    await Task.Delay(250, ct);
                }
                catch (OperationCanceledException ex) when (mutationStarted)
                {
                    RollbackOrRethrow(install, log, ex);
                }
            }
        }

        bool patched = installPlan.Steps.Any(step =>
            step.Action is InstallPlanAction.Prepatch or InstallPlanAction.ApplyPatchFile);
        if (patched && bundleConversionService is not null)
        {
            // Some bundles converted and others not is a half-finished install, and reporting it as installed hid
            // that from players. So a failed or cancelled conversion rolls the install back like a failed patch.
            try
            {
                await Task.Run(() => bundleConversionService.Convert(install, log, ct), ct);
            }
            catch (Exception ex)
            {
                RollbackOrRethrow(install, log, ex);
            }
        }
    }

    private async Task RevertBundleConversion(string install, Action<LogMessage> log, CancellationToken ct)
    {
        if (bundleConversionService is null)
        {
            return;
        }

        await Task.Run(() => bundleConversionService.Revert(install, log, ct), ct);
    }

    // Runs during a rollback, which may follow a cancellation, so it does not take the plan's token.
    private void TryRevertBundleConversion(string install, Action<LogMessage> log)
    {
        if (bundleConversionService is null)
        {
            return;
        }

        try
        {
            bundleConversionService.Revert(install, log, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log(Loc.Message("Install.BundleRevertFailed", ex.ToLogMessage()));
        }
    }

    /// <summary>
    /// Called when a Prepatch/ApplyPatchFile step throws partway through. If a known-good snapshot
    /// (uninstall.zip) exists, restores it so a corrupt patch or a mid-write failure (e.g. disk full)
    /// doesn't leave the install half-patched, then throws <see cref="InstallFailedException"/> describing
    /// what happened. Note this restores the whole install to the last snapshot, which may discard other
    /// patches already applied earlier in the same plan - still strictly better than a half-patched install.
    /// If there is no snapshot to restore (nothing has been written to the install dir yet), rethrows the
    /// original exception unchanged.
    /// </summary>
    private void RollbackOrRethrow(string install, Action<LogMessage> log, Exception original)
    {
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
        {
            ExceptionDispatchInfo.Capture(original).Throw();
            return; // unreachable, keeps the compiler happy
        }

        log(Loc.Message("Install.StepFailed", original.ToLogMessage()));

        // The snapshot is a stock install, and the restore leaves the bundles in StreamingAssets as they are.
        // Converted bundles draw magenta in stock and in Redux builds from before the URP port, so the
        // conversion is undone too. The next install converts again if its build needs it.
        TryRevertBundleConversion(install, log);

        Exception? rollbackFailure = null;
        try
        {
            cacheService.RecursivelyRestoreCache(install, true);
        }
        catch (Exception rollbackEx)
        {
            rollbackFailure = rollbackEx;
        }

        if (rollbackFailure is null)
        {
            log(Loc.Message("Install.RolledBack"));
            if (original is OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(original).Throw();
            }
            throw Loc.Message("Install.Error.RolledBack", original.ToLogMessage()).ToException(message =>
                new InstallFailedException(message, original, rolledBack: true));
        }

        log(Loc.Message("Install.RollbackFailed", rollbackFailure.ToLogMessage()));
        throw Loc.Message("Install.Error.RollbackFailed", original.ToLogMessage(), rollbackFailure.ToLogMessage())
            .ToException(message => new InstallFailedException(message, original, rolledBack: false));
    }

    private void EnsureEnoughDiskSpace(InstallPlan installPlan, string install, Action<LogMessage> log)
    {
        var remotePatches = installPlan.Steps
            .Where(step => step.DownloadRequest is not null)
            .Select(step => step.DownloadRequest!)
            .ToList();
        if (remotePatches.Count > 0)
        {
            long largestPatch = remotePatches.Max(request => request.Patch.Size);
            long stagingRequired = checked(largestPatch * 2 + DISK_SAFETY_BYTES);
            EnsureVolumeSpace(
                remotePatches[0].StorageDirectory, stagingRequired,
                "Install.Disk.Download", log);
            EnsureVolumeSpace(
                fileSystem.Path.GetTempPath(), checked(largestPatch + DISK_SAFETY_BYTES),
                "Install.Disk.Temporary", log);
        }

        long requiredBytes = installPlan.Cost;

        // The first prepatch on an install snapshots the whole directory into uninstall.zip alongside it,
        // so that needs to fit on the same drive too.
        bool needsCacheSnapshot = installPlan.Steps.Any(s => s.Action == InstallPlanAction.Prepatch) &&
                                   !fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip"));
        if (needsCacheSnapshot)
        {
            requiredBytes += GetSnapshotSize(install, "", cacheService.IgnoredDirectories);
        }

        requiredBytes = (long)(requiredBytes * RequiredSpaceSafetyMargin);
        if (requiredBytes <= 0) return;

        EnsureVolumeSpace(install, requiredBytes, "Install.Disk.Install", log);
    }

    // Each purpose has its own pair of whole sentences, since the purpose cannot be spliced into one in every language.
    private void EnsureVolumeSpace(string path, long requiredBytes, string purposeKey, Action<LogMessage> log)
    {
        if (requiredBytes <= 0) return;
        var availableBytes = diskSpaceService.GetAvailableFreeSpace(path);
        if (availableBytes is null)
        {
            log(Loc.Message(purposeKey + ".Unknown"));
            return;
        }

        if (availableBytes < requiredBytes)
        {
            throw Loc.Message(purposeKey + ".NotEnough", FormatBytes(requiredBytes), FormatBytes(availableBytes.Value))
                .ToException(message => new InsufficientDiskSpaceException(
                    message, path, requiredBytes, availableBytes.Value));
        }
    }

    private long GetSnapshotSize(string directory, string prefix, List<string> ignoredDirectories)
    {
        if (ignoredDirectories.Contains(prefix)) return 0;

        long total = 0;
        var dir = fileSystem.DirectoryInfo.New(directory);
        foreach (var file in dir.GetFiles())
        {
            if (file.Name == "uninstall.zip" && prefix == "") continue;
            try { total += file.Length; }
            catch (IOException) { /* file may have been removed/locked concurrently; ignore for this estimate */ }
        }

        foreach (var subDir in dir.GetDirectories())
        {
            total += GetSnapshotSize(subDir.FullName, fileSystem.Path.Combine(prefix, subDir.Name), ignoredDirectories);
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F0} MB";
    }
}