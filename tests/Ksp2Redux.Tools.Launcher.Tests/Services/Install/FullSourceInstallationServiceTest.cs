using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Production full-source catalog, generation operations and restart behavior on disposable roots.
/// </summary>
public partial class FullSourceInstallationServiceTest
{
    private const string BOOT = "KSP2_x64_Data/boot.config";
    private const string BUNDLE = "KSP2_x64_Data/StreamingAssets/aa/content.bundle";
    private const string RETAINED = "shared/retained.txt";
    private const string UNKNOWN = "shared/user-save.bin";
    private const string USER_BYTES = "literal unknown user bytes";
    private const string STOCK_BOOT = "gfx-enable-gfx-jobs=1\r\nhdr-display-enabled=0\r\nstock=exact\r\n";

    private record Fixture(IFileSystem Fs, string Root, string Install, string Storage,
        Ksp2InstallEntry Entry, ILauncherConfigService Configuration, Mock<IFullSourcePreparationService> Preparation,
        FullSourceStockPreparation Stock, FullSourcePreparedTarget Target2, FullSourcePreparedTarget Target3,
        IOperatingSystemService Os, IDiskSpaceService Space)
    {
        internal Mock<IGameProcessService> GameProcesses { get; } = new();

        internal FullSourceInstallationService Service(Action<string, bool>? fault = null,
            Action<string, bool>? engineFault = null) => new(Fs, Configuration, new ZipFileService(Fs), Space,
                Preparation.Object, Os, Mock.Of<ILogService>(), GameProcesses.Object, new InstallFileLockService(Fs, Os), fault,
                new InstallTransactionEngine(Fs, Space.GetAvailableFreeSpace, engineFault,
                    Fs is MockFileSystem ? _ => { } : null));

        internal string Head => Fs.Path.Combine(Storage, "full-source-v1", Entry.Id.ToString("N"), "installation.json");
        internal string Archive => Fs.Path.Combine(Install, "uninstall.zip");
    }

    private static Fixture Create(IFileSystem? fileSystem = null, string? root = null, bool bootBom = false)
    {
        IFileSystem fs = fileSystem ?? new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        root ??= @"C:\full-source-disposable";
        string install = fs.Path.Combine(root, "game");
        string storage = fs.Path.Combine(root, "launcher");
        string stockRoot = fs.Path.Combine(root, "stock-input");
        string source2 = fs.Path.Combine(root, "release-two");
        string source3 = fs.Path.Combine(root, "release-three");
        fs.Directory.CreateDirectory(storage);
        Dictionary<string, string> stockFiles = new()
        {
            [Ksp2Install.KSP2_EXE_NAME] = "original stock executable", [BOOT] = (bootBom ? "\uFEFF" : "") + STOCK_BOOT,
            [BUNDLE] = "original complete streaming asset", ["obsolete.bin"] = "stock obsolete bytes",
            ["retired/tree/owned.bin"] = "original owned directory content"
        };
        Dictionary<string, string> target2Files = new()
        {
            [Ksp2Install.KSP2_EXE_NAME] = "full source generation two", [BOOT] = (bootBom ? "\uFEFF" : "") + STOCK_BOOT.Replace("stock=exact", "release=two"),
            [BUNDLE] = "full source streaming generation two", ["added.bin"] = "generation two addition", [RETAINED] = "explicit retained bytes"
        };
        Dictionary<string, string> target3Files = new()
        {
            [Ksp2Install.KSP2_EXE_NAME] = "full source generation three", [BOOT] = (bootBom ? "\uFEFF" : "") + STOCK_BOOT.Replace("stock=exact", "release=three"),
            [BUNDLE] = "full source streaming generation three", ["added.bin"] = "generation three addition", [RETAINED] = "explicit retained bytes"
        };
        PutTree(fs, install, stockFiles);
        PutTree(fs, stockRoot, stockFiles);
        PutTree(fs, source2, target2Files);
        PutTree(fs, source3, target3Files);
        foreach (string tree in new[] { install, stockRoot, source2, source3 })
        {
            fs.Directory.CreateDirectory(fs.Path.Combine(tree, "empty"));
        }
        Put(fs, install, RETAINED, "explicit retained bytes");
        Put(fs, install, UNKNOWN, USER_BYTES);
        Put(fs, install, "retired/user.bin", USER_BYTES);
        var stock = Manifest("storefront/build-original", stockFiles);
        var release2 = Manifest("release-two", target2Files);
        var release3 = Manifest("release-three", target3Files);
        var target2 = new FullSourcePreparedTarget(new FullSourceReleaseIdentity("release-two", "beta", "2.0.0.1.200",
            Digest(release2.ToJson()), "test independently pinned authentication receipt two"), release2, source2, true);
        var target3 = new FullSourcePreparedTarget(new FullSourceReleaseIdentity("release-three", "internal", "3.0.0.1.300",
            Digest(release3.ToJson()), "test independently pinned authentication receipt three"), release3, source3, true);
        var preparation = new FullSourceStockPreparation(stock, stockRoot, stock, [RETAINED], ["shared"]);
        var entry = new Ksp2InstallEntry { ExePath = fs.Path.Combine(install, Ksp2Install.KSP2_EXE_NAME), Name = "Disposable", DisableGraphicsJobs = true };
        var config = new LauncherConfig(fs.Path.Combine(storage, "test-config.json")) { Ksp2Installs = [entry], ActiveKsp2InstallId = entry.Id };
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(config);
        configuration.Setup(service => service.GetLocalStorageDirectory()).Returns(storage);
        var verifier = new Mock<IFullSourcePreparationService>(MockBehavior.Strict);
        verifier.Setup(service => service.VerifyTargetAsync(It.IsAny<FullSourcePreparedTarget>(), It.IsAny<CancellationToken>()))
            .Returns((FullSourcePreparedTarget candidate, CancellationToken _) =>
                candidate == target2 || candidate == target3 ? Task.CompletedTask : Task.FromException(new InvalidDataException("Independent test authority rejects target.")));
        verifier.Setup(service => service.VerifyStockAsync(It.IsAny<FullSourceStockPreparation>(), It.IsAny<CancellationToken>()))
            .Returns((FullSourceStockPreparation candidate, CancellationToken _) =>
                candidate == preparation ? Task.CompletedTask : Task.FromException(new InvalidDataException("Independent test authority rejects stock.")));
        var os = new Mock<IOperatingSystemService>();
        os.Setup(service => service.IsWindows()).Returns(true);
        var space = new Mock<IDiskSpaceService>();
        space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        return new Fixture(fs, root, install, storage, entry, configuration.Object, verifier, preparation, target2, target3, os.Object, space.Object);
    }

    private static StockBaselineManifest Manifest(string identity, Dictionary<string, string> files) =>
        new(identity, "independently pinned disposable input contract", files.Select(file =>
            new StockBaselineFile(file.Key, Encoding.UTF8.GetByteCount(file.Value), Digest(Encoding.UTF8.GetBytes(file.Value)))), ["empty"]);

    private static void PutTree(IFileSystem fs, string root, Dictionary<string, string> files)
    {
        foreach (var file in files)
        {
            Put(fs, root, file.Key, file.Value);
        }
    }

    private static void Put(IFileSystem fs, string root, string path, string text)
    {
        string destination = At(fs, root, path);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(destination)!);
        fs.File.WriteAllBytes(destination, Encoding.UTF8.GetBytes(text));
    }

    private static string At(IFileSystem fs, string root, string path) => fs.Path.Combine(root, path.Replace('/', fs.Path.DirectorySeparatorChar));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static Task<FullSourceInstallationStatus> Apply(Fixture fixture, FullSourceInstallationService? service = null,
        FullSourcePreparedTarget? target = null, bool initial = true, bool repair = false, CancellationToken ct = default) =>
        (service ?? fixture.Service()).ApplyAsync(fixture.Entry.Id, target ?? fixture.Target2, initial ? fixture.Stock : null, repair, _ => { }, ct);

    private static void Preserved(Fixture fixture, byte[]? archive = null)
    {
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, UNKNOWN)), Is.EqualTo(USER_BYTES));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, RETAINED)), Is.EqualTo("explicit retained bytes"));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "retired/user.bin")), Is.EqualTo(USER_BYTES));
        if (archive is not null)
            Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Archive), Is.EqualTo(archive));
    }

    private static void Generation(Fixture fixture, FullSourceInstallationStatus status)
    {
        foreach (StockBaselineFile file in status.Generation.Files)
        {
            byte[] bytes = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, file.Path));
            Assert.That(bytes.LongLength, Is.EqualTo(file.Length), file.Path);
            Assert.That(Digest(bytes), Is.EqualTo(file.Sha256), file.Path);
        }
        Assert.That(status.Generation.Files.Any(file => file.Path is RETAINED or UNKNOWN), Is.False);
        Preserved(fixture);
    }

    /// <summary>
    /// Full stock backup, apply, update, channel change, downgrade and uninstall retain exact original and unknown bytes.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CompleteGenerationTransitions_RetainArchiveOwnershipAndExactStockBoot()
    {
        Fixture fixture = Create();
        FullSourceInstallationStatus installed = await Apply(fixture);
        Generation(fixture, installed);
        Assert.That(installed.Release, Is.EqualTo(fixture.Target2.Identity));
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(fixture.Archive, fixture.Stock.Stock);
        string boot = fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT));
        Assert.That(boot, Does.Contain("gfx-enable-gfx-jobs=0\r\n").And.Contain("hdr-display-enabled=0\r\n"));
        Assert.That(installed.Generation.Files.Single(file => file.Path == BOOT), Is.Not.EqualTo(fixture.Target2.Manifest.Files.Single(file => file.Path == BOOT)));
        FullSourceInstallationStatus updated = await Apply(fixture, target: fixture.Target3, initial: false);
        Generation(fixture, updated);
        Assert.That(updated.Release, Is.EqualTo(fixture.Target3.Identity));
        FullSourceInstallationStatus downgraded = await Apply(fixture, initial: false);
        Generation(fixture, downgraded);
        Assert.That(downgraded.Release, Is.EqualTo(fixture.Target2.Identity));
        FullSourceInstallationStatus stock = await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        Generation(fixture, stock);
        Assert.That(stock.Release, Is.Null);
        Assert.That(stock.Generation.ToJson(), Is.EqualTo(fixture.Stock.Stock.ToJson()));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, "added.bin")), Is.False);
        Preserved(fixture, archive);
        Assert.That(fixture.Fs.Directory.EnumerateDirectories(fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}"))
            .Count(path => path.EndsWith("-work", StringComparison.Ordinal)), Is.EqualTo(4));
    }

    private static IEnumerable<TestCaseData> RestartBoundaries()
    {
        Fixture fixture = Create();
        var boundaries = new List<(string Name, bool After)>();
        Task.Run(() => Apply(fixture, fixture.Service((name, after) => boundaries.Add((name, after)),
            (name, after) => boundaries.Add(($"engine:{name}", after))))).GetAwaiter().GetResult();
        for (int index = 0; index < boundaries.Count; index++)
        {
            yield return new TestCaseData(index, boundaries[index].Name, boundaries[index].After)
                .SetName($"FullSourceRestart_{index}_{boundaries[index].Name}_{boundaries[index].After}");
        }
    }

    /// <summary>
    /// Every catalog and engine boundary restarts with a deterministic generation and no duplicate mutation.
    /// </summary>
    /// <param name="boundaryIndex">The operation boundary to interrupt.</param>
    /// <param name="name">The observed operation name.</param>
    /// <param name="after">True for an interruption after completion.</param>
    /// <returns>The verification task.</returns>
    [TestCaseSource(nameof(RestartBoundaries))]
    public async Task EveryPublicationBoundary_RestartsAndCanRetry(int boundaryIndex, string name, bool after)
    {
        Fixture fixture = Create();
        int count = -1;
        void Fail(string _, bool __)
        {
            if (++count == boundaryIndex)
                throw new IOException($"Simulated process loss at {name} {after}");
        }
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service(Fail, (operation, completed) => Fail($"engine:{operation}", completed))));
        byte[]? archive = fixture.Fs.File.Exists(fixture.Archive) ? fixture.Fs.File.ReadAllBytes(fixture.Archive) : null;
        FullSourceInstallationStatus? recovered = fixture.Service().Reconcile(fixture.Entry.Id);
        if (recovered is not null)
            Generation(fixture, recovered);
        Preserved(fixture, archive);
        int mutations = 0;
        FullSourceInstallationStatus? repeated = fixture.Service(engineFault: (_, _) => mutations++).Reconcile(fixture.Entry.Id);
        Assert.That(mutations, Is.Zero, "Terminal reconciliation must never apply another generation.");
        Assert.That(repeated?.Generation.ToJson(), Is.EqualTo(recovered?.Generation.ToJson()));
        FullSourceInstallationStatus retried = await Apply(fixture, initial: recovered?.Release is null);
        Generation(fixture, retried);
        Preserved(fixture, archive);
    }

    /// <summary>
    /// Ordinary updates reject drift, while explicit repair observes only known owned paths and handles missing files.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task ExplicitRepair_HandlesChangedAndMissingOwnedFilesWithoutAdoptingUnknownContent()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Put(fixture.Fs, fixture.Install, BUNDLE, "changed owned bytes requiring undo");
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, "added.bin"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture, initial: false));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BUNDLE)), Is.EqualTo("changed owned bytes requiring undo"));
        FullSourceInstallationStatus repaired = await Apply(fixture, initial: false, repair: true);
        Generation(fixture, repaired);
        Preserved(fixture, archive);
    }

    /// <summary>
    /// A repair rollback restores its observed damaged bytes and missing paths without publishing a new identity.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task InterruptedRepair_RestoresObservedDamageAndPreservesPriorIdentity()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        Put(fixture.Fs, fixture.Install, BUNDLE, "observed damaged bytes");
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, "added.bin"));
        bool interrupted = false;
        var service = fixture.Service(engineFault: (name, after) =>
        {
            if (!interrupted && name.StartsWith("install-", StringComparison.Ordinal) && after)
            {
                interrupted = true;
                throw new IOException("Repair process loss after mutation");
            }
        });
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, service, fixture.Target3, false, true));
        FullSourceInstallationStatus status = fixture.Service().Reconcile(fixture.Entry.Id)!;
        Assert.That(status.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        Assert.That(status.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BUNDLE)), Is.EqualTo("observed damaged bytes"));
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, "added.bin")), Is.False);
        Preserved(fixture);
        Dictionary<string, string> damaged = LiveUseSnapshot(fixture);
        Exception? launchFailure = Assert.Catch<Exception>(() => fixture.Service().AcquireLaunchLease(fixture.Install));
        Assert.That(launchFailure, Is.InstanceOf<IOException>().Or.TypeOf<InvalidDataException>());
        Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
        LiveUseUnchanged(fixture, damaged);
        fixture.GameProcesses.Verify(process => process.Start(It.IsAny<System.Diagnostics.ProcessStartInfo>()), Times.Never);
        Generation(fixture, await Apply(fixture, initial: false, repair: true));
    }

    /// <summary>
    /// Existing partial legacy snapshots and independently mismatched input contracts remain byte-for-byte intact.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task PartialArchiveAndInputMismatch_RefuseBeforeInstallationMutation()
    {
        Fixture fixture = Create();
        fixture.Fs.File.WriteAllText(fixture.Archive, "legacy partial archive evidence");
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture));
        Assert.That(fixture.Fs.File.ReadAllText(fixture.Archive), Is.EqualTo("legacy partial archive evidence"));
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.False);
        fixture.Fs.File.Delete(fixture.Archive);
        Put(fixture.Fs, fixture.Target2.SourceRoot, BUNDLE, "bad prepared source bytes");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture));
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.False);
        Generation(fixture, new FullSourceInstallationStatus(fixture.Entry.Id, fixture.Stock.Stock, null, null, null));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Corrupt catalog metadata and an internally rehashed root redirection cannot authorize path mutation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CorruptCatalogAndRehashedRedirect_RetainEvidenceAndRejectAuthority()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] original = fixture.Fs.File.ReadAllBytes(fixture.Head);
        fixture.Fs.File.WriteAllText(fixture.Head, "corrupt catalog bytes");
        Assert.Throws<InvalidDataException>(() => fixture.Service().Reconcile(fixture.Entry.Id));
        Assert.That(fixture.Fs.File.ReadAllText(fixture.Head), Is.EqualTo("corrupt catalog bytes"));
        JsonNode envelope = JsonNode.Parse(original)!;
        JsonNode payload = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!;
        payload["InstallRoot"] = fixture.Target3.SourceRoot;
        byte[] changed = JsonSerializer.SerializeToUtf8Bytes(payload);
        envelope["Payload"] = Convert.ToBase64String(changed);
        envelope["Sha256"] = Digest(changed);
        byte[] redirect = JsonSerializer.SerializeToUtf8Bytes(envelope);
        fixture.Fs.File.WriteAllBytes(fixture.Head, redirect);
        Assert.Throws<InvalidDataException>(() => fixture.Service().Reconcile(fixture.Entry.Id));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Head), Is.EqualTo(redirect));
        fixture.Fs.File.WriteAllBytes(fixture.Head, original);
        Generation(fixture, fixture.Service().Reconcile(fixture.Entry.Id)!);
    }

    /// <summary>
    /// A committed engine result is published after marker cleanup even when the immutable archive later fails.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CommitWithoutMarkerAndUnhealthyArchive_PublishesCorrectGenerationAndReportsBackupSeparately()
    {
        Fixture fixture = Create();
        var service = fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && after)
                throw new IOException("Crash after engine committed and cleared marker");
        });
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, service));
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(fixture.Install, ".redux-transaction.active")), Is.False);
        fixture.Fs.File.WriteAllText(fixture.Archive, "unhealthy archive retained bytes");
        FullSourceInstallationStatus recovered = fixture.Service().Reconcile(fixture.Entry.Id)!;
        Assert.That(recovered.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.That(recovered.TransactionResult, Is.EqualTo(InstallTransactionResult.Committed));
        Assert.That(recovered.ArchiveFailure, Is.Not.Null);
        Generation(fixture, recovered);
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, initial: false));
        Assert.That(fixture.Fs.File.ReadAllText(fixture.Archive), Is.EqualTo("unhealthy archive retained bytes"));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Canonical rehashed malformed catalog contracts fail explicitly without losing any bytes or authorizing launch.
    /// </summary>
    /// <param name="property">The required contract or policy to corrupt.</param>
    /// <returns>The verification task.</returns>
    [TestCase("Stock")]
    [TestCase("Ownership")]
    [TestCase("RetainedFiles")]
    [TestCase("UnownedDirectories")]
    public async Task MissingCatalogContract_RetainsBytesAndBlocksRecovery(string property)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        JsonNode envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(fixture.Head))!;
        JsonNode payload = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!;
        payload[property] = null;
        byte[] changed = JsonSerializer.SerializeToUtf8Bytes(payload);
        envelope["Payload"] = Convert.ToBase64String(changed);
        envelope["Sha256"] = Digest(changed);
        byte[] corrupted = JsonSerializer.SerializeToUtf8Bytes(envelope);
        fixture.Fs.File.WriteAllBytes(fixture.Head, corrupted);
        Assert.Throws<InvalidDataException>(() => fixture.Service().Reconcile(fixture.Entry.Id));
        var guard = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), fixture.Service());
        Assert.Throws<InvalidDataException>(() => guard.EnsureLaunchAllowed(fixture.Install));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Head), Is.EqualTo(corrupted));
        Preserved(fixture);
    }

    /// <summary>
    /// A UTF-8 signature cannot hide the first setting or duplicate it, and uninstall restores the original signature bytes.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BootUtf8Signature_DerivesOneSettingAndRestoresExactOriginalBytes()
    {
        Fixture fixture = Create(bootBom: true);
        await Apply(fixture);
        string boot = fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT));
        string[] lines = boot.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Count(line => line.StartsWith("gfx-enable-gfx-jobs=", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines.Single(line => line.StartsWith("gfx-enable-gfx-jobs=", StringComparison.Ordinal)), Is.EqualTo("gfx-enable-gfx-jobs=0"));
        await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(Encoding.UTF8.GetBytes("\uFEFF" + STOCK_BOOT)));
    }
}
