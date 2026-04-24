// See https://aka.ms/new-console-template for more information

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Uploader;
using Octokit;
using Tomlyn;

var manifest = args[0];

var uploadManifest = Toml.ToModel<UploadManifest>(File.ReadAllText(manifest), manifest, new TomlModelOptions
{
    ConvertPropertyName = x => x,
});

var repoSplit = uploadManifest.Repository.Split('/');
var repoOwner = repoSplit[0];
var repoName = repoSplit[1];

var github = new GitHubClient(new ProductHeaderValue("Ksp2Redux.Tools.Uploader"))
{
    Credentials = new Credentials(uploadManifest.Token)
};

var tag = "v" + uploadManifest.Version;

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

List<JsonPatch> patchesToPrepend = [];

var existingAssets = (await github.Repository.Release.GetAllAssets(repoOwner, repoName, createdRelease.Id)).ToList();

var extensionsToReplace = uploadManifest.Patches
    .Select(p => Path.GetExtension(p.File))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

foreach (var existing in existingAssets.Where(a => extensionsToReplace.Contains(Path.GetExtension(a.Name))).ToList())
{
    await github.Repository.Release.DeleteAsset(repoOwner, repoName, existing.Id);
    Console.WriteLine($"Deleted existing asset: {existing.Name}");
}

foreach (var patch in uploadManifest.Patches)
{
    var jsonPatch = new JsonPatch
    {
        Version = uploadManifest.Version,
        ReleasedAt = DateTime.UtcNow,
        ChecksumSha256 = GetChecksum(patch.File),
        Size = new FileInfo(patch.File).Length,
        Requires = new JsonRequires
        {
            Version = patch.PreviousVersion,
        }
    };

    var fileName = Path.GetFileName(patch.File);

    await using var stream = File.OpenRead(patch.File);

    var upload = new ReleaseAssetUpload
    {
        FileName = fileName,
        ContentType = "application/octet-stream",
        RawData = stream,
    };

    var asset = await github.Repository.Release.UploadAsset(createdRelease, upload);
    
    jsonPatch.Url = asset.BrowserDownloadUrl;
    
    patchesToPrepend.Add(jsonPatch);
    
    Console.WriteLine($"Uploaded release asset at: {asset.Url}");
}

// So now we update the feed

var existingFeed =
    await github.Repository.Content.GetAllContentsByRef(repoOwner, repoName, uploadManifest.File,
        uploadManifest.Branch);

var feedFile = existingFeed[0]!;
var feedSha = feedFile.Sha;

var feedJson = JsonSerializer.Deserialize<JsonManifest>(feedFile.Content);

feedJson.Patches = patchesToPrepend.Concat(feedJson.Patches).ToArray();
feedJson.GeneratedAt = DateTime.UtcNow;

var newFeedContent = JsonSerializer.Serialize(feedJson, new JsonSerializerOptions()
{
    WriteIndented = true,
});

var updateRequest = new UpdateFileRequest($"Update for release {uploadManifest.Version}", newFeedContent, feedSha,
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
