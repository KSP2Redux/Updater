using System.Text.Json;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    private static void UseLegacyRetention(Fixture fixture)
    {
        JsonNode envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(fixture.Head))!;
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        head["RetentionPolicy"] = "retain-all-v1";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(head);
        envelope["Payload"] = Convert.ToBase64String(payload);
        envelope["Sha256"] = Digest(payload);
        fixture.Fs.File.WriteAllBytes(fixture.Head, JsonSerializer.SerializeToUtf8Bytes(envelope));
    }

    private static async Task PrepareReviewFixture(Fixture fixture, string state)
    {
        bool partial = state == "partial-retirement";
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (!partial && name == "retirement-authority:open" && !after)
                throw new IOException("Published generation retained before retirement.");
        }, (name, after) =>
        {
            if (partial && name == "retirement-delete-file" && after)
                throw new IOException("Retirement interrupted after one verified file deletion.");
        })));
        if (state == "legacy-nonretired")
            UseLegacyRetention(fixture);
    }

    private static Dictionary<string, string> PreviousPayloads(Fixture fixture, string work)
    {
        string source = work[..^5] + "-source";
        return fixture.Fs.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Concat(fixture.Fs.Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path)));
    }

    private static async Task VerifyDirectLegacyUninstall(Fixture fixture)
    {
        string previousWork = LastWork(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        FullSourceInstallationStatus restored = await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        Generation(fixture, restored);
        Assert.That(restored.Generation.ToJson(), Is.EqualTo(fixture.Stock.Stock.ToJson()));
        Assert.That(restored.Release, Is.Null);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
        Assert.That(fixture.Fs.Directory.Exists(previousWork[..^5] + "-source"), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(previousWork, "payload-retirement.complete")), Is.True);
        Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["Pending"], Is.Null);
        Assert.That(LastWork(fixture), Is.Not.EqualTo(previousWork));
        Preserved(fixture, archive);
    }

    private static async Task VerifyRepairDefersPreviousRetirement(Fixture fixture)
    {
        string previousWork = LastWork(fixture);
        Dictionary<string, string> previous = PreviousPayloads(fixture, previousWork);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Put(fixture.Fs, fixture.Install, BUNDLE, "independently observed damaged owned bytes");
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, "added.bin"));
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>(), "Ordinary retirement must still reject the damaged published generation.");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture, target: fixture.Target3, initial: false));
        FullSourceInstallationStatus repaired = await Apply(fixture, target: fixture.Target3, initial: false, repair: true);
        Generation(fixture, repaired);
        Assert.That(repaired.Release, Is.EqualTo(fixture.Target3.Identity));
        Assert.That(PreviousPayloads(fixture, previousWork), Is.EquivalentTo(previous),
            "Explicit repair must preserve every remaining prior source, payload and journal byte.");
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(previousWork, "payload-retirement.complete")), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(LastWork(fixture), "payload-retirement.complete")), Is.True);
        Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["Pending"], Is.Null);
        Preserved(fixture, archive);
    }

    /// <summary>
    /// Direct uninstall rereads the policy-upgraded head before publishing its next pending transaction.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageReview_DirectLegacyUninstallUsesUpgradedCatalog()
    {
        Fixture fixture = Create();
        await PrepareReviewFixture(fixture, "legacy-nonretired");
        await VerifyDirectLegacyUninstall(fixture);
    }

    /// <summary>
    /// Explicit repair reaches its observation path without requiring retirement of a damaged previous generation.
    /// </summary>
    /// <param name="state">The previous generation's retention state.</param>
    /// <returns>The verification task.</returns>
    [TestCase("legacy-nonretired")]
    [TestCase("current-nonretired")]
    [TestCase("partial-retirement")]
    public async Task StorageReview_ExplicitRepairDefersPreviousRetirement(string state)
    {
        Fixture fixture = Create();
        await PrepareReviewFixture(fixture, state);
        await VerifyRepairDefersPreviousRetirement(fixture);
    }

    /// <summary>
    /// Small real Windows roots exercise direct legacy uninstall and repair after partial retirement.
    /// </summary>
    /// <param name="repair">True for partial-retirement repair, false for direct legacy uninstall.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    public async Task StorageReview_NativeWindowsCoordinatorTransitions(bool repair)
    {
        Fixture fixture = NativeFixture();
        await PrepareReviewFixture(fixture, repair ? "partial-retirement" : "legacy-nonretired");
        string previousWork = LastWork(fixture);
        if (repair)
            await VerifyRepairDefersPreviousRetirement(fixture);
        else
            await VerifyDirectLegacyUninstall(fixture);
        string report = fixture.Fs.Path.Combine(fixture.Root, "storage-review-result.json");
        fixture.Fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture.Root, installation = fixture.Entry.Id, repair, previousWork,
            previousPayloadsPreserved = repair, exactStockRestored = !repair,
            archiveSha256 = Digest(fixture.Fs.File.ReadAllBytes(fixture.Archive)),
            publishedHeadSha256 = Digest(fixture.Fs.File.ReadAllBytes(fixture.Head)),
            pending = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["Pending"] is not null
        }));
        TestContext.AddTestAttachment(report, "Reviewed native Windows coordinator-transition evidence.");
    }
}
