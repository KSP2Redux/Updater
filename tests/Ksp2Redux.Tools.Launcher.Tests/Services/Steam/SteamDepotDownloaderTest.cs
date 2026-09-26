using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Moq;
using SteamKit2;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Steam;

public class SteamDepotDownloaderTest
{
    // The depots section Steam returned for app 954850 on 2026-09-23.
    private static KeyValue Ksp2DepotsSection()
    {
        var depots = new KeyValue("depots");
        depots.Children.Add(Depot("954851", "windows", "3982435242459808716", "33337259792"));
        depots.Children.Add(Depot("954852", "macos", "2894272240027278247", "0"));
        depots.Children.Add(Depot("954853", "linux", "5561556978087969695", "0"));
        depots.Children.Add(new KeyValue("branches"));
        return depots;
    }

    private static KeyValue Depot(string id, string? osList, string gid, string size)
    {
        var depot = new KeyValue(id);
        if (osList is not null)
        {
            var config = new KeyValue("config");
            config.Children.Add(new KeyValue("oslist", osList));
            depot.Children.Add(config);
        }

        var branch = new KeyValue("public");
        branch.Children.Add(new KeyValue("gid", gid));
        branch.Children.Add(new KeyValue("size", size));
        var manifests = new KeyValue("manifests");
        manifests.Children.Add(branch);
        depot.Children.Add(manifests);
        return depot;
    }

    [Test]
    public void SelectWindowsDepots_Ksp2_PicksOnlyTheWindowsDepot()
    {
        // Act
        var depots = SteamDepotDownloader.SelectWindowsDepots(Ksp2DepotsSection(), "public");

        // Assert
        Assert.That(depots, Is.EqualTo(new[] { new SteamDepot(954851, 3982435242459808716, 33337259792) }));
    }

    // Depots with no oslist ship to every platform, so a Windows install needs them too.
    [Test]
    public void SelectWindowsDepots_DepotWithoutOsList_IsIncluded()
    {
        // Arrange
        var section = new KeyValue("depots");
        section.Children.Add(Depot("100", null, "42", "10"));

        // Act
        var depots = SteamDepotDownloader.SelectWindowsDepots(section, "public");

        // Assert
        Assert.That(depots.Select(d => d.DepotId), Is.EqualTo(new uint[] { 100 }));
    }

    // Shared redistributables belong to another app and are installed by Steam itself, not by us.
    [Test]
    public void SelectWindowsDepots_DepotFromAnotherApp_IsSkipped()
    {
        // Arrange
        var section = Ksp2DepotsSection();
        var shared = Depot("228988", "windows", "1", "1");
        shared.Children.Add(new KeyValue("depotfromapp", "228980"));
        section.Children.Add(shared);

        // Act
        var depots = SteamDepotDownloader.SelectWindowsDepots(section, "public");

        // Assert
        Assert.That(depots.Select(d => d.DepotId), Is.EqualTo(new uint[] { 954851 }));
    }

    // Older product info stores the manifest id directly on the branch instead of under "gid".
    [Test]
    public void SelectWindowsDepots_LegacyManifestFormat_ReadsTheManifestId()
    {
        // Arrange
        var section = new KeyValue("depots");
        var depot = new KeyValue("200");
        var manifests = new KeyValue("manifests");
        manifests.Children.Add(new KeyValue("public", "777"));
        depot.Children.Add(manifests);
        section.Children.Add(depot);

        // Act
        var depots = SteamDepotDownloader.SelectWindowsDepots(section, "public");

        // Assert
        Assert.That(depots.Single().ManifestId, Is.EqualTo(777UL));
    }

    [Test]
    public void ResolveInside_WindowsSeparators_BecomeLocalPath()
    {
        // Arrange
        var downloader = Build(out var fileSystem);

        // Act
        var path = downloader.ResolveInside("/games/ksp2", @"KSP2_x64_Data\Plugins\steam_api64.dll");

        // Assert
        Assert.That(path, Is.EqualTo(fileSystem.Path.Combine("/games/ksp2", "KSP2_x64_Data", "Plugins", "steam_api64.dll")));
    }

    // Manifest file names come from the network.
    [Test]
    public void ResolveInside_PathEscapingTheInstall_Throws()
    {
        // Arrange
        var downloader = Build(out _);

        // Act and assert
        Assert.Throws<InvalidDataException>(() => downloader.ResolveInside("/games/ksp2", @"..\..\.ssh\authorized_keys"));
    }

    private static SteamDepotDownloader Build(out MockFileSystem fileSystem)
    {
        fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.MacOS));
        return new SteamDepotDownloader(new Mock<ISteamSessionService>().Object, fileSystem, new Mock<ILogService>().Object);
    }

    [TestCase(@"C:\Games", @"C:\Games\Kerbal Space Program 2")]
    [TestCase(@"C:\Games\", @"C:\Games\Kerbal Space Program 2")]
    [TestCase(@"C:\Games\Kerbal Space Program 2", @"C:\Games\Kerbal Space Program 2")]
    [TestCase(@"C:\Games\kerbal space program 2\", @"C:\Games\kerbal space program 2")]
    public void GameFolderIn_ChosenFolder_EndsInOneKsp2Folder(string chosen, string expected)
    {
        // Arrange
        var fileSystem = new Testably.Abstractions.Testing.MockFileSystem(o =>
            o.SimulatingOperatingSystem(Testably.Abstractions.Testing.SimulationMode.Windows));

        // Act
        var folder = SteamDepotDownloader.GameFolderIn(fileSystem, chosen);

        // Assert
        Assert.That(folder, Is.EqualTo(expected));
    }
}
