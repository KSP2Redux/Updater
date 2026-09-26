using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class LauncherParityCommandsTest
{
    [Test]
    public async Task Settings_Default_ReportsTheLauncherDefaults()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new LauncherSettingsCommand().RunWithContextAsync(harness.Context, new LauncherSettingsSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("patchSource").GetString(), Is.EqualTo("r2"));
        Assert.That(harness.Json.GetProperty("concurrentChunks").GetInt32(), Is.EqualTo(4));
        Assert.That(harness.Json.GetProperty("verboseLogging").GetBoolean(), Is.False);
    }

    [Test]
    public async Task SettingsSet_AllThree_AreSavedToTheLauncherConfig()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new LauncherSettingsSetCommand().RunWithContextAsync(harness.Context,
            new LauncherSettingsSetSettings { PatchSource = "GitHub", ConcurrentChunks = 8, VerboseLogging = "on" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        var saved = harness.FileSystem.File.ReadAllText(harness.Context.ConfigService.Config.StoragePath);
        Assert.That(harness.Context.ConfigService.Config.PatchDownloadSource, Is.EqualTo(PatchDownloadSource.GitHub));
        Assert.That(saved, Does.Contain("\"MaxConcurrentChunkDownloads\": 8"));
        Assert.That(saved, Does.Contain("\"VerboseLogging\": true"));
    }

    [TestCase("ftp", null, null)]
    [TestCase(null, 0, null)]
    [TestCase(null, 9, null)]
    [TestCase(null, null, "loud")]
    public async Task SettingsSet_InvalidValue_ChangesNothing(string? source, int? chunks, string? verbose)
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new LauncherSettingsSetCommand().RunWithContextAsync(harness.Context,
            new LauncherSettingsSetSettings { PatchSource = source, ConcurrentChunks = chunks, VerboseLogging = verbose });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Context.ConfigService.Config.PatchDownloadSource, Is.EqualTo(PatchDownloadSource.R2));
        Assert.That(harness.Context.ConfigService.Config.MaxConcurrentChunkDownloads, Is.EqualTo(4));
    }

    [Test]
    public async Task SettingsSet_NothingGiven_IsAUsageError()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new LauncherSettingsSetCommand().RunWithContextAsync(harness.Context, new LauncherSettingsSetSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
    }

    [Test]
    public async Task Open_InstallPrint_GivesTheGameFolderWithoutOpeningIt()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.Installs.AddInstall(harness.CreateGame(@"C:\Games\Kerbal Space Program 2"), "Testing");

        // Act
        var exit = await new OpenCommand().RunWithContextAsync(harness.Context, new OpenSettings { Folder = "install", ShouldPrint = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("path").GetString(), Is.EqualTo(@"C:\Games\Kerbal Space Program 2"));
        Assert.That(harness.Json.GetProperty("opened").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Open_GameDataBeforeTheFirstRun_SaysKsp2CreatesIt()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.GameDataFolder.Setup(g => g.Resolve(It.IsAny<Ksp2InstallEntry?>())).Returns(@"C:\Users\Player\AppData\LocalLow\Intercept Games\Kerbal Space Program 2");

        // Act
        var exit = await new OpenCommand().RunWithContextAsync(harness.Context, new OpenSettings { Folder = "game-data", ShouldPrint = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.PATH_NOT_FOUND));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("first time it runs"));
    }

    [Test]
    public async Task Open_UnknownFolder_IsAUsageError()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new OpenCommand().RunWithContextAsync(harness.Context, new OpenSettings { Folder = "desktop", ShouldPrint = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
    }

    [Test]
    public async Task News_Feed_ListsTheNewestPostsFirst()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.News.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items =
            [
                new FeedItem { Title = "Older", Link = "https://ksp2redux.org/blog/older", PublishingDate = new DateTime(2026, 9, 1) },
                new FeedItem { Title = "Undated", Link = "https://ksp2redux.org/blog/undated" },
                new FeedItem { Title = "Newest", Link = "https://ksp2redux.org/blog/newest", PublishingDate = new DateTime(2026, 9, 24) },
                new FeedItem { Title = "Middle", Link = "https://ksp2redux.org/blog/middle", PublishingDate = new DateTime(2026, 9, 15) },
            ],
        });

        // Act
        var exit = await new NewsCommand().RunWithContextAsync(harness.Context, new NewsSettings { Take = 2 });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        var titles = harness.Json.EnumerateArray().Select(post => post.GetProperty("title").GetString()).ToList();
        Assert.That(titles, Is.EqualTo(new[] { "Newest", "Middle" }));
    }

    [Test]
    public async Task News_FeedUnreachable_FailsWithAMessage()
    {
        // Arrange
        var harness = new CliCommandHarness();
        harness.News.Setup(n => n.GetSyndicationFeed()).ThrowsAsync(new TimeoutException("Timed out."));

        // Act
        var exit = await new NewsCommand().RunWithContextAsync(harness.Context, new NewsSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.FEED_UNAVAILABLE));
    }
}
