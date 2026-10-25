namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Finding in-place edits no journal records, which no revert can put back.
/// </summary>
public class UnrecordedEditsTest
{
    private const string CONTENT = "content.bundle";
    private const string SHADERS = "sharedshaders_assets_all.bundle";
    private const long STOCK_SHADER_PATH_ID = 77;

    [Test]
    public void FindUnrecordedEdits_JournaledConversion_FindsNothing()
    {
        // Arrange
        var game = ConvertedGame();

        // Act
        var unrecorded = game.Converter().FindUnrecordedEdits();

        // Assert
        Assert.That(unrecorded, Is.Empty);
    }

    [Test]
    public void FindUnrecordedEdits_ConversionWhoseJournalWasLost_FindsTheEditedBundle()
    {
        // Arrange
        var game = ConvertedGame();
        game.FileSystem.File.Delete(game.Layout.JournalPath);

        // Act
        var unrecorded = game.Converter().FindUnrecordedEdits();

        // Assert
        Assert.That(unrecorded, Is.EqualTo(new[] { CONTENT }));
    }

    [Test]
    public void FindUnrecordedEdits_StockMaterialOnAStockShader_FindsNothing()
    {
        // Arrange
        TestGame game = new();
        game.Write(SHADERS, StockShaderBundle());
        var file = new SerializedFileBuilder(TestGame.CONTENT_CAB)
            .External(ShaderBundleIds.SHARED_SHADERS_CAB)
            .Object(TestGame.MATERIAL_PATH_ID, SyntheticBundles.MATERIAL, SyntheticBundles.Material("StockMaterial", 1, STOCK_SHADER_PATH_ID));
        game.Write(CONTENT, SyntheticBundles.Bundle(false, (TestGame.CONTENT_CAB, file.Build(), true)));

        // Act
        var unrecorded = game.Converter().FindUnrecordedEdits();

        // Assert
        Assert.That(unrecorded, Is.Empty);
    }

    [Test]
    public void Apply_ConversionWhoseJournalWasLost_ReportsTheBundleItCannotJournal()
    {
        // Arrange: the edits now look like materials already on a replacement bundle, so nothing re-records them.
        var game = ConvertedGame();
        game.FileSystem.File.Delete(game.Layout.JournalPath);

        // Act
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));

        // Assert
        Assert.That(result.Plan.UnrecordedEdits, Is.EqualTo(new[] { CONTENT }));
        Assert.That(game.Journal()!.InPlace, Is.Empty);
    }

    private static TestGame ConvertedGame()
    {
        TestGame game = new();
        game.Write(SHADERS, StockShaderBundle());
        game.Write(CONTENT, TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB));
        var result = game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));
        Assert.That(result.EditsWritten, Is.EqualTo(3), "the content bundle should be converted in place");
        return game;
    }

    // The stock shared shader bundle, which never holds the synthetic path IDs conversions retarget to.
    private static byte[] StockShaderBundle()
    {
        var file = new SerializedFileBuilder(ShaderBundleIds.SHARED_SHADERS_CAB)
            .Object(STOCK_SHADER_PATH_ID, SyntheticBundles.SHADER, SyntheticBundles.Shader("Stock/Shader"));
        return SyntheticBundles.Bundle(false, (ShaderBundleIds.SHARED_SHADERS_CAB, file.Build(), true));
    }
}
