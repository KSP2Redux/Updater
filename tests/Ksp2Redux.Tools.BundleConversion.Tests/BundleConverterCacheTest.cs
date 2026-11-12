namespace Ksp2Redux.Tools.BundleConversion.Tests;

public class BundleConverterCacheTest
{
    private const string CACHE = @"C:\Project\Library\ReduxBundleConversion";
    private const string CONTENT = "content.bundle";
    private const string OTHER = "other.bundle";

    [Test]
    public void Apply_IntoACache_LeavesTheInstallUntouchedAndJournalsTheInPlaceEdits()
    {
        // Arrange
        TestGame game = new();
        var stockContent = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        var stockOther = TestGame.ReferencingBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stockContent);
        game.Write(OTHER, stockOther);
        var converter = game.CacheConverter(CACHE);

        // Act
        var result = converter.Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Assert
        Assert.That(game.Read(CONTENT), Is.EqualTo(stockContent));
        Assert.That(game.Read(OTHER), Is.EqualTo(stockOther));
        Assert.That(game.FileSystem.Directory.Exists(game.FileSystem.Path.Combine(TestGame.ROOT, "Redux")), Is.False);
        Assert.That(result.EditsWritten, Is.Zero);
        Assert.That(result.EditsDeferred, Is.EqualTo(3));
        Assert.That(result.CopiesWritten, Is.EqualTo(1));

        Assert.That(converter.Layout.JournalPath, Does.StartWith(CACHE));
        var journal = new JournalStore(game.FileSystem, converter.Layout.JournalPath).Read()!;
        Assert.That(journal.GameDirectory, Is.EqualTo(TestGame.ROOT));
        Assert.That(journal.InPlace.Single().File, Is.EqualTo(CONTENT));
        Assert.That(journal.InPlace.Single().Edits, Has.Count.EqualTo(3));
        var copy = converter.Layout.Resolve(journal.Rewritten.Single().Output);
        Assert.That(copy, Does.StartWith(CACHE));
        Assert.That(game.FileSystem.File.Exists(copy), Is.True);
    }

    [Test]
    public void Apply_IntoACacheOverAnInstallTheLauncherConverted_PlansFromTheStockBytes()
    {
        // Arrange: the launcher converted the install in place, so the bundle holds converted bytes.
        TestGame game = new();
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB));
        var manifest = TestGame.Manifest(TestGame.EMBEDDED_SHADER);
        game.Converter().Apply(manifest);
        var converted = game.Read(CONTENT);
        var installEdits = game.Journal()!.InPlace.Single().Edits;
        var converter = game.CacheConverter(CACHE);

        // Act
        converter.Apply(manifest);

        // Assert
        var cacheEdits = new JournalStore(game.FileSystem, converter.Layout.JournalPath).Read()!.InPlace.Single().Edits;
        Assert.That(cacheEdits.Select(edit => (edit.Offset, edit.Old, edit.New)), Is.EquivalentTo(installEdits.Select(edit => (edit.Offset, edit.Old, edit.New))));
        Assert.That(game.Read(CONTENT), Is.EqualTo(converted));
    }

    [Test]
    public void Revert_OfACache_DeletesItsCopiesAndJournalAndLeavesTheInstall()
    {
        // Arrange
        TestGame game = new();
        var stockContent = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        var stockOther = TestGame.ReferencingBundle(compress: true, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stockContent);
        game.Write(OTHER, stockOther);
        var converter = game.CacheConverter(CACHE);
        converter.Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Act
        var result = converter.Revert();

        // Assert
        Assert.That(result.JournalDeleted, Is.True);
        Assert.That(result.CopiesDeleted, Is.EqualTo(1));
        Assert.That(result.EditsReverted, Is.Zero);
        Assert.That(game.FileSystem.File.Exists(converter.Layout.JournalPath), Is.False);
        Assert.That(game.Read(CONTENT), Is.EqualTo(stockContent));
        Assert.That(game.Read(OTHER), Is.EqualTo(stockOther));
    }
}