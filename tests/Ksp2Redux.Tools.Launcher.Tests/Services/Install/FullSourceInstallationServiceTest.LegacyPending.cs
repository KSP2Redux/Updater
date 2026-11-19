using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Operations an earlier build left pending, and the identity and source pins of installations it wrote.
/// </summary>
public partial class FullSourceInstallationServiceTest
{
    // An install of release two whose update to release three an earlier build interrupted at a boundary, stored the
    // way that build stored it: the head and both operations list boot.config, and the pending operation is bound to
    // the digest of the legacy head from before it began.
    private static async Task<(string Operation, byte[] Head, byte[] OperationBytes)> LegacyPending(Fixture fixture,
        string boundary, bool after)
    {
        await Apply(fixture);
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, isAfter) =>
        {
            if (name == boundary && isAfter == after)
                throw new IOException("Process loss");
        }), fixture.Target3, initial: false));
        WriteLegacyRecords(fixture);
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        var pending = head["Pending"]!.GetValue<Guid>();
        string pendingFile = fixture.Fs.Path.Combine(fixture.Fs.Path.GetDirectoryName(fixture.Head)!, $"{pending:N}.json");
        var before = (JsonObject)head.DeepClone();
        before["Pending"] = null;
        Rewrite(fixture, pendingFile, journal =>
        {
            LegacyBytes(journal, "Current");
            LegacyBytes(journal, "Target");
            LegacyBytes(journal, "ReleaseManifest");
            journal["Release"]!["ManifestSha256"] = Digest(LegacyContract(fixture.Target3.Manifest.ToJson()));
            journal["PreviousDigest"] = Digest(Json(before));
        });
        return (pendingFile, fixture.Fs.File.ReadAllBytes(fixture.Head), fixture.Fs.File.ReadAllBytes(pendingFile));
    }

    /// <summary>
    /// An operation of an earlier build that stopped while preparing, with nothing changed in the game folder, is rolled
    /// back by whatever touches the installation first, and the installation works as before.
    /// </summary>
    /// <param name="entryPoint">What touches the installation first.</param>
    /// <returns>The verification task.</returns>
    [TestCase("reconcile")]
    [TestCase("launch")]
    [TestCase("update")]
    public async Task LegacyPendingInPreparation_IsRolledBackAndTheInstallationWorks(string entryPoint)
    {
        Fixture fixture = Create();
        var (operation, _, operationBytes) = await LegacyPending(fixture, "stage-create", false);
        Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["Pending"], Is.Not.Null);

        switch (entryPoint)
        {
            case "reconcile":
                FullSourceInstallationStatus status = fixture.Service().Reconcile(fixture.Entry.Id)!;
                Assert.That(status.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
                Assert.That(status.Release, Is.EqualTo(fixture.Target2.Identity));
                Generation(fixture, status);
                break;
            case "launch":
                new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), fixture.Service()).EnsureLaunchAllowed(fixture.Install);
                fixture.Service().AcquireLaunchLease(fixture.Install)?.Dispose();
                break;
            default:
                Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
                break;
        }

        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        ExpectCatalogMigrated(fixture);
        Assert.That(fixture.Fs.File.ReadAllBytes(operation), Is.EqualTo(operationBytes), "The operation file stays as evidence.");
        if (entryPoint != "update")
        {
            Assert.That(head["Pending"], Is.Null);
            Assert.That(head["Release"]!["ManifestSha256"]!.GetValue<string>(), Is.EqualTo(fixture.Target2.Identity.ManifestSha256));
            Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        }
    }

    /// <summary>
    /// An operation of an earlier build that got past preparation cannot be finished here. The installation, the
    /// operation and the head stay as they are, and the message says how to start over.
    /// </summary>
    /// <param name="after">Whether the earlier build stopped after the engine applied the generation.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacyPendingPastPreparation_IsRefusedWithAWayOutAndEvidenceRetained(bool after)
    {
        Fixture fixture = Create();
        var (operation, head, operationBytes) = await LegacyPending(fixture, "engine-apply", after);
        Dictionary<string, string> tree = fixture.Fs.Directory.EnumerateFiles(fixture.Install, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path)));

        var reconcile = Assert.Throws<InvalidDataException>(() => fixture.Service().Reconcile(fixture.Entry.Id));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture, target: fixture.Target3, initial: false));
        Assert.Throws<InvalidDataException>(() => fixture.Service().AcquireLaunchLease(fixture.Install));

        Assert.That(reconcile!.Message, Does.Contain("uninstall the game in Steam or Epic"));
        Assert.That(reconcile.Message, Does.Contain(fixture.Fs.Path.GetDirectoryName(fixture.Head)));
        Assert.That(reconcile.Message, Does.Contain(fixture.Fs.Path.Combine(fixture.Install, ".redux-transaction.active")));
        Assert.That(reconcile.Message, Does.Contain(fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}")));
        Assert.That(reconcile.Message, Does.Contain("Your saves are not affected."));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Head), Is.EqualTo(head));
        Assert.That(fixture.Fs.File.ReadAllBytes(operation), Is.EqualTo(operationBytes));
        Assert.That(fixture.Fs.Directory.EnumerateFiles(fixture.Install, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path))), Is.EqualTo(tree));
    }

    /// <summary>
    /// Following the refusal's instructions gives a clean start. A storefront reinstall can leave the engine marker in
    /// the game folder, so the message names it, and until it is deleted every action stays blocked.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LegacyPendingRefusal_FollowedByItsInstructions_AllowsANewInstall()
    {
        Fixture fixture = Create();
        await LegacyPending(fixture, "engine-apply", true);
        string marker = fixture.Fs.Path.Combine(fixture.Install, ".redux-transaction.active");
        // The earlier build was in the engine when it stopped, so its marker is in the game folder.
        fixture.Fs.File.WriteAllText(marker, "engine marker");
        string message = Assert.Throws<InvalidDataException>(() => fixture.Service().Reconcile(fixture.Entry.Id))!.Message;
        string[] named = System.Text.RegularExpressions.Regex.Matches(message, "'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        Assert.That(named, Is.EqualTo(new[]
        {
            fixture.Install, marker, fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}"),
            fixture.Fs.Path.GetDirectoryName(fixture.Head)
        }));

        // Restore the original game files the way a storefront does: the leftover marker survives it.
        fixture.Fs.Directory.Delete(fixture.Install, true);
        foreach (var file in fixture.Stock.Stock.Files)
        {
            Put(fixture.Fs, fixture.Install, file.Path, fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Root, $"stock-input/{file.Path}")));
        }
        foreach (string directory in fixture.Stock.Stock.Directories)
        {
            fixture.Fs.Directory.CreateDirectory(At(fixture.Fs, fixture.Install, directory));
        }
        // The fixture's release keeps this unowned file in the game folder, as every installation of it has.
        Put(fixture.Fs, fixture.Install, RETAINED, "explicit retained bytes");
        fixture.Fs.File.WriteAllText(marker, "leftover");
        // The folders the message names are deleted first, and only the marker is left to block.
        fixture.Fs.Directory.Delete(named[2], true);
        fixture.Fs.Directory.Delete(named[3], true);
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), fixture.Service());
        Assert.Throws<IOException>(() => state.EnsureLaunchAllowed(fixture.Install));
        Assert.That(marker, Is.EqualTo(named[1]));

        fixture.Fs.File.Delete(marker);

        Assert.That(fixture.Service().HasCatalog(fixture.Install), Is.False);
        Assert.DoesNotThrow(() => state.EnsureLaunchAllowed(fixture.Install));
        FullSourceInstallationStatus installed = await Apply(fixture);
        Assert.That(installed.Release, Is.EqualTo(fixture.Target2.Identity));
        foreach (StockBaselineFile file in installed.Generation.Files)
        {
            Assert.That(Digest(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, file.Path))), Is.EqualTo(file.Sha256), file.Path);
        }
        Assert.That((await Apply(fixture, target: fixture.Target3, initial: false)).Release, Is.EqualTo(fixture.Target3.Identity));
    }

    /// <summary>
    /// The window shows the refusal of an operation of an earlier build instead of letting it escape the launch command.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public async Task LegacyPendingPastPreparation_LaunchShowsTheMessageInsteadOfCrashing()
    {
        Fixture fixture = Create();
        await LegacyPending(fixture, "engine-apply", true);
        var coordinator = fixture.Service();
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var messages = new Mock<IMessageBoxService>();
        var home = ReadinessHome(fixture, coordinator, state, messages.Object);

        await home.LaunchGame();

        fixture.GameProcesses.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        Assert.That(messages.Invocations.Any(call => call.Arguments.OfType<string>().Any(text => text.Contains("uninstall the game in Steam or Epic"))), Is.True);
    }

    /// <summary>
    /// An installation migrated from an earlier build counts as installed for the release the feed publishes in its
    /// older form, and for nothing else. Repair and update of that release keep working.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task MigratedInstallation_IsInstalledForItsFeedRelease_AndOnlyThatOne()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        WriteLegacyRecords(fixture);
        byte[] published = LegacyContract(fixture.Target2.Manifest.ToJson());
        FullSourceReleaseIdentity identity = fixture.Service().GetPublishedIdentity(fixture.Entry.Id)!;
        Assert.That(identity, Is.EqualTo(fixture.Target2.Identity));

        var (feed, version) = await FeedOf(published, "release-two");
        InstallPlan plan = feed.GetFullSourcePlan(version)!;
        var installed = new Ksp2Install(fixture.Fs, Mock.Of<IModuleDefinitionService>(), fixture.Entry.ExePath, identity);

        Assert.That(plan.FullSourceManifestSha256, Is.EqualTo(identity.ManifestSha256));
        Assert.That(installed.IsInstalledVersion(version, plan), Is.True);
        Assert.That(installed.IsInstalledVersion(version, feed.GetFullSourcePlan(version, repair: true)), Is.True);
        Assert.DoesNotThrow(() => plan.RequireFullSourceMatch(identity));

        // The same release published without the entry agrees as well.
        var (currentFeed, currentVersion) = await FeedOf(fixture.Target2.Manifest.ToJson(), "release-two");
        Assert.That(installed.IsInstalledVersion(currentVersion, currentFeed.GetFullSourcePlan(currentVersion)), Is.True);

        // A release whose contract differs is another release, whichever form it is published in.
        foreach (bool legacy in new[] { true, false })
        {
            byte[] changed = fixture.Target3.Manifest.ToJson();
            if (legacy)
                changed = LegacyContract(changed);
            var (otherFeed, otherVersion) = await FeedOf(changed, "release-two");
            InstallPlan other = otherFeed.GetFullSourcePlan(otherVersion)!;
            Assert.That(installed.IsInstalledVersion(otherVersion, other), Is.False);
            Assert.Throws<InvalidDataException>(() => other.RequireFullSourceMatch(identity));
        }

        Generation(fixture, await Apply(fixture, initial: false, repair: true));
        Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(identity));
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
    }

    private static async Task<(ManifestReleasesFeed Feed, GameVersion Version)> FeedOf(byte[] target, string releaseId)
    {
        var provider = new Mock<IManifestReleasesFeedProviderService>();
        var manifest = new ReleaseManifest
        {
            Channel = "beta", GeneratedAt = new DateTime(2026, 1, 1), SchemaVersion = 3,
            Patches =
            [
                new ReleasePatch
                {
                    Version = "2.0.0.1.200", Requires = new PatchRequirement { Version = null }, Size = 1, Url = "https://x",
                    ChecksumSha256 = "0", ReleasedAt = DateTime.UtcNow,
                    FullSource = new FullSourceReleaseDescriptor(releaseId, "beta", "2.0.0.1.200", target, Digest(target),
                        "https://fixture.test/release.json", "https://fixture-backup.test/release.json", [], 1)
                }
            ]
        };
        provider.Setup(service => service.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(manifest);
        var feed = new ManifestReleasesFeed(provider.Object, Mock.Of<ILogService>(), @"C:\downloads",
            new FeedInfo { Repository = "KSP2Redux/Redux", Filename = "manifest-beta.json" });
        Assert.That(await feed.UpdateManifest(), Is.True);
        return (feed, feed.GetAllVersions().Single());
    }

    /// <summary>
    /// Source pins an earlier build took that name boot.config are dropped when the catalog migrates, so the
    /// recovery of the original backup can still finish. The other pins stay.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LegacyRecoverySources_LoseTheirBootConfigPins()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        var bootPin = new StockBaselineFile(BOOT, 3, new string('A', 64));
        var bundlePin = new StockBaselineFile(BUNDLE, 5, new string('B', 64));
        var sources = new StockArchiveSources(false, null, [bootPin, bundlePin], [$"{BOOT}@10-3: original", $"{BUNDLE}@0-2: converted"]);
        WriteLegacyRecords(fixture);
        Rewrite(fixture, fixture.Head, catalog =>
        {
            catalog["ExistingBackup"] = JsonSerializer.SerializeToNode(new StockBaselineFile(fixture.Archive, archive.Length, Digest(archive)));
            catalog["RecoverySources"] = JsonSerializer.SerializeToNode(sources);
        });

        _ = fixture.Service().GetPublishedIdentity(fixture.Entry.Id);

        JsonObject migrated = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        var kept = migrated["RecoverySources"]!.Deserialize<StockArchiveSources>()!;
        Assert.That(kept.CurrentFiles, Is.EqualTo(new[] { bundlePin }));
        Assert.That(kept.JournalRanges, Is.EqualTo(new[] { $"{BUNDLE}@0-2: converted" }));
        ExpectCatalogMigrated(fixture);
    }

    /// <summary>
    /// When boot.config was the only file an earlier build recovered through the journal, the pin of that journal goes with
    /// it, because a new observation of the other files reads no journal. A journal that served another file stays pinned.
    /// </summary>
    /// <param name="alsoBundle">Whether another file was recovered through the journal too.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacyRecoverySources_DropTheJournalPinWhenBootConfigWasItsOnlyFile(bool alsoBundle)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        var journal = new StockBaselineFile("journal.json", 7, new string('J', 64));
        var bundlePin = new StockBaselineFile(BUNDLE, 5, new string('B', 64));
        var sources = new StockArchiveSources(false, journal, alsoBundle ? [new StockBaselineFile(BOOT, 3, new string('A', 64)), bundlePin]
            : [new StockBaselineFile(BOOT, 3, new string('A', 64))], alsoBundle ? [$"{BOOT}@10-3: original", $"{BUNDLE}@0-2: converted"] : [$"{BOOT}@10-3: original"]);
        WriteLegacyRecords(fixture);
        Rewrite(fixture, fixture.Head, catalog =>
        {
            catalog["ExistingBackup"] = JsonSerializer.SerializeToNode(new StockBaselineFile(fixture.Archive, archive.Length, Digest(archive)));
            catalog["RecoverySources"] = JsonSerializer.SerializeToNode(sources);
        });

        _ = fixture.Service().GetPublishedIdentity(fixture.Entry.Id);

        var kept = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["RecoverySources"]!.Deserialize<StockArchiveSources>()!;
        Assert.That(kept.Journal, alsoBundle ? Is.EqualTo(journal) : Is.Null);
        Assert.That(kept.JournalRanges, Has.Length.EqualTo(alsoBundle ? 1 : 0));
    }
}
