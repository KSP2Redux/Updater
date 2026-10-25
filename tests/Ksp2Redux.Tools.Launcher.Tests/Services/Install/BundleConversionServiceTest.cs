using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.BundleConversion.Tests;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// A Redux build from before the URP port draws converted bundles magenta, so installing one over a converted
/// install has to put the stock bundles back.
/// </summary>
public class BundleConversionServiceTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string BundleName = "plain.bundle";
    private const long EditOffset = 16;

    private static readonly byte[] StockBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly byte[] ConvertedBytes = [21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32];

    [Test]
    public void ConvertForABuildWithoutShaderBundles_PutsTheConvertedBundlesBackToStock()
    {
        var (fs, bundlePath, journalPath) = MakeConvertedInstall();

        // The installed build ships no Redux/ShaderBundles manifest.
        new BundleConversionService(fs).Convert(InstallDir, _ => { }, CancellationToken.None);

        var expected = new byte[64];
        StockBytes.CopyTo(expected, EditOffset);
        Assert.That(fs.File.ReadAllBytes(bundlePath), Is.EqualTo(expected));
        Assert.That(fs.File.Exists(journalPath), Is.False);
    }

    [Test]
    public void RevertingABundleAnotherProgramHoldsOpenFailsAndKeepsTheJournal()
    {
        var (fs, bundlePath, journalPath) = MakeConvertedInstall();
        using var editor = fs.File.Open(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // An uninstall that carried on would delete the journal, and the bundle would stay converted for good.
        Assert.Throws<IOException>(() => new BundleConversionService(fs).Revert(InstallDir, _ => { }, CancellationToken.None));

        Assert.That(fs.File.Exists(journalPath), Is.True);
    }

    [Test]
    public void RevertingAnInstallWhoseJournalWasLost_PutsSteamsCopyOfTheConvertedBundleBack()
    {
        // Arrange
        var (game, stock) = GameWithUnrecordedEdits();
        var downloader = StockDownloader(game, stock, out var restored);

        // Act
        new BundleConversionService(game.FileSystem, SignedIn().Object, downloader.Object).Revert(TestGame.ROOT, _ => { }, CancellationToken.None);

        // Assert
        Assert.That(restored, Is.EqualTo(new[] { $"{BundleConversionLayout.BUNDLE_ROOT}/{CONTENT}" }));
        Assert.That(game.Read(CONTENT), Is.EqualTo(stock));
        Assert.That(game.Converter().FindUnrecordedEdits(), Is.Empty);
    }

    [Test]
    public void RevertingAnInstallWhoseJournalWasLost_WithoutSteam_FailsAndLeavesTheBundle()
    {
        // Arrange
        var (game, _) = GameWithUnrecordedEdits();
        var converted = game.Read(CONTENT);
        var session = new Mock<ISteamSessionService>();
        session.SetupGet(steam => steam.HasSavedLogin).Returns(false);

        // Act / Assert: carrying on would hand a build without the URP shaders magenta bundles.
        Assert.Throws<IOException>(() => new BundleConversionService(game.FileSystem, session.Object, new Mock<ISteamDepotDownloader>().Object)
            .Revert(TestGame.ROOT, _ => { }, CancellationToken.None));
        Assert.That(game.Read(CONTENT), Is.EqualTo(converted));
    }

    [Test]
    public void ConvertingAnInstallWhoseJournalWasLost_RestoresFromSteamAndJournalsTheEdits()
    {
        // Arrange
        var (game, stock) = GameWithUnrecordedEdits();
        WriteManifest(game);
        var downloader = StockDownloader(game, stock, out _);

        // Act
        new BundleConversionService(game.FileSystem, SignedIn().Object, downloader.Object).Convert(TestGame.ROOT, _ => { }, CancellationToken.None);

        // Assert: converted again, and this time a revert can find every edit.
        Assert.That(game.Journal()!.InPlace.Select(entry => entry.File), Is.EqualTo(new[] { CONTENT }));
        Assert.That(game.Converter().FindUnrecordedEdits(), Is.Empty);
    }

    [Test]
    public void ConvertingAnInstallWhoseJournalWasLost_WithoutSteam_StillConverts()
    {
        // Arrange: the URP build draws these bundles fine, so missing Steam must not stop its install.
        var (game, _) = GameWithUnrecordedEdits();
        WriteManifest(game);
        var session = new Mock<ISteamSessionService>();
        session.SetupGet(steam => steam.HasSavedLogin).Returns(false);
        List<string> log = [];

        // Act
        new BundleConversionService(game.FileSystem, session.Object, new Mock<ISteamDepotDownloader>().Object)
            .Convert(TestGame.ROOT, log.Add, CancellationToken.None);

        // Assert
        Assert.That(log, Has.Some.StartsWith("Warning:"));
        Assert.That(game.FileSystem.File.Exists(game.Layout.JournalPath), Is.True);
    }

    private const string CONTENT = "content.bundle";
    private const string SHADERS = "sharedshaders_assets_all.bundle";

    // A content bundle converted in place, with the journal that recorded it gone.
    private static (TestGame Game, byte[] Stock) GameWithUnrecordedEdits()
    {
        TestGame game = new();
        var shaders = new SerializedFileBuilder(ShaderBundleIds.SHARED_SHADERS_CAB)
            .Object(77, SyntheticBundles.SHADER, SyntheticBundles.Shader("Stock/Shader"));
        game.Write(SHADERS, SyntheticBundles.Bundle(false, (ShaderBundleIds.SHARED_SHADERS_CAB, shaders.Build(), true)));
        var stock = TestGame.ContentBundle(compress: false, ShaderBundleIds.SHARED_SHADERS_CAB);
        game.Write(CONTENT, stock);
        game.Converter().Apply(TestGame.Manifest(TestGame.EMBEDDED_SHADER));
        game.FileSystem.File.Delete(game.Layout.JournalPath);
        Assert.That(game.Converter().FindUnrecordedEdits(), Is.EqualTo(new[] { CONTENT }), "the setup should leave unrecorded edits");
        return (game, stock);
    }

    private static void WriteManifest(TestGame game)
    {
        var path = game.FileSystem.Path.Combine(TestGame.ROOT, BundleConversionService.SHADER_MANIFEST.Replace('/', '\\'));
        game.FileSystem.Directory.CreateDirectory(game.FileSystem.Path.GetDirectoryName(path)!);
        var manifest = TestGame.Manifest(TestGame.EMBEDDED_SHADER).Manifest;
        game.FileSystem.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest));
    }

    private static Mock<ISteamSessionService> SignedIn()
    {
        var session = new Mock<ISteamSessionService>();
        session.SetupGet(steam => steam.HasSavedLogin).Returns(true);
        session.SetupGet(steam => steam.IsSignedIn).Returns(true);
        return session;
    }

    // Steam's copy of a file is the stock bundle the test started from.
    private static Mock<ISteamDepotDownloader> StockDownloader(TestGame game, byte[] stock, out List<string> restored)
    {
        List<string> requested = [];
        var downloader = new Mock<ISteamDepotDownloader>();
        downloader
            .Setup(steam => steam.RestoreFilesAsync(It.IsAny<uint>(), TestGame.ROOT, It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<uint, string, IReadOnlyCollection<string>, IProgress<SteamDownloadProgress>, CancellationToken>((_, _, paths, _, _) =>
            {
                requested.AddRange(paths);
                game.Write(CONTENT, stock);
            })
            .Returns(Task.CompletedTask);
        restored = requested;
        return downloader;
    }

    private static (MockFileSystem Fs, string BundlePath, string JournalPath) MakeConvertedInstall()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var bundleDirectory = fs.Path.Combine(InstallDir, BundleConversionLayout.BUNDLE_ROOT.Replace('/', '\\'));
        fs.Directory.CreateDirectory(bundleDirectory);
        var bundle = new byte[64];
        ConvertedBytes.CopyTo(bundle, EditOffset);
        var bundlePath = fs.Path.Combine(bundleDirectory, BundleName);
        fs.File.WriteAllBytes(bundlePath, bundle);

        var journalPath = fs.Path.Combine(InstallDir, BundleConversionLayout.CONVERSION_ROOT.Replace('/', '\\'),
            BundleConversionLayout.JOURNAL_FILE);
        var journal = new ConversionJournal();
        journal.InPlace.Add(new JournalInPlaceFile
        {
            File = BundleName,
            Size = bundle.Length,
            Edits =
            [
                new JournalEdit
                {
                    Offset = EditOffset,
                    Old = Convert.ToHexString(StockBytes).ToLowerInvariant(),
                    New = Convert.ToHexString(ConvertedBytes).ToLowerInvariant(),
                },
            ],
        });
        new JournalStore(fs, journalPath).Write(journal);
        return (fs, bundlePath, journalPath);
    }
}