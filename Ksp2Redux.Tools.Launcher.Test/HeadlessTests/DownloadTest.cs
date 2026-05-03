using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Security.Cryptography;
using Avalonia.Headless.NUnit;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test.HeadlessTests;

public class DownloadTest
{
    private const string DefaultChannel = "beta";
    private const string OtherChannel = "stable";
    
    [AvaloniaTest]
    public void Download_SteamStockToReduxDefaultChannel1Rollup_DownloadsCorrectVersion()
    {
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed{ Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        byte[] patch1RollupZipBytes = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A];
        using SHA256 sha256 = SHA256.Create();

        ManifestReleasesFeed.Patch patch1Rollup = new()
        {
            checksum_sha256 = BitConverter.ToString(sha256.ComputeHash(patch1RollupZipBytes)).Replace("-", ""),
            releasedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            requires = new() { version = null },
            size = 10,
            url = "https://github.com/patch1Rollup.patch",
            version = "0.0.0.1.1234",
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

        string dowloadLocation = @"AppDataLocal\Ksp2Redux\download-cache\patch1Rollup.patch";

        MockFileData fileToAdd1 = new("fileToAdd1 - Content");
        MockFileData originalFileToPatch1 = new("fileToPatch1 - OldContent");
        MockFileData fileToPatch1 = new("fileToPatch1 - Content");
        MockFileData fileToAdd2 = new("fileToAdd2 - Content");
        MockFileData originalFileToPatch2 = new("fileToPatch2 - OldContent");
        MockFileData fileToPatch2 = new("fileToPatch2 - Content");
        
        string patchManifest = $$"""
            {
              "operations": [
                {
                  "fileName": "fileToAdd1.file",
                  "action": 1,
                  "originalHash": null,
                  "finalHash": "{{BitConverter.ToString(sha256.ComputeHash(fileToAdd1.Contents)).Replace("-", "")}}"
                },
                {
                  "fileName": "fileToPatch1.file",
                  "action": 0,
                  "originalHash": "{{BitConverter.ToString(sha256.ComputeHash(originalFileToPatch1.Contents)).Replace("-", "")}}",
                  "finalHash": "{{BitConverter.ToString(sha256.ComputeHash(fileToPatch1.Contents)).Replace("-", "")}}"
                },
                {
                  "fileName": "Folder\\fileToAdd2.file",
                  "action": 1,
                  "originalHash": null,
                  "finalHash": "{{BitConverter.ToString(sha256.ComputeHash(fileToAdd2.Contents)).Replace("-", "")}}"
                },
                {
                  "fileName": "Folder\\fileToPatch2.file",
                  "action": 0,
                  "originalHash": "{{BitConverter.ToString(sha256.ComputeHash(originalFileToPatch2.Contents)).Replace("-", "")}}",
                  "finalHash": "{{BitConverter.ToString(sha256.ComputeHash(fileToPatch2.Contents)).Replace("-", "")}}"
                }
              ]
            }                                   
            """;
        // TODO: must contain .bsfid files (at least two at different folder depths)
        // TODO: files to patch must be in the folder with the install, hashes should be correct
        Mock<IZipArchive> zippedPatch = new();
        Mock<IZipArchiveEntry> zipManifestEntry = new();
        MemoryStream manifestEntryStream = new MemoryStream(/* Manifest string */);
        
        zippedPatch.Setup(u => u.GetEntry("manifest.json")).Returns(zipManifestEntry.Object);
        zipManifestEntry.Setup(z => z.Open()).Returns(manifestEntryStream);

        TestAppBuilder.ZipFileService.Setup(z => z.OpenRead(dowloadLocation))
            .Returns(zippedPatch.Object);
        
        
        
        // Assert
        // TODO: files to add are added with correct content
        // TODO: files to patch have the correct content
        // TODO: version displayed as the current version is correct
        // TODO: home button is Launch
        // TODO: current install in config is the correct version
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