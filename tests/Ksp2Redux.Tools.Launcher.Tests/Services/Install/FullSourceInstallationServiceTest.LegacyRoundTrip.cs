using System.Text;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;
using GameDistribution = Ksp2Redux.Tools.Launcher.Models.Distribution;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    /// <summary>
    /// Legacy patching can run before and after a full-source round trip in the same folder.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativeLegacyRoundTrip_InstallsBeforeUpgradeAndAfterStockHandoffAsync()
    {
        var fs = new RealFileSystem();
        string root = fs.Path.Combine(fs.Path.GetTempPath(), $"redux-legacy-roundtrip-{Guid.NewGuid():N}");
        try
        {
            await VerifyLegacyRoundTripAsync(Create(fs, root));
        }
        finally
        {
            if (fs.Directory.Exists(root))
                fs.Directory.Delete(root, true);
        }
    }

    private static async Task VerifyLegacyRoundTripAsync(Fixture fixture)
    {
        const string mainAssembly = "KSP2_x64_Data/Managed/Assembly-CSharp.dll";
        const string legacyVersion = "0.2.8.5.103184";
        const string legacyExe = "legacy patched executable";
        const string prepatchMarker = "prepatched.nodelete";
        const string legacyFile = "Redux/legacy-patch.bin";
        const string staleFile = "Redux/leftover.bin";
        const string shippedFile = "Redux/shipped.bin";
        const string shippedContents = "v3 published bytes";
        const string userConfig = "Redux/config.json";
        const string configContents = "{\"userPreference\":true}";
        const string userMod = "mods/user.txt";
        var fs = fixture.Fs;

        string original = fixture.Stock.SourceRoot;
        using (var module = TestHelpers.GenerateMockVersionID(("VERSION_TEXT", "0.2.2.0.32914"),
                   ("DEBUG_INFO", "BUILD_INFO")).module)
        {
            WriteModule(original, module);
        }
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(At(fs, fixture.Install, mainAssembly))!);
        fs.File.Copy(At(fs, original, mainAssembly), At(fs, fixture.Install, mainAssembly));
        byte[] stockModule = fs.File.ReadAllBytes(At(fs, original, mainAssembly));
        var originalStock = fixture.Stock.Stock;
        // The legacy snapshot stores files. The real Windows storefront contract has no required empty directories.
        var stockContract = new StockBaselineManifest(originalStock.BaselineIdentity, originalStock.Provenance,
            originalStock.Files.Append(new StockBaselineFile(mainAssembly, stockModule.LongLength, Digest(stockModule))));
        var stock = fixture.Stock with
        {
            Stock = stockContract,
            CurrentOwned = stockContract,
            SourceRoot = fixture.Install,
            InstallationId = fixture.Entry.Id,
        };

        Put(fs, fixture.Target2.SourceRoot, shippedFile, shippedContents);
        var originalTarget = fixture.Target2.Manifest;
        byte[] shippedBytes = Encoding.UTF8.GetBytes(shippedContents);
        var targetManifest = new StockBaselineManifest(originalTarget.BaselineIdentity, originalTarget.Provenance,
            originalTarget.Files.Append(new StockBaselineFile(shippedFile, shippedBytes.LongLength, Digest(shippedBytes))),
            originalTarget.Directories);
        var v3 = fixture.Target2 with
        {
            Identity = fixture.Target2.Identity with { ManifestSha256 = Digest(targetManifest.ToJson()) },
            Manifest = targetManifest
        };

        string overlay = At(fs, fixture.Root, "legacy-overlay");
        Put(fs, overlay, Ksp2Install.KSP2_EXE_NAME, legacyExe);
        Put(fs, overlay, legacyFile, "v2 patch content");
        using (var module = TestHelpers.GenerateMockVersionID(("VERSION_TEXT", legacyVersion),
                   ("DEBUG_INFO", "test-legacy-release"), ("CHANNEL_NAME", "stable")).module)
        {
            WriteModule(overlay, module);
        }
        string overlayPatch = At(fs, fixture.Root, "legacy-overlay.patch");
        using (Ksp2Patch.FromDiff(fs, overlayPatch, original, overlay)) { }

        string prepatch = At(fs, fixture.Root, "legacy-prepatch");
        Put(fs, prepatch, prepatchMarker, "test prepatch content");
        string prepatchPath = At(fs, fixture.Root, "legacy-prepatch.patch");
        using (Ksp2Patch.FromDiff(fs, prepatchPath, original, prepatch)) { }
        Put(fs, fixture.Install, userMod, USER_BYTES);
        Put(fs, fixture.Install, staleFile, "stale content in the first legacy snapshot");

        var versionPatch = new ReleasePatch
        {
            Version = legacyVersion,
            Requires = new PatchRequirement(),
            Url = "https://fixture.test/legacy-overlay.patch",
            ChecksumSha256 = Digest(fs.File.ReadAllBytes(overlayPatch)),
            Size = fs.FileInfo.New(overlayPatch).Length,
            ReleasedAt = DateTime.UnixEpoch
        };
        var provider = new Mock<IManifestReleasesFeedProviderService>(MockBehavior.Strict);
        provider.Setup(service => service.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(new ReleaseManifest
        {
            Channel = "stable", SchemaVersion = 2, GeneratedAt = DateTime.UnixEpoch, Patches = [versionPatch]
        });
        var log = Mock.Of<ILogService>();
        var v2Feed = new ManifestReleasesFeed(provider.Object, log, fixture.Storage, new FeedInfo
        {
            Repository = "https://github.com/fixture/releases", Filename = "manifest-stable.json"
        });
        Assert.That(await v2Feed.UpdateManifest(), Is.True);

        var downloads = new Mock<IPatchDownloadService>(MockBehavior.Strict);
        downloads.Setup(service => service.EnqueueAll(It.IsAny<IReadOnlyList<PatchDownloadRequest>>(),
                It.IsAny<PatchDownloadSource>(), It.IsAny<int>(), It.IsAny<Action<LogMessage>>(),
                It.IsAny<Action<long, long>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<PatchDownloadRequest> requests, PatchDownloadSource _, int _, Action<LogMessage> _,
                Action<long, long> _, CancellationToken _) => requests.Select(request =>
            {
                Assert.That(request.Patch, Is.EqualTo(versionPatch));
                string staged = At(fs, fixture.Storage, $"{Guid.NewGuid():N}.patch");
                fs.File.Copy(overlayPatch, staged);
                return Task.FromResult(staged);
            }).ToArray());
        var assembly = new Mock<IAssemblyService>(MockBehavior.Strict);
        assembly.Setup(service => service.GetManifestResourceStream("Ksp2Redux.Tools.Launcher.Prepatches.portable-prepatch.patch"))
            .Returns(() => fs.File.OpenRead(prepatchPath));

        var zip = new ZipFileService(fs);
        var modules = new ModuleDefinitionService();
        var boot = new BootConfigSettingsService(fixture.Configuration, fs, log);
        var coordinator = fixture.Service(bootConfig: boot);
        var state = new InstallationStateService(fs, zip, coordinator);
        var plans = new InstallPlanService(fs, new CacheService(fs, zip, state), SystemEnvironmentProvider.Instance,
            assembly.Object, modules, zip, fixture.Space, downloads.Object, fixture.Configuration, state,
            fullSource: coordinator, preparation: fixture.Preparation.Object, bootConfig: boot);

        InstallPlan legacyPlan = await InstallLegacyAsync();
        AssertLegacySnapshotText(staleFile, "stale content in the first legacy snapshot");

        var upgrade = new InstallPlan
        {
            FullSourceReleaseId = v3.Identity.ReleaseId,
            FullSourceChannel = v3.Identity.Channel,
            FullSourceVersion = v3.Identity.Version,
            FullSourceManifestSha256 = v3.Identity.ManifestSha256,
        };
        const string preparationInterrupted = "Fixture release could not be downloaded.";
        int preparations = 0;
        fixture.Preparation.Setup(service => service.FetchSelectedPlanAsync(upgrade, fixture.Entry.Id, false, null,
                It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>()))
            .ReturnsAsync(() =>
            {
                AssertStockBytes();
                Assert.That(fs.File.Exists(fixture.Archive), Is.False);
                preparations++;
                if (preparations is 1 or 3)
                {
                    Assert.Throws<IOException>(() =>
                    {
                        using var lease = state.AcquireLaunchLease(fixture.Install);
                    });
                }
                if (preparations == 1)
                    throw new IOException(preparationInterrupted);
                if (preparations == 3)
                    Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userConfig)), Is.EqualTo(configContents));
                return (v3, stock);
            });
        var target = v3 with { MayConsumeSource = true };
        fixture.Preparation.Setup(service => service.VerifyTargetAsync(target, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        fixture.Preparation.Setup(service => service.VerifyStockAsync(stock, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var failure = await Assert.ThrowsAsync<IOException>(() => ApplyAsync(upgrade));
        Assert.That(failure!.Message, Is.EqualTo(preparationInterrupted));
        await ApplyAsync(upgrade);
        AssertV3Bytes();
        Assert.That(fs.File.Exists(At(fs, fixture.Install, staleFile)), Is.False);
        Put(fs, fixture.Install, userConfig, configContents);
        await new StockArchive(fs, zip).VerifyAsync(fixture.Archive, stockContract);
        byte[] v3Archive = fs.File.ReadAllBytes(fixture.Archive);

        await Assert.ThrowsAsync<IOException>(() => ApplyAsync(legacyPlan));
        Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(v3.Identity));
        Assert.That(fs.File.ReadAllText(fixture.Entry.ExePath), Is.EqualTo("full source generation two"));
        Assert.That(fs.File.ReadAllBytes(fixture.Archive), Is.EqualTo(v3Archive));

        var uninstall = new InstallPlan();
        uninstall.Uninstall();
        await ApplyAsync(uninstall);
        AssertStockBytes();
        Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userConfig)), Is.EqualTo(configContents));
        Assert.That(fs.File.Exists(fixture.Head), Is.False);
        Assert.That(fs.File.Exists(fixture.Archive), Is.False);

        await InstallLegacyAsync();
        Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userConfig)), Is.EqualTo(configContents));
        AssertLegacySnapshotText(userConfig, configContents);
        string storage = At(fs, fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string retained = fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single();
        Assert.That(fs.File.ReadAllBytes(retained), Is.EqualTo(v3Archive));

        await ApplyAsync(upgrade);
        Assert.That(preparations, Is.EqualTo(3));
        AssertV3Bytes();
        Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userConfig)), Is.EqualTo(configContents));
        Assert.That(fs.File.ReadAllBytes(fixture.Archive), Is.EqualTo(v3Archive));
        Assert.That(fs.File.Exists(retained), Is.False);

        Task ApplyAsync(InstallPlan plan) => plans.ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        async Task<InstallPlan> InstallLegacyAsync()
        {
            var current = new Ksp2Install(fs, modules, fixture.Entry.ExePath);
            Assert.That(current.GameVersion?.BuildNumber, Is.EqualTo("32914"));
            InstallPlan plan = v2Feed.GetPatchListToVersion(current.GameVersion!, v2Feed.GetAllVersions().Single());
            await ApplyAsync(plan);
            var installed = new Ksp2Install(fs, modules, fixture.Entry.ExePath);
            Assert.That(installed.GameVersion?.BuildNumber, Is.EqualTo("103184"));
            Assert.That(installed.Distribution, Is.EqualTo(GameDistribution.Redux));
            Assert.That(fs.File.ReadAllText(fixture.Entry.ExePath), Is.EqualTo(legacyExe));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, prepatchMarker)), Is.EqualTo("test prepatch content"));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, legacyFile)), Is.EqualTo("v2 patch content"));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userMod)), Is.EqualTo(USER_BYTES));
            Assert.That(coordinator.HasCatalog(fixture.Install), Is.False);
            using var legacyArchive = zip.OpenRead(fixture.Archive);
            Assert.That(legacyArchive.GetEntry(Ksp2Install.KSP2_EXE_NAME), Is.Not.Null);
            Assert.That(legacyArchive.GetEntry(StockBaselineManifest.MANIFEST_PATH), Is.Null);
            return plan;
        }

        void AssertStockBytes()
        {
            foreach (var file in stockContract.Files)
            {
                Assert.That(fs.File.ReadAllBytes(At(fs, fixture.Install, file.Path)),
                    Is.EqualTo(fs.File.ReadAllBytes(At(fs, original, file.Path))), file.Path);
            }
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userMod)), Is.EqualTo(USER_BYTES));
            Assert.That(fs.File.Exists(At(fs, fixture.Install, prepatchMarker)), Is.False);
            Assert.That(fs.File.Exists(At(fs, fixture.Install, legacyFile)), Is.False);
            Assert.That(fs.File.Exists(At(fs, fixture.Install, shippedFile)), Is.False);
            Assert.That(fs.File.Exists(At(fs, fixture.Install, "added.bin")), Is.False);
        }

        void AssertV3Bytes()
        {
            Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(v3.Identity));
            Assert.That(fs.File.ReadAllText(fixture.Entry.ExePath), Is.EqualTo("full source generation two"));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, BUNDLE)), Is.EqualTo("full source streaming generation two"));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, shippedFile)), Is.EqualTo(shippedContents));
            Assert.That(fs.File.Exists(At(fs, fixture.Install, legacyFile)), Is.False);
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userMod)), Is.EqualTo(USER_BYTES));
        }

        void AssertLegacySnapshotText(string path, string expected)
        {
            using var legacyArchive = zip.OpenRead(fixture.Archive);
            var entry = legacyArchive.Entries.Single(item => item.FullName.Replace('\\', '/') == path);
            using var reader = new StreamReader(entry.Open());
            Assert.That(reader.ReadToEnd(), Is.EqualTo(expected));
        }

        void WriteModule(string folder, Mono.Cecil.ModuleDefinition module)
        {
            string path = At(fs, folder, mainAssembly);
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
            using var destination = fs.File.Create(path);
            module.Write(destination);
        }
    }
}
