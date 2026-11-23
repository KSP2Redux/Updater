using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    private static byte[] Sha(byte[] bytes) => SHA256.HashData(bytes);

    private static ReleaseRecipePublication UseLegacyUpdateSource(Fixture fixture, string artifact,
        ReleaseRecipePublication publication, StockBaselineManifest prior, byte[] legacyManifest)
    {
        string marker = At(fixture.Fs, artifact, "release.json");
        var document = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(marker))!;
        string source = Convert.ToBase64String(prior.ToJson());
        var variant = document["Variants"]!.AsArray().Single(item => item!["SourceManifest"]!.GetValue<string>() == source);
        variant!["SourceManifest"] = Convert.ToBase64String(legacyManifest);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        fixture.Fs.File.WriteAllBytes(marker, bytes);
        return publication with { ArtifactSha256 = Digest(bytes) };
    }

    // How the artifact of an older release lists boot.config as a recipe source.
    private enum OlderBootSource { NotListed, ListedUnused, ReadByARecipe }

    // Publish the fixture's release the way builds did before boot.config left every contract: the target, the stock
    // definition and the artifact's source contract list the file, and the artifact copies it exactly. The CI listed the
    // file as the first recipe source too, which this can reproduce, with or without a recipe reading it.
    private static void PublishInOlderForm(Fixture fixture, OlderBootSource bootSource = OlderBootSource.NotListed)
    {
        const string boot = "KSP2_x64_Data/boot.config";
        string bootHash = new('C', 64);
        byte[] legacyTarget = fixture.Target.ToJsonWithBootConfig(15, bootHash);
        byte[] legacyStock = fixture.Stock.ToJsonWithBootConfig(15, bootHash);
        string release = At(fixture.Fs, fixture.Artifact, "release.json");
        var manifest = JsonSerializer.Deserialize<ReleaseRecipeManifest>(fixture.Fs.File.ReadAllBytes(release))!;
        var variant = manifest.Variants.Single() with
        {
            SourceManifest = legacyStock,
            ExactCopies = new Dictionary<string, string>(manifest.Variants.Single().ExactCopies ?? []) { [boot] = boot }
        };
        if (bootSource != OlderBootSource.NotListed)
        {
            var files = variant.Files.Select(file => file with { SourceIndices = file.SourceIndices!.Select(index => index + 1).ToArray() }).ToList();
            if (bootSource == OlderBootSource.ReadByARecipe)
            {
                // The first recipe also takes bytes from boot.config as its first local source.
                var bundle = files.First(file => file.Recipe.Entries.SelectMany(entry => entry.Segments).Any(segment => segment.Kind == BundleRecipeSegmentKind.Source));
                bundle.Recipe.Sources.Insert(0, new BundleRecipeSource { Kind = BundleRecipeSourceKind.RawFile, Length = 15, DataLength = 15, Sha256 = bootHash });
                foreach (var segment in bundle.Recipe.Entries.SelectMany(entry => entry.Segments).Where(segment => segment.Kind == BundleRecipeSegmentKind.Source))
                    segment.SourceIndex++;
                bundle.Recipe.Entries.SelectMany(entry => entry.Segments).First(segment => segment.Kind == BundleRecipeSegmentKind.Source).SourceIndex = 0;
                files[files.IndexOf(bundle)] = bundle with { SourceIndices = [0, .. bundle.SourceIndices!] };
            }
            variant = variant with { Sources = [new ReleaseRecipeSource(boot, BundleRecipeSourceKind.RawFile), .. variant.Sources], Files = files };
        }
        manifest = manifest with { Version = 4, TargetManifest = legacyTarget, Variants = [variant] };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        fixture.Fs.File.WriteAllBytes(release, json);
        var current = fixture.Patch.FullSource!;
        fixture.Patch = fixture.Patch with
        {
            ChecksumSha256 = Convert.ToHexString(Sha(json)), Size = json.Length,
            FullSource = new FullSourceReleaseDescriptor(current.ReleaseId, current.Channel, current.Version, legacyTarget,
                Convert.ToHexString(Sha(legacyTarget)), current.R2ArtifactUrl, current.GitHubArtifactUrl,
                [new FullSourceBaselineDefinition(legacyStock, [])], json.Length + manifest.Packs.Sum(pack => (long)pack.Length))
        };
    }

    /// <summary>
    /// A release a feed still publishes in the form from before boot.config left the contracts installs, repairs and is
    /// recognized as installed, with the same identity digest as the release without that entry.
    /// </summary>
    /// <param name="listedAsSource">True when the artifact also lists boot.config as a recipe source no recipe reads, as the CI did.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReleasePublishedInTheOlderForm_InstallsRepairsAndIsRecognizedAsInstalled(bool listedAsSource)
    {
        Fixture fixture = Create();
        PublishInOlderForm(fixture, listedAsSource ? OlderBootSource.ListedUnused : OlderBootSource.NotListed);
        Assert.That(fixture.Patch.FullSource!.TargetManifestSha256, Is.Not.EqualTo(Convert.ToHexString(Sha(fixture.Target.ToJson()))));
        var installPlans = Services(fixture).GetRequiredService<IInstallPlanService>();
        var feed = new ManifestReleasesFeed(fixture.Transport.Object, Mock.Of<ILogService>(), fixture.Storage, fixture.Config.Feeds.Single());
        Assert.That(await feed.UpdateManifest(), Is.True);
        GameVersion selected = feed.GetAllVersions().Single();

        await installPlans.ApplyToFolder(await SelectedPlan(fixture), fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        FullSourceReleaseIdentity identity = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!;
        Assert.That(identity.ManifestSha256, Is.EqualTo(Convert.ToHexString(Sha(fixture.Target.ToJson()))));
        var installed = new Ksp2Install(fixture.Fs, Mock.Of<IModuleDefinitionService>(), fixture.Entry.ExePath, identity);
        Assert.That(installed.IsInstalledVersion(selected, feed.GetFullSourcePlan(selected)), Is.True);
        Assert.That(installed.IsInstalledVersion(selected, feed.GetFullSourcePlan(selected, repair: true)), Is.True);

        Put(fixture.Fs, fixture.Install, WEM, "observed damage"u8.ToArray());
        await installPlans.ApplyToFolder(await SelectedPlan(fixture, repair: true), fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(identity));

        // The same selector with another contract is another release.
        byte[] other = new StockBaselineManifest(fixture.Target.BaselineIdentity, fixture.Target.Provenance,
            fixture.Target.Files.Select(file => file.Path == WEM ? file with { Length = file.Length + 1 } : file),
            fixture.Target.Directories).ToJsonWithBootConfig(15, new string('C', 64));
        fixture.Patch = fixture.Patch with
        {
            FullSource = fixture.Patch.FullSource with { TargetManifest = other, TargetManifestSha256 = Convert.ToHexString(Sha(other)) }
        };
        Assert.That(await feed.UpdateManifest(), Is.True);
        Assert.That(installed.IsInstalledVersion(selected, feed.GetFullSourcePlan(selected)), Is.False);
    }

    /// <summary>
    /// A release whose recipe reads bytes from boot.config cannot be installed. The launcher says so and leaves the game as it was.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task ReleaseWhoseRecipeReadsBootConfig_IsRefusedAndLeavesTheGameUnchanged()
    {
        Fixture fixture = Create();
        PublishInOlderForm(fixture, OlderBootSource.ReadByARecipe);
        var installPlans = Services(fixture).GetRequiredService<IInstallPlanService>();
        var plan = await SelectedPlan(fixture);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installPlans.ApplyToFolder(
            plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("boot.config as a recipe input"));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }
}
