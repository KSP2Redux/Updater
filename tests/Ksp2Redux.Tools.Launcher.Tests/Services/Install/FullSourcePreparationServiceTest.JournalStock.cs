using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Wrappers;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    private const string JOURNAL_BUNDLE = BundleConversionLayout.BUNDLE_ROOT + "/stock.bundle";

    private static string JournalPath(Fixture fixture) => new BundleConversionLayout(fixture.Fs, fixture.Install).JournalPath;

    private static ConversionJournal PutJournal(Fixture fixture, bool deferred = true)
    {
        byte[] original = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.StockRoot, JOURNAL_BUNDLE));
        byte[] current = (byte[])original.Clone();
        byte[] converted = original[8..16].Select(value => (byte)(value ^ 255)).ToArray();
        converted.CopyTo(current, 8);
        var journal = new ConversionJournal
        {
            ShaderManifestSha256 = new string('a', 64),
            InPlace = [new JournalInPlaceFile
            {
                File = "stock.bundle", Size = original.Length,
                Edits = [new JournalEdit { Offset = 8, Old = Convert.ToHexString(original[8..16]), New = Convert.ToHexString(converted) }]
            }]
        };
        if (deferred)
            journal.InPlace[0].Edits.Add(new JournalEdit { Offset = 120, Old = Convert.ToHexString(original[120..128]), New = new string('f', 16) });
        Put(fixture.Fs, fixture.Install, JOURNAL_BUNDLE, current);
        new JournalStore(fixture.Fs, JournalPath(fixture)).Write(journal);
        return journal;
    }

    private static Fixture CreateJournalFixture(bool native = false)
    {
        System.IO.Abstractions.IFileSystem fs = native ? new RealFileSystem() :
            new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        // The native transaction also needs room for its sibling files beside the deepest game files.
        string root = native ? Directory.CreateTempSubdirectory("redux-journal-").FullName : @"C:\journal-stock-fixture";
        string stockRoot = At(fs, root, "stock-input");
        string targetRoot = At(fs, root, "target-input");
        var files = new Dictionary<string, byte[]>
        {
            [Ksp2Install.KSP2_EXE_NAME] = Encoding.UTF8.GetBytes("fixture original executable"),
            ["KSP2_x64_Data/boot.config"] = Encoding.UTF8.GetBytes("stock=exact\r\n"),
            [JOURNAL_BUNDLE] = Enumerable.Range(0, 512).Select(index => (byte)(index * 13)).ToArray(),
            [WEM] = Encoding.UTF8.GetBytes("fixture original media")
        };
        StockBaselineManifest stock = PutManifest(fs, stockRoot, "journal-original-stock", files);
        var targetFiles = files.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone());
        targetFiles[JOURNAL_BUNDLE][400] ^= 63;
        StockBaselineManifest target = PutManifest(fs, targetRoot, RELEASE_ID, targetFiles);
        string artifact = At(fs, root, "artifact");
        var pipeline = new ReleaseRecipePipeline(fs, native ? null : _ => { });
        ReleaseRecipePublication publication = pipeline.Publish(targetRoot, target,
            [new ReleaseRecipeSourceTree(stockRoot, stock, stock.Files.Select(file =>
                new ReleaseRecipeSource(file.Path, BundleRecipeSourceKind.RawFile)).ToArray())], artifact);
        var fixture = new Fixture(fs, root, stockRoot, stock, target, artifact, publication);
        PutLegacyBackup(fixture);
        PutJournal(fixture);
        return fixture;
    }

    /// <summary>
    /// Applied and deferred journal ranges supply complete verified stock without modifying current inputs.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_PatchedOriginalAndPartialZipReachProductionCoordinator()
    {
        Fixture fixture = CreateJournalFixture();
        await ExerciseJournalRecovery(fixture, false);
    }

    /// <summary>
    /// Untrusted journal declarations and uncovered byte changes remain precise stock-coverage gaps.
    /// </summary>
    /// <param name="fault">The invalid declaration or current-source change.</param>
    /// <returns>The verification task.</returns>
    [TestCase("size"), TestCase("bounds"), TestCase("overflow"), TestCase("hex"), TestCase("odd-hex")]
    [TestCase("length"), TestCase("empty"), TestCase("duplicate"), TestCase("overlap"), TestCase("duplicate-file")]
    [TestCase("foreign"), TestCase("traversal"), TestCase("absolute"), TestCase("scope"), TestCase("game")]
    [TestCase("wrong-old"), TestCase("uncovered"), TestCase("current-size")]
    [TestCase("version"), TestCase("rewritten-only"), TestCase("missing"), TestCase("missing-current")]
    public async Task JournalStock_UntrustedOrIncompleteInputsAreRejected(string fault)
    {
        Fixture fixture = CreateJournalFixture();
        ConversionJournal journal = new JournalStore(fixture.Fs, JournalPath(fixture)).Read()!;
        JournalInPlaceFile file = journal.InPlace[0];
        JournalEdit edit = file.Edits[0];
        string active = At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE);
        byte[] bytes = fixture.Fs.File.ReadAllBytes(active);
        switch (fault)
        {
            case "size": file.Size++; break;
            case "bounds": edit.Offset = file.Size - 1; break;
            case "overflow": edit.Offset = long.MaxValue; break;
            case "hex": edit.Old = new string('z', edit.Old.Length); break;
            case "odd-hex": edit.Old = edit.New = "123"; break;
            case "length": edit.New += "00"; break;
            case "empty": edit.Old = edit.New = ""; break;
            case "duplicate": file.Edits.Add(new JournalEdit { Offset = edit.Offset, Old = edit.Old, New = edit.New }); break;
            case "overlap": file.Edits[1].Offset = 10; break;
            case "duplicate-file": journal.InPlace.Add(file); break;
            case "foreign": file.File = "foreign.bundle"; break;
            case "traversal": file.File = "../stock.bundle"; break;
            case "absolute": file.File = @"C:\outside.bundle"; break;
            case "scope": journal.BundleRoot = "KSP2_x64_Data/StreamingAssets/aa"; break;
            case "game": journal.GameDirectory = @"C:\foreign-game"; break;
            case "wrong-old": edit.Old = new string('0', edit.Old.Length); break;
            case "uncovered": bytes[300] ^= 17; break;
            case "current-size": bytes = bytes[..^1]; break;
            case "version": journal.Version++; break;
            case "rewritten-only":
                journal.InPlace.Clear();
                journal.Rewritten.Add(new JournalRewrittenFile
                {
                    File = file.File, StockSize = file.Size, StockSha256 = fixture.Stock.Files.Single(value => value.Path == JOURNAL_BUNDLE).Sha256,
                    Output = BundleConversionLayout.RelativeOutput(file.File)
                });
                break;
        }
        Put(fixture.Fs, fixture.Install, JOURNAL_BUNDLE, bytes);
        new JournalStore(fixture.Fs, JournalPath(fixture)).Write(journal);
        if (fault == "missing")
            fixture.Fs.File.Delete(JournalPath(fixture));
        if (fault == "missing-current")
            fixture.Fs.File.Delete(active);
        string originalZip = BackupHash(fixture);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(failure!.Message, Does.Contain(JOURNAL_BUNDLE).And.Contain("journal"));
        if (fault is "wrong-old" or "uncovered")
            Assert.That(failure.Message, Does.Contain("SHA-256"), "Complete trusted file hashing must reject plausible but incorrect reconstructions.");
        Assert.That(BackupHash(fixture), Is.EqualTo(originalZip));
        if (fault != "missing-current")
            Assert.That(fixture.Fs.File.ReadAllBytes(active), Is.EqualTo(bytes));
        fixture.Steam.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Ambiguous JSON, missing payload fields and excessive metadata never become accepted edit declarations.
    /// </summary>
    /// <param name="fault">The malformed produced-journal variation.</param>
    /// <returns>The verification task.</returns>
    [TestCase("duplicate-property"), TestCase("missing-offset"), TestCase("null-edit"), TestCase("unknown-property"), TestCase("budget")]
    public async Task JournalStock_MalformedMetadataIsRejectedBeforeUse(string fault)
    {
        Fixture fixture = CreateJournalFixture();
        string json = fixture.Fs.File.ReadAllText(JournalPath(fixture));
        json = fault switch
        {
            "duplicate-property" => json.Replace("\"Version\": 1", "\"Version\": 1, \"version\": 1"),
            "missing-offset" => json.Replace("\"Offset\": 8,", ""),
            "null-edit" => json.Replace("\"InPlace\": [", "\"InPlace\": [null,"),
            "unknown-property" => json.Replace("\"Version\": 1", "\"Unknown\": 1, \"Version\": 1"),
            _ => new string(' ', 16 * 1024 * 1024 + 1)
        };
        fixture.Fs.File.WriteAllText(JournalPath(fixture), json);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(failure!.Message, Does.Contain("journal"));
    }

    /// <summary>
    /// Both preparation and coordinator reject journal or source changes even if reconstructed stock would still match.
    /// </summary>
    /// <param name="changeJournal">True to change only journal whitespace, false to restore an applied range early.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    public async Task JournalStock_PreparedSourceChangesCannotCrossCoordinator(bool changeJournal)
    {
        Fixture fixture = CreateJournalFixture();
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        Assert.That(stock.RecoverySources!.JournalRanges, Has.Some.Contains("converted"));
        Assert.That(stock.RecoverySources.JournalRanges, Has.Some.Contains("original-or-deferred"));
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        if (changeJournal)
            fixture.Fs.File.AppendAllText(JournalPath(fixture), " ");
        else
            fixture.Fs.File.Copy(At(fixture.Fs, fixture.StockRoot, JOURNAL_BUNDLE), At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE), true);
        string originalZip = BackupHash(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.VerifyStockAsync(stock, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Coordinator()
            .ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
        Assert.That(BackupHash(fixture), Is.EqualTo(originalZip));
    }

    /// <summary>
    /// ZIP and exact-current sources retain preference and do not need an unrelated malformed journal.
    /// </summary>
    /// <param name="canonical">True for a complete canonical ZIP, false for a partial ZIP plus untouched originals.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    public async Task JournalStock_ExistingSourcePreferenceIsPreserved(bool canonical)
    {
        Fixture fixture = CreateJournalFixture();
        fixture.Fs.File.WriteAllText(JournalPath(fixture), "malformed unused journal");
        if (canonical)
        {
            fixture.Fs.File.Delete(BackupPath(fixture));
            await PutCanonicalBackup(fixture);
        }
        else
            fixture.Fs.File.Copy(At(fixture.Fs, fixture.StockRoot, JOURNAL_BUNDLE), At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE), true);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        Assert.That(stock.RecoverySources!.Journal, Is.Null);
        Assert.That(stock.RecoverySources.CanonicalArchive, Is.EqualTo(canonical));
    }

    /// <summary>
    /// A journal-dependent preparation cannot discard its saved observations at the authority boundary.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_MissingSavedSourcePinsAreRejected()
    {
        Fixture fixture = CreateJournalFixture();
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.VerifyStockAsync(
            stock with { RecoverySources = null }, CancellationToken.None));
    }

    /// <summary>
    /// Older non-journal preparations remain compatible with omitted optional catalog observations.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_LegacyNonJournalPreparationRemainsCompatible()
    {
        Fixture fixture = CreateJournalFixture();
        fixture.Fs.File.Copy(At(fixture.Fs, fixture.StockRoot, JOURNAL_BUNDLE), At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE), true);
        fixture.Fs.File.WriteAllText(JournalPath(fixture), "malformed unused journal");
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock with { RecoverySources = null }, false, _ => { }, CancellationToken.None);
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// A changed covered range remains explicit provenance while complete trusted byte verification decides recovery.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_ChangedCoveredRangeRequiresCompleteTrustedStockProof()
    {
        Fixture fixture = CreateJournalFixture();
        string active = At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE);
        byte[] current = fixture.Fs.File.ReadAllBytes(active);
        current[8] ^= 17;
        fixture.Fs.File.WriteAllBytes(active, current);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        Assert.That(stock.RecoverySources!.JournalRanges, Has.Some.Contains("changed"));
        Assert.That(stock.RecoverySources.CurrentFiles.Single(file => file.Path == JOURNAL_BUNDLE).Sha256, Is.EqualTo(Digest(current)));
        await ExerciseJournalRecovery(fixture, false);
    }

    /// <summary>
    /// The coordinator independently rejects changed source pins even when an injected provider does no checking.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_CoordinatorIndependentlyRechecksJournalPin()
    {
        Fixture fixture = CreateJournalFixture();
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        fixture.Fs.File.AppendAllText(JournalPath(fixture), " ");
        var provider = new Mock<IFullSourcePreparationService>();
        provider.Setup(service => service.VerifyStockAsync(It.IsAny<FullSourceStockPreparation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        provider.Setup(service => service.VerifyTargetAsync(It.IsAny<FullSourcePreparedTarget>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var coordinator = new FullSourceInstallationService(fixture.Fs, fixture.Configuration.Object,
            new ZipFileService(fixture.Fs), fixture.Space.Object, provider.Object, fixture.Os.Object,
            Mock.Of<ILogService>(), Mock.Of<IGameProcessService>(), new InstallFileLockService(fixture.Fs, fixture.Os.Object),
            engine: new InstallTransactionEngine(fixture.Fs, fixture.Space.Object.GetAvailableFreeSpace, validateFileStreams: _ => { }));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
    }

    /// <summary>
    /// Bound cache journals may contain unwritten ranges and overlays remain exact across small read boundaries.
    /// </summary>
    /// <param name="applied">True to keep one applied range, false for journal-before-write stock bytes.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    public async Task JournalStock_BoundDeferredRangesStreamAcrossReadBoundaries(bool applied)
    {
        Fixture fixture = CreateJournalFixture();
        var store = new JournalStore(fixture.Fs, JournalPath(fixture));
        ConversionJournal journal = store.Read()!;
        journal.GameDirectory = fixture.Install;
        store.Write(journal);
        if (!applied)
            fixture.Fs.File.Copy(At(fixture.Fs, fixture.StockRoot, JOURNAL_BUNDLE), At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE), true);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        Assert.That(stock.RecoverySources!.Journal is not null, Is.EqualTo(applied));
        var archive = new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs), new StockArchiveLimits { BufferBytes = 7 });
        await archive.ReadAvailableSourcesAsync(BackupPath(fixture), fixture.Install, fixture.Stock,
            async (file, input, token) =>
            {
                using var result = new MemoryStream();
                var buffer = new byte[3];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                    await result.WriteAsync(buffer.AsMemory(0, read), token);
                Assert.That(Digest(result.ToArray()), Is.EqualTo(file.Sha256));
            }, sources: stock.RecoverySources);
    }

    /// <summary>
    /// Reparse points in either journal or bundle paths cannot authorize reconstruction reads.
    /// </summary>
    /// <param name="journal">True to replace the journal path, false to replace the bundle path.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    public async Task JournalStock_LinkedInputsAreRejected(bool journal)
    {
        Fixture fixture = CreateJournalFixture();
        string path = journal ? JournalPath(fixture) : At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE);
        string linked = At(fixture.Fs, fixture.Root, "outside-selected-source.bin");
        fixture.Fs.File.Move(path, linked);
        fixture.Fs.File.CreateSymbolicLink(path, linked);
        byte[] before = fixture.Fs.File.ReadAllBytes(linked);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(fixture.Fs.File.ReadAllBytes(linked), Is.EqualTo(before));
    }

    /// <summary>
    /// Cancellation during canonical writing discards only its incomplete temporary and permits source reuse.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_CancelledWriterReusesExactReadOnlySources()
    {
        Fixture fixture = CreateJournalFixture();
        string originalZip = BackupHash(fixture);
        byte[] current = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE));
        byte[] journal = fixture.Fs.File.ReadAllBytes(JournalPath(fixture));
        var zip = new ZipFileService(fixture.Fs);
        var factory = new Mock<IZipFileService>();
        using var cancellation = new CancellationTokenSource();
        factory.Setup(service => service.NewArchive(It.IsAny<Stream>(), It.IsAny<ZipArchiveMode>(), true))
            .Returns((Stream stream, ZipArchiveMode mode, bool leaveOpen) =>
            {
                IZipArchive archive = zip.NewArchive(stream, mode, leaveOpen);
                if (mode != ZipArchiveMode.Create)
                    return archive;
                var intercepted = new Mock<IZipArchive>();
                intercepted.Setup(service => service.CreateEntry(It.IsAny<string>(), It.IsAny<CompressionLevel>()))
                    .Returns((string name, CompressionLevel level) =>
                    {
                        IZipArchiveEntry entry = archive.CreateEntry(name, level);
                        if (name == JOURNAL_BUNDLE)
                            cancellation.Cancel();
                        return entry;
                    });
                intercepted.Setup(service => service.Dispose()).Callback(archive.Dispose);
                return intercepted.Object;
            });
        var service = new StockArchive(fixture.Fs, factory.Object, availableSpace: _ => long.MaxValue);
        StockArchiveSources sources = await service.InspectAvailableSourcesAsync(BackupPath(fixture), fixture.Install, fixture.Stock);
        string candidate = At(fixture.Fs, fixture.Root, "cancelled-candidate.zip");
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.CreateRecoveredAsync(BackupPath(fixture),
            fixture.Install, candidate, fixture.Stock, cancellation.Token, sources));
        Assert.That(fixture.Fs.File.Exists(candidate), Is.False);
        Assert.That(fixture.Fs.Directory.EnumerateFiles(fixture.Root, ".stock-archive-*.tmp"), Is.Empty);
        Assert.That(BackupHash(fixture), Is.EqualTo(originalZip));
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE)), Is.EqualTo(current));
        Assert.That(fixture.Fs.File.ReadAllBytes(JournalPath(fixture)), Is.EqualTo(journal));
        var retry = new StockArchive(fixture.Fs, zip, availableSpace: _ => long.MaxValue);
        await retry.CreateRecoveredAsync(BackupPath(fixture), fixture.Install, candidate, fixture.Stock, sources: sources);
        await retry.VerifyAsync(candidate, fixture.Stock);
    }

    /// <summary>
    /// Cancellation before candidate construction retains all journal inputs and permits an ordinary restart.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task JournalStock_CancelledCandidateCanRestartFromRetainedSources()
    {
        Fixture fixture = CreateJournalFixture();
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        string originalZip = BackupHash(fixture);
        byte[] current = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await fixture.Coordinator((name, after) =>
        {
            if (name == "archive-recovery-create" && !after)
                cancellation.Cancel();
        }).ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, cancellation.Token));
        Assert.That(BackupHash(fixture), Is.EqualTo(originalZip));
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE)), Is.EqualTo(current));
        fixture.Coordinator().Reconcile(fixture.Entry.Id);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, null, false, _ => { }, CancellationToken.None);
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(BackupPath(fixture), fixture.Stock);
    }

    /// <summary>
    /// Real Windows sources stay leased through consumption and support exact backup recovery and restart.
    /// </summary>
    /// <param name="interrupt">True to restart after complete candidate creation.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    [Platform("Win")]
    public async Task JournalStock_NativeWindowsSourcesStayReadOnlyThroughRecovery(bool interrupt)
    {
        Fixture fixture = CreateJournalFixture(true);
        bool checkedLease = false;
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).ReadAvailableSourcesAsync(BackupPath(fixture), fixture.Install,
            fixture.Stock, async (file, source, token) =>
            {
                if (file.Path == JOURNAL_BUNDLE)
                {
                    Assert.That(source.CanWrite, Is.False);
                    Assert.Throws<IOException>(() => fixture.Fs.File.OpenWrite(JournalPath(fixture)).Dispose());
                    Assert.Throws<IOException>(() => fixture.Fs.File.OpenWrite(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE)).Dispose());
                    checkedLease = true;
                }
                using var output = new MemoryStream();
                await source.CopyToAsync(output, token);
                Assert.That(Digest(output.ToArray()), Is.EqualTo(file.Sha256));
            });
        Assert.That(checkedLease, Is.True);
        await ExerciseJournalRecovery(fixture, interrupt);
    }

    private static async Task ExerciseJournalRecovery(Fixture fixture, bool interrupt)
    {
        string originalZip = BackupHash(fixture);
        byte[] current = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE));
        byte[] journal = fixture.Fs.File.ReadAllBytes(JournalPath(fixture));
        if (fixture.Fs is RealFileSystem)
        {
            Put(fixture.Fs, fixture.Root, "journal-input.json", journal);
            Put(fixture.Fs, fixture.Root, "initial-current.bundle", current);
            fixture.Fs.File.WriteAllBytes(At(fixture.Fs, fixture.Root, "stock-manifest.json"), fixture.Stock.ToJson());
        }
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE)), Is.EqualTo(current));
        Assert.That(fixture.Fs.File.ReadAllBytes(JournalPath(fixture)), Is.EqualTo(journal));
        Assert.That(BackupHash(fixture), Is.EqualTo(originalZip));
        bool checkedCandidate = false;
        var coordinator = fixture.Coordinator((name, after) =>
        {
            if (name != "archive-recovery-create" || !after)
                return;
            checkedCandidate = true;
            Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, JOURNAL_BUNDLE)), Is.EqualTo(current));
            Assert.That(fixture.Fs.File.ReadAllBytes(JournalPath(fixture)), Is.EqualTo(journal));
            Assert.That(BackupHash(fixture), Is.EqualTo(originalZip));
            if (interrupt)
                throw new IOException("Journal fixture interrupted with complete candidate retained.");
        });
        if (interrupt)
        {
            await Assert.ThrowsAsync<IOException>(async () => await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
            fixture.Coordinator().Reconcile(fixture.Entry.Id);
            await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, null, false, _ => { }, CancellationToken.None);
        }
        else
            await coordinator.ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        Assert.That(checkedCandidate, Is.True);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(BackupPath(fixture), fixture.Stock);
        fixture.Fs.File.Delete(JournalPath(fixture));
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
        string retained = fixture.Fs.Directory.EnumerateFiles(fixture.Root, "legacy-*.zip", SearchOption.AllDirectories).Single();
        using (Stream input = fixture.Fs.File.OpenRead(retained))
            Assert.That(Convert.ToHexString(SHA256.HashData(input)), Is.EqualTo(originalZip));
        if (fixture.Fs is RealFileSystem)
        {
            string report = At(fixture.Fs, fixture.Root, "journal-stock-result.json");
            fixture.Fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
            {
                fixture.Root, fixture.Install, retained, originalZip, interrupt,
                sourceObservations = stock.RecoverySources,
                journalInputHash = Digest(journal), initialCurrentHash = Digest(current),
                stockHash = Digest(fixture.Stock.ToJson()), completeBackupHash = BackupHash(fixture),
                currentBytesPreservedUntilCandidate = checkedCandidate, journalBytesPreservedUntilCandidate = true,
                stockRestoredWithoutJournal = true, unknownUserFilesPreserved = true, proprietaryDownloads = 0, fullGameCopies = 0
            }));
            TestContext.AddTestAttachment(report, "Tiny Windows journal recovery through actual preparation and coordinator.");
        }
        fixture.Steam.VerifyNoOtherCalls();
    }
}
