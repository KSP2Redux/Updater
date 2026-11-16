using Microsoft.Extensions.DependencyInjection;
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

    [Test]
    public async Task CanonicalArchive_AdoptsExactAuthenticatedFullSourceGeneration()
    {
        Fixture fixture = Create();
        await PutCanonicalBackup(fixture);
        string hash = BackupHash(fixture);
        foreach (var file in fixture.Target.Files)
            Put(fixture.Fs, fixture.Install, file.Path, fixture.Fs.File.ReadAllBytes(At(fixture.Fs, At(fixture.Fs, fixture.Root, "frozen-target"), file.Path)));
        var plan = new InstallPlan { FullSourceReleaseId = RELEASE_ID, FullSourceChannel = CHANNEL, FullSourceVersion = VERSION };
        await Services(fixture).GetRequiredService<IInstallPlanService>().ApplyToFolder(plan, fixture.Install,
            _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.That(BackupHash(fixture), Is.EqualTo(hash));
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }
}
