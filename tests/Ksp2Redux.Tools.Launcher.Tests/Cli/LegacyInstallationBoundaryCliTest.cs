using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Moq;
using System.IO.Compression;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

/// <summary>
/// Exercises the CLI command paths that previously mutated or launched pending installations.
/// </summary>
public class LegacyInstallationBoundaryCliTest
{
    private const string INSTALL_ROOT = @"C:\Games\Boundary";

    /// <summary>
    /// All CLI launch paths reject pending state before boot writes or Wine operations.
    /// </summary>
    /// <param name="macOS">True for the Wine path.</param>
    /// <param name="steam">True for the Steam launch setting.</param>
    /// <param name="marker">The marker bytes, whose content grants no authority.</param>
    /// <returns>The completed test.</returns>
    [TestCase(false, false, "")]
    [TestCase(false, true, "x")]
    [TestCase(true, true, "{corrupt")]
    public async Task PendingTransactionBlocksCliLaunchBeforeBootWrite(bool macOS, bool steam, string marker)
    {
        var harness = new CliCommandHarness();
        string exe = harness.CreateGame(INSTALL_ROOT);
        string boot = harness.FileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64_Data", "boot.config");
        string pending = harness.FileSystem.Path.Combine(INSTALL_ROOT, ".redux-transaction.active");
        harness.FileSystem.File.WriteAllText(boot, "hdr-display-enabled=0\r\n");
        harness.FileSystem.File.WriteAllText(pending, marker);
        var entry = harness.Installs.AddInstall(exe);
        entry.DisableGraphicsJobs = true;
        entry.LaunchThroughSteam = steam;
        harness.OperatingSystem.Setup(service => service.IsMacOS()).Returns(macOS);
        byte[] before = harness.FileSystem.File.ReadAllBytes(boot);

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context, new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("unfinished installation transaction"));
        Assert.That(harness.FileSystem.File.ReadAllBytes(boot), Is.EqualTo(before));
        Assert.That(harness.FileSystem.File.ReadAllText(pending), Is.EqualTo(marker));
        harness.Wine.Verify(service => service.Detect(), Times.Never);
    }

    /// <summary>
    /// An explicitly selected pending install cannot be bypassed by a healthy active profile.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Test]
    public async Task CliLaunchGuardsTheSelectedInstallationIdentity()
    {
        var harness = new CliCommandHarness();
        harness.Installs.AddInstall(harness.CreateGame(@"C:\Games\Active"));
        harness.Installs.AddInstall(harness.CreateGame(INSTALL_ROOT), "Pending");
        harness.FileSystem.File.WriteAllText(harness.FileSystem.Path.Combine(INSTALL_ROOT, ".redux-transaction.active"), "");

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true, Install = "Pending" });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain(INSTALL_ROOT));
    }

    /// <summary>
    /// Direct CLI bundle reversal retains the converted bytes and journal when a transaction is pending.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Test]
    public async Task DirectCliBundleRevertPreservesPendingJournalAndBytes()
    {
        var harness = new CliCommandHarness();
        harness.CreateGame(INSTALL_ROOT);
        var layout = new BundleConversionLayout(harness.FileSystem, INSTALL_ROOT);
        harness.FileSystem.Directory.CreateDirectory(layout.BundleDirectory);
        harness.FileSystem.File.WriteAllBytes(layout.BundlePath("a.bundle"), [0, 0, 0xAA, 0xBB, 0]);
        new JournalStore(harness.FileSystem, layout.JournalPath).Write(new ConversionJournal
        {
            InPlace = [new JournalInPlaceFile { File = "a.bundle", Size = 5,
                Edits = [new JournalEdit { Offset = 2, Old = "0102", New = "aabb" }] }]
        });
        byte[] journal = harness.FileSystem.File.ReadAllBytes(layout.JournalPath);
        harness.FileSystem.File.WriteAllText(harness.FileSystem.Path.Combine(INSTALL_ROOT, ".redux-transaction.active"), "");

        int exit = await new BundlesRevertCommand().RunWithContextAsync(harness.Context,
            new BundlesRevertSettings { Game = INSTALL_ROOT });

        Assert.That(exit, Is.EqualTo(ExitCode.BUNDLE_CONVERSION_FAILED));
        Assert.That(harness.FileSystem.File.ReadAllBytes(layout.BundlePath("a.bundle")), Is.EqualTo(new byte[] { 0, 0, 0xAA, 0xBB, 0 }));
        Assert.That(harness.FileSystem.File.ReadAllBytes(layout.JournalPath), Is.EqualTo(journal));
    }

    /// <summary>
    /// Completed full archives reach the CLI launch route while managed boot bytes stay intact.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Test]
    public async Task CompletedFullArchiveReachesCliWineLaunchWithoutBootMutation()
    {
        var harness = new CliCommandHarness();
        string exe = harness.CreateGame(INSTALL_ROOT);
        string archivePath = harness.FileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip");
        using (Stream input = harness.FileSystem.File.Create(archivePath))
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry(StockBaselineManifest.MANIFEST_PATH).Open());
            writer.Write("claim");
        }
        string boot = harness.FileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64_Data", "boot.config");
        harness.FileSystem.File.WriteAllText(boot, "hdr-display-enabled=0\r\n");
        var entry = harness.Installs.AddInstall(exe);
        entry.DisableGraphicsJobs = true;
        harness.OperatingSystem.Setup(service => service.IsMacOS()).Returns(true);
        byte[] before = harness.FileSystem.File.ReadAllBytes(boot);

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context, new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("none was found"));
        harness.Wine.Verify(service => service.Detect(), Times.Once);
        Assert.That(harness.FileSystem.File.ReadAllBytes(boot), Is.EqualTo(before));
    }
}
