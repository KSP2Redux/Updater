using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Common.Transactions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

/// <summary>
/// Interrupted metadata publication and preparation cleanup regression coverage.
/// </summary>
public partial class InstallTransactionEngineTest
{
    private static byte[] Record(InstallTransactionPlan plan, string state)
    {
        string payload = $"1|{plan.GetDigest()}|{state}";
        return Encoding.ASCII.GetBytes($"{payload}|{Digest(Encoding.ASCII.GetBytes(payload))}\n");
    }

    private static (IFileSystem Fs, InstallTransactionPlan Plan, string Journal) PublishedPhase(string phase, bool recovery)
    {
        if (recovery)
        {
            var (fs, plan) = phase is "aborting" or "aborted" ? PreparationInterrupted() : Interrupted();
            Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
            {
                if (name == $"recovery-{phase}:write" && after)
                    throw new IOException("interrupted recovery publication");
            }).Recover(plan));
            return (fs, plan, fs.Path.Combine(WORK, "recovery"));
        }
        if (phase is "rollingback" or "rolledback")
        {
            var (fs, plan) = Interrupted();
            byte[] original = fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal"));
            if (phase == "rolledback")
            {
                Engine(fs).Recover(plan);
                fs.File.Delete(fs.Path.Combine(WORK, "recovery"));
            }
            fs.File.WriteAllBytes(fs.Path.Combine(INSTALL, ".redux-transaction.active"), Encoding.ASCII.GetBytes(plan.GetDigest()));
            fs.File.WriteAllBytes(fs.Path.Combine(WORK, "journal"), phase == "rolledback"
                ? [.. original, .. Record(plan, "rollingback"), .. Record(plan, "rolledback")]
                : [.. original, .. Record(plan, "rollingback")]);
            return (fs, plan, fs.Path.Combine(WORK, "journal"));
        }
        var (initialFs, initialPlan) = Fixture();
        Assert.Throws<IOException>(() => Engine(initialFs, (name, after) =>
        {
            if (name == $"journal-{phase}:write" && after)
                throw new IOException("interrupted installation publication");
        }).Apply(initialPlan));
        return (initialFs, initialPlan, initialFs.Path.Combine(WORK, "journal"));
    }

    private static (IFileSystem Fs, InstallTransactionPlan Plan) PreparationInterrupted()
    {
        var (fs, plan) = Fixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "stage-1:open" && !after)
                throw new IOException("interrupted staging");
        }).Apply(plan));
        return (fs, plan);
    }

    private static void AssertUndo(IFileSystem fs, InstallTransactionPlan plan)
    {
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            byte[] bytes = fs.File.ReadAllBytes(fs.Path.Combine(WORK, "undo", $"{index:D8}"));
            Assert.That(bytes.LongLength, Is.EqualTo(plan.Current.Files[index].Length));
            Assert.That(Digest(bytes), Is.EqualTo(plan.Current.Files[index].Sha256));
        }
    }

    /// <summary>
    /// A missing commit newline does not strand a mixed active generation with verified undo.
    /// </summary>
    [Test]
    public void PendingCommitPrefix_WithMixedGenerationRestoresOldBytes()
    {
        var (fs, plan) = Interrupted();
        string path = fs.Path.Combine(WORK, "journal");
        byte[] bytes = [.. fs.File.ReadAllBytes(path), .. Record(plan, "committed")[..^1]];
        fs.File.WriteAllBytes(path, bytes);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
        AssertUndo(fs, plan);
        Assert.That(fs.File.ReadAllBytes(path), Is.EqualTo(bytes));
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Every byte prefix of every install and recovery record converges after another interruption.
    /// </summary>
    /// <param name="phase">The interrupted record state.</param>
    /// <param name="recovery">True for the recovery journal, false for the original journal.</param>
    [TestCase("preparing", false)]
    [TestCase("prepared", false)]
    [TestCase("mutating", false)]
    [TestCase("rollingback", false)]
    [TestCase("rolledback", false)]
    [TestCase("committed", false)]
    [TestCase("aborting", true)]
    [TestCase("aborted", true)]
    [TestCase("rollingback", true)]
    [TestCase("rolledback", true)]
    public void EveryJournalByteTruncation_RecoversRepeatedly(string phase, bool recovery)
    {
        var (_, lengthPlan) = Fixture();
        int recordLength = Record(lengthPlan, phase).Length;
        for (int offset = 0; offset <= recordLength; offset++)
        {
            var (fs, plan, path) = PublishedPhase(phase, recovery);
            byte[] bytes = fs.File.ReadAllBytes(path);
            byte[] truncated = bytes[..(bytes.Length - recordLength + offset)];
            fs.File.WriteAllBytes(path, truncated);
            byte[] installJournal = fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal"));
            bool interrupted = false;
            try
            {
                Engine(fs, (_, _) =>
                {
                    interrupted = true;
                    throw new IOException("recovery interrupted again before its first write");
                }).Recover(plan);
            }
            catch (IOException) when (interrupted)
            {
                AssertPreserved(fs, plan);
            }
            InstallTransactionResult expected = phase == "committed" && offset == recordLength
                ? InstallTransactionResult.Committed : InstallTransactionResult.RolledBack;
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(expected), $"{phase} offset {offset}");
            AssertGeneration(fs, plan, expected == InstallTransactionResult.Committed);
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(expected), $"repeat {phase} offset {offset}");
            AssertGeneration(fs, plan, expected == InstallTransactionResult.Committed);
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal")), Is.EqualTo(installJournal));
            if (phase is "mutating" or "rollingback" or "rolledback" or "committed")
                AssertUndo(fs, plan);
        }
        TestContext.Progress.WriteLine($"Verified {recordLength + 1} offsets for {(recovery ? "recovery" : "install")} {phase}.");
    }

    private static (IFileSystem Fs, InstallTransactionPlan Plan) InitializationInterrupted(string kind)
    {
        if (kind == "preparing")
            return PreparationInterrupted();
        if (kind == "mutating")
            return Interrupted();
        var (fs, plan) = Fixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == (kind == "marker" ? "write-active:write" : "write-plan:write") && after)
                throw new IOException("initialization interrupted");
        }).Apply(plan));
        string path = kind == "marker" ? fs.Path.Combine(INSTALL, ".redux-transaction.active") : fs.Path.Combine(WORK, "plan.json");
        fs.File.WriteAllBytes(path, fs.File.ReadAllBytes(path)[..7]);
        return (fs, plan);
    }

    private static IEnumerable<TestCaseData> RecoveryBoundaries()
    {
        foreach (string kind in new[] { "marker", "plan", "preparing", "mutating" })
        {
            var (fs, plan) = InitializationInterrupted(kind);
            var boundaries = new List<(string Name, bool After)>();
            Engine(fs, (name, after) => boundaries.Add((name, after))).Recover(plan);
            for (int index = 0; index < boundaries.Count; index++)
            {
                yield return new TestCaseData(kind, index)
                    .SetName($"RecoveryCrash_{kind}_{index}_{boundaries[index].Name}_{boundaries[index].After}");
            }
        }
    }

    /// <summary>
    /// Initialization completion, preparation cleanup and rollback survive every recovery boundary.
    /// </summary>
    /// <param name="kind">The interrupted initialization or installation variant.</param>
    /// <param name="boundary">The recovery operation boundary index.</param>
    [TestCaseSource(nameof(RecoveryBoundaries))]
    public void EveryInitializationAndCleanupBoundary_Converges(string kind, int boundary)
    {
        var (fs, plan) = InitializationInterrupted(kind);
        int observed = 0;
        Assert.Throws<IOException>(() => Engine(fs, (_, _) =>
        {
            if (observed++ == boundary)
                throw new IOException("interrupted recovery boundary");
        }).Recover(plan));
        AssertPreserved(fs, plan);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(WORK, "plan.json")), Is.EqualTo(plan.ToJson()));
        if (kind == "mutating")
            AssertUndo(fs, plan);
    }

    /// <summary>
    /// Partial initial plan and marker writes recover at every byte offset without claiming another tree.
    /// </summary>
    /// <param name="kind">The publication being truncated.</param>
    [TestCase("marker")]
    [TestCase("plan")]
    [TestCase("legacy-marker")]
    public void EveryInitialPublicationByteTruncation_Converges(string kind)
    {
        var (_, lengthPlan) = Fixture();
        int length = kind == "plan" ? lengthPlan.ToJson().Length : 64;
        for (int offset = 0; offset <= length; offset++)
        {
            var (fs, plan) = InitializationInterrupted(kind == "legacy-marker" ? "plan" : kind);
            if (kind == "legacy-marker")
                fs.File.WriteAllBytes(fs.Path.Combine(WORK, "plan.json"), plan.ToJson());
            string path = kind == "plan" ? fs.Path.Combine(WORK, "plan.json") : fs.Path.Combine(INSTALL, ".redux-transaction.active");
            byte[] expected = kind == "plan" ? plan.ToJson() : Encoding.ASCII.GetBytes(plan.GetDigest());
            fs.File.WriteAllBytes(path, expected[..offset]);
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack), $"{kind} offset {offset}");
            AssertGeneration(fs, plan, false);
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            AssertGeneration(fs, plan, false);
        }
        TestContext.Progress.WriteLine($"Verified {length + 1} initialization offsets for {kind}.");
    }

    /// <summary>
    /// A changed byte at every position of a pending commit fails before any recovery write.
    /// </summary>
    [Test]
    public void CorruptPendingCommitPrefixes_RetainEveryByte()
    {
        var (_, lengthPlan) = Fixture();
        for (int offset = 0; offset < Record(lengthPlan, "committed").Length; offset++)
        {
            var (fs, plan) = Interrupted();
            byte[] tail = Record(plan, "committed")[..(offset + 1)];
            tail[offset] ^= 0x80;
            string path = fs.Path.Combine(WORK, "journal");
            byte[] bytes = [.. fs.File.ReadAllBytes(path), .. tail];
            fs.File.WriteAllBytes(path, bytes);
            var snapshot = fs.Directory.EnumerateFiles(ROOT, "*", SearchOption.AllDirectories)
                .ToDictionary(file => file, fs.File.ReadAllBytes);
            int writes = 0;
            Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => writes++).Recover(plan), $"offset {offset}");
            Assert.That(writes, Is.Zero);
            foreach (var file in snapshot)
            {
                Assert.That(fs.File.ReadAllBytes(file.Key), Is.EqualTo(file.Value), file.Key);
            }
            AssertUndo(fs, plan);
            AssertPreserved(fs, plan);
        }
    }

    /// <summary>
    /// Verified partial target and undo copies are cleaned without deleting their retained intent.
    /// </summary>
    /// <param name="directory">The preparation copy category.</param>
    [TestCase("target")]
    [TestCase("undo")]
    public void OwnPreparationCopyPrefixes_CleanAndRepeat(string directory)
    {
        var (_, lengthPlan) = Fixture();
        int length = (int)(directory == "target" ? lengthPlan.Target.Files[0].Length : lengthPlan.Current.Files[0].Length);
        for (int offset = 0; offset <= length; offset++)
        {
            var (fs, plan, _) = PublishedPhase("prepared", false);
            fs.File.WriteAllBytes(fs.Path.Combine(WORK, "journal"), Record(plan, "preparing"));
            string path = fs.Path.Combine(WORK, directory, "00000000");
            fs.File.WriteAllBytes(path, fs.File.ReadAllBytes(path)[..offset]);
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            AssertGeneration(fs, plan, false);
            Assert.That(fs.Directory.Exists(fs.Path.Combine(WORK, directory)), Is.False);
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            AssertGeneration(fs, plan, false);
        }
    }

    /// <summary>
    /// A completed cleanup never reacquires staged files recreated by an external writer.
    /// </summary>
    [Test]
    public void CompletedPreparationCleanup_RejectsRecreatedWork()
    {
        var (fs, plan) = PreparationInterrupted();
        Engine(fs).Recover(plan);
        byte[] bytes = fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, plan.Target.Files[0].Path.Replace('/', '\\')));
        Put(fs, WORK, "target/00000000", bytes);
        int writes = 0;
        Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => writes++).Recover(plan));
        Assert.That(writes, Is.Zero);
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(WORK, "target", "00000000")), Is.EqualTo(bytes));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Foreign partial metadata, unexplained work and conflicting staging bytes retain all evidence.
    /// </summary>
    /// <param name="kind">The conflict variant.</param>
    [TestCase("empty-work")]
    [TestCase("partial-marker-with-work")]
    [TestCase("foreign-plan")]
    [TestCase("unknown-work")]
    [TestCase("foreign-staging")]
    [TestCase("foreign-recovery")]
    [TestCase("external-active")]
    public void IncompleteEvidenceConflicts_FailBeforeWrites(string kind)
    {
        var (fs, plan) = kind is "foreign-staging" or "foreign-recovery" or "external-active"
            ? PreparationInterrupted() : InitializationInterrupted("plan");
        switch (kind)
        {
            case "empty-work":
                fs.File.Delete(fs.Path.Combine(WORK, "plan.json"));
                fs.File.Delete(fs.Path.Combine(INSTALL, ".redux-transaction.active"));
                break;
            case "partial-marker-with-work":
                fs.File.WriteAllBytes(fs.Path.Combine(INSTALL, ".redux-transaction.active"), Encoding.ASCII.GetBytes(plan.GetDigest())[..7]);
                break;
            case "foreign-plan":
                fs.File.WriteAllBytes(fs.Path.Combine(WORK, "plan.json"), [88, 89]);
                break;
            case "unknown-work":
                Put(fs, WORK, "user-file", USER_BYTES);
                break;
            case "foreign-staging":
                fs.File.WriteAllBytes(fs.Path.Combine(WORK, "target", "00000000"), USER_BYTES);
                break;
            case "foreign-recovery":
                fs.File.WriteAllBytes(fs.Path.Combine(WORK, "recovery"), Record(plan, "rollingback"));
                break;
            case "external-active":
                Put(fs, INSTALL, "game.exe", USER_BYTES);
                break;
        }
        var snapshot = fs.Directory.EnumerateFiles(ROOT, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, fs.File.ReadAllBytes);
        int writes = 0;
        Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => writes++).Recover(plan));
        Assert.That(writes, Is.Zero);
        foreach (var file in snapshot)
        {
            Assert.That(fs.File.ReadAllBytes(file.Key), Is.EqualTo(file.Value), file.Key);
        }
        AssertPreserved(fs, plan);
    }
}
