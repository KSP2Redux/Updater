using System.Net;
using System.Reflection;
using System.Text;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

/// <summary>
/// Production online authority transport and credential boundaries using injected HTTP responses.
/// </summary>
public partial class FullSourceFeedTransportTest
{
    /// <summary>
    /// HTTPS artifact transport rejects a final redirect to plaintext before accepting its body.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_HttpsArtifactRedirectToHttp_IsRejected()
    {
        var handler = new Handler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1]),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://publisher.test/pack.bin")
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var response = await Service(handler).DownloadFileAsync(new FeedInfo(),
                "https://publisher.test/pack.bin", 0, CancellationToken.None);
        });
    }

    private class Handler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"schemaVersion\":3,\"channel\":\"beta\",\"generatedAt\":\"2026-10-14T00:00:00Z\",\"patches\":[]}")
        };

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Respond(request));
        }
    }

    private static ManifestReleasesFeedProviderService Service(HttpMessageHandler handler)
    {
        var assembly = new Mock<IAssemblyService>();
        assembly.Setup(service => service.GetName()).Returns(new AssemblyName("Fixture") { Version = new Version(1, 0) });
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(new LauncherConfig());
        return new ManifestReleasesFeedProviderService(assembly.Object, configuration.Object, Mock.Of<ILogService>(), handler);
    }

    /// <summary>
    /// A configured HTTPS authority supplies provenance independently of cached artifact digests.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task HttpsConfiguredAuthority_ReturnsFreshNonsecretProvenance()
    {
        int calls = 0;
        var handler = new Handler();
        var original = handler.Respond;
        handler.Respond = request =>
        {
            calls++;
            Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://publisher.test/manifest-beta.json"));
            Assert.That(request.Headers.Authorization, Is.Null);
            return original(request);
        };
        var service = Service(handler);
        var feed = new FeedInfo { R2ManifestUrl = "https://publisher.test/manifest-beta.json", Token = "fixture-secret" };
        TrustedReleaseManifest first = await service.GetTrustedManifestAsync(feed, CancellationToken.None);
        await service.GetTrustedManifestAsync(feed, CancellationToken.None);
        Assert.That(first.Authority, Is.EqualTo("online-https:https://publisher.test/manifest-beta.json"));
        Assert.That(calls, Is.EqualTo(2));
    }

    /// <summary>
    /// HTTP and embedded-credential configuration cannot authorize full-source releases.
    /// </summary>
    /// <param name="url">The insecure configured authority.</param>
    /// <returns>The verification task.</returns>
    [TestCase("http://publisher.test/feed.json"), TestCase("https://user:password@publisher.test/feed.json")]
    public async Task InsecureAuthority_IsRejectedBeforeTransport(string url)
    {
        var handler = new Handler { Respond = _ => throw new AssertionException("Transport must not run.") };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Service(handler).GetTrustedManifestAsync(new FeedInfo { R2ManifestUrl = url }, CancellationToken.None));
    }

    /// <summary>
    /// Metadata redirects leaving HTTPS do not retain release authority.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task HttpRedirect_IsRejected()
    {
        var handler = new Handler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://publisher.test/feed.json")
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Service(handler).GetTrustedManifestAsync(new FeedInfo { R2ManifestUrl = "https://publisher.test/feed.json" }, CancellationToken.None));
    }

    /// <summary>
    /// Duplicate metadata fields and excessive declared lengths are rejected.
    /// </summary>
    /// <param name="oversized">True to inject an excessive length, false to inject repeated fields.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false), TestCase(true)]
    public async Task MalformedOrOversizedFeed_IsRejected(bool oversized)
    {
        var handler = new Handler
        {
            Respond = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"schemaVersion\":3,\"schemaVersion\":3}", Encoding.UTF8)
                };
                if (oversized)
                    response.Content.Headers.ContentLength = 64 * 1024 * 1024 + 1;
                return response;
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Service(handler).GetTrustedManifestAsync(new FeedInfo { R2ManifestUrl = "https://publisher.test/feed.json" }, CancellationToken.None));
    }

    /// <summary>
    /// A GitHub token goes only to the configured repository's asset API, never arbitrary publisher URLs.
    /// </summary>
    /// <param name="url">The requested download location.</param>
    /// <param name="authenticated">True only for the configured GitHub asset API.</param>
    /// <returns>The verification task.</returns>
    [TestCase("https://publisher.test/pack.bin", false)]
    [TestCase("https://api.github.com/repos/another/repository/releases/assets/1", false)]
    [TestCase("http://api.github.com/repos/fixture/authority/releases/assets/1", false)]
    [TestCase("https://api.github.com/repos/fixture/authority/releases/assets/1", true)]
    public async Task AssetDownload_RestrictsAuthorizationHeader(string url, bool authenticated)
    {
        var handler = new Handler
        {
            Respond = request =>
            {
                Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo(authenticated ? "fixture-secret" : null));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            }
        };
        using var response = await Service(handler).DownloadFileAsync(new FeedInfo
        {
            Repository = "https://github.com/fixture/authority", Token = "fixture-secret"
        }, url, 0, CancellationToken.None);
        Assert.That(response.IsSuccessStatusCode, Is.True);
    }

    /// <summary>
    /// The configured GitHub content authority uses its token only on the API request.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task ConfiguredGitHubAuthority_UsesExpectedRepositoryContentApi()
    {
        var handler = new Handler();
        var original = handler.Respond;
        handler.Respond = request =>
        {
            Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://api.github.com/repos/fixture/authority/contents/manifest-beta.json?ref=main"));
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("fixture-secret"));
            return original(request);
        };
        var trusted = await Service(handler).GetTrustedManifestAsync(new FeedInfo
        {
            Repository = "https://github.com/fixture/authority", Filename = "manifest-beta.json", Token = "fixture-secret"
        }, CancellationToken.None);
        Assert.That(trusted.Authority, Does.Not.Contain("fixture-secret").And.StartWith("online-https:"));
    }
}
