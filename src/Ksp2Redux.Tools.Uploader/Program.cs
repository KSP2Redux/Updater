using System.IO.Abstractions;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Distribution;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Uploader;
using Octokit;
using Testably.Abstractions;
using Tomlyn;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Ksp2Redux.Tools.Uploader <upload-manifest.toml>");
    return 1;
}

string manifestPath = args[0];
var uploadManifest = TomlSerializer.Deserialize<UploadManifest>(
    File.ReadAllText(manifestPath), new TomlSerializerOptions { SourceName = manifestPath });
if (uploadManifest is null)
    throw new InvalidDataException("Failed to parse upload manifest.");

string[] repositoryParts = uploadManifest.Repository.Split('/');
if (repositoryParts.Length != 2)
    throw new InvalidOperationException("Repository must use the owner/name form.");
string repoOwner = repositoryParts[0];
string repoName = repositoryParts[1];
string tag = "v" + uploadManifest.Version;
bool isMigration = uploadManifest.MigrateExisting;
bool isDeleteOnly = !isMigration && uploadManifest.Patches is not { Count: > 0 };
bool isPublicChannel = uploadManifest.Channel is "stable" or "beta";
if (isMigration && !isPublicChannel)
    throw new InvalidOperationException("Historical migration is supported only for stable and beta feeds.");
if (uploadManifest.StageOnly && !isMigration)
    throw new InvalidOperationException("Stage-only publication is supported only for historical migration.");
if (uploadManifest.R2ManifestOnly && !isMigration)
    throw new InvalidOperationException("R2-manifest-only publication is supported only for historical migration.");
if (uploadManifest.StageOnly && uploadManifest.R2ManifestOnly)
    throw new InvalidOperationException("StageOnly and R2ManifestOnly cannot both be enabled.");
if (isPublicChannel && !HasR2Configuration(uploadManifest))
    throw new InvalidOperationException("Stable and beta publication requires the EU R2 configuration.");
if (isDeleteOnly && string.IsNullOrWhiteSpace(uploadManifest.Label))
    throw new InvalidOperationException("The manifest has no patches and no label.");

var github = new GitHubClient(new ProductHeaderValue("Ksp2Redux.Tools.Uploader"))
{
    Credentials = new Credentials(uploadManifest.Token)
};
string workingDirectory = Path.Combine(Path.GetTempPath(), $"ksp2redux-upload-{Guid.NewGuid():N}");
Directory.CreateDirectory(workingDirectory);

try
{
    var existingFeed = await github.Repository.Content.GetAllContentsByRef(
        repoOwner, repoName, uploadManifest.File, uploadManifest.Branch);
    var feedFile = existingFeed[0];
    var feedJson = JsonSerializer.Deserialize<ReleaseManifest>(feedFile.Content)
                   ?? throw new InvalidDataException("Failed to parse the existing release manifest.");
    var additions = new List<ReleasePatch>();
    Release? release = null;
    List<ReleaseAsset> existingAssets = [];
    using var r2 = isPublicChannel ? new R2Publisher(uploadManifest) : null;

    if (!isDeleteOnly)
    {
        release = await GetOrCreateReleaseAsync(
            github, repoOwner, repoName, tag, uploadManifest, ReadChangelogSection(uploadManifest.Changelog));
        existingAssets = (await github.Repository.Release.GetAllAssets(repoOwner, repoName, release.Id)).ToList();
    }

    DateTime releasedAt = DateTime.UtcNow;
    if (isMigration)
    {
        feedJson.Patches = await MigrateLegacyPatchesAsync(
            feedJson.Patches, uploadManifest, github, repoOwner, repoName,
            r2!, workingDirectory);
    }
    foreach (var patchEntry in uploadManifest.Patches ?? [])
    {
        if (!isPublicChannel)
        {
            var asset = await UploadLegacyPatchAsync(
                github, repoOwner, repoName, release!, existingAssets, patchEntry.File);
            additions.Add(CreateLegacyPatch(uploadManifest, patchEntry, asset.BrowserDownloadUrl, releasedAt));
            continue;
        }

        string packageDirectory = Path.Combine(workingDirectory, Guid.NewGuid().ToString("N"));
        var packaged = await PatchChunker.SplitAsync(
            new RealFileSystem(), patchEntry.File, packageDirectory, PatchChunker.DEFAULT_CHUNK_SIZE);
        await VerifyReconstructionAsync(packaged);
        string requiredVersion = string.IsNullOrWhiteSpace(patchEntry.PreviousVersion)
            ? "base"
            : Uri.EscapeDataString(patchEntry.PreviousVersion);
        string prefix = $"patches/{uploadManifest.Channel}/{Uri.EscapeDataString(uploadManifest.Version)}/" +
                        $"{requiredVersion}/{packaged.ChecksumSha256.ToLowerInvariant()}";
        var chunks = new List<ReleasePatchChunk>();

        foreach (var chunk in packaged.Chunks)
        {
            string partName = $"part-{chunk.Index + 1:D4}-of-{packaged.Chunks.Count:D4}.bin";
            string objectKey = $"{prefix}/{partName}";
            string githubAssetName = $"patch-{packaged.ChecksumSha256.ToLowerInvariant()}-{partName}";
            await r2!.UploadImmutableAsync(objectKey, chunk.Path, chunk.ChecksumSha256, CancellationToken.None);
            var asset = await UploadExactAssetAsync(
                github, release!, existingAssets, chunk.Path, githubAssetName, chunk.Size);
            chunks.Add(new ReleasePatchChunk
            {
                Index = chunk.Index,
                Size = chunk.Size,
                ChecksumSha256 = chunk.ChecksumSha256,
                R2Url = r2!.GetPublicUrl(objectKey),
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

    var removed = ApplyRetention(feedJson, additions, uploadManifest, isPublicChannel);
    feedJson.GeneratedAt = DateTime.UtcNow;
    if (isPublicChannel)
        feedJson.SchemaVersion = 2;
    ReleaseManifestValidator.Validate(feedJson);
    string feedContent = JsonSerializer.Serialize(feedJson, new JsonSerializerOptions { WriteIndented = true });

    if (uploadManifest.StageOnly)
    {
        var currentFeed = (await github.Repository.Content.GetAllContentsByRef(
            repoOwner, repoName, uploadManifest.File, uploadManifest.Branch))[0];
        if (!string.Equals(currentFeed.Sha, feedFile.Sha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The live manifest {uploadManifest.File} changed while chunks were being staged. " +
                "It was left untouched; rerun staging to include the new content.");

        Console.WriteLine(
            $"Staging complete: prepared schema {feedJson.SchemaVersion} with {feedJson.Patches.Count} patch entries. " +
            $"The live {uploadManifest.File} manifest was left unchanged; no cache purge or retention deletion ran.");
        await r2!.ReportUsageAsync(CancellationToken.None);
        return 0;
    }

    if (uploadManifest.R2ManifestOnly)
    {
        var currentFeed = (await github.Repository.Content.GetAllContentsByRef(
            repoOwner, repoName, uploadManifest.File, uploadManifest.Branch))[0];
        if (!string.Equals(currentFeed.Sha, feedFile.Sha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The live GitHub manifest {uploadManifest.File} changed while the R2 manifest was prepared. " +
                "Nothing was published; rerun to include the new content.");

        await r2!.UploadManifestAsync(uploadManifest.File, feedContent, CancellationToken.None);
        string r2ManifestUrl = r2.GetPublicUrl(uploadManifest.File);
        await PurgeManifestAsync(uploadManifest, r2ManifestUrl);
        await VerifyPublicManifestAsync(r2ManifestUrl, feedContent);

        var unchangedGitHubFeed = (await github.Repository.Content.GetAllContentsByRef(
            repoOwner, repoName, uploadManifest.File, uploadManifest.Branch))[0];
        if (!string.Equals(unchangedGitHubFeed.Sha, feedFile.Sha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The GitHub manifest {uploadManifest.File} changed during R2 publication. " +
                "Rerun R2-manifest-only publication so both feeds describe the same releases.");

        Console.WriteLine(
            $"Published schema {feedJson.SchemaVersion} to R2 only. " +
            $"The live GitHub {uploadManifest.File} manifest was left unchanged; no retention deletion ran.");
        await r2.ReportUsageAsync(CancellationToken.None);
        return 0;
    }

    var updateRequest = new UpdateFileRequest(
        isDeleteOnly ? $"Cleanup for label {uploadManifest.Label}" : $"Update for release {uploadManifest.Version}",
        feedContent, feedFile.Sha, uploadManifest.Branch);
    var updateResult = await github.Repository.Content.UpdateFile(
        repoOwner, repoName, uploadManifest.File, updateRequest);
    Console.WriteLine($"Updated GitHub release manifest with commit {updateResult.Commit.Sha}.");

    if (isPublicChannel)
    {
        await r2!.UploadManifestAsync(uploadManifest.File, feedContent, CancellationToken.None);
        string r2ManifestUrl = r2.GetPublicUrl(uploadManifest.File);
        await PurgeManifestAsync(uploadManifest, r2ManifestUrl);
        string githubManifestUrl =
            $"https://raw.githubusercontent.com/{repoOwner}/{repoName}/{uploadManifest.Branch}/{uploadManifest.File}";
        await VerifyPublicManifestAsync(r2ManifestUrl, feedContent);
        await VerifyPublicManifestAsync(githubManifestUrl, feedContent);
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
}
finally
{
    Directory.Delete(workingDirectory, true);
}

return 0;

static bool HasR2Configuration(UploadManifest manifest) =>
    !string.IsNullOrWhiteSpace(manifest.R2AccountId) &&
    !string.IsNullOrWhiteSpace(manifest.R2Endpoint) &&
    !string.IsNullOrWhiteSpace(manifest.R2Bucket) &&
    !string.IsNullOrWhiteSpace(manifest.R2AccessKeyId) &&
    !string.IsNullOrWhiteSpace(manifest.R2SecretAccessKey) &&
    !string.IsNullOrWhiteSpace(manifest.CloudflareZoneId) &&
    !string.IsNullOrWhiteSpace(manifest.CloudflareApiToken);

static ReleasePatch CreateLegacyPatch(
    UploadManifest upload, PatchUploadEntry entry, string url, DateTime releasedAt)
{
    var info = new FileInfo(entry.File);
    return new ReleasePatch
    {
        Version = upload.Version,
        Label = upload.Label,
        ReleasedAt = releasedAt,
        ChecksumSha256 = GetChecksum(entry.File),
        Size = info.Length,
        Requires = new PatchRequirement { Version = entry.PreviousVersion },
        Url = url
    };
}

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
        if (patch.Chunks is not null)
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
        string prefix = $"patches/{upload.Channel}/{Uri.EscapeDataString(patch.Version)}/" +
                        $"{requiredVersion}/{packaged.ChecksumSha256.ToLowerInvariant()}";
        var chunks = new List<ReleasePatchChunk>();
        foreach (var chunk in packaged.Chunks)
        {
            string partName = $"part-{chunk.Index + 1:D4}-of-{packaged.Chunks.Count:D4}.bin";
            string objectKey = $"{prefix}/{partName}";
            string assetName = $"patch-{packaged.ChecksumSha256.ToLowerInvariant()}-{partName}";
            await r2.UploadImmutableAsync(objectKey, chunk.Path, chunk.ChecksumSha256, CancellationToken.None);
            var asset = await UploadExactAssetAsync(
                github, release, releaseAssets, chunk.Path, assetName, chunk.Size);
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
    GitHubClient github, string owner, string repo, string tag, UploadManifest manifest, string? changelog)
{
    try
    {
        var release = await github.Repository.Release.Get(owner, repo, tag);
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
        return change ? await github.Repository.Release.Edit(owner, repo, release.Id, update) : release;
    }
    catch (NotFoundException)
    {
        return await github.Repository.Release.Create(owner, repo, new NewRelease(tag)
        {
            Name = $"KSP2 Redux {manifest.Version}",
            Body = changelog ?? "Automated upload for KSP2 Redux",
            Draft = false,
            Prerelease = manifest.Prerelease
        });
    }
}

static async Task<ReleaseAsset> UploadLegacyPatchAsync(
    GitHubClient github, string owner, string repo, Release release,
    List<ReleaseAsset> existingAssets, string path)
{
    string name = Path.GetFileName(path);
    foreach (var stale in existingAssets.Where(asset =>
                 string.Equals(Path.GetExtension(asset.Name), Path.GetExtension(name), StringComparison.OrdinalIgnoreCase)).ToList())
    {
        await github.Repository.Release.DeleteAsset(owner, repo, stale.Id);
        existingAssets.Remove(stale);
    }
    return await UploadExactAssetAsync(
        github, release, existingAssets, path, name, new FileInfo(path).Length);
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
    await using var stream = File.OpenRead(path);
    var asset = await github.Repository.Release.UploadAsset(release, new ReleaseAssetUpload
    {
        FileName = assetName,
        ContentType = "application/octet-stream",
        RawData = stream
    });
    if (asset.Size != expectedSize)
        throw new InvalidOperationException(
            $"GitHub reports {asset.Size} bytes for '{assetName}', expected {expectedSize}.");
    existingAssets.Add(asset);
    return asset;
}

static List<ReleasePatch> ApplyRetention(
    ReleaseManifest feed, List<ReleasePatch> additions, UploadManifest upload, bool publicChannel)
{
    var removed = new List<ReleasePatch>();
    if (publicChannel)
    {
        removed.AddRange(feed.Patches.Where(patch => patch.Version == upload.Version));
        feed.Patches = additions.Concat(feed.Patches.Except(removed)).ToList();
        if (upload.Channel == "beta")
        {
            var retainedVersions = feed.Patches
                .GroupBy(patch => patch.Version, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Max(patch => patch.ReleasedAt))
                .Take(Math.Max(1, upload.RetainBetaReleases))
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rotated = feed.Patches.Where(patch => !retainedVersions.Contains(patch.Version)).ToList();
            removed.AddRange(rotated);
            feed.Patches = feed.Patches.Where(patch => retainedVersions.Contains(patch.Version)).ToList();
        }
        return removed;
    }
    if (!string.IsNullOrWhiteSpace(upload.Label))
    {
        var stale = feed.Patches.Where(patch => patch.Label == upload.Label).ToList();
        removed.AddRange(stale);
        feed.Patches = additions.Concat(feed.Patches.Except(stale)).ToList();
    }
    else
    {
        feed.Patches = additions.Concat(feed.Patches).ToList();
    }
    return removed;
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

static string? TryGetR2ObjectKey(string publicBaseUrl, string url)
{
    string prefix = publicBaseUrl.TrimEnd('/') + "/";
    return url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
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
