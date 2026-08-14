namespace Ksp2Redux.Tools.Common.Models;

/// <summary>
/// Validates release-manifest versions and patch transport definitions.
/// </summary>
public static class ReleaseManifestValidator
{
    /// <summary>
    /// Validates a release manifest before it is used or published.
    /// </summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <exception cref="InvalidDataException">Thrown when the manifest is unsupported or inconsistent.</exception>
    public static void Validate(ReleaseManifest manifest)
    {
        if (manifest.SchemaVersion is < 1 or > 2)
            throw new InvalidDataException(
                $"Release manifest schema {manifest.SchemaVersion} is not supported. Update the KSP2 Redux launcher.");

        if (string.IsNullOrWhiteSpace(manifest.Channel))
            throw new InvalidDataException("The release manifest does not identify its channel.");

        if (manifest.Patches == null)
            throw new InvalidDataException("The release manifest has no patch list.");

        foreach (var patch in manifest.Patches)
        {
            if (manifest.SchemaVersion == 1 && patch.Chunks is not null)
                throw new InvalidDataException("Chunked patches require release manifest schema 2.");
            ValidatePatch(patch);
        }
    }

    /// <summary>
    /// Validates one release patch transport definition.
    /// </summary>
    /// <param name="patch">The patch to validate.</param>
    /// <exception cref="InvalidDataException">Thrown when the patch transport is inconsistent.</exception>
    public static void ValidatePatch(ReleasePatch patch)
    {
        bool hasLegacyUrl = !string.IsNullOrWhiteSpace(patch.Url);
        bool hasChunks = patch.Chunks is not null;
        if (hasLegacyUrl == hasChunks)
            throw new InvalidDataException(
                $"Patch {patch.Version} must contain either one legacy URL or a chunk list, but not both.");

        if (patch.Size <= 0)
            throw new InvalidDataException($"Patch {patch.Version} has an invalid size.");

        if (!IsSha256(patch.ChecksumSha256))
            throw new InvalidDataException($"Patch {patch.Version} has an invalid SHA-256 checksum.");

        if (!hasChunks)
            return;

        if (patch.Chunks!.Count == 0)
            throw new InvalidDataException($"Patch {patch.Version} has an empty chunk list.");

        long totalSize = 0;
        for (var index = 0; index < patch.Chunks.Count; index++)
        {
            var chunk = patch.Chunks[index];
            if (chunk.Index != index)
                throw new InvalidDataException($"Patch {patch.Version} has a missing or duplicate chunk index at {index}.");
            if (chunk.Size <= 0)
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} has an invalid size.");
            if (!IsSha256(chunk.ChecksumSha256))
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} has an invalid SHA-256 checksum.");
            if (!IsHttpUrl(chunk.R2Url) || !IsHttpUrl(chunk.GitHubUrl))
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} does not have both download URLs.");

            checked
            {
                totalSize += chunk.Size;
            }
        }

        if (totalSize != patch.Size)
            throw new InvalidDataException(
                $"Patch {patch.Version} declares {patch.Size} bytes but its chunks contain {totalSize} bytes.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";
}
