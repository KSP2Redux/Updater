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

    private static void WriteCatalogWithIncompleteLegacyIdentityMigration(Fixture fixture, string publishedDigest)
    {
        string head = At(fixture.Fs, fixture.Storage, $"full-source-v1/{fixture.Entry.Id:N}/installation.json");
        var envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(head))!.AsObject();
        var catalog = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!.AsObject();
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Fs.Path.GetDirectoryName(head)!,
            $"{catalog["LastTransaction"]!.GetValue<Guid>():N}.json"));

        // Older installs with derived settings stored different boot bytes in ownership and the published contract.
        catalog["Stock"] = Convert.ToBase64String(fixture.Stock.ToJsonWithBootConfig(15, new string('C', 64)));
        catalog["Ownership"] = Convert.ToBase64String(fixture.Target.ToJsonWithBootConfig(20, new string('D', 64)));
        var release = catalog["Release"]!.AsObject();
        release["ManifestSha256"] = publishedDigest;
        release.Remove("PublishedManifestSha256");

        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(catalog, options);
        envelope["Payload"] = Convert.ToBase64String(payload);
        envelope["Sha256"] = Digest(payload);
        fixture.Fs.File.WriteAllBytes(head, JsonSerializer.SerializeToUtf8Bytes(envelope, options));
    }

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

    private static byte[] HistoricalBoot => "hdr-display-enabled=1\r\nenable-directstorage=1\r\ngfx-threading-mode=6\r\nbuild-guid=fixture-release\r\n"u8.ToArray();

    // The older publisher included boot.config in the target and source contracts, and emitted its exact bytes.
    // It also listed the file as the first recipe source, which this can reproduce with or without recipe readers.
    private static void PublishInOlderForm(Fixture fixture, OlderBootSource bootSource = OlderBootSource.NotListed)
    {
        const string boot = BootConfigFile.RELATIVE_PATH;
        byte[] stockBoot = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.StockRoot, boot));
        string bootHash = Digest(stockBoot);
        Put(fixture.Fs, fixture.Install, boot, "hdr-display-enabled=0\r\ncustom=retained\r\nbuild-guid=fixture-stock\r\n"u8.ToArray());
        byte[] legacyStock = fixture.Stock.ToJsonWithBootConfig(stockBoot.Length, bootHash);
        string release = At(fixture.Fs, fixture.Artifact, "release.json");
        var manifest = JsonSerializer.Deserialize<ReleaseRecipeManifest>(fixture.Fs.File.ReadAllBytes(release))!;
        var variant = manifest.Variants.Single() with { SourceManifest = legacyStock };
        if (bootSource != OlderBootSource.NotListed)
        {
            var files = variant.Files.Select(file => file with { SourceIndices = file.SourceIndices!.Select(index => index + 1).ToArray() }).ToList();
            if (bootSource == OlderBootSource.ReadByARecipe)
            {
                // The first recipe also takes bytes from boot.config as its first local source.
                var bundle = files.First(file => file.Recipe.Entries.SelectMany(entry => entry.Segments).Any(segment => segment.Kind == BundleRecipeSegmentKind.Source));
                bundle.Recipe.Sources.Insert(0, new BundleRecipeSource
                {
                    Kind = BundleRecipeSourceKind.RawFile, Length = stockBoot.Length, DataLength = stockBoot.Length, Sha256 = bootHash
                });
                foreach (var segment in bundle.Recipe.Entries.SelectMany(entry => entry.Segments).Where(segment => segment.Kind == BundleRecipeSegmentKind.Source))
                    segment.SourceIndex++;
                bundle.Recipe.Entries.SelectMany(entry => entry.Segments).First(segment => segment.Kind == BundleRecipeSegmentKind.Source).SourceIndex = 0;
                files[files.IndexOf(bundle)] = bundle with { SourceIndices = [0, .. bundle.SourceIndices!] };
            }
            variant = variant with { Sources = [new ReleaseRecipeSource(boot, BundleRecipeSourceKind.RawFile), .. variant.Sources], Files = files };
        }
        manifest = AddHistoricalBootRecipe(fixture, fixture.Artifact, manifest with { Version = 4, Variants = [variant] }, fixture.Target);
        byte[] json = fixture.Fs.File.ReadAllBytes(release);
        var current = fixture.Patch.FullSource!;
        fixture.Patch = fixture.Patch with
        {
            ChecksumSha256 = Convert.ToHexString(Sha(json)), Size = json.Length,
            FullSource = new FullSourceReleaseDescriptor(current.ReleaseId, current.Channel, current.Version, manifest.TargetManifest,
                Digest(manifest.TargetManifest), current.R2ArtifactUrl, current.GitHubArtifactUrl,
                [new FullSourceBaselineDefinition(legacyStock, [])], json.Length + manifest.Packs.Sum(pack => (long)pack.Length))
        };
    }

    private static ReleaseRecipeManifest AddHistoricalBootRecipe(Fixture fixture, string root, ReleaseRecipeManifest manifest,
        StockBaselineManifest target)
    {
        byte[] boot = HistoricalBoot;
        string sha = Digest(boot);
        int pack = manifest.Packs.Count;
        Put(fixture.Fs, root, $"pack-{pack:D6}.bin", boot);
        var recipe = new ReleaseFileRecipe(BootConfigFile.RELATIVE_PATH, new BundleRecipe
        {
            Version = BundleRecipe.RAW_TARGET_VERSION,
            PackingProfile = BundleRecipe.RAW_PACKING_PROFILE,
            TargetLength = boot.Length,
            TargetSha256 = sha,
            PayloadLength = boot.Length,
            Entries = [new BundleRecipeEntry
            {
                Length = boot.Length, Segments = [new() { Kind = BundleRecipeSegmentKind.Payload, Length = boot.Length }]
            }]
        }, [manifest.Chunks.Count]) { SourceIndices = [] };
        manifest = manifest with
        {
            TargetManifest = target.ToJsonWithBootConfig(boot.Length, sha),
            Variants = manifest.Variants.Select(variant => variant with
            {
                Files = variant.Files.Append(recipe).OrderBy(file => file.Path, StringComparer.Ordinal).ToList()
            }).ToList(),
            Packs = [.. manifest.Packs, new(boot.Length, sha)],
            Chunks = [.. manifest.Chunks, new(sha, boot.Length, pack, 0, boot.Length, false)]
        };
        Put(fixture.Fs, root, "release.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
        return manifest;
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
        var bootConfig = new BootConfigSettingsService(fixture.Configuration.Object, fixture.Fs, Mock.Of<ILogService>());
        var installPlans = Services(fixture, bootConfig).GetRequiredService<IInstallPlanService>();
        var feed = new ManifestReleasesFeed(fixture.Transport.Object, Mock.Of<ILogService>(), fixture.Storage, fixture.Config.Feeds.Single());
        Assert.That(await feed.UpdateManifest(), Is.True);
        GameVersion selected = feed.GetAllVersions().Single();

        await installPlans.ApplyToFolder(await SelectedPlan(fixture), fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        string boot = At(fixture.Fs, fixture.Install, BootConfigFile.RELATIVE_PATH);
        var installedSettings = BootConfigFile.ReadSettings(fixture.Fs.File.ReadAllBytes(boot));
        foreach (var (key, value) in BootConfigFile.ReadSettings(HistoricalBoot))
            Assert.That(installedSettings[key], Is.EqualTo(value));
        Assert.That(installedSettings["custom"], Is.EqualTo("retained"));
        Assert.That(fixture.Fs.File.ReadAllText(boot), Does.Contain("build-guid=fixture-stock"));
        Assert.That(fixture.Coordinator().GetPublishedBootConfigSettings(fixture.Entry.Id),
            Is.EqualTo(BootConfigFile.ReadSettings(HistoricalBoot)));
        FullSourceReleaseIdentity identity = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!;
        Assert.That(identity.ManifestSha256, Is.EqualTo(Convert.ToHexString(Sha(fixture.Target.ToJson()))));
        var installed = new Ksp2Install(fixture.Fs, Mock.Of<IModuleDefinitionService>(), fixture.Entry.ExePath, identity);
        Assert.That(installed.IsInstalledVersion(selected, feed.GetFullSourcePlan(selected)), Is.True);
        Assert.That(installed.IsInstalledVersion(selected, feed.GetFullSourcePlan(selected, repair: true)), Is.True);

        Put(fixture.Fs, fixture.Install, WEM, "observed damage"u8.ToArray());
        Put(fixture.Fs, fixture.Install, BootConfigFile.RELATIVE_PATH, "hdr-display-enabled=0\r\ncustom=retained\r\n"u8.ToArray());
        await installPlans.ApplyToFolder(await SelectedPlan(fixture, repair: true), fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        Assert.That(fixture.Fs.File.ReadAllText(boot), Does.Contain("hdr-display-enabled=1"));
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
    /// Historical settings are authenticated again against the fresh feed even though they never enter the stage.
    /// </summary>
    [Test]
    public async Task HistoricalBootSettings_RequireAuthenticEvidenceAndMatchingSettings()
    {
        Fixture fixture = Create();
        PublishInOlderForm(fixture);
        var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        Assert.That(prepared.BootConfigSettings, Is.EqualTo(BootConfigFile.ReadSettings(HistoricalBoot)));
        Assert.That(prepared.Manifest.Files.Any(file => BootConfigFile.IsExcluded(file.Path)), Is.False);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, prepared.SourceRoot, BootConfigFile.RELATIVE_PATH)), Is.False);
        await fixture.Preparation.VerifyTargetAsync(prepared, CancellationToken.None);

        var changed = prepared with { HistoricalBootConfig = prepared.HistoricalBootConfig!.ToArray() };
        changed.HistoricalBootConfig![0] ^= 1;
        var forged = prepared with { BootConfigSettings = new Dictionary<string, string> { ["gfx-threading-mode"] = "2" } };
        foreach (var invalid in new[] { changed, forged, prepared with { HistoricalBootConfig = null } })
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparation.VerifyTargetAsync(invalid, CancellationToken.None));
    }

    /// <summary>
    /// A separate historical update may copy the prior boot file. Recover the original target settings from the
    /// stock recipe even if the player has changed their installed copy.
    /// </summary>
    [Test]
    public async Task HistoricalSeparateUpdate_UsesStockBootRecipeWhenInstalledBootWasEdited()
    {
        Fixture fixture = Create();
        PublishInOlderForm(fixture);
        byte[] previousManifest = fixture.Patch.FullSource!.TargetManifest;
        var installPlans = Services(fixture).GetRequiredService<IInstallPlanService>();
        await installPlans.ApplyToFolder(await SelectedPlan(fixture), fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        var previous = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!;
        Assert.That(previous.PublishedManifestSha256, Is.EqualTo(Digest(previousManifest)));

        const string id = "historical-target";
        const string version = "2.0.0.1.203";
        string previousRoot = At(fixture.Fs, fixture.Root, "frozen-target");
        string nextRoot = At(fixture.Fs, fixture.Root, "historic-next");
        string stockArtifact = At(fixture.Fs, fixture.Root, "historic-stock-package");
        string updateArtifact = At(fixture.Fs, fixture.Root, "historic-update-package");
        var files = fixture.Target.Files.ToDictionary(file => file.Path,
            file => fixture.Fs.File.ReadAllBytes(At(fixture.Fs, previousRoot, file.Path)), StringComparer.Ordinal);
        files["Redux/delta.txt"] = "historical update"u8.ToArray();
        var next = PutManifest(fixture.Fs, nextRoot, id, files);
        var pipeline = new ReleaseRecipePipeline(fixture.Fs, _ => { });
        var stock = pipeline.PublishHosted(nextRoot, next, [new(fixture.StockRoot, fixture.Stock, [])], stockArtifact);
        var stockRecipe = pipeline.Inspect(stockArtifact, stock.ArtifactSha256, stock.TargetManifestSha256);
        stockRecipe = AddHistoricalBootRecipe(fixture, stockArtifact, stockRecipe with { Version = 4 }, next);
        var delta = pipeline.PublishHosted(nextRoot, next, [new(previousRoot, fixture.Target, [])], updateArtifact);
        var updateRecipe = pipeline.Inspect(updateArtifact, delta.ArtifactSha256, delta.TargetManifestSha256);
        var variant = updateRecipe.Variants.Single();
        variant = variant with
        {
            SourceManifest = previousManifest,
            ExactCopies = new Dictionary<string, string>(variant.ExactCopies ?? [])
            {
                [BootConfigFile.RELATIVE_PATH] = BootConfigFile.RELATIVE_PATH
            }
        };
        byte[] updateMarker = JsonSerializer.SerializeToUtf8Bytes(updateRecipe with
        {
            TargetManifest = stockRecipe.TargetManifest, Variants = [variant]
        });
        Put(fixture.Fs, updateArtifact, "release.json", updateMarker);

        string sourceDigest = previous.PublishedManifestSha256!;
        string prefix = $"update-{sourceDigest.ToLowerInvariant()}-";
        var update = new FullSourceUpdateArtifact(sourceDigest, Digest(updateMarker), updateMarker.Length,
            updateMarker.Length + updateRecipe.Packs.Sum(pack => (long)pack.Length),
            $"https://fixture.test/build/{prefix}release.json", $"https://fixture-backup.test/build/{prefix}release.json");
        byte[] stockMarker = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, stockArtifact, "release.json"));
        var old = fixture.Patch.FullSource!;
        fixture.Patch = fixture.Patch with
        {
            Version = version, Size = stockMarker.Length, ChecksumSha256 = Digest(stockMarker),
            FullSource = new FullSourceReleaseDescriptor(id, CHANNEL, version, stockRecipe.TargetManifest,
                Digest(stockRecipe.TargetManifest), old.R2ArtifactUrl, old.GitHubArtifactUrl, old.Baselines,
                stockMarker.Length + stockRecipe.Packs.Sum(pack => (long)pack.Length)) { UpdateArtifacts = [update] }
        };
        fixture.Artifact = stockArtifact;
        fixture.ResponseOverride = (url, offset, _) =>
        {
            string name = new Uri(url).Segments.Last();
            return name.StartsWith(prefix, StringComparison.Ordinal)
                ? fixture.Response(url, offset, updateArtifact, name[prefix.Length..])
                : fixture.Response(url, offset);
        };
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Storage, $"full-source-preparation-v1/downloads/patches/{Digest(HistoricalBoot)}.patch"));
        fixture.Requests.Clear();
        var progress = new List<(long Bytes, long Total)>();

        var prepared = await fixture.Preparation.FetchTargetForInstalledReleaseAsync(id, CHANNEL, fixture.Config.Feeds[0],
            fixture.Entry.Id, previous, CancellationToken.None, (bytes, total) => progress.Add((bytes, total)));

        await fixture.Preparation.VerifyTargetAsync(prepared, CancellationToken.None);
        Assert.That(prepared.InstalledBaseline?.InstallationId, Is.EqualTo(fixture.Entry.Id));
        Assert.That(prepared.BootConfigSettings, Is.EqualTo(BootConfigFile.ReadSettings(HistoricalBoot)));
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, prepared.SourceRoot, BootConfigFile.RELATIVE_PATH)), Is.False);
        Assert.That(fixture.Requests.Select(request => new Uri(request.Url).Segments.Last()), Is.EquivalentTo(new[]
        {
            prefix + "release.json", prefix + "pack-000000.bin", "release.json", $"pack-{stockRecipe.Packs.Count - 1:D6}.bin"
        }));
        Assert.That(progress.Select(report => report.Bytes), Is.Ordered);
        Assert.That(progress.Last().Bytes, Is.EqualTo(progress.Last().Total));
    }

    /// <summary>
    /// An exact-copy boot recipe can use the source bytes only if those bytes match the published target.
    /// </summary>
    /// <param name="sourceMatches">Whether the installation still has the exact source bytes.</param>
    /// <param name="useWarmCache">Whether recipe staging uses a normalized cache with no boot.config.</param>
    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public async Task HistoricalExactCopy_RequiresMatchingSourceBytes(bool sourceMatches, bool useWarmCache)
    {
        Fixture fixture = Create();
        PublishInOlderForm(fixture);
        const string boot = BootConfigFile.RELATIVE_PATH;
        byte[] stockBoot = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.StockRoot, boot));
        string path = At(fixture.Fs, fixture.Artifact, "release.json");
        var manifest = JsonSerializer.Deserialize<ReleaseRecipeManifest>(fixture.Fs.File.ReadAllBytes(path))!;
        var variant = manifest.Variants.Single();
        variant = variant with
        {
            Files = variant.Files.Where(file => !BootConfigFile.IsExcluded(file.Path)).ToList(),
            ExactCopies = new Dictionary<string, string>(variant.ExactCopies ?? []) { [boot] = boot }
        };
        byte[] target = fixture.Target.ToJsonWithBootConfig(stockBoot.Length, Digest(stockBoot));
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(manifest with { TargetManifest = target, Variants = [variant] });
        Put(fixture.Fs, fixture.Artifact, "release.json", marker);
        fixture.Patch = fixture.Patch with
        {
            Size = marker.Length, ChecksumSha256 = Digest(marker),
            FullSource = fixture.Patch.FullSource! with
            {
                TargetManifest = target, TargetManifestSha256 = Digest(target),
                DownloadBytes = marker.Length + manifest.Packs.Sum(pack => (long)pack.Length)
            }
        };
        if (useWarmCache)
        {
            string baseline = At(fixture.Fs, fixture.Storage, $"full-source-preparation-v1/baselines/{Digest(fixture.Stock.ToJson())}");
            CopyTree(fixture.Fs, fixture.StockRoot, baseline, fixture.Stock);
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, baseline, boot)), Is.False);
        }
        byte[] installedBoot = stockBoot.ToArray();
        if (!sourceMatches)
            installedBoot[0] ^= 1;
        Put(fixture.Fs, fixture.Install, boot, installedBoot);
        if (sourceMatches)
        {
            var (prepared, _) = await fixture.Preparation.FetchInitialInstallAsync(RELEASE_ID, CHANNEL, null,
                fixture.Entry.Id, CancellationToken.None);
            await fixture.Preparation.VerifyTargetAsync(prepared, CancellationToken.None);
            Assert.That(prepared.BootConfigSettings, Is.EqualTo(BootConfigFile.ReadSettings(stockBoot)));
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, prepared.SourceRoot, boot)), Is.False);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Preparation.FetchInitialInstallAsync(RELEASE_ID, CHANNEL, null, fixture.Entry.Id,
                    CancellationToken.None));
            Assert.That(error!.Message, Does.Contain("cannot be reconstructed from verified inputs"));
        }
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
