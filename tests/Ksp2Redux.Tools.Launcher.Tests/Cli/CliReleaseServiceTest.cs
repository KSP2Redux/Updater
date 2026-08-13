using System.Net;
using System.Text;
using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliReleaseServiceTest
{
    private const string REPOSITORY = "https://github.com/KSP2Redux/Updater";
    private const string WINDOWS_ASSET = "redux-cli-x64.exe";
    private const string LINUX_ASSET = "redux-cli-x64";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string Release(string tag, string assetName, string? digest = "sha256:abc", bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "notes",
          "assets": [
            {
              "name": "{{assetName}}",
              "browser_download_url": "https://example.invalid/{{assetName}}",
              "digest": {{(digest is null ? "null" : $"\"{digest}\"")}}
            }
          ]
        }
        """;

    private static CliReleaseService Build(string releasesJson, bool isLinux = false) =>
        new(REPOSITORY, isLinux, "1.0.0", null, new StubHandler(_ => Json(releasesJson)));

    [Test]
    public async Task FindLatestAsync_SeveralReleases_PicksTheNewest()
    {
        // Arrange
        CliReleaseService service = Build($"""
            [{Release("updater-v0.4.2.1", WINDOWS_ASSET)},
             {Release("updater-v0.4.10.0", WINDOWS_ASSET)},
             {Release("updater-v0.4.9.0", WINDOWS_ASSET)}]
            """);

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release, Is.Not.Null);
        Assert.That(release!.Version, Is.EqualTo(new Version(0, 4, 10, 0)));
    }

    // The two products share a release now, so the CLI has to ignore the launcher's binaries sitting
    // in the same asset list rather than ignoring the release.
    [Test]
    public async Task FindLatestAsync_ReleaseWithOnlyLauncherAssets_FindsNothing()
    {
        // Arrange
        CliReleaseService service = Build($"[{Release("updater-v9.9.9.9", "Ksp2Redux-win-x64.exe")}]");

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release, Is.Null);
    }

    // Releases from before the CLI existed carry no CLI asset, so the newest one that does wins.
    [Test]
    public async Task FindLatestAsync_OlderReleaseCarriesTheCli_PicksThatOne()
    {
        // Arrange
        CliReleaseService service = Build($"""
            [{Release("updater-v9.9.9.9", "Ksp2Redux-win-x64.exe")},
             {Release("updater-v0.4.3.0", WINDOWS_ASSET)}]
            """);

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release!.Version, Is.EqualTo(new Version(0, 4, 3, 0)));
    }

    // The launcher's own updater picks its download with Contains("win") or Contains("linux") over
    // this same asset list, and launchers already installed cannot be fixed. A CLI asset that
    // matched either word would be handed to them as an update to themselves, so the names are
    // pinned here rather than left to whoever edits the release workflow next.
    [TestCase(true)]
    [TestCase(false)]
    public void AssetName_NeverContainsAWordTheLauncherMatchesOn(bool isLinux)
    {
        // Act
        string name = new CliReleaseService(REPOSITORY, isLinux, "1.0.0").AssetName;

        // Assert
        Assert.That(name, Does.Not.Contain("win").IgnoreCase);
        Assert.That(name, Does.Not.Contain("linux").IgnoreCase);
    }

    [Test]
    public async Task FindLatestAsync_Prerelease_IsIgnored()
    {
        // Arrange
        CliReleaseService service = Build($"""
            [{Release("updater-v9.9.9.9", WINDOWS_ASSET, prerelease: true)},
             {Release("updater-v0.4.2.2", WINDOWS_ASSET)}]
            """);

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release!.Version, Is.EqualTo(new Version(0, 4, 2, 2)));
    }

    // A release whose assets are still uploading has no digest yet, and offering it produces an
    // update that cannot be downloaded.
    [Test]
    public async Task FindLatestAsync_AssetWithoutADigest_FallsBackToTheReleaseBelowIt()
    {
        // Arrange
        CliReleaseService service = Build($"""
            [{Release("updater-v0.5.0.0", WINDOWS_ASSET, digest: null)},
             {Release("updater-v0.4.2.2", WINDOWS_ASSET)}]
            """);

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release!.Version, Is.EqualTo(new Version(0, 4, 2, 2)));
    }

    [Test]
    public async Task FindLatestAsync_NoAssetForThisPlatform_FindsNothing()
    {
        // Arrange
        CliReleaseService service = Build($"[{Release("updater-v0.4.2.2", LINUX_ASSET)}]");

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release, Is.Null);
    }

    [Test]
    public async Task FindLatestAsync_OnLinux_PicksTheLinuxAsset()
    {
        // Arrange
        CliReleaseService service = Build($"""
            [{Release("updater-v0.4.2.2", LINUX_ASSET)}]
            """, isLinux: true);

        // Act
        CliRelease? release = await service.FindLatestAsync(CancellationToken.None);

        // Assert
        Assert.That(release!.AssetName, Is.EqualTo(LINUX_ASSET));
    }

    [Test]
    public async Task DownloadAsync_ChecksumMatches_ReturnsTheBytes()
    {
        // Arrange
        byte[] payload = [1, 2, 3, 4];
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        CliReleaseService service = new(REPOSITORY, false, "1.0.0", null,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

        CliRelease release = new(new Version(1, 0), WINDOWS_ASSET, "https://example.invalid/asset", digest, null);

        // Act
        byte[] downloaded = await service.DownloadAsync(release, CancellationToken.None);

        // Assert
        Assert.That(downloaded, Is.EqualTo(payload));
    }

    [Test]
    public void DownloadAsync_ChecksumDiffers_Throws()
    {
        // Arrange
        CliReleaseService service = new(REPOSITORY, false, "1.0.0", null,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9]) }));

        CliRelease release = new(new Version(1, 0), WINDOWS_ASSET, "https://example.invalid/asset", "deadbeef", null);

        // Act, Assert
        Assert.That(
            async () => await service.DownloadAsync(release, CancellationToken.None),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
