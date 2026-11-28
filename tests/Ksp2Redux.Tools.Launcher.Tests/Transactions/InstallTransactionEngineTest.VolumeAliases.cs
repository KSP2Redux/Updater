using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

public partial class InstallTransactionEngineTest
{
    /// <summary>
    /// Accepted drive aliases cannot conceal physical overlap with source, protected or archive paths.
    /// </summary>
    /// <param name="conflict">The physically aliased conflict.</param>
    [TestCase("source"), TestCase("protected"), TestCase("archive")]
    public void VolumeAlias_PhysicalOverlapRejectedBeforeMutation(string conflict)
    {
        var (fs, plan) = Fixture();
        const string ALIAS = @"D:\alias";
        fs.Directory.CreateDirectory(ALIAS + @"\game");
        string source = plan.SourceRoot;
        StockBaselineManifest target = plan.Target;
        var reference = plan.Archive;
        string[] protectedRoots = [];
        if (conflict == "source")
        {
            source = ALIAS + @"\game";
            target = plan.Current;
            foreach (var file in plan.Current.Files)
            {
                Put(fs, source, file.Path, fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\'))));
            }
            foreach (string directory in plan.Current.Directories)
            {
                fs.Directory.CreateDirectory(fs.Path.Combine(source, directory));
            }
        }
        if (conflict == "protected")
            protectedRoots = [ALIAS + @"\game"];
        if (conflict == "archive")
        {
            string path = ALIAS + @"\game\foreign-backup.zip";
            fs.File.WriteAllBytes(path, ARCHIVE_BYTES);
            reference = reference with { Path = path };
        }
        var aliased = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, source, WORK, plan.Current, target, plan.Stock,
            reference, protectedRoots: protectedRoots, useChangedFileStaging: true);
        int mutations = 0;
        var engine = new InstallTransactionEngine(fs, _ => long.MaxValue, (_, _) => mutations++, _ => { },
            path => path.Replace(ALIAS, ROOT, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => engine.Apply(aliased));
        Assert.That(mutations, Is.Zero);
        Assert.That(fs.File.ReadAllText(fs.Path.Combine(INSTALL, "game.exe")), Is.EqualTo("old executable"));
    }

    /// <summary>
    /// Disjoint transaction storage on another letter for the same physical volume is accepted.
    /// </summary>
    [Test]
    public void VolumeAlias_DisjointSameVolumeStorageAccepted()
    {
        var (fs, plan) = Fixture();
        const string ALIAS = @"D:\alias";
        fs.Directory.CreateDirectory(ALIAS);
        var aliased = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, ALIAS + @"\transaction", plan.Current,
            plan.Target, plan.Stock, plan.Archive, useChangedFileStaging: true);
        var engine = new InstallTransactionEngine(fs, _ => long.MaxValue, validateFileStreams: _ => { },
            pathIdentity: path => path.Replace(ALIAS, ROOT, StringComparison.Ordinal));
        Assert.That(engine.Apply(aliased), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, aliased, true);
    }
}
