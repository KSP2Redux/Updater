using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Uploader;

namespace Ksp2Redux.Tools.Launcher.Tests.Uploader;

public class UploadManifestTest
{
    [Test]
    public void SameVersionDelta_IsRejectedBeforePublication()
    {
        var upload = Upload() with
        {
            Patches = [new PatchUploadEntry { File = "delta.patch", PreviousVersion = "0.2.9.0.2" }]
        };

        Assert.That(upload.ValidatePatchRequirements,
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("to itself"));
    }

    [Test]
    public void FullPatchAndEarlierDelta_AreAccepted()
    {
        var upload = Upload() with
        {
            Patches =
            [
                new PatchUploadEntry { File = "full.patch" },
                new PatchUploadEntry { File = "delta.patch", PreviousVersion = "0.2.9.0.1" }
            ]
        };

        Assert.That(upload.ValidatePatchRequirements, Throws.Nothing);
    }

    [TestCase(null)]
    [TestCase("internal")]
    [TestCase("stable")]
    public void Republish_ReplacesFullAndDeltaEntriesWithoutDuplicatingHistory(string? channel)
    {
        var older = Patch(1);
        var originalFull = Patch(2);
        var originalDelta = Patch(2, 1);
        var replacement = Patch(2) with { Size = 150 };
        var feed = Feed(older, originalFull, originalDelta);
        var upload = Upload() with { Channel = channel };

        var removed = upload.ApplyRetention(feed, [replacement]);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EquivalentTo(new[] { originalFull, originalDelta }));
            Assert.That(feed.Patches, Is.EqualTo(new[] { replacement, older }));
        });

        upload.ApplyRetention(feed, [replacement]);
        Assert.That(feed.Patches, Is.EqualTo(new[] { replacement, older }));
    }

    [Test]
    public void LabelledRepublish_ReplacesTheLabelAndAllOldRoutesForTheVersion()
    {
        var oldLabel = Patch(1) with { Label = "review" };
        var oldVersion = Patch(2) with { Label = "previous-label" };
        var unrelated = Patch(3);
        var replacement = Patch(2) with { Label = "review", Size = 150 };
        var feed = Feed(oldLabel, oldVersion, unrelated);
        var upload = Upload() with { Label = "review" };

        var removed = upload.ApplyRetention(feed, [replacement]);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EquivalentTo(new[] { oldLabel, oldVersion }));
            Assert.That(feed.Patches, Is.EqualTo(new[] { replacement, unrelated }));
        });
    }

    [Test]
    public void LabelCleanup_PreservesUnrelatedVersions()
    {
        var removedPatch = Patch(1) with { Label = "review" };
        var unrelated = Patch(2);
        var feed = Feed(removedPatch, unrelated);

        var removed = (Upload() with { Label = "review" }).ApplyRetention(feed, []);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(new[] { removedPatch }));
            Assert.That(feed.Patches, Is.EqualTo(new[] { unrelated }));
        });
    }

    [Test]
    public void BetaRotation_KeepsEveryRouteForRetainedVersions()
    {
        var expired = Patch(0);
        var olderFull = Patch(1);
        var olderDelta = Patch(1, 0);
        var replacement = Patch(2);
        var feed = Feed(expired, olderFull, olderDelta);
        var upload = Upload() with { Channel = "beta", RetainBetaReleases = 2 };

        var removed = upload.ApplyRetention(feed, [replacement]);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(new[] { expired }));
            Assert.That(feed.Patches, Is.EqualTo(new[] { replacement, olderFull, olderDelta }));
        });
    }

    /// <summary>
    /// Recipe publication cannot replace an older launcher's manifest, even before artifact inspection.
    /// </summary>
    /// <param name="filename">The protected legacy feed path.</param>
    [TestCase("Manifest.Develop.json")]
    [TestCase("Manifest.Next.json")]
    [TestCase("manifest-mr.json")]
    [TestCase("manifest-beta.json")]
    [TestCase("manifest-stable.json")]
    [TestCase("feeds/Manifest.Develop.json")]
    public void RecipePublication_RejectsLegacyFeedBeforeIO(string filename)
    {
        var upload = Upload() with { File = filename, FullSourcePublication = "absent-publication", Channel = "internal" };
        Assert.That(upload.ValidatePatchRequirements,
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("recipe feed"));
    }

    /// <summary>
    /// Separate recipe files can be admitted without changing legacy patch validation.
    /// </summary>
    /// <param name="filename">The separate recipe feed path.</param>
    [TestCase("Manifest.Develop.v3.json")]
    [TestCase("Manifest.Next.v3.json")]
    [TestCase("manifest-mr.v3.json")]
    public void RecipePublication_AcceptsSeparateFeedFile(string filename)
    {
        var upload = Upload() with { File = filename, FullSourcePublication = "absent-publication", Channel = "internal" };
        Assert.That(upload.ValidatePatchRequirements, Throws.Nothing);
    }

    [Test]
    public void EnvironmentToken_IsResolvedWithoutPersistingItsValue()
    {
        var environment = new Moq.Mock<IEnvironmentProvider>();
        environment.Setup(value => value.GetEnvironmentVariable("EXISTING_CI_SECRET")).Returns("fixture-transient-token");
        var upload = Upload() with { Token = "", TokenEnvironmentVariable = "EXISTING_CI_SECRET" };
        Assert.That(upload.ResolveToken(environment.Object), Is.EqualTo("fixture-transient-token"));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(upload), Does.Not.Contain("fixture-transient-token"));
    }

    [Test]
    public void MissingEnvironmentToken_DoesNotFallBackToAnotherCredential()
    {
        var environment = new Moq.Mock<IEnvironmentProvider>();
        var upload = Upload() with { TokenEnvironmentVariable = "UNAVAILABLE_CI_SECRET" };
        Assert.That(() => upload.ResolveToken(environment.Object), Throws.TypeOf<InvalidOperationException>());
    }

    [TestCase("Manifest.Develop.json")]
    [TestCase("Manifest.Next.json")]
    [TestCase("manifest-mr.json")]
    public void SchemaThreeDocument_CannotBeWrittenToAnyLegacyDestination(string filename)
    {
        var feed = Feed();
        feed.SchemaVersion = 3;
        var upload = Upload() with { File = filename };
        Assert.That(() => upload.ValidateFeedDestination(feed), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => (upload with { File = "manifest-mr.v3.json" }).ValidateFeedDestination(feed), Throws.Nothing);
    }

    [Test]
    public void LegacyPatchRoutes_CannotEnterARecipeFeed()
    {
        var feed = Feed(Patch(1));
        feed.SchemaVersion = 3;
        Assert.That(() => (Upload() with { File = "manifest-mr.v3.json" }).ValidateFeedDestination(feed),
            Throws.TypeOf<InvalidDataException>());
    }
    private static UploadManifest Upload() => new()
    {
        Repository = "example/test",
        Branch = "main",
        File = "manifest.json",
        Token = "unused-test-token",
        Version = "0.2.9.0.2"
    };

    private static ReleaseManifest Feed(params ReleasePatch[] patches) => new()
    {
        SchemaVersion = 1,
        Channel = "internal",
        GeneratedAt = DateTime.UtcNow,
        Patches = patches.ToList()
    };

    private static ReleasePatch Patch(int build, int? previous = null) => new()
    {
        Version = $"0.2.9.0.{build}",
        Requires = new PatchRequirement { Version = previous.HasValue ? $"0.2.9.0.{previous}" : null },
        Url = $"https://example.test/{build}-from-{previous}.patch",
        Size = 100,
        ChecksumSha256 = new string('0', 64),
        ReleasedAt = new DateTime(2026, 11, 12, 0, 0, build, DateTimeKind.Utc)
    };
}
