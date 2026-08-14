using Ksp2Redux.Tools.Common.Models;

namespace Ksp2Redux.Tools.Launcher.Models;

/// <summary>
/// A logical patch that must be downloaded and reconstructed before application.
/// </summary>
/// <param name="Feed">The feed that supplies authentication and source metadata.</param>
/// <param name="Patch">The patch transport definition.</param>
/// <param name="StorageDirectory">The local download-cache directory.</param>
public sealed record PatchDownloadRequest(FeedInfo Feed, ReleasePatch Patch, string StorageDirectory);
