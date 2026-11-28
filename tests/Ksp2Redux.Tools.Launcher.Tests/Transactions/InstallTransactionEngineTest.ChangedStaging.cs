using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

public partial class InstallTransactionEngineTest
{
    private static (IFileSystem Fs, InstallTransactionPlan Plan) ChangedStagingFixture()
    {
        var fixture = Fixture();
        byte[] unchanged = new byte[512 * 1024];
        byte[] retained = Encoding.UTF8.GetBytes("explicitly retained unowned bytes");
        Put(fixture.Fs, INSTALL, "unchanged.bin", unchanged);
        Put(fixture.Fs, SOURCE, "unchanged.bin", unchanged);
        Put(fixture.Fs, INSTALL, "retained.bin", retained);
        Put(fixture.Fs, SOURCE, "retained.bin", retained);
        var keep = new StockBaselineFile("unchanged.bin", unchanged.Length, Digest(unchanged));
        var unowned = new StockBaselineFile("retained.bin", retained.Length, Digest(retained));
        var current = new StockBaselineManifest("stock-with-unchanged", "synthetic contract", fixture.Plan.Current.Files.Append(keep), fixture.Plan.Current.Directories);
        var target = new StockBaselineManifest("target-with-unchanged", "synthetic contract", fixture.Plan.Target.Files.Append(keep).Append(unowned), fixture.Plan.Target.Directories);
        var plan = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, WORK, current, target, current,
            fixture.Plan.Archive, [unowned.Path], useChangedFileStaging: true);
        return (fixture.Fs, plan);
    }

    /// <summary>
    /// An update writes only changed target and undo bytes while verifying unchanged and unowned active bytes.
    /// </summary>
    [Test]
    public void ChangedFileStaging_WritesOnlyChangedOwnedBlobs()
    {
        var (fs, plan) = ChangedStagingFixture();
        var operations = new List<string>();
        Engine(fs, (name, after) => { if (after) operations.Add(name); }).Apply(plan);
        Assert.That(operations.Count(name => name.StartsWith("stage-", StringComparison.Ordinal) && name.EndsWith(":open", StringComparison.Ordinal)), Is.EqualTo(3));
        Assert.That(operations.Count(name => name.StartsWith("undo-", StringComparison.Ordinal) && name.EndsWith(":open", StringComparison.Ordinal)), Is.EqualTo(3));
        AssertGeneration(fs, plan, true);
    }

    /// <summary>
    /// Changed-only space admission does not reserve unchanged complete game bytes a second time.
    /// </summary>
    [Test]
    public void ChangedFileStaging_AdmitsSpaceForChangesWithoutUnchangedCopies()
    {
        var (fs, plan) = ChangedStagingFixture();
        var engine = new InstallTransactionEngine(fs, _ => 65L * 1024 * 1024, validateFileStreams: _ => { });
        Assert.That(engine.Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, plan, true);
    }

    /// <summary>
    /// Unchanged and retained target files do not require duplicate prepared source files.
    /// </summary>
    [Test]
    public void ChangedFileStaging_DoesNotReadUnusedSourceFiles()
    {
        var (fs, plan) = ChangedStagingFixture();
        fs.File.Delete(fs.Path.Combine(SOURCE, "unchanged.bin"));
        fs.File.Delete(fs.Path.Combine(SOURCE, "retained.bin"));
        Assert.That(Engine(fs).Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, plan, true);
    }

    /// <summary>
    /// Every observed staging and publication boundary can restart under the changed-file policy.
    /// </summary>
    [Test]
    public void ChangedFileStaging_EveryBoundaryRecoversAndCanRetry()
    {
        var sample = ChangedStagingFixture();
        var boundaries = new List<(string Name, bool After)>();
        Engine(sample.Fs, (name, after) => boundaries.Add((name, after))).Apply(sample.Plan);
        for (int interruption = 0; interruption < boundaries.Count; interruption++)
        {
            var (fs, plan) = ChangedStagingFixture();
            int observed = 0;
            int selected = interruption;
            Assert.Throws<IOException>(() => Engine(fs, (_, _) =>
            {
                if (observed++ == selected)
                    throw new IOException($"Synthetic interruption {selected} {boundaries[selected]}.");
            }).Apply(plan));
            InstallTransactionResult result = Engine(fs).Recover(plan);
            AssertGeneration(fs, plan, result == InstallTransactionResult.Committed);
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(result));
            if (result == InstallTransactionResult.Committed)
                continue;
            var retry = new InstallTransactionPlan(Guid.NewGuid(), plan.InstallRoot, plan.SourceRoot, WORK + "-retry",
                plan.Current, plan.Target, plan.Stock, plan.Archive, plan.RetainedUnownedFiles, useChangedFileStaging: true);
            Assert.That(Engine(fs).Apply(retry), Is.EqualTo(InstallTransactionResult.Committed));
            AssertGeneration(fs, retry, true);
        }
        TestContext.Progress.WriteLine($"Changed staging before/after boundaries verified: {boundaries.Count}.");
    }

    /// <summary>
    /// An externally changed unchanged file blocks rollback before any active mutation without inventing undo ownership.
    /// </summary>
    [Test]
    public void ChangedFileStaging_ExternalUnchangedDamageFailsBeforeRecoveryMutation()
    {
        var (fs, plan) = ChangedStagingFixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "install-0" && !after)
                throw new IOException("Synthetic interruption before publication.");
        }).Apply(plan));
        byte[] foreign = Encoding.UTF8.GetBytes("external change to unchanged bytes");
        Put(fs, INSTALL, "unchanged.bin", foreign);
        int mutations = 0;
        Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => mutations++).Recover(plan));
        Assert.That(mutations, Is.Zero);
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "unchanged.bin")), Is.EqualTo(foreign));
    }

    /// <summary>
    /// A blob at an unstaged unchanged index is foreign evidence and is never acquired by preparation cleanup.
    /// </summary>
    [Test]
    public void ChangedFileStaging_ForeignSkippedBlobIsRetained()
    {
        var (fs, plan) = ChangedStagingFixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "undo-0:open" && !after)
                throw new IOException("Synthetic interrupted preparation.");
        }).Apply(plan));
        int index = plan.Current.Files.ToList().FindIndex(file => file.Path == "unchanged.bin");
        string blob = fs.Path.Combine(WORK, "undo", $"{index:D8}");
        byte[] foreign = Encoding.UTF8.GetBytes("foreign bytes are not an owned stage");
        fs.File.WriteAllBytes(blob, foreign);
        int mutations = 0;
        Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => mutations++).Recover(plan));
        Assert.That(mutations, Is.Zero);
        Assert.That(fs.File.ReadAllBytes(blob), Is.EqualTo(foreign));
    }

    /// <summary>
    /// Cancellation during staging or publication restores the known generation under the changed-file policy.
    /// </summary>
    /// <param name="boundary">The observed operation boundary that requests cancellation.</param>
    [TestCase("stage-0:write"), TestCase("journal-mutating:flush"), TestCase("install-0")]
    public void ChangedFileStaging_CancellationReconcilesKnownBytes(string boundary)
    {
        var (fs, plan) = ChangedStagingFixture();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Engine(fs, (name, after) =>
        {
            if (name == boundary && after)
                cancellation.Cancel();
        }).Apply(plan, cancellation.Token));
        AssertGeneration(fs, plan, false);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
    }
}
