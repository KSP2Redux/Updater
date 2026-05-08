using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test.HeadlessTests;

public class DownloadTest
{
    private const string DefaultChannel = "beta";
    private const string OtherChannel = "stable";
    
    [AvaloniaTest]
    public async Task Download_SteamStockToReduxDefaultChannel1Rollup_DownloadsCorrectVersion()
    {
        // Arrange
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed{ Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        using SHA256 sha256 = SHA256.Create();
        
        // Arrange Prepatch
        MemoryStream prepatchStream = new([0x00, 0x01, 0x02]);
        TestAppBuilder.AssemblyService.Setup(a =>
                a.GetManifestResourceStream("Ksp2Redux.Tools.Launcher.Prepatches.steam-prepatch.patch"))
            .Returns(prepatchStream);
        
        MockFileData originalPrepatchFileToPatch = new("Prepatch - fileToPatch - OldContent");
        MockFileData prepatchFileToRemove = new("Prepatch - fileToRemove - Content");
        
        TestAppBuilder.FileSystem.AddFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToPatch.file",
            originalPrepatchFileToPatch);
        TestAppBuilder.FileSystem.AddFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToRemove.file",
            prepatchFileToRemove);
        
        MockFileData prepatchFileToAdd = new("Prepatch - fileToAdd - Content");
        string prepatchFileToPatchContent = "Prepatch - fileToPatch - Content";
        MockFileData prepatchFileToPatch = new(TestHelpers.GetDiff(originalPrepatchFileToPatch.Contents, prepatchFileToPatchContent));
        
        string prepatchManifest = $$"""
          {
            "operations": [
              {
                "fileName": "prepatchFileToPatch.file",
                "action": 0,
                "originalHash": "{{Convert.ToBase64String(sha256.ComputeHash(originalPrepatchFileToPatch.Contents))}}",
                "finalHash": "{{Convert.ToBase64String(sha256.ComputeHash(Encoding.ASCII.GetBytes(prepatchFileToPatchContent)))}}"
              },
              {
                "fileName": "prepatchFileToAdd.file",
                "action": 1,
                "originalHash": null,
                "finalHash": "{{Convert.ToBase64String(sha256.ComputeHash(prepatchFileToAdd.Contents))}}"
              },
              {
                "fileName": "prepatchFileToRemove.file",
                "action": 2,
                "originalHash": null,
                "finalHash": null
              }
            ]
          }
          """;
        Mock<IZipArchive> prepatchArchive = new();
        Mock<IZipArchiveEntry> prepatchManifestEntry = new();
        MemoryStream prepatchManifestEntryStream = new(Encoding.ASCII.GetBytes(prepatchManifest));
        
        prepatchArchive.Setup(p => p.GetEntry("manifest.json")) .Returns(prepatchManifestEntry.Object);
        prepatchManifestEntry.Setup(e => e.Open()).Returns(prepatchManifestEntryStream);
        
        Mock<IZipArchiveEntry> prepatchFileToAddEntry = new();
        prepatchArchive.Setup(p => p.GetEntry("prepatchFileToAdd.file")).Returns(prepatchFileToAddEntry.Object);
        prepatchFileToAddEntry.Setup(e => e.ExtractToFile(TestAppBuilder.FileSystem, It.IsAny<string>()))
            .Callback((IFileSystem f, string destination) =>
            {
                TestAppBuilder.FileSystem.AddFile(destination, prepatchFileToAdd);
            });
        
        Mock<IZipArchiveEntry> prepatchFileToPatchEntry = new();
        prepatchArchive.Setup(p => p.GetEntry("prepatchFileToPatch.file.bsdiff")).Returns(prepatchFileToPatchEntry.Object);
        prepatchFileToPatchEntry.Setup(e => e.ExtractToFile(TestAppBuilder.FileSystem, It.IsAny<string>()))
            .Callback((IFileSystem f, string destination) =>
            {
                TestAppBuilder.FileSystem.AddFile(destination, prepatchFileToPatch);
            });
        
        TestAppBuilder.ZipFileService.Setup(z => z.OpenRead(It.Is<string>(s => s.Contains("temp"))))
            .Returns((string path) =>
            {
                if (TestAppBuilder.FileSystem.FileExists(path) == false)
                    Assert.Fail($"Tried to open a zip file at {path}, but this file was never added in the mock file system");
                byte[] fileBytes = TestAppBuilder.FileSystem.File.ReadAllBytes(path);
                byte[] streamBytes = prepatchStream.ToArray();
                if (fileBytes.SequenceEqual(streamBytes) == false)
                    Assert.Fail($"Tried to open a zip file at {path}, but the content of this file doesn't match the prepatch content that was set up in the test:\n" +
                                $"\tExpected: {Convert.ToBase64String(streamBytes)}\n" +
                                $"\tActual: {Convert.ToBase64String(fileBytes)}");
                return prepatchArchive.Object;
            });
        
        // Arrange Patch1
        byte[] patch1RollupZipBytes = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A];

        ManifestReleasesFeed.Patch patch1Rollup = new()
        {
            checksum_sha256 = Convert.ToBase64String(sha256.ComputeHash(patch1RollupZipBytes)),
            releasedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            requires = new() { version = null },
            size = 10,
            url = "https://github.com/patch1Rollup.patch",
            version = "0.2.3.1.1234",
        };
        
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(
                It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel))))
            .ReturnsAsync(new ManifestReleasesFeed.Manifest
            {
                channel = DefaultChannel,
                generatedAt = new DateTime(2020, 1, 4),
                patches = [patch1Rollup],
                schemaVersion = 1
            });
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(
                It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel) == false)))
            .ReturnsAsync((FeedInfo f) => new ManifestReleasesFeed.Manifest
            {
                channel = f.Filename.Split('-', '.')[1],
                generatedAt = new DateTime(2020, 1, 4),
                patches = [],
                schemaVersion = 1
            });
        
        // Arrange Patch download
        HttpResponseMessage downloadResponse = new()
        {
            Content = new ByteArrayContent(patch1RollupZipBytes)
            {
                Headers = { ContentLength = patch1RollupZipBytes.Length },
            },
            StatusCode = HttpStatusCode.OK
        };

        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.DownloadPatchAsync(
                It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel)), patch1Rollup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadResponse);

        // Arrange Patch manifest and files
        MockFileData originalFileToPatch1 = new("fileToPatch1 - OldContent");
        MockFileData originalFileToPatch2 = new("fileToPatch2 - OldContent");

        TestAppBuilder.FileSystem.AddFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\fileToPatch1.file",
            originalFileToPatch1);
        TestAppBuilder.FileSystem.AddFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\Folder\fileToPatch2.file",
            originalFileToPatch2);
        
        MockFileData fileToAdd1 = new("fileToAdd1 - Content");
        string fileToPatch1Content = "fileToPatch1 - Content";
        MockFileData fileToPatch1 = new(TestHelpers.GetDiff(originalFileToPatch1.Contents, fileToPatch1Content));
        MockFileData fileToAdd2 = new("fileToAdd2 - Content");
        string fileToPatch2Content = "fileToPatch2 - Content";
        MockFileData fileToPatch2 = new(TestHelpers.GetDiff(originalFileToPatch2.Contents, fileToPatch2Content));
        
        string patchManifest = $$"""
            {
              "operations": [
                {
                  "fileName": "fileToAdd1.file",
                  "action": 1,
                  "originalHash": null,
                  "finalHash": "{{Convert.ToBase64String(sha256.ComputeHash(fileToAdd1.Contents))}}"
                },
                {
                  "fileName": "fileToPatch1.file",
                  "action": 0,
                  "originalHash": "{{Convert.ToBase64String(sha256.ComputeHash(originalFileToPatch1.Contents))}}",
                  "finalHash": "{{Convert.ToBase64String(sha256.ComputeHash(Encoding.ASCII.GetBytes(fileToPatch1Content)))}}"
                },
                {
                  "fileName": "Folder\\fileToAdd2.file",
                  "action": 1,
                  "originalHash": null,
                  "finalHash": "{{Convert.ToBase64String(sha256.ComputeHash(fileToAdd2.Contents))}}"
                },
                {
                  "fileName": "Folder\\fileToPatch2.file",
                  "action": 0,
                  "originalHash": "{{Convert.ToBase64String(sha256.ComputeHash(originalFileToPatch2.Contents))}}",
                  "finalHash": "{{Convert.ToBase64String(sha256.ComputeHash(Encoding.ASCII.GetBytes(fileToPatch2Content)))}}"
                }
              ]
            }                                   
            """;
        Mock<IZipArchive> zippedPatch = new();
        Mock<IZipArchiveEntry> zipManifestEntry = new();
        MemoryStream manifestEntryStream = new(Encoding.ASCII.GetBytes(patchManifest));
        
        zippedPatch.Setup(u => u.GetEntry("manifest.json")).Returns(zipManifestEntry.Object);
        zipManifestEntry.Setup(z => z.Open()).Returns(manifestEntryStream);

        Mock<IZipArchiveEntry> zipFileToAdd1Entry = new();
        zippedPatch.Setup(u => u.GetEntry("fileToAdd1.file")).Returns(zipFileToAdd1Entry.Object);
        zipFileToAdd1Entry.Setup(e => e.ExtractToFile(TestAppBuilder.FileSystem, It.IsAny<string>()))
            .Callback((IFileSystem f, string destination) =>
            {
                TestAppBuilder.FileSystem.AddFile(destination, fileToAdd1);
            });
        Mock<IZipArchiveEntry> zipFileToPatch1Entry = new();
        zippedPatch.Setup(u => u.GetEntry("fileToPatch1.file.bsdiff")).Returns(zipFileToPatch1Entry.Object);
        zipFileToPatch1Entry.Setup(e => e.ExtractToFile(TestAppBuilder.FileSystem, It.IsAny<string>()))
            .Callback((IFileSystem f, string destination) =>
            {
                TestAppBuilder.FileSystem.AddFile(destination, fileToPatch1);
            });
        Mock<IZipArchiveEntry> zipFileToAdd2Entry = new();
        zippedPatch.Setup(u => u.GetEntry(@"Folder\fileToAdd2.file")).Returns(zipFileToAdd2Entry.Object);
        zipFileToAdd2Entry.Setup(e => e.ExtractToFile(TestAppBuilder.FileSystem, It.IsAny<string>()))
            .Callback((IFileSystem f, string destination) =>
            {
                TestAppBuilder.FileSystem.AddFile(destination, fileToAdd2);
            });
        Mock<IZipArchiveEntry> zipFileToPatch2Entry = new();
        zippedPatch.Setup(u => u.GetEntry(@"Folder\fileToPatch2.file.bsdiff")).Returns(zipFileToPatch2Entry.Object);
        zipFileToPatch2Entry.Setup(e => e.ExtractToFile(TestAppBuilder.FileSystem, It.IsAny<string>()))
            .Callback((IFileSystem f, string destination) =>
            {
                TestAppBuilder.FileSystem.AddFile(destination, fileToPatch2);
                
                // Around the end of the installation process, we change the VersionID in the Assembly-CSharp
                // This is not ideal, as this is not really when the files are changed
                // Improvements to this part of the test are welcomed, if possible
                var gameExeModule = TestHelpers.GenerateMockVersionID(
                    ("VERSION_TEXT", "0.2.3.1.1234"),
                    ("CHANNEL_NAME", DefaultChannel),
                    ("DEBUG_INFO", "_") // Should be the checksum once it is implemented
                );
                // We override the setup that was done in TestHelper.MockKsp2StockSteamInstall, the original should have been called already
                TestAppBuilder.ModuleDefinitionService
                    .Setup(m => m.ReadModule(
                        @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Managed\Assembly-CSharp.dll"))
                    .Returns(gameExeModule.module);
            });
        
        string dowloadLocation = @"AppDataLocal\Ksp2Redux\download-cache\patch1Rollup.patch";
        TestAppBuilder.ZipFileService.Setup(z => z.OpenRead(dowloadLocation))
            .Returns(zippedPatch.Object);
        
        Mock<IZipArchive> cacheArchive = new(); // mock for CacheService.RecursivelyCreateCache
        TestAppBuilder.ZipFileService.Setup(z => z.NewArchive(It.IsAny<Stream>(), ZipArchiveMode.Create, false))
            .Returns(cacheArchive.Object);
        
        Console.WriteLine();
        Console.WriteLine("TEST: File system before Act:\n\t- " + string.Join("\n\t- ", TestAppBuilder.FileSystem.AllNodes));
        Console.WriteLine();
        
        // Act - Assert
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        
        GroupedComboBox? versionSelectorCombobox = window
            .GetVisualDescendants()
            .OfType<GroupedComboBox>()
            .FirstOrDefault(x => x.Name == "VersionSelector");
        Assert.That(versionSelectorCombobox, Is.Not.Null);

        versionSelectorCombobox.SelectedItem = versionSelectorCombobox.GroupedItems
            .OfType<GameVersionViewModel>()
            .Single(g => g.VersionString.Contains("0.2.3.1.1234"));
        Dispatcher.UIThread.RunJobs();
        
        Button? installButton = window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(x => x.Name == "InstallButton");
        Assert.That(installButton, Is.Not.Null);
        Assert.That(installButton.IsEnabled, Is.True);
        Assert.That(installButton.IsVisible, Is.True);
        Assert.That(installButton.Command, Is.Not.Null);
        
        Console.WriteLine();
        Console.WriteLine("TEST: File system before download:\n\t- " + string.Join("\n\t- ", TestAppBuilder.FileSystem.AllNodes));
        Console.WriteLine();
        
        installButton.Focus();
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        
        HomeTabViewModel homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();
        
        // Wait for progress to not be visible
        for (int i = 0; i < 200; i++)
        {
            if (i == 199)
                Assert.Fail("Timeout waiting for progress to not be visible");
            if (homeTabViewModel.IsProgressVisible == false)
                break;
            await Task.Delay(100);
        }
        
        // Assert
        Console.WriteLine();
        Console.WriteLine("TEST: File system after test:\n\t- " + string.Join("\n\t- ", TestAppBuilder.FileSystem.AllNodes));
        Console.WriteLine();

        // Assert Prepatch
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToAdd.file"),
            Is.True);
        Assert.That(TestAppBuilder.FileSystem.GetFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToAdd.file").Contents,
            Is.EqualTo(prepatchFileToAdd.Contents));
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToPatch.file"),
            Is.True);
        Assert.That(TestAppBuilder.FileSystem.GetFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToPatch.file").TextContents,
            Is.EqualTo(prepatchFileToPatchContent));
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToRemove.file"),
            Is.False);
        
        // Assert cacheArchive.CreateEntryFromFile called for every file before act
        cacheArchive.Verify(c => c.CreateEntryFromFile(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe", 
            @"KSP2_x64.exe"), Times.Once);
        cacheArchive.Verify(c => c.CreateEntryFromFile(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Plugins\Steamworks.NET.txt", 
            @"KSP2_x64_Data\Plugins\Steamworks.NET.txt"), Times.Once);
        cacheArchive.Verify(c => c.CreateEntryFromFile(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToPatch.file", 
            @"prepatchFileToPatch.file"), Times.Once);
        cacheArchive.Verify(c => c.CreateEntryFromFile(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\prepatchFileToRemove.file", 
            @"prepatchFileToRemove.file"), Times.Once);
        cacheArchive.Verify(c => c.CreateEntryFromFile(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\fileToPatch1.file", 
            @"fileToPatch1.file"), Times.Once);
        cacheArchive.Verify(c => c.CreateEntryFromFile(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\Folder\fileToPatch2.file", 
            @"Folder\fileToPatch2.file"), Times.Once);

        // Assert patch
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\fileToAdd1.file"),
            Is.True);
        Assert.That(TestAppBuilder.FileSystem.GetFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\fileToAdd1.file").Contents,
            Is.EqualTo(fileToAdd1.Contents));
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\Folder\fileToAdd2.file"),
            Is.True);
        Assert.That(TestAppBuilder.FileSystem.GetFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\Folder\fileToAdd2.file").Contents,
            Is.EqualTo(fileToAdd2.Contents));
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\fileToPatch1.file"),
            Is.True);
        Assert.That(TestAppBuilder.FileSystem.GetFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\fileToPatch1.file").TextContents,
            Is.EqualTo(fileToPatch1Content));
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\Folder\fileToPatch2.file"),
            Is.True);
        Assert.That(TestAppBuilder.FileSystem.GetFile(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\Folder\fileToPatch2.file").TextContents,
            Is.EqualTo(fileToPatch2Content));
        
        // Assert uninstall.zip exists
        Assert.That(TestAppBuilder.FileSystem.FileExists(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\uninstall.zip"),
            Is.True);
        
        // Assert version displayed as the current version is correct
        // See above to understand how this part of the mock was set up
        Assert.That(((GameVersionViewModel)versionSelectorCombobox.SelectedItem).VersionString, Does.Contain("0.2.3.1.1234"));
        Assert.That(((GameVersionViewModel)versionSelectorCombobox.SelectedItem).Version.VersionNumber, Is.EqualTo(new Version(0, 2, 3, 1)));
        Assert.That(((GameVersionViewModel)versionSelectorCombobox.SelectedItem).Version.BuildNumber, Is.EqualTo("1234"));
        
        // Assert home button is Launch
        Button? mainButton = window
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(x => x is { IsVisible: true, IsEnabled: true })
            .SingleOrDefault(x => x.Classes.Contains("main-button"));
        Assert.That(mainButton, Is.Not.Null);
        Assert.That(mainButton.Name, Is.EqualTo("LaunchButton"));
    }
    
    
    // [AvaloniaTest]
    // public void Download_SteamStockToReduxOtherChannel_DownloadsCorrectVersion()
    //
    //
    // [AvaloniaTest]
    // public void Download_SteamReduxDefaultChannelToReduxOtherChannel_DownloadsCorrectVersion()
    //
    //
    // [AvaloniaTest]
    // public void Download_SteamStockToReduxDefaultChannelCancel_DownloadsCorrectVersion()
    //
    //
    // [AvaloniaTest]
    // public void Download_SteamStockToReduxFailedTransmissionThenSuccess_DownloadsAfterRetry()    // for when checksum is used
    //
}