using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Wrappers;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourcePreparationService
{
    private Stream OpenVerifiedPrepatch(FullSourceReleaseDescriptor descriptor, string distribution)
    {
        Stream input = InstallPlanService.OpenEmbeddedPrepatch(distribution);
        try
        {
            if (descriptor.PrepatchResources is null || !descriptor.PrepatchResources.TryGetValue(distribution, out string? expected) ||
                !Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded prepatch differs from the release's pinned normalization package.");
            input.Position = 0;
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private PatchManifest ReadPrepatch(FullSourceReleaseDescriptor descriptor, string distribution)
    {
        using Stream input = OpenVerifiedPrepatch(descriptor, distribution);
        using IZipArchive archive = _zip.NewArchive(input, ZipArchiveMode.Read, true);
        IZipArchiveEntry metadata = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The embedded prepatch has no manifest.");
        if (metadata.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The embedded prepatch metadata exceeds its budget.");
        using Stream stream = metadata.Open();
        PatchManifest manifest = JsonSerializer.Deserialize<PatchManifest>(stream)
            ?? throw new InvalidDataException("The embedded prepatch metadata is empty.");
        if (manifest.Operations.Count > 100000)
            throw new InvalidDataException("The embedded prepatch contains too many operations.");
        return manifest;
    }

    private List<FullSourceBaselineDefinition> BaselineDefinitions(FullSourceReleaseDescriptor descriptor, Ksp2InstallEntry? selected = null)
    {
        var result = descriptor.Baselines.ToList();
        if (ReleaseManifestValidator.GetPrepatchBaseline(descriptor) is not { } prepatch)
            return result;
        var entries = selected is null ? _configuration.Config.Ksp2Installs.ToArray() : new[] { selected };
        foreach (Ksp2InstallEntry entry in entries)
        {
            string root = InstallRoot(entry);
            string archive = At(root, "uninstall.zip");
            IReadOnlyDictionary<string, long>? lengths = _fs.File.Exists(archive) ? _archive.GetCandidateFileLengths(archive) : null;
            foreach (string distribution in new[] { "Steam", "Epic", "Portable" })
            {
                // A normalized tree alone never authenticates an original portable install.
                if (distribution == "Portable" && _fs.File.Exists(At(root, "prepatched.nodelete")) &&
                    entry.FullSourceBaselineIdentity != OriginalBaselineIdentity(descriptor, distribution))
                    continue;
                var derived = DeriveOriginalBaseline(descriptor, prepatch, distribution, root, lengths);
                if (derived is null)
                    continue;
                var contract = StockBaselineManifest.FromJson(derived.Manifest);
                if (result.Any(existing => SameFiles(StockBaselineManifest.FromJson(existing.Manifest), contract)))
                    continue;
                result.Add(derived);
            }
        }
        return result;
    }

    private static string OriginalBaselineIdentity(FullSourceReleaseDescriptor descriptor, string distribution) =>
        $"redux-original-windows-{distribution.ToLowerInvariant()}-{descriptor.PrepatchManifestSha256!.ToLowerInvariant()}";

    private static bool SameFiles(StockBaselineManifest left, StockBaselineManifest right) =>
        left.Files.SequenceEqual(right.Files) && left.Directories.SequenceEqual(right.Directories);

    private FullSourceBaselineDefinition? DeriveOriginalBaseline(FullSourceReleaseDescriptor descriptor,
        StockBaselineManifest prepatch, string distribution, string root, IReadOnlyDictionary<string, long>? observedLengths)
    {
        var files = prepatch.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var original = descriptor.Baselines.SelectMany(definition => StockBaselineManifest.FromJson(definition.Manifest).Files)
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var ignored = FileInformation.IgnoreFiles(_fs);
        foreach (PatchOperation operation in ReadPrepatch(descriptor, distribution).Operations)
        {
            string path = StockBaselineManifest.NormalizePath(operation.FileName.Replace('\\', '/'));
            if (ignored.Contains(path.Replace('/', _fs.Path.DirectorySeparatorChar)))
                continue;
            if (operation.Action == PatchOperation.PatchAction.Add)
            {
                files.Remove(path);
            }
            else if (operation.Action == PatchOperation.PatchAction.Remove)
            {
                if (original.TryGetValue(path, out var removed))
                {
                    if (removed.Distinct().Count() != 1)
                        throw new InvalidDataException("Original contracts disagree on a removed prepatch file.");
                    files[path] = removed[0];
                }
            }
            else
            {
                if (operation.OriginalHash is not { Length: 32 } || !files.TryGetValue(path, out var final))
                    throw new InvalidDataException("A prepatch operation lacks an independently pinned original file.");
                string hash = Convert.ToHexString(operation.OriginalHash);
                StockBaselineFile? known = original.GetValueOrDefault(path)?.SingleOrDefault(file => file.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                long length;
                if (known is not null)
                    length = known.Length;
                else if (final.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    length = final.Length;
                else if (observedLengths is not null && observedLengths.TryGetValue(path, out long archived))
                    length = archived;
                else if (_fs.File.Exists(At(root, path)))
                    length = _fs.FileInfo.New(At(root, path)).Length;
                else
                    return null;
                // Length observations are never authority. Original SHA-256 comes from the pinned package,
                // and ordinary complete stock/archive verification must prove every resulting byte.
                files[path] = final with { Length = length, Sha256 = hash };
            }
        }
        var stock = new StockBaselineManifest(OriginalBaselineIdentity(descriptor, distribution),
            $"Original Windows {distribution} bytes derived from pinned prepatch {descriptor.PrepatchManifestSha256}",
            files.Values, prepatch.Directories);
        var legacy = descriptor.Baselines.SelectMany(definition => definition.LegacyOwnershipManifests ?? [])
            .DistinctBy(bytes => Digest(bytes)).ToList();
        if (legacy.Count > 32)
            throw new InvalidDataException("Derived original ownership exceeds the existing contract budget.");
        return new FullSourceBaselineDefinition(stock.ToJson(), [], legacy.Count == 0 ? null : legacy);
    }

    private async Task<RecipeSource> PreparePrepatchSourceAsync(FullSourceReleaseDescriptor descriptor,
        StockBaselineManifest prepatch, CancellationToken ct)
    {
        if (!_os.IsWindows())
            throw new PlatformNotSupportedException("Embedded prepatch normalization requires Windows.");
        string final = BaselineRoot(prepatch);
        if (await TreeMatchesAsync(final, prepatch, ct))
            return new RecipeSource(prepatch, final);
        if (_fs.Directory.Exists(final))
            throw new InvalidDataException("The prepatch cache differs from its pinned contract and has been preserved.");
        RecipeSource original = await SelectBaselineAsync(BaselineDefinitions(descriptor), ct);
        string? distribution = null;
        var lengths = original.Contract.Files.ToDictionary(file => file.Path, file => file.Length, StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in new[] { "Steam", "Epic", "Portable" })
        {
            var definition = DeriveOriginalBaseline(descriptor, prepatch, candidate, original.Root, lengths);
            if (definition is not null && SameFiles(StockBaselineManifest.FromJson(definition.Manifest), original.Contract))
            {
                if (distribution is not null)
                    throw new InvalidDataException("Multiple embedded prepatches match the original stock contract.");
                distribution = candidate;
            }
        }
        if (distribution is null)
            throw new InvalidDataException("No pinned storefront prepatch matches the verified original stock contract.");
        string backups = At(CacheRoot(), "prepatch-backups");
        CreateDirectory(backups);
        string backup = At(backups, Digest(original.Contract.ToJson()).ToLowerInvariant() + ".zip");
        if (_fs.File.Exists(backup))
            await _archive.VerifyAsync(backup, original.Contract, ct);
        else
            await _archive.CreateAsync(original.Root, backup, original.Contract, ct);
        string staging = At(CacheRoot(), "prepatch-" + Guid.NewGuid().ToString("N"));
        await _archive.RestoreToNewDirectoryAsync(backup, original.Contract, staging, ct);
        using (Stream input = OpenVerifiedPrepatch(descriptor, distribution))
        using (var patch = new Ksp2Patch(_fs, _zip.NewArchive(input, ZipArchiveMode.Read, true)))
            await patch.AsyncApply(SystemEnvironmentProvider.Instance, staging,
                error: message => throw new InvalidDataException(message));
        if (!await TreeMatchesAsync(staging, prepatch, ct))
            throw new InvalidDataException("The applied prepatch does not match the release's pinned common baseline. Original backup and failed stage are preserved.");
        CreateDirectory(_fs.Path.GetDirectoryName(final)!);
        _paths.ValidateControlledPath(staging);
        _paths.ValidateControlledPath(final);
        _fs.Directory.Move(staging, final);
        return new RecipeSource(prepatch, final);
    }
}
