using System.IO.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Tests;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Configured-authority release preparation, exact reconstruction and coordinator behavior on disposable roots.
/// </summary>
public partial class FullSourcePreparationServiceTest
{
    private class InterruptedDownload(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private bool _readOnce;

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_readOnce)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            _readOnce = true;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 100)], cancellationToken);
        }
    }

    private const string BUNDLE = "KSP2_x64_Data/StreamingAssets/aa/content.bundle";
    private const string WEM = "KSP2_x64_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/media.wem";
    private const string RELEASE_ID = "fixture-release-two";
    private const string CHANNEL = "beta";
    private const string VERSION = "2.0.0.1.200";
    private const string AUTHORITY = "fixture:explicitly-injected-trusted-test-authority";

    private class Fixture
    {
        internal readonly IFileSystem Fs;
        internal readonly string Root;
        internal readonly string Install;
        internal readonly string Storage;
        internal string Artifact;
        internal FullPlayerProbeBinding? FullPlayer;
        internal readonly string StockRoot;
        internal readonly StockBaselineManifest Stock;
        internal readonly StockBaselineManifest Target;
        internal readonly Ksp2InstallEntry Entry;
        internal readonly LauncherConfig Config;
        internal readonly Mock<ILauncherConfigService> Configuration = new();
        internal readonly Mock<IManifestReleasesFeedProviderService> Transport = new();
        internal readonly Mock<ISteamDepotDownloader> Steam = new();
        internal readonly Mock<IOperatingSystemService> Os = new();
        internal readonly Mock<IDiskSpaceService> Space = new();
        internal readonly FullSourcePreparationService Preparation;
        internal readonly PatchDownloadService Downloads;
        internal readonly IManifestReleasesFeedProviderService Provider;
        internal ReleasePatch Patch;
        internal bool Offline;
        internal string FeedChannel = CHANNEL;
        internal ReleaseManifest? SuppliedFeed;
        internal Func<string, long, CancellationToken, HttpResponseMessage>? ResponseOverride;
        internal readonly List<(string Url, long Offset)> Requests = [];

        internal Fixture(IFileSystem fs, string root, string stockRoot, StockBaselineManifest stock,
            StockBaselineManifest target, string artifact, ReleaseRecipePublication publication,
            Func<ILauncherConfigService, IManifestReleasesFeedProviderService>? providerFactory = null,
            FullPlayerProbeBinding? fullPlayer = null, bool initializeStock = true,
            ReleaseManifest? suppliedFeed = null, ReleasePatch? suppliedPatch = null, bool seedUserFile = true)
        {
            if (fullPlayer is not null && suppliedFeed is not null)
                throw new ArgumentException("Select diagnostic binding or supplied production publisher metadata.");
            Fs = fs;
            Root = root;
            Stock = stock;
            Target = target;
            Artifact = artifact;
            FullPlayer = fullPlayer;
            StockRoot = stockRoot;
            Install = At(fs, root, "game");
            Storage = At(fs, root, "launcher");
            fs.Directory.CreateDirectory(Storage);
            if (initializeStock)
                CopyTree(fs, stockRoot, Install, stock);
            if (fullPlayer is not null)
                fs.File.Copy(fullPlayer.ArchivePath, At(fs, Install, "uninstall.zip"), false);
            if (seedUserFile)
                Put(fs, Install, "mods/user.txt", Encoding.UTF8.GetBytes("unknown user bytes retained"));
            Entry = new Ksp2InstallEntry { ExePath = At(fs, Install, Ksp2Install.KSP2_EXE_NAME), Name = "Fixture" };
            var feed = new FeedInfo { Repository = "https://github.com/fixture/release-authority", Filename = "manifest-beta.json" };
            Config = new LauncherConfig { Ksp2Installs = [Entry], Feeds = [feed], ActiveKsp2InstallId = Entry.Id };
            Configuration.SetupGet(service => service.Config).Returns(Config);
            Configuration.Setup(service => service.GetLocalStorageDirectory()).Returns(Storage);
            Os.Setup(service => service.IsWindows()).Returns(true);
            Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string path) =>
                fullPlayer is null ? long.MaxValue : fs.DriveInfo.New(fs.Path.GetPathRoot(path)!).AvailableFreeSpace);
            var pipeline = new ReleaseRecipePipeline(fs, fs is MockFileSystem ? _ => { } : null);
            SuppliedFeed = suppliedFeed;
            Patch = suppliedFeed is not null ? suppliedPatch ?? throw new ArgumentException("A supplied feed requires its selected patch.") :
                pipeline.CreateFeedEntry(artifact, publication, fullPlayer?.ReleaseId ?? RELEASE_ID,
                fullPlayer?.Channel ?? CHANNEL, fullPlayer?.Version ?? VERSION,
                "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
                [new FullSourceBaselineDefinition(stock.ToJson(), [])], false, DateTime.UtcNow);
            Transport.Setup(service => service.GetTrustedManifestAsync(It.IsAny<FeedInfo>(), It.IsAny<CancellationToken>()))
                .Returns((FeedInfo _, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (Offline)
                        throw new HttpRequestException("Injected fixture authority is offline.");
                    // Clone each fetch so mutations cannot turn a prior local object into authority.
                    return Task.FromResult(new TrustedReleaseManifest(JsonSerializer.Deserialize<ReleaseManifest>(
                        JsonSerializer.SerializeToUtf8Bytes(Feed()))!, AUTHORITY));
                });
            Transport.Setup(service => service.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(() => Feed());
            Transport.Setup(service => service.DownloadFileAsync(It.IsAny<FeedInfo>(), It.IsAny<string>(),
                    It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((FeedInfo _, string url, long offset, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    lock (Requests)
                    {
                        Requests.Add((url, offset));
                    }
                    return Task.FromResult(ResponseOverride?.Invoke(url, offset, ct) ?? Response(url, offset));
                });
            Provider = providerFactory?.Invoke(Configuration.Object) ?? Transport.Object;
            Downloads = new PatchDownloadService(fs, Provider, Mock.Of<ILogService>(), Space.Object);
            Preparation = new FullSourcePreparationService(fs, Configuration.Object, Provider, Downloads,
                () => Steam.Object, new ZipFileService(fs), Space.Object, Os.Object, fs is MockFileSystem ? _ => { } : null);
        }

        internal ReleaseManifest Feed() => SuppliedFeed ?? new() { SchemaVersion = 3, Channel = FullPlayer?.Channel ?? FeedChannel, GeneratedAt = DateTime.UtcNow, Patches = [Patch] };

        internal FullSourceInstallationService Coordinator(Action<string, bool>? fault = null,
            Action<string, bool>? engineFault = null) => new(Fs,
            Configuration.Object, new ZipFileService(Fs), Space.Object, Preparation, Os.Object, Mock.Of<ILogService>(),
            Mock.Of<IGameProcessService>(), new InstallFileLockService(Fs, Os.Object), fault,
            new InstallTransactionEngine(Fs, Space.Object.GetAvailableFreeSpace, engineFault,
                validateFileStreams: Fs is MockFileSystem ? _ => { } : null));

        internal HttpResponseMessage Response(string url, long offset, string? artifactRoot = null, string? filename = null)
        {
            filename ??= new Uri(url).Segments.Last();
            Stream input = Fs.File.OpenRead(At(Fs, artifactRoot ?? Artifact, filename));
            long length = input.Length;
            input.Position = offset;
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(input)
            };
            response.Content.Headers.ContentLength = length - offset;
            if (offset > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, length - 1, length);
            return response;
        }
    }

    private static Fixture Create()
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\preparation-fixture";
        string stockRoot = At(fs, root, "frozen-stock");
        string targetRoot = At(fs, root, "frozen-target");
        var stockFiles = new Dictionary<string, byte[]>
        {
            [Ksp2Install.KSP2_EXE_NAME] = Encoding.UTF8.GetBytes("fixture original executable"),
            ["KSP2_x64_Data/boot.config"] = Encoding.UTF8.GetBytes("fixture=stock\r\n"),
            [BUNDLE] = Enumerable.Range(0, 262144).Select(index => (byte)(index * 13)).ToArray(),
            [WEM] = Encoding.UTF8.GetBytes("fixture-original-encoded-media")
        };
        var targetFiles = stockFiles.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone());
        targetFiles[BUNDLE][131072] ^= 63;
        targetFiles[WEM] = Encoding.UTF8.GetBytes("fixture-new-encoded-media");
        StockBaselineManifest stock = PutManifest(fs, stockRoot, "fixture-original-storefront", stockFiles);
        StockBaselineManifest target = PutManifest(fs, targetRoot, RELEASE_ID, targetFiles);
        string artifact = At(fs, root, "artifact");
        var pipeline = new ReleaseRecipePipeline(fs, _ => { });
        ReleaseRecipePublication publication = pipeline.Publish(targetRoot, target,
            [new ReleaseRecipeSourceTree(stockRoot, stock, stock.Files.Select(file =>
                new ReleaseRecipeSource(file.Path, BundleRecipeSourceKind.RawFile)).ToArray())], artifact);
        return new Fixture(fs, root, stockRoot, stock, target, artifact, publication);
    }

    private static StockBaselineManifest PutManifest(IFileSystem fs, string root, string identity, Dictionary<string, byte[]> files)
    {
        foreach (var file in files)
        {
            Put(fs, root, file.Key, file.Value);
        }
        fs.Directory.CreateDirectory(At(fs, root, "empty"));
        return new StockBaselineManifest(identity, "explicit disposable fixture, not production authentication",
            files.Select(file => new StockBaselineFile(file.Key, file.Value.Length, Digest(file.Value))), ["empty"]);
    }

    private static void Put(IFileSystem fs, string root, string path, byte[] bytes)
    {
        string destination = At(fs, root, path);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(destination)!);
        fs.File.WriteAllBytes(destination, bytes);
    }

    private static void CopyTree(IFileSystem fs, string source, string destination, StockBaselineManifest manifest)
    {
        foreach (var directory in manifest.Directories)
        {
            fs.Directory.CreateDirectory(At(fs, destination, directory));
        }
        foreach (var file in manifest.Files)
        {
            string path = At(fs, destination, file.Path);
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
            fs.File.Copy(At(fs, source, file.Path), path, true);
        }
    }

    private static void AssertTree(IFileSystem fs, string root, StockBaselineManifest expected)
    {
        foreach (var file in expected.Files)
        {
            byte[] bytes = fs.File.ReadAllBytes(At(fs, root, file.Path));
            Assert.That(bytes.LongLength, Is.EqualTo(file.Length), file.Path);
            Assert.That(Digest(bytes), Is.EqualTo(file.Sha256), file.Path);
        }
    }

    private static string At(IFileSystem fs, string root, string path) => fs.Path.Combine(root, path.Replace('/', fs.Path.DirectorySeparatorChar));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Fresh authority metadata drives exact staged files and real coordinator update, repair and uninstall.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task FeedToPreparationToCoordinator_ExactGenerationsAndImmutableStock()
    {
        Fixture fixture = Create();
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        AssertTree(fixture.Fs, target.SourceRoot, fixture.Target);
        Assert.That(stock.SourceRoot, Is.EqualTo(fixture.Install));
        Assert.That(fixture.Fs.Directory.Exists(At(fixture.Fs, fixture.Storage, "full-source-preparation-v1/baselines")), Is.False);
        string originalFile = At(fixture.Fs, fixture.Install, WEM);
        byte[] originalBytes = fixture.Fs.File.ReadAllBytes(originalFile);
        string archivePath = At(fixture.Fs, fixture.Install, "uninstall.zip");
        var streamStatistics = ((MockFileSystem)fixture.Fs).Statistics.FileStream;
        string preparedMedia = At(fixture.Fs, target.SourceRoot, WEM);
        int readsBeforeApply = 0;
        bool sawVerifiedArchiveBeforeMutation = false;
        FullSourceInstallationService coordinator = fixture.Coordinator((boundary, after) =>
        {
            if (boundary != "archive-create" || !after)
                return;
            AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
            Assert.That(fixture.Fs.File.Exists(archivePath), Is.True);
            Assert.That(streamStatistics[preparedMedia].Methods.Count(method => method.Name is "Read" or "ReadAsync") - readsBeforeApply,
                Is.EqualTo(2), "The small staged file should be read to EOF once before the stock backup.");
            sawVerifiedArchiveBeforeMutation = true;
        });
        Put(fixture.Fs, fixture.Install, WEM, Encoding.UTF8.GetBytes("changed after stock preparation"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
        Assert.That(fixture.Fs.File.Exists(archivePath), Is.False);
        Assert.That(coordinator.HasCatalog(fixture.Install), Is.False);
        fixture.Fs.File.WriteAllBytes(originalFile, originalBytes);
        readsBeforeApply = streamStatistics[preparedMedia].Methods.Count(method => method.Name is "Read" or "ReadAsync");
        await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        Assert.That(sawVerifiedArchiveBeforeMutation, Is.True);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        byte[] archive = fixture.Fs.File.ReadAllBytes(archivePath);
        await coordinator.ApplyAsync(fixture.Entry.Id,
            await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None), null, false, _ => { }, CancellationToken.None);
        Put(fixture.Fs, fixture.Install, WEM, Encoding.UTF8.GetBytes("observed damage"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ApplyAsync(fixture.Entry.Id, target, null, false, _ => { }, CancellationToken.None));
        await coordinator.ApplyAsync(fixture.Entry.Id, target, null, true, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        fixture.Offline = true;
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, "uninstall.zip")), Is.EqualTo(archive));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
        fixture.Steam.Verify(service => service.DownloadDepotsAsync(It.IsAny<uint>(), It.IsAny<IReadOnlyList<SteamDepot>>(),
            It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A cached stage never authorizes an operation when the configured online authority is unavailable.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task OfflineCache_DoesNotSelfAuthorize()
    {
        Fixture fixture = Create();
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        fixture.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Preparation.VerifyTargetAsync(target, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Identity, permission, target contract and baseline mutations cannot cross the authority boundary.
    /// </summary>
    /// <param name="field">The independently bound field to change.</param>
    /// <returns>The verification task.</returns>
    [TestCase("channel"), TestCase("version"), TestCase("manifest"), TestCase("permission"), TestCase("authentication")]
    public async Task ChangedPreparedClaims_AreRejected(string field)
    {
        Fixture fixture = Create();
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        target = field switch
        {
            "channel" => target with { Identity = target.Identity with { Channel = "internal" } },
            "version" => target with { Identity = target.Identity with { Version = "3.0.0.1.300" } },
            "manifest" => target with { Manifest = fixture.Stock },
            "permission" => target with { AllowsDerivedBootConfig = true },
            _ => target with { Identity = target.Identity with { Authentication = "local-sha256" } }
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.VerifyTargetAsync(target, CancellationToken.None));
    }

    /// <summary>
    /// Wrong metadata is rejected before target staging or live-file mutation.
    /// </summary>
    /// <param name="field">The authority or artifact inconsistency to inject.</param>
    /// <returns>The verification task.</returns>
    [TestCase("channel"), TestCase("version"), TestCase("manifest"), TestCase("baseline")]
    public async Task WrongAuthorityMetadata_IsRejectedBeforeStaging(string field)
    {
        Fixture fixture = Create();
        FullSourceReleaseDescriptor descriptor = fixture.Patch.FullSource!;
        descriptor = field switch
        {
            "channel" => descriptor with { Channel = "internal" },
            "version" => descriptor with { Version = "3.0.0.1.300" },
            "manifest" => descriptor with { TargetManifestSha256 = new string('0', 64) },
            _ => descriptor with { Baselines = [new FullSourceBaselineDefinition(fixture.Target.ToJson(), [])] }
        };
        fixture.Patch = fixture.Patch with { FullSource = descriptor };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Corrupt or truncated pack bodies cannot produce a prepared stage.
    /// </summary>
    /// <param name="truncate">True to truncate, false to corrupt the pack.</param>
    /// <returns>The verification task.</returns>
    [TestCase(true), TestCase(false)]
    public async Task InvalidPacks_FailWithoutChangingInstallation(bool truncate)
    {
        Fixture fixture = Create();
        fixture.ResponseOverride = (url, offset, _) =>
        {
            if (url.EndsWith("release.json", StringComparison.Ordinal))
                return fixture.Response(url, offset);
            byte[] bytes = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Artifact, new Uri(url).Segments.Last()));
            if (truncate)
                bytes = bytes[..^1];
            else
                bytes[0] ^= 255;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        };
        await Assert.ThrowsAsync<PatchDownloadException>(async () => await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Stop pack transfers if the stage cannot fit, and recheck after downloading.
    /// </summary>
    /// <param name="spaceLostDuringDownload">True if the stage initially fits.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    public async Task PreparationChecksStageSpaceBeforeAndAfterPackDownloads(bool spaceLostDuringDownload)
    {
        Fixture fixture = Create();
        string stages = At(fixture.Fs, fixture.Storage, "full-source-preparation-v1/stages");
        bool RequestedPack() => fixture.Requests.Any(request =>
            new Uri(request.Url).Segments.Last().StartsWith("pack-", StringComparison.Ordinal));
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(stages)).Returns(() =>
            spaceLostDuringDownload && !RequestedPack() ? long.MaxValue : 0L);

        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));

        Assert.That(error!.Message, Does.StartWith("Insufficient or unknown preparation space."));
        Assert.That(RequestedPack(), Is.EqualTo(spaceLostDuringDownload));
        Assert.That(fixture.Fs.Directory.EnumerateFileSystemEntries(stages), Is.Empty);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Admit Windows preparation against the combined stage and additional pack cache footprint.
    /// </summary>
    /// <param name="cache">The initial pack cache state.</param>
    /// <returns>The verification task.</returns>
    [TestCase("cold")]
    [TestCase("partial")]
    [TestCase("warm")]
    public async Task PreparationAccountsForPackCacheGrowthBeforeTransfer(string cache)
    {
        Fixture fixture = Create();
        string packPath = fixture.Fs.Directory.EnumerateFiles(fixture.Artifact, "pack-*.bin").Single();
        byte[] pack = fixture.Fs.File.ReadAllBytes(packPath);
        string filename = fixture.Fs.Path.GetFileName(packPath);
        string completed = $"full-source-preparation-v1/downloads/patches/{Digest(pack)}.patch";
        int partialLength = cache == "partial" ? pack.Length / 2 : 0;
        if (cache == "warm")
            Put(fixture.Fs, fixture.Storage, completed, pack);
        else if (cache == "partial")
            Put(fixture.Fs, fixture.Storage, completed + ".partial", pack[..partialLength]);

        long additionalBytes = cache == "warm" ? 0 : pack.Length - partialLength;
        long available = fixture.Target.TotalBytes + 512L * 1024 * 1024 + additionalBytes;
        string stages = At(fixture.Fs, fixture.Storage, "full-source-preparation-v1/stages");
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(stages))
            .Returns(cache == "cold" ? available - 1 : available);

        if (cache == "cold")
        {
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
            Assert.That(error!.Message, Does.StartWith("Insufficient or unknown preparation space."));
            Assert.That(fixture.Requests.Select(request => new Uri(request.Url).Segments.Last()), Is.EqualTo(new[] { "release.json" }));
            Assert.That(fixture.Fs.Directory.EnumerateFileSystemEntries(stages), Is.Empty);
            AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
            return;
        }

        var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        AssertTree(fixture.Fs, prepared.SourceRoot, fixture.Target);
        var offsets = fixture.Requests.Where(request => new Uri(request.Url).Segments.Last() == filename)
            .Select(request => request.Offset);
        Assert.That(offsets, Is.EqualTo(cache == "partial" ? new long[] { partialLength } : Array.Empty<long>()));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Skip the packs belonging only to an unselected, independently pinned baseline.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task PreparationDownloadsOnlyPacksForTheSelectedBaseline()
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\preparation-exact-copy-fixture";
        string stockRoot = At(fs, root, "stock");
        string alternateRoot = At(fs, root, "alternate");
        string targetRoot = At(fs, root, "target");
        string artifact = At(fs, root, "artifact");
        var files = new Dictionary<string, byte[]>
        {
            [Ksp2Install.KSP2_EXE_NAME] = Encoding.UTF8.GetBytes("fixture original executable"),
        };
        var stock = PutManifest(fs, stockRoot, "selected", files);
        var alternate = PutManifest(fs, alternateRoot, "alternate", new Dictionary<string, byte[]>
        {
            [Ksp2Install.KSP2_EXE_NAME] = Encoding.UTF8.GetBytes("different fixture executable"),
        });
        var target = PutManifest(fs, targetRoot, RELEASE_ID, files);
        var pipeline = new ReleaseRecipePipeline(fs, _ => { });
        var publication = pipeline.PublishHosted(targetRoot, target,
            [new ReleaseRecipeSourceTree(stockRoot, stock, []), new ReleaseRecipeSourceTree(alternateRoot, alternate, [])],
            artifact);
        var patch = pipeline.CreateFeedEntry(artifact, publication, RELEASE_ID, CHANNEL, VERSION,
            "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
            [new FullSourceBaselineDefinition(stock.ToJson(), []), new FullSourceBaselineDefinition(alternate.ToJson(), [])],
            false, DateTime.UtcNow);
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = CHANNEL, GeneratedAt = DateTime.UtcNow, Patches = [patch] };
        var fixture = new Fixture(fs, root, stockRoot, stock, target, artifact, publication,
            suppliedFeed: feed, suppliedPatch: patch);

        var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);

        Assert.That(publication.StoredPackBytes, Is.Positive);
        Assert.That(fixture.Requests, Is.EqualTo(new[] { ("https://fixture.test/build/release.json", 0L) }));
        AssertTree(fixture.Fs, prepared.SourceRoot, target);
    }

    /// <summary>
    /// Apply a normal update from an authenticated generation or repair through the stock recipe.
    /// </summary>
    /// <param name="scenario">The supported update, unsupported generation, damaged source, or explicit repair path.</param>
    /// <param name="separate">True for an independently downloaded update artifact.</param>
    /// <returns>The verification task.</returns>
    [TestCase("update", false)]
    [TestCase("unsupported", false)]
    [TestCase("damaged", false)]
    [TestCase("repair", false)]
    [TestCase("update", true)]
    [TestCase("unsupported", true)]
    [TestCase("damaged", true)]
    [TestCase("repair", true)]
    public async Task InstallPlanSelectsVerifiedReduxSourceOrStock(string scenario, bool separate)
    {
        Fixture fixture = Create();
        var service = Services(fixture).GetRequiredService<IInstallPlanService>();
        await service.ApplyToFolder(new InstallPlan
        {
            FullSourceReleaseId = RELEASE_ID,
            FullSourceChannel = CHANNEL,
            FullSourceVersion = VERSION,
        }, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        string backupPath = At(fixture.Fs, fixture.Install, "uninstall.zip");
        string backupHash = Digest(fixture.Fs.File.ReadAllBytes(backupPath));
        string previousRoot = At(fixture.Fs, fixture.Root, "frozen-target");
        string updateRoot = At(fixture.Fs, fixture.Root, "frozen-update");
        string artifact = At(fixture.Fs, fixture.Root, "update-artifact");
        var updateFiles = fixture.Target.Files.ToDictionary(file => file.Path,
            file => fixture.Fs.File.ReadAllBytes(At(fixture.Fs, previousRoot, file.Path)), StringComparer.Ordinal);
        updateFiles["Redux/delta.txt"] = Encoding.UTF8.GetBytes("small release delta");
        var target = PutManifest(fixture.Fs, updateRoot, "fixture-release-four", updateFiles);
        var prior = scenario == "unsupported"
            ? new StockBaselineManifest("other-prior-release", fixture.Target.Provenance, fixture.Target.Files, fixture.Target.Directories)
            : fixture.Target;
        var pipeline = new ReleaseRecipePipeline(fixture.Fs, _ => { });
        var stockSource = new ReleaseRecipeSourceTree(fixture.StockRoot, fixture.Stock, []);
        var updateSource = new ReleaseRecipeSourceTree(previousRoot, prior, []);
        var publication = pipeline.PublishHosted(updateRoot, target, separate ? [stockSource] : [updateSource, stockSource], artifact);
        string priorDigest = Digest(prior.ToJson());
        var patch = pipeline.CreateFeedEntry(artifact, publication, "fixture-release-four", CHANNEL, "2.0.0.1.202",
            "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
            [new FullSourceBaselineDefinition(fixture.Stock.ToJson(), [])], false, DateTime.UtcNow, separate ? null : [priorDigest]);
        var manifest = pipeline.Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
        int[] updatePacks;
        int[] stockPacks = ReleaseRecipePipeline.GetRequiredPackIndices(manifest, fixture.Stock);
        string updatePrefix = "";
        string updateArtifactRoot = artifact;
        if (separate)
        {
            string separateRoot = At(fixture.Fs, fixture.Root, "separate-update-artifact");
            updateArtifactRoot = separateRoot;
            var update = pipeline.PublishHosted(updateRoot, target, [updateSource], separateRoot);
            var updateManifest = pipeline.Inspect(separateRoot, update.ArtifactSha256, update.TargetManifestSha256);
            long markerSize = fixture.Fs.FileInfo.New(At(fixture.Fs, separateRoot, "release.json")).Length;
            updatePrefix = $"update-{priorDigest.ToLowerInvariant()}-";
            var updateArtifact = new FullSourceUpdateArtifact(priorDigest, update.ArtifactSha256, markerSize,
                markerSize + update.StoredPackBytes, $"https://fixture.test/build/{updatePrefix}release.json",
                $"https://fixture-backup.test/build/{updatePrefix}release.json");
            patch = patch with { FullSource = patch.FullSource! with { UpdateArtifacts = [updateArtifact] } };
            updatePacks = ReleaseRecipePipeline.GetRequiredPackIndices(updateManifest, prior);
            Assert.That(updateArtifact.DownloadBytes, Is.LessThan(patch.FullSource.DownloadBytes));
            fixture.ResponseOverride = (url, offset, _) =>
            {
                string name = new Uri(url).Segments.Last();
                return name.StartsWith(updatePrefix, StringComparison.Ordinal)
                    ? fixture.Response(url, offset, separateRoot, name[updatePrefix.Length..])
                    : fixture.Response(url, offset);
            };
        }
        else
        {
            updatePacks = ReleaseRecipePipeline.GetRequiredPackIndices(manifest, prior);
            Assert.That(updatePacks.Length, Is.LessThan(stockPacks.Length));
        }
        fixture.Patch = patch;
        fixture.Artifact = artifact;
        fixture.Requests.Clear();
        string stages = At(fixture.Fs, fixture.Storage, "full-source-preparation-v1/stages");
        var beforeStages = fixture.Fs.Directory.EnumerateDirectories(stages).ToHashSet(StringComparer.Ordinal);
        if (scenario is "damaged" or "repair")
            Put(fixture.Fs, fixture.Install, BUNDLE, Encoding.UTF8.GetBytes("damaged managed bytes"));

        var plan = new InstallPlan
        {
            FullSourceReleaseId = "fixture-release-four",
            FullSourceChannel = CHANNEL,
            FullSourceVersion = "2.0.0.1.202",
            IsFullSourceRepair = scenario == "repair",
        };
        var progress = new List<(long Bytes, long Total)>();
        void RecordProgress(long bytes, long total) => progress.Add((bytes, total));
        if (scenario == "damaged")
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await service.ApplyToFolder(plan, fixture.Install, _ => { }, RecordProgress, (_, _) => { }, CancellationToken.None));
            Assert.That(failure!.Message, Does.Contain("Repair the installation before updating."));
            Assert.That(fixture.Requests.Select(request => new Uri(request.Url).Segments.Last()),
                Is.EqualTo(new[] { updatePrefix + "release.json" }));
            Assert.That(progress.Last().Bytes, Is.EqualTo(fixture.Fs.FileInfo.New(At(fixture.Fs, updateArtifactRoot, "release.json")).Length));
            Assert.That(fixture.Fs.Directory.EnumerateDirectories(stages), Is.EquivalentTo(beforeStages));
            Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BUNDLE)), Is.EqualTo("damaged managed bytes"));
            Assert.That(Digest(fixture.Fs.File.ReadAllBytes(backupPath)), Is.EqualTo(backupHash));
            Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")),
                Is.EqualTo("unknown user bytes retained"));
            return;
        }
        await service.ApplyToFolder(plan, fixture.Install, _ => { }, RecordProgress, (_, _) => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, target);

        int[] expectedPacks = scenario == "update" ? updatePacks : stockPacks;
        if (scenario is "repair" or "unsupported")
        {
            string[] changedFiles = scenario == "repair" ? [BUNDLE, "Redux/delta.txt"] : ["Redux/delta.txt"];
            var retained = target.Files.Select(file => file.Path).Except(changedFiles, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            expectedPacks = ReleaseRecipePipeline.GetRequiredPackIndices(manifest, fixture.Stock, retained);
        }
        string prefix = scenario == "update" ? updatePrefix : "";
        var requests = expectedPacks.Select(index => $"{prefix}pack-{index:D6}.bin").Append(prefix + "release.json");
        Assert.That(fixture.Requests.Select(request => new Uri(request.Url).Segments.Last()),
            Is.EquivalentTo(requests));
        string selectedArtifact = scenario == "update" ? updateArtifactRoot : artifact;
        long expectedBytes = expectedPacks.Select(index => $"pack-{index:D6}.bin").Append("release.json")
            .Sum(name => fixture.Fs.FileInfo.New(At(fixture.Fs, selectedArtifact, name)).Length);
        Assert.That(progress.Select(report => report.Bytes), Is.Ordered);
        Assert.That(progress.All(report => report.Bytes <= report.Total), Is.True);
        Assert.That(progress.Last(), Is.EqualTo((expectedBytes, expectedBytes)));
        string stage = fixture.Fs.Directory.EnumerateDirectories(stages).Single(path => !beforeStages.Contains(path));
        if (scenario == "update")
        {
            Assert.That(fixture.Fs.Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories),
                Is.EqualTo(new[] { At(fixture.Fs, stage, "Redux/delta.txt") }));
            var coordinator = fixture.Coordinator();
            var published = coordinator.GetPublishedIdentity(fixture.Entry.Id)!;
            var stale = new FullSourcePreparedTarget(published, target, stage, false, fixture.Config.Feeds[0].SnapshotAuthority())
            {
                InstalledBaseline = new FullSourceInstalledBaseline(fixture.Entry.Id, prior),
            };
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await coordinator.ApplyAsync(fixture.Entry.Id, stale, null, false, _ => { }, CancellationToken.None));
            Assert.That(error!.Message, Does.StartWith("The installed generation changed after update preparation."));
        }
        else if (scenario == "repair")
        {
            Assert.That(fixture.Fs.Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories), Is.EquivalentTo(
                new[] { At(fixture.Fs, stage, BUNDLE), At(fixture.Fs, stage, "Redux/delta.txt") }));
        }
        else if (scenario == "unsupported")
        {
            Assert.That(fixture.Fs.Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories),
                Is.EqualTo(new[] { At(fixture.Fs, stage, "Redux/delta.txt") }));
            var coordinator = fixture.Coordinator();
            var published = coordinator.GetPublishedIdentity(fixture.Entry.Id)!;
            var stale = new FullSourcePreparedTarget(published, target, stage, false, fixture.Config.Feeds[0].SnapshotAuthority())
            {
                TargetRetention = new FullSourceTargetRetention(fixture.Entry.Id,
                    fixture.Target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal), Digest(fixture.Target.ToJson())),
            };
            var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await coordinator.ApplyAsync(fixture.Entry.Id, stale, null, false, _ => { }, CancellationToken.None));
            Assert.That(failure!.Message, Does.StartWith("Prepared target retention does not belong"));
        }
        else
        {
            AssertTree(fixture.Fs, stage, target);
        }
        Assert.That(Digest(fixture.Fs.File.ReadAllBytes(backupPath)), Is.EqualTo(backupHash));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")),
            Is.EqualTo("unknown user bytes retained"));
    }

    /// <summary>
    /// A Windows repair requests no pack whose literals are used only by exact installed target files.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task RepairPreparation_DoesNotDownloadPackForRetainedFile()
    {
        const string retainedPath = "A-retained.bin";
        Fixture fixture = Create();
        await PutCanonicalBackup(fixture);
        byte[] retained = new byte[9 * 1024 * 1024];
        new Random(4186).NextBytes(retained);
        string targetRoot = At(fixture.Fs, fixture.Root, "frozen-target");
        var files = fixture.Target.Files.ToDictionary(file => file.Path,
            file => fixture.Fs.File.ReadAllBytes(At(fixture.Fs, targetRoot, file.Path)), StringComparer.Ordinal);
        files.Add(retainedPath, retained);
        var target = PutManifest(fixture.Fs, targetRoot, RELEASE_ID, files);
        Put(fixture.Fs, fixture.Install, retainedPath, retained);
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, BUNDLE));
        string artifact = At(fixture.Fs, fixture.Root, "repair-pack-artifact");
        var pipeline = new ReleaseRecipePipeline(fixture.Fs, _ => { });
        var publication = pipeline.Publish(targetRoot, target, [new ReleaseRecipeSourceTree(fixture.StockRoot, fixture.Stock, [])], artifact);
        fixture.Artifact = artifact;
        fixture.Patch = pipeline.CreateFeedEntry(artifact, publication, RELEASE_ID, CHANNEL, VERSION,
            "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
            [new FullSourceBaselineDefinition(fixture.Stock.ToJson(), [])], false, DateTime.UtcNow);
        fixture.ResponseOverride = (url, offset, _) => new Uri(url).Segments.Last() == "pack-000000.bin"
            ? throw new InvalidOperationException("Only retained content uses this pack.")
            : fixture.Response(url, offset);

        var prepared = await fixture.Preparation.FetchRepairTargetAsync(RELEASE_ID, CHANNEL, null, fixture.Entry.Id, CancellationToken.None);
        await fixture.Preparation.VerifyTargetAsync(prepared, CancellationToken.None);
        Assert.That(prepared.TargetRetention!.Paths, Does.Contain(retainedPath));
        Assert.That(fixture.Fs.Directory.EnumerateFiles(prepared.SourceRoot, "*", SearchOption.AllDirectories), Is.EquivalentTo(
            new[] { At(fixture.Fs, prepared.SourceRoot, BUNDLE), At(fixture.Fs, prepared.SourceRoot, WEM) }));
        Assert.That(fixture.Requests.Select(request => new Uri(request.Url).Segments.Last()),
            Is.EquivalentTo(new[] { "release.json", "pack-000001.bin" }));
    }

    /// <summary>
    /// Repair rejects a retained file that changes between preparation and acquiring the installation lock.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task RepairPreparation_RejectsChangedRetentionBeforeMutation()
    {
        Fixture fixture = Create();
        var coordinator = fixture.Coordinator();
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        var stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, target, CancellationToken.None);
        await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        string backup = BackupHash(fixture);
        byte[] damagedBundle = Encoding.UTF8.GetBytes("initial damage");
        Put(fixture.Fs, fixture.Install, BUNDLE, damagedBundle);
        var repair = await fixture.Preparation.FetchRepairTargetAsync(RELEASE_ID, CHANNEL, null, fixture.Entry.Id, CancellationToken.None);
        Assert.That(repair.TargetRetention!.Paths, Does.Contain(Ksp2Install.KSP2_EXE_NAME));
        Put(fixture.Fs, fixture.Install, Ksp2Install.KSP2_EXE_NAME, Encoding.UTF8.GetBytes("later damage"));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ApplyAsync(fixture.Entry.Id, repair, null, true, _ => { }, CancellationToken.None));
        Assert.That(failure!.Message, Does.StartWith("An installed file selected for reuse changed"));
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, BUNDLE)), Is.EqualTo(damagedBundle));
        Assert.That(BackupHash(fixture), Is.EqualTo(backup));
        Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(target.Identity));
    }

    /// <summary>
    /// The existing downloader resumes a partial authority-bound artifact without treating it as authenticated metadata.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task PartialArtifact_ResumesAndVerifiesBeforeUse()
    {
        Fixture fixture = Create();
        byte[] bytes = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Artifact, "release.json"));
        Put(fixture.Fs, fixture.Storage, $"full-source-preparation-v1/downloads/patches/{fixture.Patch.ChecksumSha256}.patch.partial", bytes[..100]);
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        Assert.That(fixture.Requests, Does.Contain(("https://fixture.test/build/release.json", 100L)));
        AssertTree(fixture.Fs, target.SourceRoot, fixture.Target);
    }

    /// <summary>
    /// Cancellation preserves the live stock tree and a subsequent attempt reconstructs normally.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CancelledPreparation_CanRetryWithoutLiveMutation()
    {
        Fixture fixture = Create();
        using var cancellation = new CancellationTokenSource();
        fixture.ResponseOverride = (url, _, _) =>
        {
            byte[] bytes = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Artifact, new Uri(url).Segments.Last()));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new InterruptedDownload(bytes, cancellation))
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, cancellation.Token));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        string partial = At(fixture.Fs, fixture.Storage, $"full-source-preparation-v1/downloads/patches/{fixture.Patch.ChecksumSha256}.patch.partial");
        Assert.That(fixture.Fs.FileInfo.New(partial).Length, Is.EqualTo(100));
        fixture.ResponseOverride = null;
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        AssertTree(fixture.Fs, target.SourceRoot, fixture.Target);
        Assert.That(fixture.Requests, Does.Contain(("https://fixture.test/build/release.json", 100L)));
    }

    /// <summary>
    /// A committed transaction resumes from validated local evidence after the authority goes offline.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task RestartRecovery_DoesNotFetchAuthority()
    {
        Fixture fixture = Create();
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        var stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        var coordinator = fixture.Coordinator((name, after) =>
        {
            if (name == "engine-apply" && after)
                throw new IOException("Injected process loss after engine commit.");
        });
        await Assert.ThrowsAsync<IOException>(async () =>
            await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
        fixture.Offline = true;
        fixture.Transport.Invocations.Clear();
        FullSourceInstallationStatus status = fixture.Coordinator().Reconcile(fixture.Entry.Id)!;
        Assert.That(status.Release, Is.EqualTo(target.Identity));
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        fixture.Transport.Verify(service => service.GetTrustedManifestAsync(It.IsAny<FeedInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Publisher-curated stock definitions reject arbitrary observed bytes and retain partial legacy backups.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task ChangedOriginalAndPartialBackup_AreNotPromotedToPristineStock()
    {
        Fixture fixture = Create();
        Put(fixture.Fs, fixture.Install, BUNDLE, Encoding.UTF8.GetBytes("arbitrary installation bytes"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        byte[] partial = Encoding.UTF8.GetBytes("legacy partial archive fixture");
        Put(fixture.Fs, fixture.Install, "uninstall.zip", partial);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, "uninstall.zip")), Is.EqualTo(partial));
    }

    /// <summary>
    /// Stage using verified stock inputs without treating the projected files as a complete stock backup.
    /// </summary>
    /// <param name="splitInputs">Require verified inputs from two configured installations.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task StockRecipeUsesOnlyVerifiedInputsBeforeBackupPreparation(bool splitInputs)
    {
        Fixture input = Create();
        var fs = input.Fs;
        const string boot = "KSP2_x64_Data/boot.config";
        const string unused = "KSP2_x64_Data/StreamingAssets/stock-only.bin";
        byte[] unusedBytes = new byte[2 * 1024 * 1024];
        Put(fs, input.StockRoot, unused, unusedBytes);
        var stock = new StockBaselineManifest(input.Stock.BaselineIdentity, input.Stock.Provenance,
            input.Stock.Files.Append(new StockBaselineFile(unused, unusedBytes.Length, Digest(unusedBytes))), input.Stock.Directories);
        string root = At(fs, input.Root, "projected-local-stock");
        fs.Directory.CreateDirectory(root);
        string artifact = At(fs, root, "artifact");
        var pipeline = new ReleaseRecipePipeline(fs, _ => { });
        var publication = pipeline.PublishHosted(At(fs, input.Root, "frozen-target"), input.Target,
            [new ReleaseRecipeSourceTree(input.StockRoot, stock, [])], artifact);
        var recipe = pipeline.Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
        var required = ReleaseRecipePipeline.GetRecipeInputContract(recipe, stock);
        Assert.That(required.Files.Select(file => file.Path), Is.EquivalentTo(new[] { Ksp2Install.KSP2_EXE_NAME, boot }));

        var fixture = new Fixture(fs, root, input.StockRoot, stock, input.Target, artifact, publication);
        fs.File.Delete(At(fs, fixture.Install, unused));
        if (splitInputs)
        {
            fs.File.Delete(At(fs, fixture.Install, boot));
            string other = At(fs, root, "other-install");
            Put(fs, other, boot, fs.File.ReadAllBytes(At(fs, input.StockRoot, boot)));
            fixture.Config.Ksp2Installs.Add(new Ksp2InstallEntry { ExePath = At(fs, other, Ksp2Install.KSP2_EXE_NAME), Name = "Second fixture" });
        }
        string cache = At(fs, fixture.Storage, "full-source-preparation-v1/baselines/" + Digest(stock.ToJson()));
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(cache)).Returns(512L * 1024 * 1024 + required.TotalBytes);

        var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        AssertTree(fs, prepared.SourceRoot, input.Target);
        if (splitInputs)
        {
            Assert.That(fs.Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories),
                Is.EquivalentTo(required.Files.Select(file => At(fs, cache, file.Path))));
        }
        else
        {
            Assert.That(fs.Directory.Exists(cache), Is.False);
        }
        Assert.That(fs.File.Exists(At(fs, fixture.Install, unused)), Is.False);
        Assert.That(fs.File.Exists(At(fs, fixture.Install, "uninstall.zip")), Is.False);
        Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, prepared, CancellationToken.None));
        fixture.Steam.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Admit a resumed recipe when verified cache files leave room for the remaining input.
    /// </summary>
    /// <param name="archiveSource">Use a verified stock archive for the missing input.</param>
    /// <param name="corruptCache">Use a same-size cached file with a different hash.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task StockRecipeResumeBudgetsOnlyUnverifiedCacheInputs(bool archiveSource, bool corruptCache)
    {
        Fixture fixture = Create();
        var fs = fixture.Fs;
        const string boot = "KSP2_x64_Data/boot.config";
        const string executable = Ksp2Install.KSP2_EXE_NAME;
        string artifact = At(fs, fixture.Root, "cache-resume-artifact");
        var pipeline = new ReleaseRecipePipeline(fs, _ => { });
        var publication = pipeline.PublishHosted(At(fs, fixture.Root, "frozen-target"), fixture.Target,
            [new ReleaseRecipeSourceTree(fixture.StockRoot, fixture.Stock, [])], artifact);
        fixture.Artifact = artifact;
        fixture.Patch = pipeline.CreateFeedEntry(artifact, publication, RELEASE_ID, CHANNEL, VERSION,
            "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
            [new FullSourceBaselineDefinition(fixture.Stock.ToJson(), [])], false, DateTime.UtcNow);
        var recipe = pipeline.Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
        var required = ReleaseRecipePipeline.GetRecipeInputContract(recipe, fixture.Stock);
        Assert.That(required.Files.Select(file => file.Path), Is.EquivalentTo(new[] { executable, boot }));

        string cache = At(fs, fixture.Storage, "full-source-preparation-v1/baselines/" + Digest(fixture.Stock.ToJson()));
        byte[] cachedBytes = fs.File.ReadAllBytes(At(fs, fixture.StockRoot, executable));
        if (corruptCache)
            cachedBytes[0] ^= 0xff;
        Put(fs, cache, executable, cachedBytes);
        if (archiveSource)
            await PutCanonicalBackup(fixture);
        string? archiveHash = archiveSource ? BackupHash(fixture) : null;
        fs.File.Delete(At(fs, fixture.Install, executable));
        byte[] bootBytes = fs.File.ReadAllBytes(At(fs, fixture.StockRoot, boot));
        if (archiveSource)
            fs.File.Delete(At(fs, fixture.Install, boot));
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(cache)).Returns(512L * 1024 * 1024 + bootBytes.LongLength);

        if (corruptCache)
        {
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
            Assert.That(error!.Message, Does.Contain($"Required {required.TotalBytes} bytes"));
            Assert.That(fs.File.Exists(At(fs, cache, boot)), Is.False);
        }
        else
        {
            var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
            AssertTree(fs, prepared.SourceRoot, fixture.Target);
            Assert.That(fs.File.ReadAllBytes(At(fs, cache, boot)), Is.EqualTo(bootBytes));
        }
        Assert.That(fs.File.ReadAllBytes(At(fs, cache, executable)), Is.EqualTo(cachedBytes));
        Assert.That(fs.File.Exists(At(fs, fixture.Install, executable)), Is.False);
        if (archiveSource)
            Assert.That(BackupHash(fixture), Is.EqualTo(archiveHash));
        fixture.Steam.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Resume a complete stock backup source when the installation retains a user-owned replacement.
    /// </summary>
    /// <param name="corruptCache">True if a same-size cache file has the wrong contents.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CompleteStockResumeBudgetsOnlyUnverifiedCacheFiles(bool corruptCache)
    {
        Fixture fixture = Create();
        var fs = fixture.Fs;
        fixture.Entry.FullSourceRetainedUnownedFiles.Add(BUNDLE);
        byte[] userBundle = Encoding.UTF8.GetBytes("user-owned replacement");
        Put(fs, fixture.Install, BUNDLE, userBundle);

        string cache = At(fs, fixture.Storage, "full-source-preparation-v1/baselines/" + Digest(fixture.Stock.ToJson()));
        foreach (var file in fixture.Stock.Files.Where(file => file.Path != WEM))
        {
            byte[] bytes = fs.File.ReadAllBytes(At(fs, fixture.StockRoot, file.Path));
            if (corruptCache && file.Path == BUNDLE)
                bytes[0] ^= 0xff;
            Put(fs, cache, file.Path, bytes);
        }
        long missingBytes = fixture.Stock.Files.Single(file => file.Path == WEM).Length;
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(cache))
            .Returns(512L * 1024 * 1024 + missingBytes);

        if (corruptCache)
        {
            long corruptBytes = fixture.Stock.Files.Single(file => file.Path == BUNDLE).Length;
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
            Assert.That(error!.Message, Does.Contain($"Required {missingBytes + corruptBytes} bytes"));
            Assert.That(fs.File.Exists(At(fs, cache, WEM)), Is.False);
        }
        else
        {
            var prepared = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
            Assert.That(prepared.SourceRoot, Is.EqualTo(cache));
            AssertTree(fs, cache, fixture.Stock);
            await fixture.Preparation.VerifyStockAsync(prepared, CancellationToken.None);
        }
        Assert.That(fs.File.ReadAllBytes(At(fs, fixture.Install, BUNDLE)), Is.EqualTo(userBundle));
        fixture.Steam.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Explicit pinned depot acquisition fills missing source bytes and still verifies the complete publisher contract.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task MissingBaseline_UsesExactSteamSelectorsAndVerifiesReturnedBytes()
    {
        Fixture fixture = Create();
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, BUNDLE));
        var baseline = fixture.Patch.FullSource!.Baselines[0] with { SteamDepots = [new FullSourceSteamDepot(954851, 123456)] };
        fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource with { Baselines = [baseline] } };
        fixture.Steam.Setup(service => service.DownloadDepotsAsync(954850, It.IsAny<IReadOnlyList<SteamDepot>>(),
                It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((uint _, IReadOnlyList<SteamDepot> depots, string root, IProgress<SteamDownloadProgress> _, CancellationToken _) =>
            {
                Assert.That(depots, Is.EqualTo(new[] { new SteamDepot(954851, 123456, 0) }));
                CopyTree(fixture.Fs, fixture.StockRoot, root, fixture.Stock);
                return Task.CompletedTask;
            });
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        AssertTree(fixture.Fs, target.SourceRoot, fixture.Target);
        fixture.Steam.VerifyAll();
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, BUNDLE)), Is.False);
    }

    /// <summary>
    /// A storefront response with bytes outside its complete independent baseline contract cannot stage a release.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task WrongStorefrontBytes_AreRejected()
    {
        Fixture fixture = Create();
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, BUNDLE));
        var baseline = fixture.Patch.FullSource!.Baselines[0] with { SteamDepots = [new FullSourceSteamDepot(954851, 123456)] };
        fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource with { Baselines = [baseline] } };
        fixture.Steam.Setup(service => service.DownloadDepotsAsync(It.IsAny<uint>(), It.IsAny<IReadOnlyList<SteamDepot>>(),
                It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((uint _, IReadOnlyList<SteamDepot> _, string root, IProgress<SteamDownloadProgress> _, CancellationToken _) =>
            {
                Put(fixture.Fs, root, BUNDLE, [1, 2, 3]);
                return Task.CompletedTask;
            });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
    }

    /// <summary>
    /// Stock ownership prepared for one configured installation cannot initialize another installation's catalog.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StockPreparationFromAnotherInstall_IsRejectedBeforeCatalogPublication()
    {
        Fixture fixture = Create();
        string otherRoot = At(fixture.Fs, fixture.Root, "other-game");
        CopyTree(fixture.Fs, fixture.StockRoot, otherRoot, fixture.Stock);
        var other = new Ksp2InstallEntry { ExePath = At(fixture.Fs, otherRoot, Ksp2Install.KSP2_EXE_NAME), Name = "Other fixture" };
        fixture.Config.Ksp2Installs.Add(other);
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        var original = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        var coordinator = fixture.Coordinator();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ApplyAsync(other.Id, target, original, false, _ => { }, CancellationToken.None));
        Assert.That(coordinator.HasCatalog(otherRoot), Is.False);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, otherRoot, "uninstall.zip")), Is.False);
        AssertTree(fixture.Fs, otherRoot, fixture.Stock);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }
}
