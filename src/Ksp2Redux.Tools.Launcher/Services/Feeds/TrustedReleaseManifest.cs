using Ksp2Redux.Tools.Common.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

/// <summary>
/// Fresh release metadata authenticated through an explicitly configured online authority.
/// </summary>
/// <param name="Manifest">The complete freshly fetched release feed.</param>
/// <param name="Authority">The nonsecret authority evidence, or an explicitly injected fixture identifier in tests.</param>
public record TrustedReleaseManifest(ReleaseManifest Manifest, string Authority);
