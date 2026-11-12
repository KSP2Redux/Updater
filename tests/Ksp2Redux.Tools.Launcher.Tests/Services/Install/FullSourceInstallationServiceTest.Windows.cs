using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    private static Fixture NativeFixture()
    {
        var fs = new RealFileSystem();
        string workspace = fs.Path.GetDirectoryName(typeof(FullSourceInstallationServiceTest).Assembly.Location)!;
        while (!fs.Directory.Exists(fs.Path.Combine(workspace, "Documentation", "Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace)
                ?? throw new IOException("The disposable coordinator evidence workspace was not found.");
        }
        // Leave room for the launcher's temporary sibling names beneath the game directory.
        string root = fs.Path.Combine(workspace, "Library", "Omnipatching", $"coordinator-{Guid.NewGuid():N}");
        Fixture fixture = Create(fs, root);
        string report = fs.Path.Combine(root, "inputs.json");
        fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
        {
            root, fixture.Install, fixture.Storage, installationId = fixture.Entry.Id,
            stockInput = fixture.Stock.SourceRoot, releaseTwo = fixture.Target2.SourceRoot, releaseThree = fixture.Target3.SourceRoot,
            stock = fixture.Stock.Stock.BaselineIdentity, targetTwo = fixture.Target2.Identity, targetThree = fixture.Target3.Identity
        }));
        fs.File.WriteAllBytes(fs.Path.Combine(root, "stock-manifest.json"), fixture.Stock.Stock.ToJson());
        fs.File.WriteAllBytes(fs.Path.Combine(root, "release-two-manifest.json"), fixture.Target2.Manifest.ToJson());
        fs.File.WriteAllBytes(fs.Path.Combine(root, "release-three-manifest.json"), fixture.Target3.Manifest.ToJson());
        TestContext.AddTestAttachment(report, "Retained real Windows contained inputs and generation identities.");
        TestContext.Progress.WriteLine($"Retained coordinator native evidence: {root}");
        return fixture;
    }

    /// <summary>
    /// Real Windows contained inputs pass complete generation transitions, explicit repair and exact uninstall.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativeContainedIntegration_RunsCompleteProductionOperationsAndRetainsEvidence()
    {
        Fixture fixture = NativeFixture();
        Generation(fixture, await Apply(fixture));
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        Assert.That(fixture.Entry.ReleaseChannel, Is.EqualTo("internal"));
        Assert.That(fixture.Entry.LastInstalledVersion!.BuildNumber, Is.EqualTo("300"));
        Put(fixture.Fs, fixture.Install, BUNDLE, "native independently observed damage");
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, "added.bin"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture, initial: false));
        Generation(fixture, await Apply(fixture, initial: false, repair: true));
        Assert.That(fixture.Entry.ReleaseChannel, Is.EqualTo("beta"));
        Generation(fixture, await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));
        Assert.That(fixture.Entry.LastInstalledVersion, Is.Null);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
        await new StockArchive(fixture.Fs, new Ksp2Redux.Tools.Common.Services.ZipFileService(fixture.Fs))
            .VerifyAsync(fixture.Archive, fixture.Stock.Stock);
        Preserved(fixture, archive);
        fixture.Fs.File.WriteAllText(fixture.Fs.Path.Combine(fixture.Root, "complete.txt"), "All contained generation, repair and uninstall assertions passed.");
    }

    /// <summary>
    /// Native publication and ownership restart edges retain original archives and publish exactly one generation.
    /// </summary>
    /// <param name="boundary">The publication or engine boundary.</param>
    /// <param name="after">True for interruption after completion.</param>
    /// <returns>The verification task.</returns>
    [TestCase("catalog-pending:publish", false)]
    [TestCase("catalog-pending:publish", true)]
    [TestCase("archive-create", false)]
    [TestCase("archive-create", true)]
    [TestCase("operation-ready:publish", false)]
    [TestCase("operation-ready:publish", true)]
    [TestCase("engine-apply", false)]
    [TestCase("engine-apply", true)]
    [TestCase("operation-result:publish", false)]
    [TestCase("operation-result:publish", true)]
    [TestCase("catalog-ownership:publish", false)]
    [TestCase("catalog-ownership:publish", true)]
    [TestCase("identity-projection", false)]
    [TestCase("identity-projection", true)]
    [Platform("Win")]
    public async Task NativeRestartPublication_ReconcilesWithoutUsingMarkerAuthority(string boundary, bool after)
    {
        Fixture fixture = NativeFixture();
        bool failed = false;
        var service = fixture.Service((name, completed) =>
        {
            if (!failed && name == boundary && completed == after)
            {
                failed = true;
                throw new IOException($"Contained native process loss at {name} {completed}");
            }
        });
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, service));
        Assert.That(failed, Is.True);
        byte[]? archive = fixture.Fs.File.Exists(fixture.Archive) ? fixture.Fs.File.ReadAllBytes(fixture.Archive) : null;
        fixture.Entry.LastInstalledVersion = null;
        fixture.Entry.ReleaseChannel = "beta";
        FullSourceInstallationStatus? status = fixture.Service().Reconcile(fixture.Entry.Id);
        if (status is not null)
            Generation(fixture, status);
        int engineOperations = 0;
        fixture.Service(engineFault: (_, _) => engineOperations++).Reconcile(fixture.Entry.Id);
        Assert.That(engineOperations, Is.Zero);
        Preserved(fixture, archive);
        Generation(fixture, await Apply(fixture, initial: status?.Release is null));
        Preserved(fixture, archive);
    }

    /// <summary>
    /// A locked owned destination aborts safely and a locked archive never triggers replacement or deletion.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativeLockedDestinationAndArchive_RetainKnownUnknownAndBackupBytes()
    {
        Fixture fixture = NativeFixture();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        using (FileSystemStream locked = fixture.Fs.FileStream.New(At(fixture.Fs, fixture.Install, BUNDLE), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<FilesInUseException>(async () => await Apply(fixture, target: fixture.Target3, initial: false));
        }
        Generation(fixture, fixture.Service().Reconcile(fixture.Entry.Id)!);
        using (FileSystemStream locked = fixture.Fs.FileStream.New(fixture.Archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            FullSourceInstallationStatus status = fixture.Service().Reconcile(fixture.Entry.Id)!;
            Assert.That(status.Release, Is.EqualTo(fixture.Target2.Identity));
            Assert.That(status.ArchiveFailure, Is.Not.Null);
            await Assert.ThrowsAsync<FilesInUseException>(async () => await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));
        }
        Generation(fixture, fixture.Service().Reconcile(fixture.Entry.Id)!);
        Preserved(fixture, archive);
    }

    /// <summary>
    /// Native destination publication failure after staging recovers the prior generation with all undo retained.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativeLockedRenameAfterStaging_RollsBackWithoutDeletingArchive()
    {
        Fixture fixture = NativeFixture();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        FileSystemStream? locked = null;
        var service = fixture.Service(engineFault: (name, after) =>
        {
            if (!after && name.StartsWith("install-", StringComparison.Ordinal) && locked is null)
                locked = fixture.Fs.FileStream.New(At(fixture.Fs, fixture.Install, BUNDLE), FileMode.Open,
                    FileAccess.Read, FileShare.Read);
        });
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, service, target: fixture.Target3, initial: false));
            Assert.That(locked, Is.Not.Null, "The external reader must appear after the guarded staging phase.");
        }
        finally
        {
            locked?.Dispose();
        }
        FullSourceInstallationStatus recovered = fixture.Service().Reconcile(fixture.Entry.Id)!;
        Assert.That(recovered.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.That(recovered.TransactionResult, Is.EqualTo(Ksp2Redux.Tools.Common.Transactions.InstallTransactionResult.RolledBack));
        Generation(fixture, recovered);
        Preserved(fixture, archive);
    }
}
