// See https://aka.ms/new-console-template for more information

using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Uploader;
using Octokit;
using Tomlyn;

var manifest = args[0];

var uploadManifest = TomlSerializer.Deserialize<UploadManifest>(File.ReadAllText(manifest), new TomlSerializerOptions
{
    SourceName = manifest
});

if (uploadManifest == null)
{
    Console.WriteLine("Failed to parse manifest");
    return;
}

var repoSplit = uploadManifest.Repository.Split('/');
var repoOwner = repoSplit[0];
var repoName = repoSplit[1];

var github = new GitHubClient(new ProductHeaderValue("Ksp2Redux.Tools.Uploader"))
{
    Credentials = new Credentials(uploadManifest.Token)
};

var tag = "v" + uploadManifest.Version;

var isDeleteOnly = uploadManifest.Patches is not { Count: > 0 };

if (isDeleteOnly && string.IsNullOrWhiteSpace(uploadManifest.Label))
{
    Console.Error.WriteLine("Manifest has no patches and no label. Nothing to upload and nothing to delete.");
    Environment.Exit(1);
}

List<ReleasePatch> patchesToPrepend = [];

if (!isDeleteOnly)
{
    var releaseBody = ReadChangelogSection(uploadManifest.Changelog)
                      ?? "Automated upload for KSP2 Redux (Fill in later if not on dev pipeline)";

    Release createdRelease;
    try
    {
        createdRelease = await github.Repository.Release.Get(repoOwner, repoName, tag);
        Console.WriteLine($"Found existing release at: {createdRelease.HtmlUrl}");

        if (!string.IsNullOrWhiteSpace(uploadManifest.Changelog))
        {
            var update = createdRelease.ToUpdate();
            update.Body = releaseBody;
            createdRelease = await github.Repository.Release.Edit(repoOwner, repoName, createdRelease.Id, update);
            Console.WriteLine("Updated release body from changelog");
        }
    }
    catch (NotFoundException)
    {
        var newRelease = new NewRelease(tag)
        {
            Name = $"KSP2 Redux {uploadManifest.Version}",
            Body = releaseBody,
            Draft = false,
            Prerelease = false,
        };

        createdRelease = await github.Repository.Release.Create(repoOwner, repoName, newRelease);

        Console.WriteLine($"Created release at: {createdRelease.HtmlUrl}");
    }

    var existingAssets = (await github.Repository.Release.GetAllAssets(repoOwner, repoName, createdRelease.Id)).ToList();

    var extensionsToReplace = uploadManifest.Patches!
        .Select(p => Path.GetExtension(p.File))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var existing in existingAssets.Where(a => extensionsToReplace.Contains(Path.GetExtension(a.Name))).ToList())
    {
        await github.Repository.Release.DeleteAsset(repoOwner, repoName, existing.Id);
        Console.WriteLine($"Deleted existing asset: {existing.Name}");
    }


    foreach (var patch in uploadManifest.Patches ?? [])
    {
        var fileName = Path.GetFileName(patch.File);

        await using var stream = File.OpenRead(patch.File);

        var upload = new ReleaseAssetUpload
        {
            FileName = fileName,
            ContentType = "application/octet-stream",
            RawData = stream,
        };

        var asset = await github.Repository.Release.UploadAsset(createdRelease, upload);

        var localSize = new FileInfo(patch.File).Length;
        if (asset.Size != localSize)
        {
            throw new InvalidOperationException(
                $"Uploaded asset '{fileName}' reports size {asset.Size} bytes on GitHub, but the local file is {localSize} bytes. " +
                "The upload may have been truncated or corrupted - not publishing this release.");
        }

        var releasePatch = new ReleasePatch
        {
            Version = uploadManifest.Version,
            Label = uploadManifest.Label,
            ReleasedAt = DateTime.UtcNow,
            ChecksumSha256 = GetChecksum(patch.File),
            Size = localSize,
            Requires = new PatchRequirement
            {
                Version = patch.PreviousVersion,
            },
            Url = asset.BrowserDownloadUrl,
        };

        patchesToPrepend.Add(releasePatch);

        Console.WriteLine($"Uploaded release asset at: {asset.Url}");
    }
}
else
{
    Console.WriteLine($"Delete-only request for label '{uploadManifest.Label}'. Skipping release creation and asset upload.");
}

// So now we update the feed

var existingFeed =
    await github.Repository.Content.GetAllContentsByRef(repoOwner, repoName, uploadManifest.File,
        uploadManifest.Branch);

var feedFile = existingFeed[0]!;
var feedSha = feedFile.Sha;

var feedJson = JsonSerializer.Deserialize<ReleaseManifest>(feedFile.Content);

if (feedJson == null)
{
    Console.WriteLine("Failed to parse feed");
    return;
}

if (!string.IsNullOrWhiteSpace(uploadManifest.Label))
{
    var staleEntries = feedJson.Patches
        .Where(p => string.Equals(p.Label, uploadManifest.Label, StringComparison.Ordinal))
        .ToList();

    var tagsToDelete = staleEntries
        .Where(p => !string.IsNullOrWhiteSpace(p.Version))
        .Select(p => "v" + p.Version)
        .Where(t => t != tag)
        .ToHashSet(StringComparer.Ordinal);

    foreach (var tagToDelete in tagsToDelete)
    {
        try
        {
            var staleRelease = await github.Repository.Release.Get(repoOwner, repoName, tagToDelete);
            await github.Repository.Release.Delete(repoOwner, repoName, staleRelease.Id);
            Console.WriteLine($"Deleted stale release for label '{uploadManifest.Label}': {tagToDelete}");
        }
        catch (NotFoundException)
        {
            Console.WriteLine($"Stale release already gone, skipping: {tagToDelete}");
        }
    }

    feedJson.Patches = feedJson.Patches
        .Where(p => !string.Equals(p.Label, uploadManifest.Label, StringComparison.Ordinal))
        .ToList();
}

feedJson.Patches = patchesToPrepend.Concat(feedJson.Patches).ToList();
feedJson.GeneratedAt = DateTime.UtcNow;

var newFeedContent = JsonSerializer.Serialize(feedJson, new JsonSerializerOptions()
{
    WriteIndented = true,
});

var commitMessage = isDeleteOnly
    ? $"Cleanup for label {uploadManifest.Label}"
    : $"Update for release {uploadManifest.Version}";

var updateRequest = new UpdateFileRequest(commitMessage, newFeedContent, feedSha,
    uploadManifest.Branch);

var updateResult = await github.Repository.Content.UpdateFile(repoOwner, repoName, uploadManifest.File, updateRequest);

Console.WriteLine($"Updated release manifest with commit {updateResult.Commit.Sha}");

return;

string GetChecksum(string path)
{
    using var stream = File.OpenRead(path);
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(stream);
    return Convert.ToHexString(hashBytes);
}

static string? ReadChangelogSection(string? changelogPath)
{
    if (string.IsNullOrWhiteSpace(changelogPath)) return null;
    if (!File.Exists(changelogPath))
    {
        Console.WriteLine($"Changelog file not found: {changelogPath}");
        return null;
    }

    var lines = File.ReadAllLines(changelogPath);
    var section = new List<string>();
    foreach (var line in lines)
    {
        if (line.Trim() == "---")
        {
            if (section.Count > 0) break;
            continue;
        }
        section.Add(line);
    }

    var text = string.Join('\n', section).Trim();
    return text.Length == 0 ? null : text;
}
