using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    private static string BackupPath(Fixture fixture) => At(fixture.Fs, fixture.Install, "uninstall.zip");

    private static async Task PutCanonicalBackup(Fixture fixture)
    {
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs), availableSpace: _ => long.MaxValue)
            .CreateAsync(fixture.StockRoot, BackupPath(fixture), fixture.Stock);
    }

    private static void PutLegacyBackup(Fixture fixture)
    {
        using var output = fixture.Fs.FileStream.New(BackupPath(fixture), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipFileService(fixture.Fs).NewArchive(output, ZipArchiveMode.Create, true);
        foreach (StockBaselineFile file in fixture.Stock.Files.Where(file => !file.Path.Contains("/StreamingAssets/", StringComparison.Ordinal)))
        {
            // CacheService uses native Windows entry separators and does not write empty directories.
            using Stream entry = archive.CreateEntry(file.Path.Replace('/', '\\')).Open();
            using var source = fixture.Fs.File.OpenRead(At(fixture.Fs, fixture.StockRoot, file.Path));
            source.CopyTo(entry);
        }
        using Stream unknown = archive.CreateEntry("legacy-user/settings.txt").Open();
        unknown.Write(Encoding.UTF8.GetBytes("preserve unknown original ZIP entry"));
    }

    private static string BackupHash(Fixture fixture)
    {
        using var input = fixture.Fs.File.OpenRead(BackupPath(fixture));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    /// <summary>
    /// Legacy migration stages only changes to the verified owned files.
    /// </summary>
    /// <param name="exact">Whether the installed ownership already matches every published target file.</param>
    /// <returns>The verification task.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task BackupAdoption_LegacyMigrationStagesOnlyChangedOwnedFiles(bool exact)
    {
        Fixture fixture = Create();
        await PutCanonicalBackup(fixture);
        string backup = BackupHash(fixture);
        string targetRoot = At(fixture.Fs, fixture.Root, "frozen-target");
        var legacy = exact ? fixture.Target : new StockBaselineManifest("verified-legacy", fixture.Target.Provenance,
            fixture.Target.Files.Select(file => file.Path == BUNDLE ? fixture.Stock.Files.Single(stock => stock.Path == BUNDLE) : file),
            fixture.Target.Directories);
        foreach (var file in legacy.Files)
        {
            string root = !exact && file.Path == BUNDLE ? fixture.StockRoot : targetRoot;
            Put(fixture.Fs, fixture.Install, file.Path, fixture.Fs.File.ReadAllBytes(At(fixture.Fs, root, file.Path)));
        }
        var descriptor = fixture.Patch.FullSource!;
        fixture.Patch = fixture.Patch with { FullSource = descriptor with
        {
            Baselines = [descriptor.Baselines[0] with { LegacyOwnershipManifests = [legacy.ToJson()] }]
        } };
        if (exact)
            fixture.ResponseOverride = (_, _, _) => throw new InvalidOperationException("Adoption must not request target data.");

        var (target, stock) = await fixture.Preparation.FetchLegacyMigrationAsync(RELEASE_ID, CHANNEL, null, fixture.Entry.Id,
            CancellationToken.None);
        Assert.That(target.TargetRetention?.LegacyOwnershipSha256, Is.EqualTo(Digest(legacy.ToJson())));
        string[] staged = fixture.Fs.Directory.EnumerateFiles(target.SourceRoot, "*", SearchOption.AllDirectories).ToArray();
        Assert.That(staged, Is.EquivalentTo(exact ? Array.Empty<string>() : new[] { At(fixture.Fs, target.SourceRoot, BUNDLE) }));
        Assert.That(fixture.Requests.Count, exact ? Is.Zero : Is.GreaterThan(0));
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        Assert.That(BackupHash(fixture), Is.EqualTo(backup));
    }

    /// <summary>
    /// Archive-backed preparation writes only recipe inputs and verifies even unused original files.
    /// </summary>
    /// <param name="damageArchive">Whether the unreferenced stock media is corrupt.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task BackupAdoption_RecipeProjectionRetainsFullArchiveVerification(bool damageArchive)
    {
        Fixture fixture = Create();
        string targetRoot = At(fixture.Fs, fixture.Root, "frozen-target");
        string artifact = At(fixture.Fs, fixture.Root, "projected-stock-artifact");
        var pipeline = new ReleaseRecipePipeline(fixture.Fs, _ => { });
        var source = new ReleaseRecipeSourceTree(fixture.StockRoot, fixture.Stock,
            [new ReleaseRecipeSource(BUNDLE, BundleRecipeSourceKind.RawFile)]);
        var publication = pipeline.PublishHosted(targetRoot, fixture.Target, [source], artifact);
        fixture.Artifact = artifact;
        fixture.Patch = pipeline.CreateFeedEntry(artifact, publication, RELEASE_ID, CHANNEL, VERSION,
            "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
            [new FullSourceBaselineDefinition(fixture.Stock.ToJson(), [])], false, DateTime.UtcNow);
        await PutCanonicalBackup(fixture);
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, BUNDLE));
        string baseline = At(fixture.Fs, fixture.Storage, "full-source-preparation-v1/baselines/" + Digest(fixture.Stock.ToJson()));
        if (damageArchive)
        {
            using (var input = fixture.Fs.File.Open(BackupPath(fixture), FileMode.Open, FileAccess.ReadWrite))
            using (var archive = new ZipFileService(fixture.Fs).NewArchive(input, ZipArchiveMode.Update, true))
            using (Stream entry = archive.Entries.Single(entry => entry.FullName == WEM).Open())
                entry.WriteByte(0);
            string corruptHash = BackupHash(fixture);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None));
            Assert.That(BackupHash(fixture), Is.EqualTo(corruptHash));
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, baseline, BUNDLE)), Is.False);
            return;
        }

        string originalHash = BackupHash(fixture);
        long unusedBytes = fixture.Stock.Files.Single(file => file.Path == WEM).Length;
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(baseline))
            .Returns(512L * 1024 * 1024 + fixture.Stock.TotalBytes - unusedBytes);
        var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        AssertTree(fixture.Fs, prepared.SourceRoot, fixture.Target);
        Assert.That(fixture.Fs.Directory.EnumerateFiles(baseline, "*", SearchOption.AllDirectories), Is.EquivalentTo(
            new[] { BUNDLE, "KSP2_x64_Data/boot.config", Ksp2Install.KSP2_EXE_NAME }.Select(path => At(fixture.Fs, baseline, path))));
        Assert.That(BackupHash(fixture), Is.EqualTo(originalHash));
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, BUNDLE)), Is.False);
    }

    /// <summary>
    /// A complete canonical backup supplies initial stock without a separate baseline source tree.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_CompleteArchiveSuppliesCoordinatorWithoutBaselineDirectory()
    {
        Fixture fixture = Create();
        await PutCanonicalBackup(fixture);
        string originalHash = BackupHash(fixture);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        string baseline = At(fixture.Fs, fixture.Storage, "full-source-preparation-v1/baselines/" + Digest(fixture.Stock.ToJson()));
        Assert.That(fixture.Fs.Directory.Exists(baseline), Is.False);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, target, CancellationToken.None);
        Assert.That(stock.SourceRoot, Is.Empty);
        Assert.That(fixture.Fs.Directory.Exists(baseline), Is.False);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        Assert.That(BackupHash(fixture), Is.EqualTo(originalHash));
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(BackupHash(fixture), Is.EqualTo(originalHash));
    }

    /// <summary>
    /// Legacy partial content and matching current streaming bytes produce a complete independently verified backup.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_PartialLegacyAndExactCurrentFilesRecoverCompleteStock()
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        string originalHash = BackupHash(fixture);
        Put(fixture.Fs, fixture.Install, Ksp2Install.KSP2_EXE_NAME, Encoding.UTF8.GetBytes("known stock-owned path with legacy Redux bytes"));
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        Assert.That(stock.SourceRoot, Is.Empty);
        Assert.That(BackupHash(fixture), Is.EqualTo(originalHash), "Preparation must preserve the original archive.");
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(BackupPath(fixture), fixture.Stock);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        string parent = fixture.Fs.Path.GetDirectoryName(fixture.Install)!;
        string[] preserved = fixture.Fs.Directory.EnumerateFiles(parent, "legacy-*.zip", SearchOption.AllDirectories).ToArray();
        Assert.That(preserved, Has.Length.EqualTo(1));
        using (var input = fixture.Fs.File.OpenRead(preserved[0]))
            Assert.That(Convert.ToHexString(SHA256.HashData(input)), Is.EqualTo(originalHash));
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
    }

    /// <summary>
    /// Changed streaming bytes absent from the legacy snapshot report the exact unrecoverable stock file.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_MissingOriginalStreamingBytesReportGapAndPreserveInputs()
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        string originalHash = BackupHash(fixture);
        byte[] changed = Encoding.UTF8.GetBytes("changed media with no original backup");
        Put(fixture.Fs, fixture.Install, WEM, changed);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(failure!.Message, Does.Contain(WEM));
        Assert.That(BackupHash(fixture), Is.EqualTo(originalHash));
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, WEM)), Is.EqualTo(changed));
        Assert.That(fixture.Fs.Directory.Exists(At(fixture.Fs, fixture.Storage, "full-source-v1")), Is.False);
        fixture.Steam.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Added legacy files acquire ownership only from an independently authenticated published installed-file contract.
    /// </summary>
    /// <param name="declareOwnership">True to publish the known legacy installed-file contract.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task BackupAdoption_AddedLegacyFilesRequirePublishedOwnership(bool declareOwnership)
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        const string ADDED = "Redux/legacy-installed.dll";
        byte[] bytes = Encoding.UTF8.GetBytes("exact independently recorded legacy addition");
        Put(fixture.Fs, fixture.Install, ADDED, bytes);
        Put(fixture.Fs, fixture.Install, "Redux/user.txt", Encoding.UTF8.GetBytes("unowned user data"));
        if (declareOwnership)
        {
            var known = new StockBaselineManifest("known-legacy-build", "publisher supplied installed legacy build record",
                fixture.Stock.Files.Append(new StockBaselineFile(ADDED, bytes.Length, Digest(bytes))), fixture.Stock.Directories);
            var baseline = fixture.Patch.FullSource!.Baselines[0] with { LegacyOwnershipManifests = [known.ToJson()] };
            fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource with { Baselines = [baseline] } };
        }
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        Assert.That(stock.CurrentOwned.Files.Any(file => file.Path == ADDED), Is.EqualTo(declareOwnership));
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, ADDED)), Is.EqualTo(!declareOwnership));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "Redux/user.txt")), Is.EqualTo("unowned user data"));
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, ADDED)), Is.EqualTo(!declareOwnership));
    }

    /// <summary>
    /// Every archive promotion edge restarts from pinned authority without losing original or user bytes.
    /// </summary>
    /// <param name="boundary">The interrupted archive operation.</param>
    /// <param name="after">True for interruption after completion.</param>
    /// <returns>The verification task.</returns>
    [TestCase("archive-recovery-create", false)]
    [TestCase("archive-recovery-create", true)]
    [TestCase("archive-recovery-ready:publish", false)]
    [TestCase("archive-recovery-ready:publish", true)]
    [TestCase("archive-legacy-preserve", false)]
    [TestCase("archive-legacy-preserve", true)]
    [TestCase("archive-recovery-promote", false)]
    [TestCase("archive-recovery-promote", true)]
    [TestCase("engine-apply", false)]
    [TestCase("engine-apply", true)]
    public async Task BackupAdoption_PromotionRestartsPreserveOriginalAndInstalledGeneration(string boundary, bool after)
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        string originalHash = BackupHash(fixture);
        Put(fixture.Fs, fixture.Install, Ksp2Install.KSP2_EXE_NAME, Encoding.UTF8.GetBytes("legacy current executable bytes"));
        var priorVersion = new GameVersion { VersionNumber = new Version(1, 2, 3, 4), BuildNumber = "17", Channel = "beta" };
        fixture.Entry.LastInstalledVersion = priorVersion;
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        bool interrupted = false;
        await Assert.ThrowsAsync<IOException>(async () => await fixture.Coordinator((name, complete) =>
        {
            if (!interrupted && name == boundary && complete == after)
            {
                interrupted = true;
                throw new IOException("Simulated archive adoption process loss.");
            }
        }).ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
        Assert.That(interrupted, Is.True);
        FullSourceInstallationStatus status = fixture.Coordinator().Reconcile(fixture.Entry.Id)!;
        AssertTree(fixture.Fs, fixture.Install, status.Generation);
        if (status.Release is null)
            Assert.That(fixture.Entry.LastInstalledVersion, Is.SameAs(priorVersion));
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, null, false, _ => { }, CancellationToken.None);
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(BackupPath(fixture), fixture.Stock);
        string preserved = fixture.Fs.Directory.EnumerateFiles(fixture.Root, "legacy-*.zip", SearchOption.AllDirectories).Single();
        using (var input = fixture.Fs.File.OpenRead(preserved))
            Assert.That(Convert.ToHexString(SHA256.HashData(input)), Is.EqualTo(originalHash));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
    }

    /// <summary>
    /// Directories cannot substitute for an empty original file merely because their content hashes match.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_DirectoryEntryCannotSupplyEmptyStockFile()
    {
        Fixture fixture = Create();
        using (var output = fixture.Fs.File.Create(BackupPath(fixture)))
        using (var zip = new ZipFileService(fixture.Fs).NewArchive(output, ZipArchiveMode.Create, true))
            zip.CreateEntry("empty-original.bin/");
        var expected = new StockBaselineManifest("empty-file-case", "independent synthetic file contract",
            [new StockBaselineFile("empty-original.bin", 0, Digest([]))]);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs))
                .VerifyAvailableSourcesAsync(BackupPath(fixture), fixture.Install, expected));
    }

    /// <summary>
    /// Unsafe legacy entry names and ambiguous aliases cannot acquire conversion authority.
    /// </summary>
    /// <param name="name">The unsafe or duplicate entry name.</param>
    /// <returns>The verification task.</returns>
    [TestCase("../outside.bin")]
    [TestCase("C:\\outside.bin")]
    [TestCase("KSP2_X64.EXE")]
    public async Task BackupAdoption_UnsafeLegacyEntriesPreserveArchiveAndInstallation(string name)
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        using (var input = fixture.Fs.File.Open(BackupPath(fixture), FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipFileService(fixture.Fs).NewArchive(input, ZipArchiveMode.Update, true))
        using (Stream entry = archive.CreateEntry(name).Open())
            entry.WriteByte(1);
        string original = BackupHash(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(BackupHash(fixture), Is.EqualTo(original));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Canonical archive identity and provenance cannot be silently rewritten through the legacy recovery fallback.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_ForeignCanonicalProvenanceIsRejected()
    {
        Fixture fixture = Create();
        var other = new StockBaselineManifest(fixture.Stock.BaselineIdentity, "different original provenance",
            fixture.Stock.Files, fixture.Stock.Directories);
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs), availableSpace: _ => long.MaxValue)
            .CreateAsync(fixture.StockRoot, BackupPath(fixture), other);
        string original = BackupHash(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(BackupHash(fixture), Is.EqualTo(original));
    }

    /// <summary>
    /// Unrecognized retention collisions and changed source pins fail before catalog or installation mutation.
    /// </summary>
    /// <param name="collision">True for an unowned retention path, false for changed original archive bytes.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task BackupAdoption_OriginalPinAndRetentionOwnershipAreRequired(bool collision)
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        string original = BackupHash(fixture);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        string retained = At(fixture.Fs, fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}/legacy-{original}.zip");
        if (collision)
            Put(fixture.Fs, fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}/legacy-{original}.zip", Encoding.UTF8.GetBytes("unknown retention bytes"));
        else
        {
            using var output = fixture.Fs.File.Open(BackupPath(fixture), FileMode.Append, FileAccess.Write);
            output.WriteByte(1);
        }
        Assert.That(async () => await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None), Throws.Exception);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        if (collision)
        {
            Assert.That(BackupHash(fixture), Is.EqualTo(original));
            Assert.That(fixture.Fs.File.ReadAllText(retained), Is.EqualTo("unknown retention bytes"));
        }
        string catalog = At(fixture.Fs, fixture.Storage, $"full-source-v1/{fixture.Entry.Id:N}/installation.json");
        Assert.That(fixture.Fs.File.Exists(catalog), Is.False);
    }

    /// <summary>
    /// A changed complete candidate after original preservation leaves the original and pending authority intact.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_CorruptCandidateAfterPreservationDoesNotGuessRecovery()
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        string original = BackupHash(fixture);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await fixture.Coordinator((name, after) =>
        {
            if (name == "archive-legacy-preserve" && after)
                throw new IOException("Original retained before interrupted promotion.");
        }).ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
        string storage = At(fixture.Fs, fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string candidate = fixture.Fs.Directory.EnumerateFiles(storage, "*-stock.zip").Single();
        fixture.Fs.File.WriteAllText(candidate, "unrecognized candidate bytes");
        Assert.Throws<InvalidDataException>(() => fixture.Coordinator().Reconcile(fixture.Entry.Id));
        Assert.That(fixture.Fs.File.ReadAllText(candidate), Is.EqualTo("unrecognized candidate bytes"));
        using var preserved = fixture.Fs.File.OpenRead(At(fixture.Fs, storage, $"legacy-{original}.zip"));
        Assert.That(Convert.ToHexString(SHA256.HashData(preserved)), Is.EqualTo(original));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(fixture.Fs.File.Exists(BackupPath(fixture)), Is.False);
    }

    /// <summary>
    /// Missing added-file authority rejects without binding a partial catalog, so a fresh published record can be retried.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupAdoption_UnownedCollisionCanRetryWithFreshPublishedLegacyContract()
    {
        const string ADDED = "Redux/known-added.dll";
        Fixture fixture = CreateAdoptionFixture(false, true);
        PutLegacyBackup(fixture);
        byte[] previous = Encoding.UTF8.GetBytes("previous legacy installed runtime bytes");
        Put(fixture.Fs, fixture.Install, ADDED, previous);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
        string head = At(fixture.Fs, fixture.Storage, $"full-source-v1/{fixture.Entry.Id:N}/installation.json");
        Assert.That(fixture.Fs.File.Exists(head), Is.False, "An unresolved unowned collision must not bind initial catalog ownership.");
        var known = new StockBaselineManifest("previous-legacy-build", "publisher supplied legacy installed-file record",
            fixture.Stock.Files.Append(new StockBaselineFile(ADDED, previous.Length, Digest(previous))), fixture.Stock.Directories);
        var baseline = fixture.Patch.FullSource!.Baselines[0] with { LegacyOwnershipManifests = [known.ToJson()] };
        fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource with { Baselines = [baseline] } };
        stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, target, CancellationToken.None);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }

    /// <summary>
    /// Archive scratch and catalog storage cannot write into another configured installation.
    /// </summary>
    /// <param name="catalogOverlap">True for launcher storage, false for archive scratch storage.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task BackupAdoption_OtherConfiguredInstallCannotBecomeMigrationStorage(bool catalogOverlap)
    {
        Fixture fixture = CreateAdoptionFixture(false);
        PutLegacyBackup(fixture);
        string original = BackupHash(fixture);
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        string otherRoot = catalogOverlap ? fixture.Storage : At(fixture.Fs, fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        Put(fixture.Fs, otherRoot, Ksp2Install.KSP2_EXE_NAME, Encoding.UTF8.GetBytes("other configured installation bytes"));
        fixture.Config.Ksp2Installs.Add(new Ksp2InstallEntry
        {
            ExePath = At(fixture.Fs, otherRoot, Ksp2Install.KSP2_EXE_NAME), Name = "Other fixture installation"
        });
        Dictionary<string, string> observed = fixture.Fs.Directory.EnumerateFiles(otherRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path)));
        Assert.That(async () => await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None), Throws.Exception);
        Dictionary<string, string> after = fixture.Fs.Directory.EnumerateFiles(otherRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path)));
        Assert.That(after, Is.EquivalentTo(observed), "The other installation cannot receive migration payloads or catalog files.");
        Assert.That(BackupHash(fixture), Is.EqualTo(original));
    }

    private static Fixture CreateAdoptionFixture(bool native, bool addedTarget = false)
    {
        System.IO.Abstractions.IFileSystem fs = native ? new RealFileSystem() :
            new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        string workspace = fs.Path.GetDirectoryName(typeof(FullSourcePreparationServiceTest).Assembly.Location)!;
        while (native && !fs.Directory.Exists(At(fs, workspace, "Documentation/Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("The native evidence workspace is absent.");
        }
        // Keep the retained fixture within the launcher's Windows sibling path budget.
        string root = native
            ? At(fs, workspace, $"Library/Omnipatching/launcher-backup-adoption/native-{Guid.NewGuid().ToString("N")[..12]}")
            : @"C:\small-backup-adoption";
        string stockRoot = At(fs, root, "stock-input");
        string targetRoot = At(fs, root, "target-input");
        var files = new Dictionary<string, byte[]>
        {
            [Ksp2Install.KSP2_EXE_NAME] = Encoding.UTF8.GetBytes("stock executable bytes"),
            ["KSP2_x64_Data/boot.config"] = Encoding.UTF8.GetBytes("stock=exact\r\n"),
            [BUNDLE] = Encoding.UTF8.GetBytes("original stock bundle bytes"),
            [WEM] = Encoding.UTF8.GetBytes("original stock encoded media bytes")
        };
        StockBaselineManifest stock = PutManifest(fs, stockRoot, "native-original-stock", files);
        var targetFiles = files.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone());
        targetFiles[BUNDLE] = Encoding.UTF8.GetBytes("verified target bundle bytes");
        if (addedTarget)
            targetFiles["Redux/known-added.dll"] = Encoding.UTF8.GetBytes("verified new target runtime bytes");
        StockBaselineManifest target = PutManifest(fs, targetRoot, RELEASE_ID, targetFiles);
        string artifact = At(fs, root, "artifact");
        var pipeline = new ReleaseRecipePipeline(fs, native ? null : _ => { });
        ReleaseRecipePublication publication = pipeline.Publish(targetRoot, target,
            [new ReleaseRecipeSourceTree(stockRoot, stock, stock.Files.Select(file =>
                new ReleaseRecipeSource(file.Path, BundleRecipeSourceKind.RawFile)).ToArray())], artifact);
        return new Fixture(fs, root, stockRoot, stock, target, artifact, publication);
    }

    /// <summary>
    /// Real Windows partial-backup promotion and interrupted recovery retain an exact original and restore full stock.
    /// </summary>
    /// <param name="interrupt">True to interrupt between preservation and complete backup publication.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    public async Task BackupAdoption_NativeWindowsPromotesAndRecoversWithoutFullTreeCopy(bool interrupt)
    {
        Fixture fixture = CreateAdoptionFixture(true);
        PutLegacyBackup(fixture);
        string originalHash = BackupHash(fixture);
        Put(fixture.Fs, fixture.Install, Ksp2Install.KSP2_EXE_NAME, Encoding.UTF8.GetBytes("legacy executable bytes"));
        FullSourceStockPreparation stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
        FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        if (interrupt)
        {
            await Assert.ThrowsAsync<IOException>(async () => await fixture.Coordinator((name, after) =>
            {
                if (name == "archive-legacy-preserve" && after)
                    throw new IOException("Native adoption interrupted with original safely retained.");
            }).ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None));
            fixture.Coordinator().Reconcile(fixture.Entry.Id);
            await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, null, false, _ => { }, CancellationToken.None);
        }
        else
        {
            await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, stock, false, _ => { }, CancellationToken.None);
        }
        await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(stock.SourceRoot, Is.Empty);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
        Assert.That(fixture.Fs.Directory.EnumerateDirectories(At(fixture.Fs, fixture.Storage, "full-source-preparation-v1"), "restore-*").Any(), Is.False);
        fixture.Steam.VerifyNoOtherCalls();
        string retained = fixture.Fs.Directory.EnumerateFiles(fixture.Root, "legacy-*.zip", SearchOption.AllDirectories).Single();
        using (var input = fixture.Fs.File.OpenRead(retained))
            Assert.That(Convert.ToHexString(SHA256.HashData(input)), Is.EqualTo(originalHash));
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(BackupPath(fixture), fixture.Stock);
        string report = At(fixture.Fs, fixture.Root, "archive-adoption-result.json");
        fixture.Fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture.Root, fixture.Install, fixture.Storage, retained, originalHash, interrupt,
            stockSourceDirectoryRequired = false, preparedSource = stock.SourceRoot,
            completeBackupHash = BackupHash(fixture), stockContractHash = Digest(fixture.Stock.ToJson()),
            exactStockRestored = true, unknownUserFilesPreserved = true,
            proprietaryDownloads = 0, fullGameCopies = 0
        }));
        fixture.Fs.File.WriteAllBytes(At(fixture.Fs, fixture.Root, "stock-manifest.json"), fixture.Stock.ToJson());
        TestContext.AddTestAttachment(report, "Small Windows archive adoption and exact restoration evidence.");
    }
}
