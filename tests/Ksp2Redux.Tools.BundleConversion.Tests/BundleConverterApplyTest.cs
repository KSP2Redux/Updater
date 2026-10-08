using Ksp2Redux.Tools.BundleConversion.Bundles;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

public class BundleConverterApplyTest
{
    private const string CONTENT = "content.bundle";
    private const string OTHER = "other.bundle";

    [Test]
    public void Apply_UncompressedBundleWithTheExternal_RetargetsMaterialFieldAndPreloadInPlace()
    {
        // Arrange
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Assert
        var plan = result.Plan.Bundles.Single();
        Assert.That(plan.Mode, Is.EqualTo(BundleEditMode.InPlace));
        Assert.That(plan.Edits.Select(edit => edit.Kind), Is.EquivalentTo(new[]
        {
            ShaderReferenceKind.Material,
            ShaderReferenceKind.MonoBehaviour,
            ShaderReferenceKind.Preload,
        }));
        Assert.That(result.EditsWritten, Is.EqualTo(3));

        var edited = game.Read(CONTENT);
        Assert.That(edited, Has.Length.EqualTo(stock.Length));
        var expected = Hex.PPtr(1, TestGame.TargetPathId);
        foreach (var edit in plan.Edits)
        {
            Assert.That(edited.AsSpan((int)edit.FileOffset, Hex.PPTR_SIZE).ToArray(), Is.EqualTo(expected));
        }

        Assert.That(DifferingBytes(stock, edited), Is.LessThanOrEqualTo(3 * Hex.PPTR_SIZE));
        Assert.That(ShaderPointers(game, game.Layout.BundlePath(CONTENT)), Is.All.EqualTo((1, TestGame.TargetPathId)));

        var journal = game.Journal()!;
        Assert.That(journal.InPlace.Single().File, Is.EqualTo(CONTENT));
        Assert.That(journal.InPlace.Single().Size, Is.EqualTo(stock.Length));
        Assert.That(journal.InPlace.Single().Edits, Has.Count.EqualTo(3));
        Assert.That(journal.Rewritten, Is.Empty);
    }

    [Test]
    public void Apply_RunTwice_SecondRunChangesNothing()
    {
        // Arrange
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB));
        game.Write(OTHER, TestGame.ReferencingBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB));
        var manifest = TestGame.Manifest(TestGame.EMBEDDED_SHADER);
        game.Converter().Apply(manifest);
        var bundle = game.Read(CONTENT);
        var copy = game.ReadCopy(OTHER);
        var journal = game.FileSystem.File.ReadAllText(game.Layout.JournalPath);

        // Act
        var second = game.Converter().Apply(manifest);

        // Assert
        Assert.That(second.EditsWritten, Is.Zero);
        Assert.That(second.EditsAlreadyApplied, Is.EqualTo(3));
        Assert.That(second.CopiesWritten, Is.Zero);
        Assert.That(second.CopiesUpToDate, Is.EqualTo(1));
        Assert.That(game.Read(CONTENT), Is.EqualTo(bundle));
        Assert.That(game.ReadCopy(OTHER), Is.EqualTo(copy));
        Assert.That(game.FileSystem.File.ReadAllText(game.Layout.JournalPath), Is.EqualTo(journal));
    }

    [Test]
    public void Apply_AfterTheStockFileIsRestored_WritesTheEditsAgain()
    {
        // Arrange: a Steam file verification puts the stock bytes back without touching the journal.
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);
        var manifest = TestGame.Manifest(TestGame.EMBEDDED_SHADER);
        game.Converter().Apply(manifest);
        var converted = game.Read(CONTENT);
        game.Write(CONTENT, stock);
        Assert.That(game.Converter().Verify().Count(EditState.Stock), Is.EqualTo(3));

        // Act
        var result = game.Converter().Apply(manifest);

        // Assert
        Assert.That(result.EditsWritten, Is.EqualTo(3));
        Assert.That(game.Read(CONTENT), Is.EqualTo(converted));
        Assert.That(game.Converter().Verify().IsComplete, Is.True);
    }

    [Test]
    public void Apply_ForeignBytesWhereAnEditGoes_LeavesThemAloneAndReportsThem()
    {
        // Arrange
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB));
        var manifest = TestGame.Manifest(TestGame.EMBEDDED_SHADER);
        var first = game.Converter().Apply(manifest);
        var preload = first.Plan.Bundles.Single().Edits.First(edit => edit.Kind == ShaderReferenceKind.Preload && edit.Old.SequenceEqual(Hex.PPtr(0, TestGame.SHADER_PATH_ID)));
        var foreign = Hex.PPtr(0, 777);
        Patch(game, CONTENT, preload.FileOffset, foreign);

        // Act
        var result = game.Converter().Apply(manifest);

        // Assert
        Assert.That(result.ForeignEdits.Select(edit => edit.Offset), Is.EqualTo(new[] { preload.FileOffset }));
        Assert.That(game.Read(CONTENT).AsSpan((int)preload.FileOffset, Hex.PPTR_SIZE).ToArray(), Is.EqualTo(foreign));
        Assert.That(game.Converter().Verify().Count(EditState.Foreign), Is.EqualTo(1));
    }

    [Test]
    public void Apply_CompressedBundle_WritesAConvertedCopyAndLeavesTheStockFileAlone()
    {
        // Arrange
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Assert
        Assert.That(result.Plan.Bundles.Single().Mode, Is.EqualTo(BundleEditMode.Rewrite));
        Assert.That(result.CopiesWritten, Is.EqualTo(1));
        Assert.That(game.Read(CONTENT), Is.EqualTo(stock));

        var copyPath = game.Layout.Resolve(BundleConversionLayout.RelativeOutput(CONTENT));
        Assert.That(ShaderPointers(game, copyPath), Is.All.EqualTo((1, TestGame.TargetPathId)));
        var scanned = BundleScanner.Scan(game.FileSystem, copyPath, []);
        Assert.That(scanned.Layout!.IsUncompressed, Is.False);
        Assert.That(scanned.Files.Single().Externals, Is.EqualTo(new[] { ShaderBundleIds.SHARED_SHADERS_CAB }));

        var entry = game.Journal()!.Rewritten.Single();
        Assert.That(entry.StockSize, Is.EqualTo(stock.Length));
        Assert.That(entry.StockSha256, Is.EqualTo(Hex.Sha256(stock)));
        Assert.That(entry.OutputSha256, Is.EqualTo(Hex.Sha256(game.ReadCopy(CONTENT))));
        Assert.That(game.Converter().Verify().IsComplete, Is.True);
    }

    [Test]
    public void Apply_NoExternalForTheTarget_AppendsItInTheConvertedCopy()
    {
        // Arrange: uncompressed, but the file never referenced the shared shader bundle.
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.BUILTIN_SHADERS_CAB));

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Assert
        var plan = result.Plan.Bundles.Single();
        Assert.That(plan.Mode, Is.EqualTo(BundleEditMode.Rewrite));
        Assert.That(plan.AddedExternals[TestGame.CONTENT_CAB], Is.EqualTo(new[] { ShaderBundleIds.SHARED_SHADERS_CAB }));

        var copyPath = game.Layout.Resolve(BundleConversionLayout.RelativeOutput(CONTENT));
        var scanned = BundleScanner.Scan(game.FileSystem, copyPath, []);
        Assert.That(scanned.Files.Single().Externals, Is.EqualTo(new[] { ShaderBundleIds.BUILTIN_SHADERS_CAB, ShaderBundleIds.SHARED_SHADERS_CAB }));
        Assert.That(ShaderPointers(game, copyPath), Is.All.EqualTo((2, TestGame.TargetPathId)));
        Assert.That(scanned.Files.Single().Shaders[TestGame.SHADER_PATH_ID], Is.EqualTo(TestGame.EMBEDDED_SHADER));
    }

    [Test]
    public void Apply_ShaderListedInTwoReplacementBundles_PrefersTheOneTheFileAlreadyReferences()
    {
        // Arrange
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.BUILTIN_SHADERS_CAB));
        var manifest = TestGame.Manifest(
            (TestGame.EMBEDDED_SHADER, ShaderBundleIds.SHARED_SHADERS_CAB),
            (TestGame.EMBEDDED_SHADER, ShaderBundleIds.BUILTIN_SHADERS_CAB));

        // Act
        var result = game.Converter().Apply(manifest);

        // Assert
        var plan = result.Plan.Bundles.Single();
        Assert.That(plan.Mode, Is.EqualTo(BundleEditMode.InPlace));
        Assert.That(plan.Edits.Select(edit => edit.Target.Cab), Is.All.EqualTo(ShaderBundleIds.BUILTIN_SHADERS_CAB));
    }

    [Test]
    public void Apply_ShaderEmbeddedInAnotherBundle_IsResolvedThroughThatBundle()
    {
        // Arrange
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: true));
        game.Write(OTHER, TestGame.ReferencingBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB));

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Assert
        var other = result.Plan.Bundles.Single(bundle => bundle.File == OTHER);
        Assert.That(other.Mode, Is.EqualTo(BundleEditMode.InPlace));
        Assert.That(other.Edits.Select(edit => (edit.Kind, edit.ShaderName)), Is.EquivalentTo(new[]
        {
            (ShaderReferenceKind.Material, TestGame.EMBEDDED_SHADER),
            (ShaderReferenceKind.Preload, TestGame.EMBEDDED_SHADER),
        }));
        Assert.That(ShaderPointers(game, game.Layout.BundlePath(OTHER)), Is.All.EqualTo((2, TestGame.TargetPathId)));
    }

    [Test]
    public void Apply_ShaderMissingFromTheManifest_IsSkippedAndJournaled()
    {
        // Arrange
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);

        // Act
        var result = game.Converter().Apply(TestGame.Manifest("Some/Other Shader"));

        // Assert
        Assert.That(result.Plan.Bundles, Is.Empty);
        Assert.That(game.Read(CONTENT), Is.EqualTo(stock));
        var skipped = game.Journal()!.Skipped.Single();
        Assert.That((skipped.File, skipped.Shader, skipped.Reason), Is.EqualTo((CONTENT, TestGame.EMBEDDED_SHADER, "not in manifest")));
    }

    [Test]
    public void Apply_ShaderDroppedFromTheManifest_PutsEarlierEditsBackAndDeletesCopies()
    {
        // Arrange
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        var other = TestGame.ReferencingBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);
        game.Write(OTHER, other);
        game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Act
        var result = game.Converter().Apply(TestGame.Manifest("Some/Other Shader"));

        // Assert
        Assert.That(result.EditsReverted, Is.EqualTo(3));
        Assert.That(result.CopiesDeleted, Is.EqualTo(1));
        Assert.That(game.Read(CONTENT), Is.EqualTo(stock));
        Assert.That(game.CopyExists(OTHER), Is.False);
        var journal = game.Journal()!;
        Assert.That(journal.InPlace, Is.Empty);
        Assert.That(journal.Rewritten, Is.Empty);
    }

    [Test]
    public void Apply_ManifestMovesAShader_UpdatesEditsAppliedByAnEarlierRun()
    {
        // Arrange
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB, ShaderBundleIds.BUILTIN_SHADERS_CAB));
        game.Converter().Apply(TestGame.Manifest((TestGame.EMBEDDED_SHADER, ShaderBundleIds.SHARED_SHADERS_CAB)));

        // Act
        var result = game.Converter().Apply(TestGame.Manifest((TestGame.EMBEDDED_SHADER, ShaderBundleIds.BUILTIN_SHADERS_CAB)));

        // Assert
        Assert.That(result.EditsUpdated, Is.EqualTo(3));
        Assert.That(result.ForeignEdits, Is.Empty);
        Assert.That(ShaderPointers(game, game.Layout.BundlePath(CONTENT)), Is.All.EqualTo((2, TestGame.TargetPathId)));
        Assert.That(game.Converter().Verify().IsComplete, Is.True);
    }

    [Test]
    public void Apply_MonoBehaviourShaderField_IsRetargetedAndReportedWithItsScript()
    {
        // Arrange
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB));

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER), dryRun: true);

        // Assert
        var field = result.Plan.FieldReferences.Single();
        Assert.That(field.Kind, Is.EqualTo(ShaderReferenceKind.MonoBehaviour));
        Assert.That(field.Script, Is.EqualTo("Test.BlurThing [Assembly-CSharp]"));
        Assert.That(field.FieldPath, Is.EqualTo("blurShader"));
        Assert.That(field.Outcome, Is.EqualTo("retargeted"));
    }

    [Test]
    public void Apply_DryRun_WritesNothing()
    {
        // Arrange
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);
        game.Write(OTHER, TestGame.ReferencingBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB));

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER), dryRun: true);

        // Assert
        Assert.That(result.Plan.Bundles, Has.Count.EqualTo(2));
        Assert.That(game.Read(CONTENT), Is.EqualTo(stock));
        Assert.That(game.FileSystem.Directory.Exists(game.Layout.ConversionDirectory), Is.False);
    }

    [Test]
    public void Revert_AfterApply_RestoresStockAndRemovesEverythingTheConverterWrote()
    {
        // Arrange
        TestGame game = new();
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        var other = TestGame.ReferencingBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);
        game.Write(OTHER, other);
        game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Act
        var result = game.Converter().Revert();
        var again = game.Converter().Revert();

        // Assert
        Assert.That(result.EditsReverted, Is.EqualTo(3));
        Assert.That(result.CopiesDeleted, Is.EqualTo(1));
        Assert.That(result.JournalDeleted, Is.True);
        Assert.That(game.Read(CONTENT), Is.EqualTo(stock));
        Assert.That(game.Read(OTHER), Is.EqualTo(other));
        Assert.That(game.FileSystem.Directory.Exists(game.Layout.ConversionDirectory), Is.False);
        Assert.That(again.HadJournal, Is.False);
    }

    private static void Patch(TestGame game, string file, long offset, byte[] bytes)
    {
        var data = game.Read(file);
        Buffer.BlockCopy(bytes, 0, data, (int)offset, bytes.Length);
        game.Write(file, data);
    }

    private static int DifferingBytes(byte[] a, byte[] b) => a.Zip(b).Count(pair => pair.First != pair.Second);

    // Reads the bundle back with the scanner and lists every shader PPtr and preload entry that
    // pointed at the embedded shader before, as (file ID, path ID).
    private static IEnumerable<(int, long)> ShaderPointers(TestGame game, string path)
    {
        var scanned = BundleScanner.Scan(game.FileSystem, path, []);
        Assert.That(scanned.Error, Is.Null);
        foreach (var file in scanned.Files)
        {
            foreach (var field in file.ShaderFields)
            {
                yield return (field.FileId, field.PathId);
            }

            foreach (var preload in file.Preloads.Where(entry => entry.PathId != 2 && entry.PathId != TestGame.MATERIAL_PATH_ID && entry.PathId != TestGame.BEHAVIOUR_PATH_ID))
            {
                yield return (preload.FileId, preload.PathId);
            }
        }
    }
}
