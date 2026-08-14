using Ksp2Redux.Tools.Common.Models;

namespace Ksp2Redux.Tools.Launcher.Tests.Models;

public class ReleaseManifestValidatorTest
{
    private const string HASH = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Test]
    public void Validate_VersionOneLegacyPatch_Accepts()
    {
        Assert.That(() => ReleaseManifestValidator.Validate(CreateManifest(CreateLegacyPatch())), Throws.Nothing);
    }

    [Test]
    public void Validate_VersionTwoMixedTransport_Accepts()
    {
        var manifest = CreateManifest(CreateLegacyPatch(), CreateChunkedPatch());
        manifest.SchemaVersion = 2;
        Assert.That(() => ReleaseManifestValidator.Validate(manifest), Throws.Nothing);
    }

    [Test]
    public void Validate_FutureSchema_RequestsLauncherUpdate()
    {
        var manifest = CreateManifest(CreateLegacyPatch());
        manifest.SchemaVersion = 3;
        Assert.That(() => ReleaseManifestValidator.Validate(manifest),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Update"));
    }

    [Test]
    public void Validate_BothTransports_Rejects()
    {
        var patch = CreateChunkedPatch() with { Url = "https://example.test/patch" };
        Assert.That(() => ReleaseManifestValidator.ValidatePatch(patch), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Validate_NonContiguousChunks_Rejects()
    {
        var patch = CreateChunkedPatch();
        patch.Chunks![0] = patch.Chunks[0] with { Index = 1 };
        Assert.That(() => ReleaseManifestValidator.ValidatePatch(patch), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Validate_ChunkSizeSumMismatch_Rejects()
    {
        var patch = CreateChunkedPatch() with { Size = 2 };
        Assert.That(() => ReleaseManifestValidator.ValidatePatch(patch), Throws.TypeOf<InvalidDataException>());
    }

    private static ReleaseManifest CreateManifest(params ReleasePatch[] patches) => new()
    {
        SchemaVersion = 1,
        Channel = "stable",
        GeneratedAt = DateTime.UtcNow,
        Patches = patches.ToList()
    };

    private static ReleasePatch CreateLegacyPatch() => new()
    {
        Version = "1.0.0.1",
        Requires = new PatchRequirement(),
        Url = "https://example.test/release.patch",
        ChecksumSha256 = HASH,
        Size = 1,
        ReleasedAt = DateTime.UtcNow
    };

    private static ReleasePatch CreateChunkedPatch() => new()
    {
        Version = "1.0.0.2",
        Requires = new PatchRequirement(),
        ChecksumSha256 = HASH,
        Size = 1,
        ReleasedAt = DateTime.UtcNow,
        Chunks =
        [
            new ReleasePatchChunk
            {
                Index = 0,
                Size = 1,
                ChecksumSha256 = HASH,
                R2Url = "https://download.ksp2redux.org/part.bin",
                GitHubUrl = "https://github.com/example/releases/part.bin"
            }
        ]
    };
}
