using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class BundlesCommandsTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";

    [Test]
    public async Task Convert_WithoutGame_IsAUsageError()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new BundlesConvertCommand().RunWithContextAsync(harness.Context, new BundlesConvertSettings { ShaderManifest = "manifest.json" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Convert_MissingManifest_ReportsPathNotFoundAndChangesNothing()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.CreateGame(GAME);

        // Act
        var exit = await new BundlesConvertCommand().RunWithContextAsync(harness.Context, new BundlesConvertSettings
        {
            Game = GAME,
            ShaderManifest = @"C:\nowhere\manifest.json",
        });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.PATH_NOT_FOUND));
        Assert.That(harness.FileSystem.Directory.Exists(harness.FileSystem.Path.Combine(GAME, "Redux")), Is.False);
    }

    [Test]
    public async Task Verify_NoJournal_ReportsNotConverted()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.CreateGame(GAME);

        // Act
        var exit = await new BundlesVerifyCommand().RunWithContextAsync(harness.Context, new BundlesVerifySettings { Game = GAME });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.BUNDLES_NOT_CONVERTED));
        Assert.That(harness.Json.GetProperty("hasJournal").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Revert_JournalWithAnAppliedEdit_PutsTheStockBytesBack()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.CreateGame(GAME);
        var layout = new BundleConversionLayout(harness.FileSystem, GAME);
        harness.FileSystem.Directory.CreateDirectory(layout.BundleDirectory);
        harness.FileSystem.File.WriteAllBytes(layout.BundlePath("a.bundle"), [0, 0, 0xAA, 0xBB, 0]);
        new JournalStore(harness.FileSystem, layout.JournalPath).Write(new ConversionJournal
        {
            InPlace = [new JournalInPlaceFile { File = "a.bundle", Size = 5, Edits = [new JournalEdit { Offset = 2, Old = "0102", New = "aabb" }] }],
        });

        // Act
        var exit = await new BundlesRevertCommand().RunWithContextAsync(harness.Context, new BundlesRevertSettings { Game = GAME });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("editsReverted").GetInt32(), Is.EqualTo(1));
        Assert.That(harness.FileSystem.File.ReadAllBytes(layout.BundlePath("a.bundle")), Is.EqualTo(new byte[] { 0, 0, 1, 2, 0 }));
        Assert.That(harness.FileSystem.File.Exists(layout.JournalPath), Is.False);
    }
}
