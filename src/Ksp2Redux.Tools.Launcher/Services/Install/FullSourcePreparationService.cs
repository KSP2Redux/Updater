using System.Collections.Frozen;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Prepare exact full-source releases through configured online feeds and separately verified stock caches.
/// </summary>
/// <remarks>
/// Release and baseline definitions come from fresh configured authority metadata. Local paths, archives
/// and digests never supply authority. Recovery uses the coordinator's persisted evidence independently.
/// One cross-process cache lease excludes concurrent preparation writers. Native preparation requires Windows.
/// </remarks>
public partial class FullSourcePreparationService : IFullSourcePreparationService
{
    private readonly IFileSystem _fs;
    private readonly ILauncherConfigService _configuration;
    private readonly IManifestReleasesFeedProviderService _feeds;
    private readonly IPatchDownloadService _downloads;
    private readonly Func<ISteamDepotDownloader> _steam;
    private readonly IDiskSpaceService _space;
    private readonly IOperatingSystemService _os;
    private readonly StockArchive _archive;
    private readonly IZipFileService _zip;
    private readonly InstallTransactionEngine _paths;
    private readonly ReleaseRecipePipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private record AuthorityRelease(FeedInfo Feed, ReleasePatch Patch, string Authority)
    {
        internal FullSourceReleaseDescriptor Descriptor => Patch.FullSource!;
    }

    private record RecipeArtifact(string Url, string Marker, string Sha256, string PackPrefix, ReleaseRecipeManifest Manifest);
    internal record RecipeSource(StockBaselineManifest Contract, string Root, Guid? InstalledId = null, bool Projected = false)
    {
        internal string? PublishedSourceSha256 { get; init; }
    }

    /// <summary>
    /// Construct preparation using the launcher's existing authority, download, storefront and cache systems.
    /// </summary>
    /// <param name="fileSystem">The filesystem used for all access.</param>
    /// <param name="configuration">The explicit feed and installation configuration.</param>
    /// <param name="feeds">The fresh online authority transport.</param>
    /// <param name="downloads">The existing bounded, resumable verified download service.</param>
    /// <param name="steam">The deferred signed-in Steam service, avoiding the coordinator's installation-guard dependency cycle.</param>
    /// <param name="zip">The complete stock archive service.</param>
    /// <param name="space">The existing disk-space provider.</param>
    /// <param name="os">The launcher host platform.</param>
    /// <param name="validateFileStreams">The virtual filesystem stream validator, or null for native validation.</param>
    public FullSourcePreparationService(IFileSystem fileSystem, ILauncherConfigService configuration,
        IManifestReleasesFeedProviderService feeds, IPatchDownloadService downloads, Func<ISteamDepotDownloader> steam,
        IZipFileService zip, IDiskSpaceService space, IOperatingSystemService os,
        Action<string>? validateFileStreams = null)
    {
        if (fileSystem is RealFileSystem && validateFileStreams is not null)
            throw new ArgumentException("Native preparation cannot bypass Windows file stream validation.", nameof(validateFileStreams));
        _fs = fileSystem;
        _zip = zip;
        _configuration = configuration;
        _feeds = feeds;
        _downloads = downloads;
        _steam = steam;
        _space = space;
        _os = os;
        _archive = new StockArchive(fileSystem, zip, availableSpace: space.GetAvailableFreeSpace);
        _paths = new InstallTransactionEngine(fileSystem, space.GetAvailableFreeSpace, validateFileStreams: validateFileStreams);
        _pipeline = new ReleaseRecipePipeline(fileSystem, validateFileStreams);
    }

    /// <inheritdoc />
    public async Task<FullSourcePreparedTarget> FetchTargetAsync(string releaseId, string channel, CancellationToken ct,
        Action<long, long>? downloadProgress = null)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolveAsync(releaseId, channel, ct);
        return await PrepareTargetAsync(release, ct, downloadProgress);
    }

    /// <inheritdoc />
    public async Task<FullSourcePreparedTarget> FetchTargetAsync(string releaseId, string channel, FeedInfo feed, CancellationToken ct,
        Action<long, long>? downloadProgress = null)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolveAsync(releaseId, channel, ct, feed);
        return await PrepareTargetAsync(release, ct, downloadProgress);
    }

    /// <inheritdoc />
    public async Task<FullSourcePreparedTarget> FetchTargetForInstalledReleaseAsync(string releaseId, string channel,
        FeedInfo? feed, Guid installationId, FullSourceReleaseIdentity installedRelease, CancellationToken ct,
        Action<long, long>? downloadProgress = null)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolveAsync(releaseId, channel, ct, feed);
        return await PrepareTargetAsync(release, ct, downloadProgress, installationId, installedRelease);
    }

    /// <inheritdoc />
    public async Task<FullSourcePreparedTarget> FetchRepairTargetAsync(string releaseId, string channel,
        FeedInfo? feed, Guid installationId, CancellationToken ct, Action<long, long>? downloadProgress = null)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolveAsync(releaseId, channel, ct, feed);
        return await PrepareTargetAsync(release, ct, downloadProgress, repairInstallationId: installationId);
    }

    /// <inheritdoc />
    public async Task<(FullSourcePreparedTarget Target, FullSourceStockPreparation Stock)> FetchInitialInstallAsync(
        string releaseId, string channel, FeedInfo? feed, Guid installationId, CancellationToken ct,
        Action<long, long>? downloadProgress = null)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolveAsync(releaseId, channel, ct, feed);
        return await PrepareInitialInstallAsync(release, installationId, ct, downloadProgress);
    }

    /// <inheritdoc />
    public async Task<InstallPlan> ResolveInstallPlanAsync(string releaseId, string channel, CancellationToken ct)
    {
        EnsureSupported();
        var release = await ResolveAsync(releaseId, channel, ct);
        return new InstallPlan
        {
            FullSourceReleaseId = releaseId, FullSourceChannel = channel,
            FullSourceVersion = release.Descriptor.Version,
            FullSourceManifestSha256 = release.Descriptor.GetNormalizedTargetManifestSha256(),
            FullSourceFeed = release.Feed.SnapshotAuthority()
        };
    }

    /// <inheritdoc />
    public async Task<(FullSourcePreparedTarget Target, FullSourceStockPreparation? Stock)> FetchSelectedPlanAsync(
        InstallPlan plan, Guid installationId, bool hasCatalog, FullSourceReleaseIdentity? installedRelease,
        CancellationToken ct, Action<long, long>? downloadProgress = null)
    {
        EnsureSupported();
        string releaseId = plan.FullSourceReleaseId ?? throw new InvalidDataException("The plan has no release selector.");
        string channel = plan.FullSourceChannel ?? throw new InvalidDataException("The plan has no release channel.");
        AuthorityRelease release = await ResolveAsync(releaseId, channel, ct, plan.FullSourceFeed);
        plan.RequireFullSourceMatch(Identity(release));
        if (!hasCatalog)
            return await PrepareInitialInstallAsync(release, installationId, ct, downloadProgress);
        if (plan.IsFullSourceRepair)
            return (await PrepareTargetAsync(release, ct, downloadProgress, repairInstallationId: installationId), null);
        if (installedRelease is not null)
            return (await PrepareTargetAsync(release, ct, downloadProgress, installationId, installedRelease), null);
        return (await PrepareTargetAsync(release, ct, downloadProgress), null);
    }

    private async Task<(FullSourcePreparedTarget Target, FullSourceStockPreparation Stock)> PrepareInitialInstallAsync(
        AuthorityRelease release, Guid installationId, CancellationToken ct, Action<long, long>? downloadProgress)
    {
        var stock = await PrepareStockAsync(installationId, [release], ct);
        return (await PrepareTargetAsync(release, ct, downloadProgress), stock);
    }

    private async Task<FullSourcePreparedTarget> PrepareTargetAsync(AuthorityRelease release, CancellationToken ct,
        Action<long, long>? downloadProgress,
        Guid? installationId = null, FullSourceReleaseIdentity? installedRelease = null, Guid? repairInstallationId = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using FileSystemStream lease = Lease();
            FullSourceReleaseDescriptor descriptor = release.Descriptor;
            var update = descriptor.UpdateArtifacts?.FirstOrDefault(candidate =>
                installedRelease?.MatchesManifestDigest(candidate.SourceDigest) == true);
            long markerBytes = 0;
            if (installationId is { } id && installedRelease is not null && update is not null)
            {
                var artifact = await InspectArtifactAsync(release, ArtifactUrl(update.R2ArtifactUrl, update.GitHubArtifactUrl),
                    update.MarkerSize, update.MarkerSha256, update.AssetPrefix, markerBytes, update.DownloadBytes, downloadProgress, ct);
                markerBytes += update.MarkerSize;
                ReleaseRecipePipeline.ValidateUpdateFeedArtifact(artifact.Manifest, descriptor.TargetManifest, update);
                var installed = ReleaseRecipePipeline.GetSourceContract(artifact.Manifest, update.SourceDigest);
                if (await VerifyInstalledRecipeSourceAsync(id, installed, installedRelease, update.SourceDigest, ct) is { } installedSource)
                    return await PrepareArtifactAsync(release, artifact, installedSource, markerBytes, downloadProgress, ct);
            }

            var stock = await InspectArtifactAsync(release, ArtifactUrl(descriptor.R2ArtifactUrl, descriptor.GitHubArtifactUrl),
                release.Patch.Size, release.Patch.ChecksumSha256, "", markerBytes, descriptor.DownloadBytes, downloadProgress, ct);
            markerBytes += release.Patch.Size;
            ReleaseRecipePipeline.ValidateFeedArtifact(stock.Manifest, descriptor, release.Patch.Size);
            // Without a direct source, the stock recipe can skip target files the installation already supplies.
            string? sourceDigest = descriptor.UpdateSourceDigests?.FirstOrDefault(digest => installedRelease?.MatchesManifestDigest(digest) == true);
            bool hasDirectSource = update is not null || sourceDigest is not null;
            Guid? retentionId = repairInstallationId ?? (!hasDirectSource && installedRelease is not null ? installationId : null);
            FullSourceTargetRetention? retention = null;
            if (retentionId is { } retainingId && _os.IsWindows())
            {
                string root = InstallRoot(Entry(retainingId));
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in StockBaselineManifest.FromJson(descriptor.TargetManifest).Files)
                {
                    if (await MatchesAsync(At(root, file.Path), file, ct))
                        paths.Add(file.Path);
                }
                retention = new FullSourceTargetRetention(retainingId, paths.ToFrozenSet(StringComparer.Ordinal), installedRelease?.ManifestSha256);
            }
            var source = await SelectRecipeSourceAsync(descriptor, stock.Manifest, installationId, installedRelease, sourceDigest, ct, retention?.Paths);
            return await PrepareArtifactAsync(release, stock, source, markerBytes, downloadProgress, ct, retention);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecipeArtifact> InspectArtifactAsync(AuthorityRelease release, string url,
        long size, string checksum, string packPrefix, long previousMarkerBytes, long declaredBytes,
        Action<long, long>? downloadProgress, CancellationToken ct)
    {
        var request = Request(release.Feed, url, size, checksum, release.Descriptor.Version);
        var downloads = _downloads.EnqueueAll([request], _configuration.Config.PatchDownloadSource,
            _configuration.Config.MaxConcurrentChunkDownloads, _ => { },
            (bytes, _) => downloadProgress?.Invoke(previousMarkerBytes + bytes, previousMarkerBytes + declaredBytes), ct);
        string marker = await downloads[0];
        var manifest = _pipeline.InspectDownloaded(marker, size, checksum, release.Descriptor.TargetManifestSha256, ct);
        return new RecipeArtifact(url, marker, checksum, packPrefix, manifest);
    }

    private async Task<FullSourcePreparedTarget> PrepareArtifactAsync(AuthorityRelease release, RecipeArtifact artifact,
        RecipeSource selection, long markerBytes, Action<long, long>? downloadProgress,
        CancellationToken ct, FullSourceTargetRetention? retention = null)
    {
        FullSourceReleaseDescriptor descriptor = release.Descriptor;
        var (contract, source, installedId, projected) = selection;
        ReleaseRecipeManifest manifest = artifact.Manifest;
        int[] requiredPacks = ReleaseRecipePipeline.GetRequiredPackIndices(manifest, contract, retention?.Paths);
        long totalBytes = markerBytes + requiredPacks.Sum(index => (long)manifest.Packs[index].Length);
        string stages = At(CacheRoot(), "stages");
        CreateDirectory(stages);
        var targetContract = StockBaselineManifest.FromJson(descriptor.TargetManifest);
        var installed = installedId is { } id && _os.IsWindows()
            ? new FullSourceInstalledBaseline(id, contract) { PublishedSourceSha256 = selection.PublishedSourceSha256 }
            : null;
        var staged = installed is not null ? ReleaseRecipePipeline.UpdateStageContract(targetContract, contract)
            : retention is null ? targetContract : ReleaseRecipePipeline.RetainedStageContract(targetContract, retention.Paths);
        // Windows preparation keeps its stages and pack cache under the same controlled root.
        long cacheGrowth = _os.IsWindows() ? EstimatePackCacheGrowth(manifest, requiredPacks, ct) : 0;
        EnsureSpace(stages, checked(staged.TotalBytes + cacheGrowth));

        // Keep pending transport tasks bounded as well as active transfer buffers.
        int batchSize = Math.Clamp(_configuration.Config.MaxConcurrentChunkDownloads, 1, 8);
        var downloadedPacks = new string?[manifest.Packs.Count];
        long completedBytes = markerBytes;
        downloadProgress?.Invoke(completedBytes, totalBytes);
        for (int first = 0; first < requiredPacks.Length; first += batchSize)
        {
            var requests = new List<PatchDownloadRequest>();
            int count = Math.Min(batchSize, requiredPacks.Length - first);
            for (int offset = 0; offset < count; offset++)
            {
                int index = requiredPacks[first + offset];
                var pack = manifest.Packs[index];
                requests.Add(Request(release.Feed, new Uri(new Uri(artifact.Url), $"{artifact.PackPrefix}pack-{index:D6}.bin").AbsoluteUri,
                    pack.Length, pack.Sha256, descriptor.Version));
            }
            long batchStart = completedBytes;
            IReadOnlyList<Task<string>> tasks = _downloads.EnqueueAll(requests, _configuration.Config.PatchDownloadSource,
                batchSize, _ => { }, (bytes, _) => downloadProgress?.Invoke(batchStart + bytes, totalBytes), ct);
            string[] paths = await Task.WhenAll(tasks);
            completedBytes += requests.Sum(request => request.Patch.Size);
            downloadProgress?.Invoke(completedBytes, totalBytes);
            for (int offset = 0; offset < count; offset++)
            {
                downloadedPacks[requiredPacks[first + offset]] = paths[offset];
            }
        }
        // Pack downloads can consume space on the staging volume.
        if (requiredPacks.Length != 0)
            EnsureSpace(stages, staged.TotalBytes);
        string stage = At(stages, Guid.NewGuid().ToString("N"));
        StockBaselineManifest target = await Task.Run(() =>
        {
            if (installed is not null)
                return _pipeline.PrepareDownloadedUpdate(artifact.Marker, artifact.Sha256,
                    descriptor.TargetManifestSha256, downloadedPacks, source, contract, stage, ct);
            if (projected)
                return _pipeline.PrepareDownloadedFromRecipeInputs(artifact.Marker, artifact.Sha256,
                    descriptor.TargetManifestSha256, downloadedPacks, source, contract, stage, ct, retention?.Paths);
            return _pipeline.PrepareDownloaded(artifact.Marker, artifact.Sha256,
                descriptor.TargetManifestSha256, downloadedPacks, source, contract, stage, ct, retention?.Paths);
        }, ct);
        return new FullSourcePreparedTarget(Identity(release), target, stage, release.Feed.SnapshotAuthority())
        {
            InstalledBaseline = installed, TargetRetention = retention, BootConfigSettings = descriptor.BootConfigSettings
        };
    }

    private long EstimatePackCacheGrowth(ReleaseRecipeManifest manifest, int[] requiredPacks, CancellationToken ct)
    {
        if (requiredPacks.Length == 0)
            return 0;

        string patches = At(CacheRoot(), "downloads/patches");
        long total = 0;
        foreach (var pack in requiredPacks.Select(index => manifest.Packs[index])
                     .DistinctBy(pack => pack.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string completed = At(patches, $"{pack.Sha256.ToUpperInvariant()}.patch");
            // The downloader can resume partial bytes or reclaim a rejected cache file before replacing it.
            long stored = Math.Min(pack.Length, CacheFileLength(completed));
            long partial = Math.Min(pack.Length, CacheFileLength(completed + ".partial"));
            total = checked(total + Math.Max(0, pack.Length - stored - partial));
        }
        return total;
    }

    private long CacheFileLength(string path)
    {
        _paths.ValidateControlledPath(path);
        return _fs.File.Exists(path) ? _fs.FileInfo.New(path).Length : 0;
    }

    private async Task<RecipeSource> SelectRecipeSourceAsync(
        FullSourceReleaseDescriptor descriptor, ReleaseRecipeManifest manifest, Guid? installationId,
        FullSourceReleaseIdentity? installedRelease, string? sourceDigest, CancellationToken ct, IReadOnlySet<string>? retainedTargetPaths)
    {
        if (installationId is { } id && installedRelease is not null && sourceDigest is not null)
        {
            var contract = ReleaseRecipePipeline.GetSourceContract(manifest, sourceDigest);
            if (await VerifyInstalledRecipeSourceAsync(id, contract, installedRelease, sourceDigest, ct) is { } source)
                return source;
        }
        if (ReleaseManifestValidator.GetPrepatchBaseline(descriptor) is { } prepatch)
            return await PreparePrepatchSourceAsync(descriptor, prepatch, ct);
        return await SelectBaselineAsync(descriptor.Baselines, ct, manifest, retainedTargetPaths);
    }

    private async Task<RecipeSource?> VerifyInstalledRecipeSourceAsync(Guid installationId, StockBaselineManifest contract,
        FullSourceReleaseIdentity installedRelease, string sourceDigest, CancellationToken ct)
    {
        string normalized = Digest(contract.ToJson());
        if (!normalized.Equals(installedRelease.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update recipe source differs from the installed release contract.");
        string root = InstallRoot(Entry(installationId));
        if (await TreeMatchesAsync(root, contract, ct))
            return new RecipeSource(contract, root, installationId)
            {
                PublishedSourceSha256 = normalized.Equals(sourceDigest, StringComparison.OrdinalIgnoreCase) ? null : sourceDigest
            };

        // A normal Windows update must verify the old owned files before it can publish a new generation.
        if (_os.IsWindows())
            throw new InvalidDataException("Installed Redux files differ from the update baseline. Repair the installation before updating.");
        return null;
    }

    /// <inheritdoc />
    public async Task<FullSourceStockPreparation> FetchStockAsync(Guid installationId, CancellationToken ct)
    {
        EnsureSupported();
        return await PrepareStockAsync(installationId, await ReleasesAsync(ct), ct);
    }

    /// <inheritdoc />
    public async Task<FullSourceStockPreparation> FetchStockAsync(Guid installationId, FullSourcePreparedTarget target, CancellationToken ct)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolvePreparedAsync(target, ct);
        return await PrepareStockAsync(installationId, [release], ct);
    }

    private async Task<FullSourceStockPreparation> PrepareStockAsync(Guid installationId, List<AuthorityRelease> releases, CancellationToken ct)
    {
        Ksp2InstallEntry entry = Entry(installationId);
        string root = InstallRoot(entry);
        string archivePath = At(root, "uninstall.zip");
        bool existingArchive = _fs.File.Exists(archivePath);
        if (existingArchive && !_os.IsWindows())
            throw new IOException("An existing uninstall.zip requires reviewed migration. It has been preserved.");
        var candidates = releases.SelectMany(release => BaselineDefinitions(release.Descriptor, entry).Select(baseline => (Release: release, Baseline: baseline)))
            .DistinctBy(candidate => Digest(candidate.Baseline.Manifest)).ToList();
        FullSourceBaselineDefinition? selected = null;
        AuthorityRelease? authority = null;
        StockBaselineManifest? selectedOwnership = null;
        InvalidDataException? coverageFailure = null;
        foreach (var candidate in candidates)
        {
            FullSourceBaselineDefinition baseline = candidate.Baseline;
            StockBaselineManifest stock = StockBaselineManifest.FromJson(baseline.Manifest);
            if (entry.FullSourceBaselineIdentity is { } identity && stock.BaselineIdentity != identity)
                continue;
            StockBaselineManifest owned;
            if (existingArchive)
            {
                ValidateStockInputPaths(root, stock);
                _paths.ValidateControlledPath(archivePath);
                try
                {
                    await _archive.VerifyAsync(archivePath, stock, ct);
                }
                catch (InvalidDataException failure)
                {
                    coverageFailure = failure;
                    continue;
                }
                owned = await CanonicalArchiveOwnershipAsync(root, stock, candidate.Release.Descriptor, entry, ct);
                if (!await TreeMatchesAsync(root, owned, ct))
                    continue;
            }
            else
            {
                owned = Ownership(stock, entry);
                if (!await TreeMatchesAsync(root, owned, ct))
                    continue;
            }
            if (selected is not null)
                throw new InvalidDataException("Multiple original stock contracts match. Configure the original baseline identity explicitly.");
            selected = baseline;
            authority = candidate.Release;
            selectedOwnership = owned;
        }
        if (selected is null)
        {
            if (coverageFailure is not null)
                ExceptionDispatchInfo.Capture(coverageFailure).Throw();
            var failure = new InvalidDataException("The configured installation does not match a complete trusted original stock ownership contract. Legacy conversion needs reviewed preparation.");
            failure.Data["FullSourceStockMismatch"] = true;
            throw failure;
        }
        StockBaselineManifest selectedStock = StockBaselineManifest.FromJson(selected.Manifest);
        if (existingArchive)
        {
            StockBaselineFile pin = await ObserveArchiveAsync(archivePath, ct);
            return new FullSourceStockPreparation(selectedStock, "", selectedOwnership!,
                entry.FullSourceRetainedUnownedFiles.ToArray(), entry.FullSourceUnownedDirectories.ToArray(), entry.Id,
                Identity(authority!), authority!.Feed.SnapshotAuthority(), pin);
        }
        if (_os.IsWindows() && (selectedOwnership!.ToJson().AsSpan().SequenceEqual(selectedStock.ToJson()) ||
            await TreeMatchesAsync(root, selectedStock, ct)))
        {
            return new FullSourceStockPreparation(selectedStock, root, selectedOwnership!,
                entry.FullSourceRetainedUnownedFiles.ToArray(), entry.FullSourceUnownedDirectories.ToArray(), entry.Id,
                Identity(authority!), authority!.Feed.SnapshotAuthority());
        }
        await _gate.WaitAsync(ct);
        try
        {
            using FileSystemStream lease = Lease();
            var prepared = await SelectBaselineAsync([selected], ct);
            return new FullSourceStockPreparation(prepared.Contract, prepared.Root, Ownership(prepared.Contract, entry),
                entry.FullSourceRetainedUnownedFiles.ToArray(), entry.FullSourceUnownedDirectories.ToArray(), entry.Id,
                Identity(authority!), authority!.Feed.SnapshotAuthority());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task VerifyTargetAsync(FullSourcePreparedTarget target, CancellationToken ct) =>
        VerifyPreparedStageAsync(target, ct);

    internal async Task<StockBaselineManifest> VerifyPreparedStageAsync(FullSourcePreparedTarget target, CancellationToken ct)
    {
        EnsureSupported();
        AuthorityRelease release = await ResolvePreparedAsync(target, ct);
        RequireCacheChild(target.SourceRoot, "stages");
        var staged = target.Manifest;
        if (target.TargetRetention is { } retention)
        {
            if (!_os.IsWindows() || target.InstalledBaseline is not null)
                throw new InvalidDataException("Target retention is incompatible with this prepared release.");
            _ = Entry(retention.InstallationId);
            staged = ReleaseRecipePipeline.RetainedStageContract(target.Manifest, retention.Paths);
        }
        if (target.InstalledBaseline is { } installed)
        {
            string digest = installed.PublishedSourceSha256 ?? Digest(installed.Manifest.ToJson());
            bool matchingSource = release.Descriptor.UpdateSourceDigests?.Contains(digest, StringComparer.OrdinalIgnoreCase) == true ||
                release.Descriptor.UpdateArtifacts?.Any(artifact => artifact.SourceDigest.Equals(digest, StringComparison.OrdinalIgnoreCase)) == true;
            if (!_os.IsWindows() || !matchingSource)
                throw new InvalidDataException("The changed-file stage has no matching authenticated installed baseline.");
            _ = Entry(installed.InstallationId);
            staged = ReleaseRecipePipeline.UpdateStageContract(target.Manifest, installed.Manifest);
        }
        if (!await TreeMatchesAsync(target.SourceRoot, staged, ct))
            throw new InvalidDataException("Prepared target bytes differ from their authenticated stage contract.");
        return staged;
    }

    /// <inheritdoc />
    public async Task VerifyStockAsync(FullSourceStockPreparation preparation, CancellationToken ct)
    {
        EnsureSupported();
        Ksp2InstallEntry entry = Entry(preparation.InstallationId
            ?? throw new InvalidDataException("Production stock preparation requires its configured installation identity."));
        if (preparation.AuthorityFeed is null || preparation.Release is null)
            throw new InvalidDataException("Production stock preparation requires its independently selected configured authority.");
        AuthorityRelease release = await ResolveAsync(preparation.Release.ReleaseId, preparation.Release.Channel, ct, preparation.AuthorityFeed);
        byte[] expected = preparation.Stock.ToJson();
        var definitions = BaselineDefinitions(release.Descriptor, entry).ToArray();
        // A definition published before boot.config left every contract lists it. It is the same contract as without it.
        FullSourceBaselineDefinition? definition = definitions.SingleOrDefault(baseline =>
            StockBaselineManifest.FromJson(baseline.Manifest).ToJson().AsSpan().SequenceEqual(expected));
        if (preparation.Release != Identity(release) || definition is null ||
            (entry.FullSourceBaselineIdentity is { } identity && identity != preparation.Stock.BaselineIdentity) ||
            !preparation.RetainedUnownedFiles.SequenceEqual(entry.FullSourceRetainedUnownedFiles, StringComparer.Ordinal) ||
            !preparation.UnownedDirectories.SequenceEqual(entry.FullSourceUnownedDirectories, StringComparer.Ordinal))
            throw new InvalidDataException("Stock or ownership differs from online baseline definitions and explicit installation exclusions.");
        if (preparation.ExistingArchive is { } existing)
        {
            if (!_os.IsWindows() || preparation.SourceRoot != "" || existing.Path != At(InstallRoot(entry), "uninstall.zip") ||
                await ObserveArchiveAsync(existing.Path, ct) != existing || definition is null)
                throw new InvalidDataException("Existing backup differs from its configured preparation pin.");
            ValidateStockInputPaths(InstallRoot(entry), preparation.Stock);
            await _archive.VerifyAsync(existing.Path, preparation.Stock, ct);
            StockBaselineManifest owned = await CanonicalArchiveOwnershipAsync(InstallRoot(entry), preparation.Stock, release.Descriptor, entry, ct);
            if (!owned.ToJson().AsSpan().SequenceEqual(preparation.CurrentOwned.ToJson()))
                throw new InvalidDataException("Legacy owned-file observation changed after preparation.");
            return;
        }
        if (!preparation.CurrentOwned.ToJson().AsSpan().SequenceEqual(Ownership(preparation.Stock, entry).ToJson()))
            throw new InvalidDataException("Initial stock ownership differs from its trusted contract.");
        string install = InstallRoot(entry);
        bool installedSource = _os.IsWindows() && preparation.SourceRoot == install;
        if (!installedSource && preparation.SourceRoot != BaselineRoot(preparation.Stock))
            throw new InvalidDataException("Stock source is outside its controlled baseline cache.");
        if (!await TreeMatchesAsync(preparation.SourceRoot, preparation.Stock, ct) ||
            (!installedSource && !await TreeMatchesAsync(install, preparation.CurrentOwned, ct)))
            throw new InvalidDataException("Stock or initially owned bytes differ from the independently expected baseline.");
    }

    private async Task<AuthorityRelease> ResolvePreparedAsync(FullSourcePreparedTarget target, CancellationToken ct)
    {
        if (target.AuthorityFeed is null)
            throw new InvalidDataException("Production target preparation requires its independently selected configured authority.");
        AuthorityRelease release = await ResolveAsync(target.Identity.ReleaseId, target.Identity.Channel, ct, target.AuthorityFeed);
        if (target.Identity != Identity(release) ||
            !target.Manifest.ToJson().AsSpan().SequenceEqual(release.Descriptor.GetNormalizedTargetManifest()))
            throw new InvalidDataException("Prepared release identity or permission differs from fresh configured authority metadata.");
        return release;
    }

    private async Task<AuthorityRelease> ResolveAsync(string releaseId, string channel, CancellationToken ct, FeedInfo? selection = null)
    {
        if (string.IsNullOrWhiteSpace(releaseId) || string.IsNullOrWhiteSpace(channel))
            throw new InvalidDataException("An explicit release selector and channel are required.");
        List<AuthorityRelease> releases;
        if (selection is null)
        {
            releases = await ReleasesAsync(ct);
        }
        else
        {
            FeedInfo[] configured = _configuration.Config.Feeds.Where(feed => feed.HasSameAuthority(selection)).ToArray();
            if (configured.Length != 1)
                throw new InvalidDataException("The selected authority does not identify one currently configured feed.");
            releases = await ReadReleasesAsync(configured[0], ct);
        }
        AuthorityRelease[] matches = releases.Where(release => release.Descriptor.ReleaseId == releaseId &&
            release.Descriptor.Channel == channel).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The requested release and channel do not identify one fresh configured-authority entry.");
        return matches[0];
    }

    private async Task<List<AuthorityRelease>> ReleasesAsync(CancellationToken ct)
    {
        var releases = new List<AuthorityRelease>();
        Exception? unavailable = null;
        foreach (FeedInfo feed in _configuration.Config.Feeds.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                releases.AddRange(await ReadReleasesAsync(feed, ct));
            }
            catch (Exception failure) when (failure is HttpRequestException || failure is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Discovery may use another configured authority. Bound verification never takes this fallback.
                unavailable ??= failure;
            }
        }
        if (releases.Count == 0 && unavailable is not null)
            ExceptionDispatchInfo.Capture(unavailable).Throw();
        return releases;
    }

    private async Task<List<AuthorityRelease>> ReadReleasesAsync(FeedInfo feed, CancellationToken ct)
    {
        TrustedReleaseManifest trusted = await _feeds.GetTrustedManifestAsync(feed, ct);
        if (string.IsNullOrWhiteSpace(trusted.Authority) || trusted.Authority.Length > 4096)
            throw new InvalidDataException("The configured feed returned no authority evidence.");
        if (trusted.Authority.StartsWith("online-https:", StringComparison.Ordinal))
            ReleaseManifestValidator.ValidateHosted(trusted.Manifest, _os.IsLinux());
        else
            ReleaseManifestValidator.Validate(trusted.Manifest);
        return trusted.Manifest.Patches.Where(patch => patch.FullSource is not null)
            .Select(patch => new AuthorityRelease(feed, patch, trusted.Authority)).ToList();
    }

    internal async Task<RecipeSource> SelectBaselineAsync(IReadOnlyList<FullSourceBaselineDefinition> definitions,
        CancellationToken ct, ReleaseRecipeManifest? recipe = null, IReadOnlySet<string>? retainedTargetPaths = null)
    {
        RecipeSource source = await SelectVerifiedBaselineAsync(definitions, ct, recipe, retainedTargetPaths);
        // A complete cached tree proves the contract, so the tree an earlier build kept for it is no longer needed.
        if (!source.Projected && source.Root == BaselineRoot(source.Contract))
            PruneSupersededBaselines(source.Contract);
        return source;
    }

    private async Task<RecipeSource> SelectVerifiedBaselineAsync(IReadOnlyList<FullSourceBaselineDefinition> definitions,
        CancellationToken ct, ReleaseRecipeManifest? recipe, IReadOnlySet<string>? retainedTargetPaths)
    {
        InvalidDataException? archiveCoverageFailure = null;
        bool windows = _os.IsWindows();
        ReleaseRecipeManifest? stockRecipe = windows ? recipe : null;
        foreach (var definition in definitions)
        {
            StockBaselineManifest stock = StockBaselineManifest.FromJson(definition.Manifest);
            // Patch staging needs only the recipe inputs. Stock ownership and backup preparation still use the full tree.
            var inputs = stockRecipe is null ? stock : ReleaseRecipePipeline.GetRecipeInputContract(stockRecipe, stock, retainedTargetPaths);
            bool projected = stockRecipe is not null;
            string root = BaselineRoot(stock);
            // A tree an earlier build kept for this contract moves into place before anything needs the space to copy it.
            if (await AdoptSupersededBaselineAsync(stock, inputs, root, ct))
                return new RecipeSource(stock, root, Projected: projected);
            IReadOnlyList<StockBaselineFile> filesToCopy = inputs.Files;
            if (windows && !projected)
            {
                bool directoriesMatch = TreeDirectoriesMatch(root, inputs, ct);
                filesToCopy = await FindMissingCacheInputsAsync(root, filesToCopy, ct);
                if (directoriesMatch && filesToCopy.Count == 0)
                    return new RecipeSource(stock, root);
            }
            else if (await TreeMatchesAsync(root, inputs, ct))
            {
                return new RecipeSource(stock, root, Projected: projected);
            }
            if (projected)
            {
                foreach (var entry in _configuration.Config.Ksp2Installs)
                {
                    if (entry.FullSourceBaselineIdentity is { } identity && identity != stock.BaselineIdentity)
                        continue;
                    string installed = InstallRoot(entry);
                    if (await TreeMatchesAsync(installed, inputs, ct))
                        return new RecipeSource(stock, installed, Projected: true);
                }

                filesToCopy = await FindMissingCacheInputsAsync(root, filesToCopy, ct);
                if (filesToCopy.Count == 0)
                {
                    CreateDirectory(root);
                    CreateBaselineDirectories(root, inputs);
                    return new RecipeSource(stock, root, Projected: true);
                }
                foreach (var entry in _configuration.Config.Ksp2Installs)
                {
                    string installed = InstallRoot(entry);
                    string archive = At(installed, "uninstall.zip");
                    _paths.ValidateControlledPath(archive);
                    if (!_fs.File.Exists(archive))
                        continue;
                    try
                    {
                        CreateDirectory(root);
                        EnsureSpace(root, filesToCopy.Sum(file => file.Length));
                        CreateBaselineDirectories(root, inputs);
                        var requestedPaths = filesToCopy.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                        await CopyWindowsBaselineAsync(archive, installed, stock, root, ct, requestedPaths);
                        return new RecipeSource(stock, root, Projected: true);
                    }
                    catch (InvalidDataException failure)
                    {
                        archiveCoverageFailure = failure;
                        // Keep any files that completed before this source failed.
                        filesToCopy = await FindMissingCacheInputsAsync(root, filesToCopy, ct);
                        if (filesToCopy.Count == 0)
                            break;
                    }
                }
            }
            CreateDirectory(root);
            EnsureSpace(root, filesToCopy.Sum(file => file.Length));
            CreateBaselineDirectories(root, inputs);
            foreach (var file in filesToCopy)
            {
                string destination = At(root, file.Path);
                if (!windows && await MatchesAsync(destination, file, ct))
                    continue;
                foreach (var entry in _configuration.Config.Ksp2Installs)
                {
                    string candidate = At(InstallRoot(entry), file.Path);
                    if (!await MatchesAsync(candidate, file, ct))
                        continue;
                    await CopyFileAsync(candidate, destination, file, ct);
                    break;
                }
            }
            if (await TreeMatchesAsync(root, inputs, ct))
                return new RecipeSource(stock, root, Projected: projected);
            if (projected)
                continue;
            foreach (var entry in _configuration.Config.Ksp2Installs)
            {
                string archive = At(InstallRoot(entry), "uninstall.zip");
                _paths.ValidateControlledPath(archive);
                if (!_fs.File.Exists(archive))
                    continue;
                if (windows)
                {
                    try
                    {
                        await CopyWindowsBaselineAsync(archive, InstallRoot(entry), stock, root, ct);
                    }
                    catch (InvalidDataException failure)
                    {
                        archiveCoverageFailure = failure;
                        continue;
                    }
                }
                else
                {
                    try
                    {
                        await _archive.VerifyAsync(archive, stock, ct);
                    }
                    catch (InvalidDataException)
                    {
                        continue;
                    }
                    string restored = At(CacheRoot(), $"restore-{Guid.NewGuid():N}");
                    await _archive.RestoreToNewDirectoryAsync(archive, stock, restored, ct);
                    foreach (StockBaselineFile file in stock.Files)
                    {
                        await CopyFileAsync(At(restored, file.Path), At(root, file.Path), file, ct);
                    }
                }
                if (await TreeMatchesAsync(root, stock, ct))
                    return new RecipeSource(stock, root);
            }
        }
        if (archiveCoverageFailure is not null)
            ExceptionDispatchInfo.Capture(archiveCoverageFailure).Throw();
        // Exhaust reusable bytes before invoking a signed-in storefront download.
        foreach (var definition in definitions.Where(definition => definition.SteamDepots.Count != 0))
        {
            StockBaselineManifest stock = StockBaselineManifest.FromJson(definition.Manifest);
            string root = BaselineRoot(stock);
            await _steam().DownloadDepotsAsync(SteamDepotDownloader.KSP2_APP_ID,
                definition.SteamDepots.Select(depot => new SteamDepot(depot.DepotId, depot.ManifestId, 0)).ToArray(),
                root, new Progress<SteamDownloadProgress>(_ => { }), ct);
            if (!await TreeMatchesAsync(root, stock, ct))
                throw new InvalidDataException("Steam download differs from the publisher-pinned complete stock contract.");
            return new RecipeSource(stock, root);
        }
        throw new InvalidDataException("No supported complete baseline is available from verified local files, a full archive or configured Steam depot selectors.");
    }

    private async Task<IReadOnlyList<StockBaselineFile>> FindMissingCacheInputsAsync(string root,
        IReadOnlyList<StockBaselineFile> files, CancellationToken ct)
    {
        if (!_fs.Directory.Exists(root))
            return files;

        var missing = new List<StockBaselineFile>();
        foreach (var file in files)
        {
            if (!await MatchesAsync(At(root, file.Path), file, ct))
                missing.Add(file);
        }
        return missing;
    }

    private Task CopyWindowsBaselineAsync(string archive, string install, StockBaselineManifest stock, string destination,
        CancellationToken ct, IReadOnlySet<string>? requestedPaths = null)
    {
        ValidateStockInputPaths(install, stock);
        return _archive.ReadAvailableSourcesAsync(archive, install, stock,
            async (file, input, token) =>
            {
                string path = At(destination, file.Path);
                if (!await MatchesAsync(path, file, token))
                    await WriteCacheCandidateAsync(input, path, file, token);
            }, ct,
            requestedPaths: requestedPaths);
    }

    private void CreateBaselineDirectories(string root, StockBaselineManifest stock)
    {
        foreach (string directory in stock.Directories)
        {
            CreateDirectory(At(root, directory));
            if (stock.HasUnixMetadata)
                StockArchive.ApplyUnixPermissions(_fs, At(root, directory), stock.UnixDirectoryModes[directory]);
        }
    }

    private void ValidateStockInputPaths(string root, StockBaselineManifest stock)
    {
        _paths.ValidateControlledPath(root);
        foreach (string path in stock.Files.Select(file => file.Path).Concat(stock.Directories))
        {
            _paths.ValidateControlledPath(At(root, path));
        }
    }

    private async Task<StockBaselineFile> ObserveArchiveAsync(string path, CancellationToken ct)
    {
        _paths.ValidateControlledPath(path);
        await using FileSystemStream input = _fs.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 130L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Existing backup exceeds its byte budget.");
        return new StockBaselineFile(path, input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)));
    }

    private async Task<StockBaselineManifest> CanonicalArchiveOwnershipAsync(string root, StockBaselineManifest stock,
        FullSourceReleaseDescriptor descriptor, Ksp2InstallEntry entry, CancellationToken ct)
    {
        var published = Ownership(StockBaselineManifest.FromJson(descriptor.TargetManifest), entry);
        if (await TreeMatchesAsync(root, published, ct))
            return published;
        return Ownership(stock, entry);
    }

    private static StockBaselineManifest Ownership(StockBaselineManifest stock, Ksp2InstallEntry entry)
    {
        string[] files = entry.FullSourceRetainedUnownedFiles.ToArray();
        string[] directories = entry.FullSourceUnownedDirectories.ToArray();
        _ = new StockBaselineManifest("explicit-exclusions", "configured initial ownership exclusions",
            files.Select(path => new StockBaselineFile(path, 0, new string('0', 64))), directories);
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
            directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length)
            throw new InvalidDataException("Initial ownership exclusions are duplicated.");
        return new StockBaselineManifest(stock.BaselineIdentity, stock.Provenance,
            stock.Files.Where(file => !files.Contains(file.Path, StringComparer.Ordinal)), stock.Directories,
            stock.HasUnixMetadata ? stock.UnixDirectoryModes : null);
    }

    private async Task<bool> TreeMatchesAsync(string root, StockBaselineManifest manifest, CancellationToken ct)
    {
        if (!TreeDirectoriesMatch(root, manifest, ct))
            return false;

        foreach (var file in manifest.Files)
        {
            if (!await MatchesAsync(At(root, file.Path), file, ct))
                return false;
        }
        return true;
    }

    private bool TreeDirectoriesMatch(string root, StockBaselineManifest manifest, CancellationToken ct)
    {
        _paths.ValidateControlledPath(root);
        if (!_fs.Directory.Exists(root))
            return false;
        foreach (string directory in manifest.Directories)
        {
            ct.ThrowIfCancellationRequested();
            string path = At(root, directory);
            _paths.ValidateControlledPath(path);
            if (!_fs.Directory.Exists(path))
                return false;
            if (manifest.HasUnixMetadata && !StockArchive.UnixPermissionsMatch(_fs, path, manifest.UnixDirectoryModes[directory]))
                return false;
        }
        return true;
    }

    private async Task<bool> MatchesAsync(string path, StockBaselineFile expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _paths.ValidateControlledPath(path);
        if (!_fs.File.Exists(path) || _fs.FileInfo.New(path).Length != expected.Length)
            return false;
        await using var input = _fs.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase) &&
            (expected.UnixMode is null || StockArchive.UnixPermissionsMatch(_fs, path, expected.UnixMode.Value));
    }

    private async Task CopyFileAsync(string source, string destination, StockBaselineFile expected, CancellationToken ct)
    {
        if (await MatchesAsync(destination, expected, ct))
            return;
        _paths.ValidateControlledPath(source);
        if (!_fs.File.Exists(source) || _fs.FileInfo.New(source).Length != expected.Length)
            throw new InvalidDataException("A prepared download or reused baseline file differs from its independent contract.");
        await using var input = _fs.FileStream.New(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await WriteCacheCandidateAsync(input, destination, expected, ct);
    }

    private async Task WriteCacheCandidateAsync(Stream input, string destination, StockBaselineFile expected, CancellationToken ct)
    {
        CreateDirectory(_fs.Path.GetDirectoryName(destination)!);
        string candidates = At(CacheRoot(), "copy-candidates");
        CreateDirectory(candidates);
        string candidate = At(candidates, Guid.NewGuid().ToString("N"));
        _paths.ValidateControlledPath(candidate);
        await using (var output = _fs.FileStream.New(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    if (written + read > expected.Length)
                        throw new InvalidDataException("A preparation input grew beyond its independent file bound. Candidate retained.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
            await output.FlushAsync(ct);
        }
        if (expected.UnixMode is not null)
            StockArchive.ApplyUnixPermissions(_fs, candidate, expected.UnixMode.Value);
        if (!await MatchesAsync(candidate, expected, ct))
            throw new InvalidDataException("A source file changed during preparation. Candidate evidence was retained.");
        _paths.ValidateControlledPath(destination);
        StockArchive.PublishFile(_fs, candidate, destination, _fs.File.Exists(destination));
    }

    private PatchDownloadRequest Request(FeedInfo feed, string url, long length, string sha256, string version)
    {
        string storage = At(CacheRoot(), "downloads");
        string patches = At(storage, "patches");
        CreateDirectory(patches);
        string completed = At(patches, $"{sha256.ToUpperInvariant()}.patch");
        foreach (string path in new[] { completed, completed + ".partial", completed + ".tmp" })
        {
            _paths.ValidateControlledPath(path);
        }
        return new PatchDownloadRequest(feed, new ReleasePatch
        {
            Version = version, Requires = new PatchRequirement(), ReleasedAt = DateTime.MinValue,
            Url = url, Size = length, ChecksumSha256 = sha256
        }, storage);
    }

    private FullSourceReleaseIdentity Identity(AuthorityRelease release)
    {
        var descriptor = release.Descriptor;
        string normalized = descriptor.GetNormalizedTargetManifestSha256();
        return new FullSourceReleaseIdentity(descriptor.ReleaseId, descriptor.Channel, descriptor.Version, normalized, release.Authority)
        {
            PublishedManifestSha256 = normalized.Equals(descriptor.TargetManifestSha256, StringComparison.OrdinalIgnoreCase)
                ? null : descriptor.TargetManifestSha256.ToUpperInvariant()
        };
    }

    private string ArtifactUrl(string r2Url, string githubUrl) =>
        _configuration.Config.PatchDownloadSource == PatchDownloadSource.R2 ? r2Url : githubUrl;

    private string BaselineRoot(StockBaselineManifest stock) => At(CacheRoot(), $"baselines/{Digest(stock.ToJson())}");

    private string CacheRoot()
    {
        string root = At(_configuration.GetLocalStorageDirectory(), "full-source-preparation-v1");
        _paths.ValidateControlledPath(root);
        foreach (var entry in _configuration.Config.Ksp2Installs)
        {
            string install = InstallRoot(entry);
            if (Overlaps(root, install))
                throw new InvalidDataException("Preparation storage overlaps a configured installation.");
        }
        return root;
    }

    private FileSystemStream Lease()
    {
        string root = CacheRoot();
        CreateDirectory(root);
        string path = At(root, ".preparation.lock");
        _paths.ValidateControlledPath(path);
        FileSystemStream lease = _fs.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Under the lease, so no other preparation is using what an earlier run left in the trash folder.
        SweepTrash();
        return lease;
    }

    private void RequireCacheChild(string path, string folder)
    {
        _paths.ValidateControlledPath(path);
        string parent = At(CacheRoot(), folder);
        if (!path.StartsWith(parent + _fs.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            _fs.Path.GetDirectoryName(path) != parent)
            throw new InvalidDataException("Prepared input is outside its controlled cache stage.");
    }

    private Ksp2InstallEntry Entry(Guid id) => _configuration.Config.Ksp2Installs.Single(entry => entry.Id == id && id != Guid.Empty);

    private string InstallRoot(Ksp2InstallEntry entry)
    {
        _paths.ValidateControlledPath(entry.ExePath);
        if (_fs.Path.GetFileName(entry.ExePath) != Ksp2Install.KSP2_EXE_NAME)
            throw new InvalidDataException("The configured executable is not a KSP2 installation identity.");
        return _fs.Path.GetDirectoryName(entry.ExePath)!;
    }

    private void CreateDirectory(string path)
    {
        _paths.ValidateControlledPath(path);
        StockArchive.CreateDirectory(_fs, path);
    }

    private void EnsureSpace(string path, long bytes)
    {
        long? available = _space.GetAvailableFreeSpace(path);
        if (available is null || available < checked(bytes + 512L * 1024 * 1024))
            throw new IOException($"Insufficient or unknown preparation space. Required {bytes} bytes plus reserve.");
    }

    private void EnsureSupported()
    {
        if (!_os.IsWindows() && !_os.IsLinux())
            throw new PlatformNotSupportedException("Full-source preparation requires a verified Windows or Linux host adapter.");
    }

    private string At(string root, string path) => _fs.Path.Combine(root, path.Replace('/', _fs.Path.DirectorySeparatorChar));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private bool Overlaps(string left, string right) => _paths.PathsOverlap(left, right);
}
