using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using static Ksp2Redux.Tools.Launcher.Models.InstallPlan;

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
    ILauncherConfigService launcherConfigService, IInstallationStateService installationStateService,
    IBundleConversionService? bundleConversionService = null,
    IInstallFileLockService? installFileLockService = null,
    IFullSourceInstallationService? fullSource = null,
    IFullSourcePreparationService? preparation = null) : IInstallPlanService
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
        if (installPlan.FullSourceReleaseId is { } selector)
        {
            log(LogMessage.Literal($"Prepare and install verified game generation {installPlan.FullSourceVersion} ({installPlan.FullSourceChannel}, {selector})."));
            return;
        }
        if (installPlan.FullSourceTarget is { } target)
        {
            log(LogMessage.Literal($"Install verified game generation {target.Identity.Version} ({target.Identity.Channel})."));
            return;
        }
        log(Loc.Message("Install.PlanMade", installPlan.Steps.Count));
        for (var i = 0; i < installPlan.Steps.Count; i++)
        {
            log(installPlan.Steps[i] switch
            {
                UninstallStep => Loc.Message("Install.Step.Uninstall", i + 1),
                RevertToStockStep => Loc.Message("Install.Step.RevertToStock", i + 1),
                PrepatchStep => Loc.Message("Install.Step.Prepatch", i + 1),
                ApplyPatchStep apply => Loc.Message("Install.Step.ApplyPatchFile", i + 1, apply.Description),
            });
        }
    }

    public async Task ApplyToFolder(InstallPlan installPlan, string install, Action<LogMessage> log,
        Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct)
    {
        Action<string> patchLog = text => log(LogMessage.Literal(text));
        if (installPlan.FullSourceReleaseId is not null)
        {
            if (preparation is null || fullSource is null || installPlan.Steps.Count != 0 ||
                installPlan.FullSourceTarget is not null || installPlan.FullSourceChannel is null ||
                installPlan.FullSourceVersion is null)
                throw new InvalidDataException("A feed-selected full-source plan requires preparation and cannot contain legacy steps.");
            var entry = launcherConfigService.Config.Ksp2Installs.Single(item => fileSystem.Path.GetDirectoryName(item.ExePath) == install);
            fullSource.EnsureReady(install);
            stepsProgress(0, 1);
            patchLog("Authenticating and preparing the selected release...");
            bool hasCatalog = fullSource.HasCatalog(install);
            if (!hasCatalog && installPlan.IsFullSourceRepair)
                throw new InvalidDataException("Initial full-source conversion requires independently verified stock and ownership.");
            FullSourceReleaseIdentity? installedRelease = hasCatalog && !installPlan.IsFullSourceRepair
                ? fullSource.GetPublishedIdentity(entry.Id)
                : null;
            var (prepared, stock) = await preparation.FetchSelectedPlanAsync(installPlan, entry.Id, hasCatalog, installedRelease, ct, downloadProgress);
            installPlan.RequireFullSourceMatch(prepared.Identity, prepared.AllowsDerivedBootConfig);
            if (stock is null && !fullSource.HasCatalog(install))
                stock = await preparation.FetchStockAsync(entry.Id, prepared, ct);
            patchLog("Verifying and applying the prepared release...");
            await Task.Run(() => fullSource.ApplyAsync(entry.Id, prepared with { MayConsumeSource = true },
                stock, installPlan.IsFullSourceRepair, patchLog, ct, install), ct);
            stepsProgress(1, 1);
            return;
        }
        if (installPlan.FullSourceTarget is { } target)
        {
            if (fullSource is null || installPlan.FullSourceInstallationId is not { } id || installPlan.Steps.Count != 0)
                throw new InvalidDataException("A full-source plan requires its configured identity and cannot contain legacy steps.");
            var entry = launcherConfigService.Config.Ksp2Installs.Single(item => item.Id == id);
            if (fileSystem.Path.GetDirectoryName(entry.ExePath) != install)
                throw new InvalidDataException("The full-source plan root differs from its configured installation.");
            await Task.Run(() => fullSource.ApplyAsync(id, target, installPlan.FullSourceStock,
                installPlan.IsFullSourceRepair, patchLog, ct, install), ct);
            stepsProgress(1, 1);
            return;
        }
        if (fullSource?.HasCatalog(install) == true && installPlan.Steps.Count == 1 &&
            installPlan.Steps[0] is UninstallStep or RevertToStockStep)
        {
            var entry = launcherConfigService.Config.Ksp2Installs.Single(item => fileSystem.Path.GetDirectoryName(item.ExePath) == install);
            await Task.Run(() => fullSource.UninstallAsync(entry.Id, patchLog, ct, install), ct);
            stepsProgress(1, 1);
            return;
        }
        installationStateService.EnsureLegacyMutationAllowed(install);
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
            .Select((step, index) => (request: step is ApplyPatchStep { DownloadRequest: { } request } ? request : null, index))
            .Where(item => item.request is not null)
            .ToList();
        var downloadTasks = patchDownloadService.EnqueueAll(
            requestSteps.Select(item => item.request!).ToList(),
            launcherConfigService.Config.PatchDownloadSource,
            launcherConfigService.Config.MaxConcurrentChunkDownloads,
            log,
            downloadProgress,
            downloadCts.Token);
        var downloadByStep = requestSteps
            .Select((item, index) => (item.index, task: downloadTasks[index]))
            .ToDictionary(item => item.index, item => item.task);

        try
        {
            await foreach (var download in Task.WhenEach(downloadTasks))
            {
                await download;
            }
        }
        catch
        {
            downloadCts.Cancel();
            throw;
        }

        if (downloadTasks.Count > 0)
        {
            await EnsureNoFilesInUseAsync(install, log, ct);
        }

        var i = 0;
        bool mutationStarted = false;
        foreach (var step in installPlan.Steps)
        {
            installationStateService.EnsureLegacyMutationAllowed(install);
            try
            {
                runStep: switch (step)
                {
                case UninstallStep:
                    log(Loc.Message("Install.Uninstalling"));
                    // The bundle folder is not in the snapshot, and the restore deletes the journal, so undo
                    // the bundle conversion first. A bundle it could not put back would stay converted for good
                    // once the journal is gone, so a failed revert stops here with the Redux install intact.
                    await RevertBundleConversion(install, log, ct);
                    mutationStarted = true;
                    cacheService.RecursivelyRestoreCache(install);
                    break;
                case RevertToStockStep:
                    // When patching follows, the conversion after it brings the bundles up to the new shader
                    // manifest, and the journal survives the repatch, so only a plain revert undoes it here.
                    if (!installPlan.Patches)
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
                case PrepatchStep:
                {
                    log(Loc.Message("Install.ApplyingPrepatch"));
                    var exe = fileSystem.Path.Combine(install, Ksp2Install.KSP2_EXE_NAME);
                    var ksp2Install = new Ksp2Install(fileSystem, moduleDefinitionService, exe);
                    if (ksp2Install.Distribution == Distribution.Redux)
                    {
                        throw Loc.Message("Install.Error.PrepatchOnRedux").ToException(message => new Exception(message));
                    }

                    mutationStarted = true;
                    if (!fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
                    {
                        cacheService.RecursivelyCreateCache(install);
                    }

                    if (fileSystem.File.Exists(fileSystem.Path.Combine(install, "winhttp.dll")))
                    {
                        log(Loc.Message("Install.DeletingModloader"));
                        fileSystem.File.Delete(fileSystem.Path.Combine(install, "winhttp.dll"));
                    }

                    var patchFile = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), fileSystem.Path.GetRandomFileName());
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
                        case Distribution.Prepatched:
                            // Shouldn't happen, but *could*. No patch file was written, so there is nothing to apply.
                            break runStep;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    using (var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchFile))
                    {
                        installationStateService.EnsureLegacyMutationAllowed(install);
                        await patch.AsyncApply(environmentProvider, install, install, patchLog, patchLog);
                    }
                    fileSystem.File.Delete(patchFile);
                    break;
                }

                case ApplyPatchStep apply:
                {
                    string patchPath;
                    if (downloadByStep.TryGetValue(i, out var downloadTask))
                    {
                        patchPath = await downloadTask;
                    }
                    else
                    {
                        patchPath = await apply.ResolvePath!(log, downloadProgress, ct);
                    }

                    installationStateService.EnsureLegacyMutationAllowed(install);
                    mutationStarted = true;
                    using (var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchPath))
                    {
                        await patch.AsyncApply(environmentProvider, install, install, patchLog, patchLog);
                    }
                    if (apply.DeleteAfter && fileSystem.File.Exists(patchPath))
                    {
                        log(Loc.Message("Install.CleaningPatch", patchPath));
                        try { fileSystem.File.Delete(patchPath); }
                        catch (Exception ex) { log(Loc.Message("Install.PatchDeleteFailed", patchPath, ex.ToLogMessage())); }
                    }
                    break;
                }
                }
            }
            catch (Exception ex)
            {
                downloadCts.Cancel();
                if (!mutationStarted)
                {
                    throw;
                }
                await RollbackOrRethrowAsync(install, log, ex);
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
                    await RollbackOrRethrowAsync(install, log, ex);
                }
            }
        }

        bool patched = installPlan.Patches;
        if (patched && bundleConversionService is not null)
        {
            // Some bundles converted and others not is a half-finished install, and reporting it as installed hid
            // that from players. So a failed or cancelled conversion rolls the install back like a failed patch.
            try
            {
                await Task.Run(() => bundleConversionService.ConvertAsync(install, log, ct), ct);
            }
            catch (Exception ex)
            {
                await RollbackOrRethrowAsync(install, log, ex);
            }
        }
    }

    private async Task EnsureNoFilesInUseAsync(string install, Action<LogMessage> log, CancellationToken ct)
    {
        if (installFileLockService is null)
        {
            return;
        }

        log(Loc.Message("Install.CheckingFiles"));
        await Task.Run(() => installFileLockService.EnsureNoFilesInUse(install), ct);
    }

    private async Task RevertBundleConversion(string install, Action<LogMessage> log, CancellationToken ct)
    {
        if (bundleConversionService is null)
        {
            return;
        }

        await Task.Run(() => bundleConversionService.RevertAsync(install, log, ct), ct);
    }

    // Runs during a rollback, which may follow a cancellation, so it does not take the plan's token.
    private async Task TryRevertBundleConversionAsync(string install, Action<LogMessage> log)
    {
        if (bundleConversionService is null)
        {
            return;
        }

        try
        {
            await bundleConversionService.RevertAsync(install, log, CancellationToken.None);
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
    private async Task RollbackOrRethrowAsync(string install, Action<LogMessage> log, Exception original)
    {
        installationStateService.EnsureLegacyMutationAllowed(install);
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
        {
            ExceptionDispatchInfo.Capture(original).Throw();
            return; // unreachable, keeps the compiler happy
        }

        log(Loc.Message("Install.StepFailed", original.ToLogMessage()));

        // The snapshot is a stock install, and the restore leaves the bundles in StreamingAssets as they are.
        // Converted bundles draw magenta in stock and in Redux builds from before the URP port, so the
        // conversion is undone too. The next install converts again if its build needs it.
        await TryRevertBundleConversionAsync(install, log);

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
        var remotePatches = installPlan.DownloadRequests.ToList();
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
        bool needsCacheSnapshot = installPlan.Steps.Any(step => step is PrepatchStep) &&
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
            log(Loc.Message($"{purposeKey}.Unknown"));
            return;
        }

        if (availableBytes < requiredBytes)
        {
            throw Loc.Message($"{purposeKey}.NotEnough", FormatBytes(requiredBytes), FormatBytes(availableBytes.Value))
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
