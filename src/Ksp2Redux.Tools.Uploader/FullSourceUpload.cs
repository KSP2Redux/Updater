using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Transactions;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Uploader;

internal sealed class FullSourceUpload
{
    private const int MAX_CONTROL_BYTES = 16 * 1024 * 1024;

    private FullSourceUpload(ReleasePatch patch, List<Asset> assets, string r2Prefix, string githubPrefix)
    {
        Patch = patch;
        Assets = assets;
        R2Prefix = r2Prefix;
        GitHubPrefix = githubPrefix;
    }

    internal ReleasePatch Patch { get; }
    internal IReadOnlyList<Asset> Assets { get; }
    internal string R2Prefix { get; }
    internal string GitHubPrefix { get; }

    internal static FullSourceUpload Read(UploadManifest upload, string manifestPath, string owner, string repo, string tag)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(upload.FullSourcePublication!, directory));
        string artifact = Path.Combine(root, "artifact");
        string feedPath = Path.Combine(root, "release-manifest.json");
        string resultPath = Path.Combine(root, "publication-result.json");
        var fs = new RealFileSystem();
        var paths = new InstallTransactionEngine(fs);
        paths.ValidateControlledPath(feedPath);
        paths.ValidateControlledPath(resultPath);
        ReleaseManifest feed = ReadDocument<ReleaseManifest>(feedPath);
        ReleaseManifestValidator.ValidateHosted(feed);
        if (feed.SchemaVersion != 3 || feed.Channel != upload.Channel || feed.Patches.Count != 1 ||
            feed.Patches[0].Version != upload.Version || feed.Patches[0].FullSource is not { } descriptor)
            throw new InvalidDataException("Prepared full-source publication differs from the upload version or channel.");

        ReleasePatch patch = feed.Patches[0];
        ReleaseRecipePublication publication = ReadDocument<PublicationResult>(resultPath).Publication;
        if (!string.Equals(publication.ArtifactSha256, patch.ChecksumSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(publication.TargetManifestSha256, descriptor.TargetManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared feed and publication receipt identify different artifacts.");

        var pipeline = new ReleaseRecipePipeline(fs);
        ReleaseRecipeManifest recipe = pipeline.Inspect(artifact, patch.ChecksumSha256, descriptor.TargetManifestSha256);
        if (!feed.Channel.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
            !patch.Version.All(character => char.IsAsciiDigit(character) || character == '.'))
            throw new InvalidDataException("Invalid release channel or version for hosted publication.");
        string r2Prefix = $"full-source/{feed.Channel}/{patch.Version}/{patch.ChecksumSha256.ToLowerInvariant()}/";
        string githubPrefix = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/download/{Uri.EscapeDataString(tag)}/";
        string r2Url = $"{upload.R2PublicBaseUrl.TrimEnd('/')}/{r2Prefix}";
        ReleasePatch hosted = patch with
        {
            Label = upload.Label ?? patch.Label,
            FullSource = descriptor with
            {
                R2ArtifactUrl = $"{r2Url}release.json",
                GitHubArtifactUrl = $"{githubPrefix}release.json",
                UpdateArtifacts = descriptor.UpdateArtifacts?.Select(update => update with
                {
                    R2ArtifactUrl = $"{r2Url}{update.AssetPrefix}release.json",
                    GitHubArtifactUrl = $"{githubPrefix}{update.AssetPrefix}release.json",
                }).ToList(),
            },
        };
        ReleaseManifestValidator.ValidatePatch(hosted);
        long packBytes = recipe.Packs.Sum(pack => (long)pack.Length);
        if (publication.StoredPackBytes != packBytes)
            throw new InvalidDataException("Prepared publication pack bytes differ from the artifact.");
        ReleaseRecipePipeline.ValidateFeedArtifact(recipe, descriptor, patch.Size);

        var assets = new List<Asset>(recipe.Packs.Count + 1);
        AddArtifactAssets(assets, artifact, recipe, patch.Size, patch.ChecksumSha256, "");
        foreach (var update in descriptor.UpdateArtifacts ?? [])
        {
            string updateRoot = Path.Combine(root, "updates", update.SourceDigest.ToLowerInvariant(), "artifact");
            var updateRecipe = pipeline.Inspect(updateRoot, update.MarkerSha256, descriptor.TargetManifestSha256);
            ReleaseRecipePipeline.ValidateUpdateFeedArtifact(updateRecipe, descriptor.TargetManifest, update);
            ReleaseManifestValidator.ValidateHostedGameContract(ReleaseRecipePipeline.GetSourceContract(updateRecipe, update.SourceDigest));
            AddArtifactAssets(assets, updateRoot, updateRecipe, update.MarkerSize, update.MarkerSha256, update.AssetPrefix);
        }
        if (assets.Count > ReleaseRecipePipeline.MAX_HOSTED_PACKS + 1)
            throw new InvalidDataException("The combined stock and update assets exceed the hosted release asset count.");
        foreach (var asset in assets)
        {
            paths.ValidateControlledPath(asset.Path);
            if (new FileInfo(asset.Path).Length != asset.Size)
                throw new InvalidDataException($"The local release asset {asset.Name} has the wrong length.");
        }
        return new FullSourceUpload(hosted, assets, r2Prefix, githubPrefix);
    }

    private static void AddArtifactAssets(List<Asset> assets, string root, ReleaseRecipeManifest recipe,
        long markerSize, string markerHash, string prefix)
    {
        if (recipe.PackProfile != ReleaseRecipePipeline.HOSTED_PACK_PROFILE)
            throw new InvalidDataException("Prepare the publication with the hosted pack profile before uploading.");
        int start = assets.Count;
        for (int index = 0; index < recipe.Packs.Count; index++)
        {
            var pack = recipe.Packs[index];
            string name = $"pack-{index:D6}.bin";
            assets.Add(new Asset(prefix + name, Path.Combine(root, name), pack.Length, pack.Sha256));
        }
        // Publish the immutable recipe after its packs and update the mutable feeds last.
        assets.Add(new Asset(prefix + "release.json", Path.Combine(root, "release.json"), markerSize, markerHash));
        if (!Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
            .SetEquals(assets.Skip(start).Select(asset => Path.GetFileName(asset.Path))))
            throw new InvalidDataException("Publication directory does not contain exactly the declared release assets.");
    }

    internal string PrepareFeed(ReleaseManifest feed, int retainBetaReleases)
    {
        ReleaseManifestValidator.Validate(feed);
        var descriptor = Patch.FullSource!;
        if (feed.Channel != descriptor.Channel)
            throw new InvalidDataException("Destination feed has a different channel.");
        ReleasePatch[] conflicts = feed.Patches.Where(existing => existing.FullSource is { } source &&
            (source.ReleaseId == descriptor.ReleaseId || existing.Version == Patch.Version)).ToArray();
        if (conflicts.Length == 0)
            feed.Patches.Insert(0, Patch);
        else if (conflicts.Length != 1 || conflicts[0].ChecksumSha256 != Patch.ChecksumSha256 ||
            JsonSerializer.Serialize(conflicts[0].FullSource) != JsonSerializer.Serialize(descriptor))
            throw new InvalidDataException("The destination already uses this version or release ID for a different artifact.");
        feed.SchemaVersion = 3;
        if (feed.Channel == "beta")
        {
            RetainBetaVersions(feed, retainBetaReleases);
            if (!feed.Patches.Any(patch => patch.FullSource?.ReleaseId == descriptor.ReleaseId))
                throw new InvalidDataException("The full-source release falls outside the beta retention window.");
        }
        feed.GeneratedAt = DateTime.UtcNow;
        ReleaseManifestValidator.ValidateHosted(feed);
        string content = JsonSerializer.Serialize(feed, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(content) > ReleaseManifestValidator.MAX_ONLINE_FEED_BYTES)
            throw new InvalidDataException("The prepared release feed exceeds the launcher's 64 MiB metadata budget.");
        return content;
    }

    internal static List<ReleasePatch> RetainBetaVersions(ReleaseManifest feed, int retainBetaReleases)
    {
        var retainedVersions = feed.Patches
            .GroupBy(patch => patch.Version, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(patch => patch.ReleasedAt))
            .Take(Math.Max(1, retainBetaReleases))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = feed.Patches.Where(patch => !retainedVersions.Contains(patch.Version)).ToList();
        feed.Patches = feed.Patches.Where(patch => retainedVersions.Contains(patch.Version)).ToList();
        return removed;
    }

    private static T ReadDocument<T>(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MAX_CONTROL_BYTES)
            throw new InvalidDataException("Local publication control exceeds its byte budget.");
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { AllowDuplicateProperties = false })
            ?? throw new InvalidDataException("A local publication control is empty.");
    }

    internal sealed record Asset(string Name, string Path, long Size, string Sha256)
    {
        internal FileStream OpenVerified()
        {
            var stream = File.OpenRead(Path);
            try
            {
                if (stream.Length != Size || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The local release asset {Name} differs from its declared SHA-256.");
                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    private sealed record PublicationResult(ReleaseRecipePublication Publication);
}

internal sealed class FullSourceAssetIdentity
{
    public string? Digest { get; set; }
}
