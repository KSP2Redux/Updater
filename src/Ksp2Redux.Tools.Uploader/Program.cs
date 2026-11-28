using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Ksp2Redux.Tools.Common.Distribution;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Uploader;
using Octokit;
using Testably.Abstractions;
using Tomlyn;

if (args is ["capture-prepatch-baseline", string recoveryStock, string recoveryContract, string recoveryStockDigest,
    string recoveryPatch, string recoveryPatchDigest, string recoveryRoot, string recoveryManifest, string recoveryBackup,
    "--stock-recovery", string recoveryArchive])
    return await FullSourcePublication.CapturePrepatchAsync(recoveryStock, recoveryContract, recoveryStockDigest,
        recoveryPatch, recoveryPatchDigest, recoveryRoot, recoveryManifest, recoveryBackup, recoveryArchive);
if (args is ["capture-prepatch-baseline", string stockInput, string stockContract, string stockDigest,
    string prepatchInput, string prepatchDigest, string derivedRoot, string derivedContract, string originalBackup])
    return await FullSourcePublication.CapturePrepatchAsync(stockInput, stockContract, stockDigest,
        prepatchInput, prepatchDigest, derivedRoot, derivedContract, originalBackup);
if (args is ["capture-full-source-target", string relocatedPlayer, string relocatedManifest, string relocatedId, "--relocated-receipt"])
    return FullSourcePublication.CaptureTarget(relocatedPlayer, relocatedManifest, relocatedId, allowRelocatedReceipt: true);
if (args is ["restore-stock", string restoredArchive, string restoredContract, string restoredDigest, string restoredDestination])
    return await FullSourcePublication.RestoreStockAsync(restoredArchive, restoredContract, restoredDigest, restoredDestination);
if (args is ["verify-prepared", string verifiedPublication, string verifiedSource, string verifiedSourceManifest, string verifiedStage])
    return FullSourcePublication.VerifyPrepared(verifiedPublication, verifiedSource, verifiedSourceManifest, verifiedStage);
if (args is ["inspect-player", string inspectedPlayer])
    return FullSourcePublication.InspectPlayer(inspectedPlayer);
if (args is ["prepare-full-source", string optionsPath])
    return FullSourcePublication.Prepare(optionsPath);
if (args is ["capture-full-source-target", string playerRoot, string targetManifest, string releaseId])
    return FullSourcePublication.CaptureTarget(playerRoot, targetManifest, releaseId);

bool validateFullSource = args is ["validate-full-source", _];
if (args.Length != 1 && !validateFullSource)
{
    Console.Error.WriteLine("Usage: Ksp2Redux.Tools.Uploader <upload-manifest.toml>");
    Console.Error.WriteLine("       Ksp2Redux.Tools.Uploader capture-full-source-target <player-root> <target-manifest.json> <release-id>");
    Console.Error.WriteLine("       Ksp2Redux.Tools.Uploader prepare-full-source <publication-options.json>");
    Console.Error.WriteLine("       Ksp2Redux.Tools.Uploader validate-full-source <upload-manifest.toml>");
    return 1;
}

string manifestPath = args[^1];
var uploadManifest = TomlSerializer.Deserialize<UploadManifest>(
    File.ReadAllText(manifestPath), new TomlSerializerOptions { SourceName = manifestPath });
if (uploadManifest is null)
    throw new InvalidDataException("Failed to parse upload manifest.");
uploadManifest.ValidatePatchRequirements();

string[] repositoryParts = uploadManifest.Repository.Split('/');
if (repositoryParts.Length != 2 || repositoryParts.Any(string.IsNullOrWhiteSpace))
    throw new InvalidOperationException("Repository must use the owner/name form.");
string repoOwner = repositoryParts[0];
string repoName = repositoryParts[1];
string tag = $"v{uploadManifest.Version}";
bool isMigration = uploadManifest.MigrateExisting;
bool hasFullSource = !string.IsNullOrWhiteSpace(uploadManifest.FullSourcePublication);
if (hasFullSource)
    tag = $"recipe-v{uploadManifest.Version}";
bool isDeleteOnly = !isMigration && !hasFullSource && uploadManifest.Patches is not { Count: > 0 };
bool isPublicChannel = uploadManifest.Channel is "stable" or "beta";
bool isRecipeFeed = uploadManifest.File.EndsWith(".v3.json", StringComparison.OrdinalIgnoreCase);
if (isRecipeFeed)
{
    ReleaseManifestValidator.RequireRecipeFeedPath(uploadManifest.File);
    if (!hasFullSource && !isDeleteOnly)
        throw new InvalidOperationException("Legacy patch assets cannot be published to a recipe feed.");
}
if (hasFullSource && (isMigration || uploadManifest.Patches is { Count: > 0 }))
    throw new InvalidOperationException("Upload a full-source publication separately from legacy patches and migration.");
var fullSource = hasFullSource ? FullSourceUpload.Read(uploadManifest, manifestPath, repoOwner, repoName, tag) : null;
if (validateFullSource)
{
    if (fullSource is null)
        throw new InvalidOperationException("Set FullSourcePublication to the prepared publication directory.");
    foreach (var asset in fullSource.Assets)
    {
        using var file = asset.OpenVerified();
    }
    Console.WriteLine($"Verified {fullSource.Assets.Count} local full-source release assets ({fullSource.Assets.Sum(asset => asset.Size)} bytes).");
    return 0;
}
if (isMigration && !isPublicChannel)
    throw new InvalidOperationException("Historical migration is supported only for stable and beta feeds.");
if (uploadManifest.StageOnly && !isMigration)
    throw new InvalidOperationException("Stage-only publication is supported only for historical migration.");
if (uploadManifest.R2ManifestOnly && !isMigration)
    throw new InvalidOperationException("R2-manifest-only publication is supported only for historical migration.");
if (uploadManifest.StageOnly && uploadManifest.R2ManifestOnly)
    throw new InvalidOperationException("StageOnly and R2ManifestOnly cannot both be enabled.");
if (isPublicChannel && !HasR2Configuration(uploadManifest))
    throw new InvalidOperationException("Stable and beta publication require the EU R2 configuration.");
if (isDeleteOnly && string.IsNullOrWhiteSpace(uploadManifest.Label))
    throw new InvalidOperationException("The manifest has no patches and no label.");

Console.WriteLine(
    $"Publishing {tag} to {uploadManifest.Channel} as {(uploadManifest.Prerelease ? "a prerelease" : "a release")}.");

var github = new GitHubClient(new ProductHeaderValue("Ksp2Redux.Tools.Uploader"))
{
    Credentials = new Credentials(uploadManifest.ResolveToken(SystemEnvironmentProvider.Instance))
};
// Octokit enforces separate client and request timeouts. Keep both limits aligned for large uploads.
github.SetRequestTimeout(TimeSpan.FromSeconds(600));
if ((hasFullSource || isRecipeFeed) && !isPublicChannel && !(await github.Repository.Get(repoOwner, repoName)).Private)
    throw new InvalidOperationException("Internal recipe publication requires a private repository.");
string workingDirectory = Path.Combine(Path.GetTempPath(), $"ksp2redux-upload-{Guid.NewGuid():N}");
Directory.CreateDirectory(workingDirectory);

try
{
    RepositoryContent? feedFile;
    try
    {
        feedFile = await GetFeedFileAsync(github, repoOwner, repoName, uploadManifest);
    }
    catch (NotFoundException) when (fullSource is not null)
    {
        feedFile = null;
    }
    string? existingJson = feedFile?.Content;
    if (feedFile is null)
        existingJson = JsonSerializer.Serialize(new ReleaseManifest
        {
            SchemaVersion = 3, Channel = uploadManifest.Channel!, GeneratedAt = DateTime.UtcNow, Patches = []
        });
    else if (string.IsNullOrEmpty(existingJson))
    {
        // Fetch the exact blob identified by the feed SHA used for the conditional update.
        var blob = await github.Git.Blob.Get(repoOwner, repoName, feedFile.Sha);
        existingJson = Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content));
    }
    var feedJson = JsonSerializer.Deserialize<ReleaseManifest>(existingJson!)
                   ?? throw new InvalidDataException("Failed to parse the existing release manifest.");
    uploadManifest.ValidateFeedDestination(feedJson);
    string? preparedSourceFeed = fullSource?.PrepareFeed(feedJson, uploadManifest.RetainBetaReleases, uploadManifest.File);
    var additions = new List<ReleasePatch>();
    Release? release = null;
    List<ReleaseAsset> existingAssets = [];
    using var r2 = isPublicChannel ? new R2Publisher(uploadManifest) : null;

    // Every channel uploads its patches in parts. GitHub rejects release assets of 2 GiB or more,
    // and full patches have grown past that.
    var packagedPatches = new List<(PatchUploadEntry Entry, PackagedPatch Packaged)>();
    foreach (var patchEntry in uploadManifest.Patches ?? [])
    {
        string packageDirectory = Path.Combine(workingDirectory, Guid.NewGuid().ToString("N"));
        var packaged = await PatchChunker.SplitAsync(
            new RealFileSystem(), patchEntry.File, packageDirectory, PatchChunker.DEFAULT_CHUNK_SIZE);
        await VerifyReconstructionAsync(packaged);
        packagedPatches.Add((patchEntry, packaged));
    }

    if (!isDeleteOnly)
    {
        release = await GetOrCreateReleaseAsync(github, repoOwner, repoName, tag, uploadManifest,
            ReadChangelogSection(uploadManifest.Changelog), stageRelease: hasFullSource);
        existingAssets = (await github.Repository.Release.GetAllAssets(repoOwner, repoName, release.Id)).ToList();
        Console.WriteLine($"Found {existingAssets.Count} existing GitHub release asset(s).");

        if (!isPublicChannel)
        {
            var plannedAssetNames = packagedPatches
                .SelectMany(item => item.Packaged.Chunks.Select(chunk => GetChunkAssetName(item.Packaged, chunk)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await DeleteStalePatchAssetsAsync(github, repoOwner, repoName, existingAssets, plannedAssetNames);
        }
    }

    DateTime releasedAt = DateTime.UtcNow;
    if (fullSource is not null)
    {
        await UploadFullSourceAsync(github, release!, existingAssets, r2!, fullSource);
        if (release!.Draft)
        {
            var publish = release.ToUpdate();
            publish.Draft = false;
            release = await github.Repository.Release.Edit(repoOwner, repoName, release.Id, publish);
            if (release.Draft)
                throw new InvalidOperationException("The verified GitHub release remained a draft.");
            var published = (await github.Repository.Release.GetAllAssets(repoOwner, repoName, release.Id))
                .ToDictionary(asset => asset.Name, StringComparer.Ordinal);
            foreach (var asset in fullSource.Assets)
            {
                if (!published.TryGetValue(asset.Name, out var remote))
                    throw new InvalidDataException($"Published GitHub release is missing {asset.Name}.");
                VerifyFullSourceDownloadPath(remote, fullSource, asset);
            }
        }
    }
    if (isMigration)
    {
        feedJson.Patches = await MigrateLegacyPatchesAsync(
            feedJson.Patches, uploadManifest, github, repoOwner, repoName,
            r2!, workingDirectory);
    }
    foreach (var (patchEntry, packaged) in packagedPatches)
    {
        string requiredVersion = string.IsNullOrWhiteSpace(patchEntry.PreviousVersion)
            ? "base"
            : Uri.EscapeDataString(patchEntry.PreviousVersion);
        string prefix =
            $"patches/{uploadManifest.Channel}/{Uri.EscapeDataString(uploadManifest.Version)}/{requiredVersion}/{packaged.ChecksumSha256.ToLowerInvariant()}";
        var chunks = new List<ReleasePatchChunk>();

        foreach (var chunk in packaged.Chunks)
        {
            string? r2Url = null;
            if (isPublicChannel)
            {
                string objectKey = $"{prefix}/{GetChunkPartName(packaged, chunk)}";
                await r2!.UploadImmutableAsync(objectKey, chunk.Path, chunk.ChecksumSha256, CancellationToken.None);
                r2Url = r2.GetPublicUrl(objectKey);
            }
            var asset = await UploadExactAssetAsync(
                github, release!, existingAssets, chunk.Path, GetChunkAssetName(packaged, chunk), chunk.Size);
            chunks.Add(new ReleasePatchChunk
            {
                Index = chunk.Index,
                Size = chunk.Size,
                ChecksumSha256 = chunk.ChecksumSha256,
                // Internal feeds have no R2 copy. Launchers up to updater-v0.6.3.7 reject a part without
                // an R2 URL, so the GitHub URL fills both until a launcher that accepts null is released.
                // They resolve it through their authenticated GitHub path, as with legacy internal patches.
                R2Url = r2Url ?? asset.BrowserDownloadUrl,
                GitHubUrl = asset.BrowserDownloadUrl
            });
        }

        additions.Add(new ReleasePatch
        {
            Version = uploadManifest.Version,
            Label = uploadManifest.Label,
            ReleasedAt = releasedAt,
            ChecksumSha256 = packaged.ChecksumSha256,
            Size = packaged.Size,
            Requires = new PatchRequirement { Version = patchEntry.PreviousVersion },
            Chunks = chunks
        });
    }

    List<ReleasePatch> removed = [];
    if (preparedSourceFeed is null)
    {
        removed = uploadManifest.ApplyRetention(feedJson, additions);
        feedJson.GeneratedAt = DateTime.UtcNow;
        if (isPublicChannel || feedJson.Patches.Any(patch => patch.Chunks is not null))
            feedJson.SchemaVersion = Math.Max(feedJson.SchemaVersion, 2);
        ReleaseManifestValidator.Validate(feedJson);
        preparedSourceFeed = JsonSerializer.Serialize(feedJson, new JsonSerializerOptions { WriteIndented = true });
    }
    string feedContent = preparedSourceFeed;

    if (uploadManifest.StageOnly)
    {
        var currentFeed = await GetFeedFileAsync(github, repoOwner, repoName, uploadManifest);
        if (!string.Equals(currentFeed.Sha, feedFile?.Sha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The live manifest {uploadManifest.File} changed while chunks were being staged. It was left untouched; rerun staging to include the new content.");

        Console.WriteLine(
            $"Staging complete: prepared schema {feedJson.SchemaVersion} with {feedJson.Patches.Count} patch entries. The live {uploadManifest.File} manifest was left unchanged; no cache purge or retention deletion ran.");
        await r2!.ReportUsageAsync(CancellationToken.None);
        return 0;
    }

    if (uploadManifest.R2ManifestOnly)
    {
        var currentFeed = await GetFeedFileAsync(github, repoOwner, repoName, uploadManifest);
        if (!string.Equals(currentFeed.Sha, feedFile?.Sha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The live GitHub manifest {uploadManifest.File} changed while the R2 manifest was prepared. Nothing was published; rerun to include the new content.");

        await r2!.UploadManifestAsync(uploadManifest.File, feedContent, CancellationToken.None);
        string r2ManifestUrl = r2.GetPublicUrl(uploadManifest.File);
        await PurgeManifestAsync(uploadManifest, r2ManifestUrl);
        await VerifyPublicManifestAsync(r2ManifestUrl, feedContent);

        var unchangedGitHubFeed = await GetFeedFileAsync(github, repoOwner, repoName, uploadManifest);
        if (!string.Equals(unchangedGitHubFeed.Sha, feedFile?.Sha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The GitHub manifest {uploadManifest.File} changed during R2 publication. Rerun R2-manifest-only publication so both feeds describe the same releases.");

        Console.WriteLine(
            $"Published schema {feedJson.SchemaVersion} to R2 only. The live GitHub {uploadManifest.File} manifest was left unchanged; no retention deletion ran.");
        await r2.ReportUsageAsync(CancellationToken.None);
        return 0;
    }

    uploadManifest.ValidateFeedDestination(feedJson);
    string message = isDeleteOnly ? $"Cleanup for label {uploadManifest.Label}" : $"Update for release {uploadManifest.Version}";
    string feedCommit;
    if (feedFile is null)
    {
        ReleaseManifestValidator.RequireRecipeFeedPath(uploadManifest.File);
        var created = await github.Repository.Content.CreateFile(repoOwner, repoName, uploadManifest.File,
            new CreateFileRequest(message, feedContent, uploadManifest.Branch));
        feedCommit = created.Commit.Sha;
    }
    else
    {
        var updated = await github.Repository.Content.UpdateFile(repoOwner, repoName, uploadManifest.File,
            new UpdateFileRequest(message, feedContent, feedFile.Sha, uploadManifest.Branch));
        feedCommit = updated.Commit.Sha;
    }
    Console.WriteLine($"Published GitHub recipe/patch manifest with commit {feedCommit}.");

    if (isPublicChannel)
    {
        await r2!.UploadManifestAsync(uploadManifest.File, feedContent, CancellationToken.None);
        string r2ManifestUrl = r2.GetPublicUrl(uploadManifest.File);
        await PurgeManifestAsync(uploadManifest, r2ManifestUrl);
        await VerifyPublicManifestAsync(r2ManifestUrl, feedContent);
        // UpdateFile returning a commit is the GitHub publication confirmation.
        // Do not verify through raw.githubusercontent.com here: branch URLs are cached
        // and can continue serving the previous manifest for minutes after the commit.
        // Stable R2 objects are permanent by policy, including objects retired by a
        // same-version replacement. Only beta rotation removes retired R2 content.
        if (uploadManifest.Channel == "beta")
        {
            var activeKeys = feedJson.Patches
                .SelectMany(patch => patch.Chunks ?? [])
                .Select(chunk => TryGetR2ObjectKey(uploadManifest.R2PublicBaseUrl, chunk.R2Url))
                .Where(key => key is not null)
                .Select(key => key!)
                .ToHashSet(StringComparer.Ordinal);
            var retiredKeys = removed
                .SelectMany(patch => patch.Chunks ?? [])
                .Select(chunk => TryGetR2ObjectKey(uploadManifest.R2PublicBaseUrl, chunk.R2Url))
                .Where(key => key is not null && !activeKeys.Contains(key))
                .Select(key => key!);
            await r2.DeleteObjectsAsync(retiredKeys, CancellationToken.None);
        }
        await r2.ReportUsageAsync(CancellationToken.None);
    }

    // Deliberately last. External version checkers poll the swinfo document, so it
    // announces a release only once the feed commit and the verified R2 publication
    // have both landed. A label cleanup publishes no version and leaves it alone.
    if (!isDeleteOnly && !string.IsNullOrWhiteSpace(uploadManifest.SwinfoFile))
        await UpdateSwinfoVersionAsync(
            github, repoOwner, repoName, uploadManifest.SwinfoFile,
            uploadManifest.Version, uploadManifest.Branch);
}
finally
{
    Directory.Delete(workingDirectory, true);
}

return 0;

static async Task<RepositoryContent> GetFeedFileAsync(GitHubClient github, string owner, string repo, UploadManifest upload)
{
    string path = string.Join('/', upload.File.Split('/').Select(Uri.EscapeDataString));
    var uri = new Uri($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{path}", UriKind.Relative);
    var response = await github.Connection.Get<RepositoryContent>(uri,
        new Dictionary<string, string> { ["ref"] = upload.Branch }, "application/vnd.github.object+json");
    return response.Body;
}

static async Task UploadFullSourceAsync(GitHubClient github, Release release, List<ReleaseAsset> existingAssets,
    R2Publisher? r2, FullSourceUpload publication)
{
    var existing = existingAssets.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
    if (existing.Count + publication.Assets.Count(asset => !existing.ContainsKey(asset.Name)) > ReleaseRecipePipeline.MAX_HOSTED_PACKS + 1)
        throw new InvalidOperationException("This GitHub release has insufficient asset slots for the full-source artifact.");

    foreach (var asset in publication.Assets)
    {
        if (existing.TryGetValue(asset.Name, out var remote))
            await VerifyFullSourceAssetAsync(github, remote, publication, asset, release.Draft);
    }
    foreach (var asset in publication.Assets)
    {
        using var local = asset.OpenVerified();
        if (r2 is not null)
            await r2.UploadImmutableAsync(publication.R2Prefix + asset.Name, asset.Path, asset.Sha256, CancellationToken.None);
        if (existing.ContainsKey(asset.Name))
            continue;
        var uploaded = await UploadExactAssetAsync(github, release, existingAssets, asset.Path, asset.Name, asset.Size);
        await VerifyFullSourceAssetAsync(github, uploaded, publication, asset, release.Draft);
    }
}

static async Task VerifyFullSourceAssetAsync(GitHubClient github, ReleaseAsset remote, FullSourceUpload publication,
    FullSourceUpload.Asset local, bool draft)
{
    if (remote.Size != local.Size || remote.Name != local.Name)
        throw new InvalidDataException($"GitHub release asset {local.Name} differs from the prepared name or length.");
    if (!draft)
        VerifyFullSourceDownloadPath(remote, publication, local);
    if (!Uri.TryCreate(remote.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "api.github.com")
        throw new InvalidDataException("Unexpected GitHub asset metadata endpoint.");
    var response = await github.Connection.Get<FullSourceAssetIdentity>(url, null);
    if (!string.Equals(response.Body.Digest, $"sha256:{local.Sha256}", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"GitHub release asset {local.Name} differs from the prepared SHA-256.");
}

static void VerifyFullSourceDownloadPath(ReleaseAsset remote, FullSourceUpload publication, FullSourceUpload.Asset local)
{
    if (remote.Size != local.Size ||
        !string.Equals(remote.BrowserDownloadUrl, publication.GitHubPrefix + local.Name, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"GitHub release asset {local.Name} differs from the prepared path or length.");
}

static bool HasR2Configuration(UploadManifest manifest) =>
    !string.IsNullOrWhiteSpace(manifest.R2AccountId) &&
    !string.IsNullOrWhiteSpace(manifest.R2Endpoint) &&
    !string.IsNullOrWhiteSpace(manifest.R2Bucket) &&
    !string.IsNullOrWhiteSpace(manifest.R2AccessKeyId) &&
    !string.IsNullOrWhiteSpace(manifest.R2SecretAccessKey) &&
    !string.IsNullOrWhiteSpace(manifest.CloudflareZoneId) &&
    !string.IsNullOrWhiteSpace(manifest.CloudflareApiToken);

static string GetChunkPartName(PackagedPatch packaged, PackagedPatchChunk chunk) =>
    $"part-{chunk.Index + 1:D4}-of-{packaged.Chunks.Count:D4}.bin";

static string GetChunkAssetName(PackagedPatch packaged, PackagedPatchChunk chunk) =>
    $"patch-{packaged.ChecksumSha256.ToLowerInvariant()}-{GetChunkPartName(packaged, chunk)}";

static async Task<List<ReleasePatch>> MigrateLegacyPatchesAsync(
    IReadOnlyList<ReleasePatch> patches,
    UploadManifest upload,
    GitHubClient github,
    string owner,
    string repo,
    R2Publisher r2,
    string workingDirectory)
{
    var migrated = new List<ReleasePatch>(patches.Count);
    using var http = new HttpClient();
    foreach (var patch in patches)
    {
        if (patch.Chunks is not null || patch.FullSource is not null)
        {
            migrated.Add(patch);
            continue;
        }
        if (string.IsNullOrWhiteSpace(patch.Url))
            throw new InvalidDataException($"Patch {patch.Version} has no transport to migrate.");

        string tag = GetReleaseTag(patch.Url);
        string patchDirectory = Path.Combine(workingDirectory, $"migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(patchDirectory);
        string localPatch = Path.Combine(patchDirectory, "source.patch");

        using (var response = await http.GetAsync(patch.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(localPatch);
            await input.CopyToAsync(output);
        }
        var fileInfo = new FileInfo(localPatch);
        if (fileInfo.Length != patch.Size || !string.Equals(GetChecksum(localPatch), patch.ChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Published patch {patch.Version} failed its manifest checksum or size.");

        var packaged = await PatchChunker.SplitAsync(
            new RealFileSystem(), localPatch, Path.Combine(patchDirectory, "parts"),
            PatchChunker.DEFAULT_CHUNK_SIZE);
        await VerifyReconstructionAsync(packaged);
        var release = await github.Repository.Release.Get(owner, repo, tag);
        var releaseAssets = (await github.Repository.Release.GetAllAssets(owner, repo, release.Id)).ToList();
        string requiredVersion = patch.Requires.IsBasePatch
            ? "base"
            : Uri.EscapeDataString(patch.Requires.Version!);
        string prefix =
            $"patches/{upload.Channel}/{Uri.EscapeDataString(patch.Version)}/{requiredVersion}/{packaged.ChecksumSha256.ToLowerInvariant()}";
        var chunks = new List<ReleasePatchChunk>();
        foreach (var chunk in packaged.Chunks)
        {
            string objectKey = $"{prefix}/{GetChunkPartName(packaged, chunk)}";
            await r2.UploadImmutableAsync(objectKey, chunk.Path, chunk.ChecksumSha256, CancellationToken.None);
            var asset = await UploadExactAssetAsync(
                github, release, releaseAssets, chunk.Path, GetChunkAssetName(packaged, chunk), chunk.Size);
            chunks.Add(new ReleasePatchChunk
            {
                Index = chunk.Index,
                Size = chunk.Size,
                ChecksumSha256 = chunk.ChecksumSha256,
                R2Url = r2.GetPublicUrl(objectKey),
                GitHubUrl = asset.BrowserDownloadUrl
            });
        }
        migrated.Add(patch with { Url = null, Chunks = chunks });
    }
    return migrated;
}

static string GetReleaseTag(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        throw new InvalidDataException($"Invalid GitHub release URL: {url}");
    string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    int downloadIndex = Array.FindIndex(segments, segment => segment == "download");
    if (downloadIndex < 0 || downloadIndex + 1 >= segments.Length)
        throw new InvalidDataException($"Cannot determine a GitHub release tag from {url}.");
    return Uri.UnescapeDataString(segments[downloadIndex + 1]);
}

static async Task<Release> GetOrCreateReleaseAsync(
    GitHubClient github, string owner, string repo, string tag, UploadManifest manifest, string? changelog, bool stageRelease)
{
    try
    {
        var release = await github.Repository.Release.Get(owner, repo, tag);
        Console.WriteLine($"Found existing GitHub release: {release.HtmlUrl}");
        if (stageRelease)
            return release;
        var update = release.ToUpdate();
        bool change = false;
        if (!string.IsNullOrWhiteSpace(changelog))
        {
            update.Body = changelog;
            change = true;
        }
        if (release.Prerelease != manifest.Prerelease)
        {
            update.Prerelease = manifest.Prerelease;
            change = true;
        }
        if (!change)
            return release;

        var updated = await github.Repository.Release.Edit(owner, repo, release.Id, update);
        Console.WriteLine(
            $"Updated existing GitHub release (changelog: {!string.IsNullOrWhiteSpace(changelog)}, prerelease: {updated.Prerelease}).");
        return updated;
    }
    catch (NotFoundException)
    {
        if (stageRelease)
        {
            var releases = await github.Repository.Release.GetAll(owner, repo);
            var draft = releases.SingleOrDefault(release => release.Draft && release.TagName == tag);
            if (draft is not null)
                return draft;
        }
        var created = await github.Repository.Release.Create(owner, repo, new NewRelease(tag)
        {
            Name = $"KSP2 Redux {manifest.Version}",
            Body = changelog ?? "Automated upload for KSP2 Redux",
            Draft = stageRelease,
            Prerelease = manifest.Prerelease
        });
        Console.WriteLine($"Created GitHub release: {created.HtmlUrl}");
        return created;
    }
}

// An internal release holds the patches of one version only, so a rerun replaces every patch
// asset this run does not reuse: whole patches from older uploader runs and parts whose
// checksum changed. Parts with the same name are kept and verified by size on upload.
static async Task DeleteStalePatchAssetsAsync(
    GitHubClient github, string owner, string repo,
    List<ReleaseAsset> existingAssets, IReadOnlySet<string> plannedAssetNames)
{
    var staleAssets = existingAssets.Where(asset =>
        !plannedAssetNames.Contains(asset.Name) &&
        (asset.Name.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) ||
         asset.Name.StartsWith("patch-", StringComparison.OrdinalIgnoreCase)));
    foreach (var stale in staleAssets.ToList())
    {
        await github.Repository.Release.DeleteAsset(owner, repo, stale.Id);
        existingAssets.Remove(stale);
        Console.WriteLine($"Deleted existing GitHub asset before upload: {stale.Name}");
    }
}

static async Task<ReleaseAsset> UploadExactAssetAsync(
    GitHubClient github, Release release, List<ReleaseAsset> existingAssets,
    string path, string assetName, long expectedSize)
{
    var existing = existingAssets.FirstOrDefault(asset => asset.Name == assetName);
    if (existing is not null)
    {
        if (existing.Size != expectedSize)
            throw new InvalidOperationException(
                $"GitHub asset '{assetName}' exists with {existing.Size} bytes, expected {expectedSize}.");
        Console.WriteLine($"Verified existing GitHub asset: {assetName} ({expectedSize} bytes)");
        return existing;
    }
    Console.WriteLine($"Uploading GitHub asset: {assetName} ({expectedSize} bytes)");
    await using var stream = File.OpenRead(path);
    var asset = await github.Repository.Release.UploadAsset(release, new ReleaseAssetUpload
    {
        FileName = assetName,
        ContentType = "application/octet-stream",
        RawData = stream,
        Timeout = TimeSpan.FromSeconds(600)
    });
    if (asset.Size != expectedSize)
        throw new InvalidOperationException(
            $"GitHub reports {asset.Size} bytes for '{assetName}', expected {expectedSize}.");
    existingAssets.Add(asset);
    Console.WriteLine($"Uploaded GitHub asset: {asset.BrowserDownloadUrl}");
    return asset;
}

static async Task VerifyReconstructionAsync(PackagedPatch packaged)
{
    await using var source = File.OpenRead(packaged.PatchPath);
    var sourceBuffer = new byte[1024 * 1024];
    var partBuffer = new byte[1024 * 1024];
    foreach (var chunk in packaged.Chunks)
    {
        await using var part = File.OpenRead(chunk.Path);
        int partRead;
        while ((partRead = await part.ReadAsync(partBuffer)) > 0)
        {
            int sourceRead = await source.ReadAtLeastAsync(sourceBuffer.AsMemory(0, partRead), partRead, false);
            if (sourceRead != partRead || !sourceBuffer.AsSpan(0, partRead).SequenceEqual(partBuffer.AsSpan(0, partRead)))
                throw new InvalidDataException("Packaged patch reconstruction differs from its source.");
        }
    }
    if (source.ReadByte() != -1)
        throw new InvalidDataException("Packaged patch reconstruction ended before its source.");
}

static async Task PurgeManifestAsync(UploadManifest manifest, string url)
{
    using var client = new HttpClient();
    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        $"https://api.cloudflare.com/client/v4/zones/{manifest.CloudflareZoneId}/purge_cache");
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
        "Bearer", manifest.CloudflareApiToken);
    request.Content = JsonContent.Create(new { files = new[] { url } });
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    Console.WriteLine($"Purged manifest URL: {url}");
}

static async Task VerifyPublicManifestAsync(string url, string expectedJson)
{
    using var client = new HttpClient();
    string expected = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(expectedJson));
    Exception? lastFailure = null;
    for (var attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            string actualJson = await client.GetStringAsync(url);
            string actual = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(actualJson));
            if (actual == expected)
            {
                Console.WriteLine($"Verified public manifest: {url}");
                return;
            }
            lastFailure = new InvalidDataException("Manifest content does not match the published document.");
        }
        catch (Exception ex)
        {
            lastFailure = ex;
        }
        await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
    }
    throw new InvalidOperationException($"Could not verify public manifest at {url}.", lastFailure);
}

// Rewrites the version field of a swinfo document and leaves the rest of the file
// byte for byte as it was, so the hand-authored formatting survives a publish.
// No read back verification here, unlike the release manifests: a swinfo file has no
// R2 copy, and raw.githubusercontent.com serves a cached blob for minutes after a
// commit, so verifying would fail on staleness rather than on anything real.
static async Task UpdateSwinfoVersionAsync(
    GitHubClient github, string owner, string repo, string swinfoFile, string version, string branch)
{
    var existing = (await github.Repository.Content.GetAllContentsByRef(
        owner, repo, swinfoFile, branch))[0];

    // Anchored on the quotes around the key, so neither ksp2_version nor version_check
    // can match. Exactly one hit is required, because a swinfo file that grew a second
    // version field or lost the one it had is not something to guess at mid publish.
    const string versionFieldPattern =
        """
        "version"\s*:\s*"([^"]*)"
        """;
    var matches = Regex.Matches(existing.Content, versionFieldPattern);
    if (matches.Count != 1)
        throw new InvalidDataException(
            $"Expected exactly one version field in {swinfoFile}, found {matches.Count}. The published release is unaffected; fix the file and rerun the publish.");

    var currentVersion = matches[0].Groups[1];
    if (string.Equals(currentVersion.Value, version, StringComparison.Ordinal))
    {
        Console.WriteLine($"{swinfoFile} already reports version {version}, so it was left unchanged.");
        return;
    }

    string updated = string.Concat(
        existing.Content.AsSpan(0, currentVersion.Index),
        version,
        existing.Content.AsSpan(currentVersion.Index + currentVersion.Length));

    var request = new UpdateFileRequest(
        $"Update version in {swinfoFile} for release {version}", updated, existing.Sha, branch);
    var result = await github.Repository.Content.UpdateFile(owner, repo, swinfoFile, request);
    Console.WriteLine(
        $"Updated {swinfoFile} from version {currentVersion.Value} to {version} with commit {result.Commit.Sha}.");
}

static string? TryGetR2ObjectKey(string publicBaseUrl, string? url)
{
    string prefix = $"{publicBaseUrl.TrimEnd('/')}/";
    return url is not null && url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? Uri.UnescapeDataString(url[prefix.Length..])
        : null;
}

static string GetChecksum(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string? ReadChangelogSection(string? changelogPath)
{
    if (string.IsNullOrWhiteSpace(changelogPath) || !File.Exists(changelogPath))
        return null;
    var section = new List<string>();
    foreach (string line in File.ReadLines(changelogPath))
    {
        if (line.Trim() == "---")
        {
            if (section.Count > 0)
                break;
            continue;
        }
        section.Add(line);
    }
    string text = string.Join('\n', section).Trim();
    return text.Length == 0 ? null : text;
}
