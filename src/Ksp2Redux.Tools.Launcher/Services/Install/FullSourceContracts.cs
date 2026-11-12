using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// The authenticated release identity retained independently of installation journals.
/// </summary>
/// <param name="ReleaseId">The immutable published release identity.</param>
/// <param name="Channel">The authenticated release channel.</param>
/// <param name="Version">The published four-component game version followed by its build identity.</param>
/// <param name="ManifestSha256">The digest of the independently authenticated release tree contract.</param>
/// <param name="Authentication">The verifier's nonsecret authentication evidence identifier.</param>
public record FullSourceReleaseIdentity(string ReleaseId, string Channel, string Version,
    string ManifestSha256, string Authentication);

/// <summary>
/// A prepared source tree and release contract requiring independent verification before use.
/// </summary>
/// <param name="Identity">The expected release identity.</param>
/// <param name="Manifest">The authenticated complete release file contract.</param>
/// <param name="SourceRoot">The separately prepared source tree.</param>
/// <param name="AllowsDerivedBootConfig">True only when authenticated metadata permits launcher boot configuration.</param>
/// <param name="AuthorityFeed">The selected nonsecret configured authority, or null for separately injected providers.</param>
public record FullSourcePreparedTarget(FullSourceReleaseIdentity Identity, StockBaselineManifest Manifest,
    string SourceRoot, bool AllowsDerivedBootConfig, FeedInfo? AuthorityFeed = null)
{
    /// <summary>
    /// Gets the installed baseline that supplies unchanged paths when only changed files are staged.
    /// </summary>
    public FullSourceInstalledBaseline? InstalledBaseline { get; init; }

    /// <summary>
    /// Gets the exact target paths retained in the configured installation when staging from stock.
    /// </summary>
    public FullSourceTargetRetention? TargetRetention { get; init; }

    // The ordinary feed-selected installer owns this prepared stage for a single apply.
    internal bool MayConsumeSource { get; init; }
}

/// <summary>
/// The configured installation and complete prior release contract bound to a changed-file stage.
/// </summary>
/// <param name="InstallationId">The installation that must still own this generation when the update starts.</param>
/// <param name="Manifest">The complete publisher-pinned prior release contract.</param>
public record FullSourceInstalledBaseline(Guid InstallationId, StockBaselineManifest Manifest);

/// <summary>
/// Target paths omitted from a stock-based stage and verified again under the installation lock.
/// </summary>
/// <param name="InstallationId">The configured installation supplying the retained target files.</param>
/// <param name="Paths">The authenticated target paths currently matching in the installation.</param>
/// <param name="PreviousManifestSha256">The installed published generation for an update, or null for repair or migration.</param>
/// <param name="LegacyOwnershipSha256">The verified initial owned generation for legacy migration, if applicable.</param>
public record FullSourceTargetRetention(Guid InstallationId, IReadOnlySet<string> Paths, string? PreviousManifestSha256,
    string? LegacyOwnershipSha256 = null);

/// <summary>
/// Independently verified stock preparation and known initial ownership for a conversion.
/// </summary>
/// <param name="Stock">The original trusted storefront contract, including StreamingAssets and boot.config.</param>
/// <param name="SourceRoot">The verified pristine stock source, or an empty string when ExistingArchive supplies original bytes.</param>
/// <param name="CurrentOwned">The independently known current owned file and directory contract.</param>
/// <param name="RetainedUnownedFiles">The explicit file exclusions retained across generations.</param>
/// <param name="UnownedDirectories">The explicit preexisting directory exclusions.</param>
/// <param name="InstallationId">The configured initial-conversion identity, or null for separately injected providers.</param>
/// <param name="Release">The independently authenticated release supplying the original stock definition.</param>
/// <param name="AuthorityFeed">The selected nonsecret configured authority, or null for separately injected providers.</param>
/// <param name="ExistingArchive">The exact initial backup pin, or null for separately prepared pristine source files.</param>
/// <param name="RecoverySources">The exact current-file and journal source observations requiring revalidation, or null for older preparations.</param>
public record FullSourceStockPreparation(StockBaselineManifest Stock, string SourceRoot,
    StockBaselineManifest CurrentOwned, IReadOnlyList<string> RetainedUnownedFiles,
    IReadOnlyList<string> UnownedDirectories, Guid? InstallationId = null,
    FullSourceReleaseIdentity? Release = null, FeedInfo? AuthorityFeed = null, StockBaselineFile? ExistingArchive = null,
    StockArchiveSources? RecoverySources = null);

/// <summary>
/// The release and storefront authentication boundary used by both launcher interfaces.
/// </summary>
/// <remarks>
/// Implementations must authenticate identity, channel, complete manifest and derivation permission
/// independently of the prepared tree or launcher catalog. A local digest is not authentication.
/// </remarks>
public interface IFullSourcePreparationService
{
    /// <summary>
    /// Prepare original storefront input and independently established initial ownership for a configured identity.
    /// </summary>
    /// <param name="installationId">The explicitly configured installation identity.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The independently verified stock input or pinned existing backup, without mutating it.</returns>
    Task<FullSourceStockPreparation> FetchStockAsync(Guid installationId, CancellationToken ct);

    /// <summary>
    /// Prepare initial original stock using the selected release's independently refreshed authority.
    /// </summary>
    /// <param name="installationId">The explicitly configured installation identity.</param>
    /// <param name="target">The selected prepared release supplying supported original stock definitions.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The separately verified original stock and known initial ownership.</returns>
    Task<FullSourceStockPreparation> FetchStockAsync(Guid installationId, FullSourcePreparedTarget target, CancellationToken ct) =>
        FetchStockAsync(installationId, ct);

    /// <summary>
    /// Fetch and reconstruct a target from an independently authenticated release source.
    /// </summary>
    /// <param name="releaseId">The published release selector.</param>
    /// <param name="channel">The requested channel.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The prepared target requiring verification at the operation boundary.</returns>
    Task<FullSourcePreparedTarget> FetchTargetAsync(string releaseId, string channel, CancellationToken ct,
        Action<long, long>? downloadProgress = null);

    /// <summary>
    /// Fetch a selected release from its still-configured list-time authority without searching other publishers.
    /// </summary>
    /// <param name="releaseId">The published release selector.</param>
    /// <param name="channel">The requested channel.</param>
    /// <param name="feed">The selected nonsecret authority snapshot.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The authenticated prepared target requiring operation-boundary verification.</returns>
    Task<FullSourcePreparedTarget> FetchTargetAsync(string releaseId, string channel, FeedInfo feed, CancellationToken ct,
        Action<long, long>? downloadProgress = null) => FetchTargetAsync(releaseId, channel, ct, downloadProgress);

    /// <summary>
    /// Prepare a release using a matching installed Redux generation or an independently verified stock baseline.
    /// </summary>
    /// <param name="releaseId">The published target release selector.</param>
    /// <param name="channel">The requested target channel.</param>
    /// <param name="feed">The selected authority snapshot, or null to resolve configured authority.</param>
    /// <param name="installationId">The intended configured installation.</param>
    /// <param name="installedRelease">The previously published identity from that installation's catalog.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The prepared target requiring operation-boundary verification.</returns>
    Task<FullSourcePreparedTarget> FetchTargetForInstalledReleaseAsync(string releaseId, string channel, FeedInfo? feed,
        Guid installationId, FullSourceReleaseIdentity installedRelease, CancellationToken ct,
        Action<long, long>? downloadProgress = null) =>
        feed is null ? FetchTargetAsync(releaseId, channel, ct, downloadProgress) : FetchTargetAsync(releaseId, channel, feed, ct, downloadProgress);

    /// <summary>
    /// Prepare a repair using the exact target files already present in the configured installation.
    /// </summary>
    /// <param name="releaseId">The published target release selector.</param>
    /// <param name="channel">The requested target channel.</param>
    /// <param name="feed">The selected authority snapshot, or null to resolve configured authority.</param>
    /// <param name="installationId">The intended configured installation.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The prepared repair requiring operation-boundary verification.</returns>
    Task<FullSourcePreparedTarget> FetchRepairTargetAsync(string releaseId, string channel, FeedInfo? feed,
        Guid installationId, CancellationToken ct, Action<long, long>? downloadProgress = null) =>
        feed is null ? FetchTargetAsync(releaseId, channel, ct, downloadProgress) : FetchTargetAsync(releaseId, channel, feed, ct, downloadProgress);

    /// <summary>
    /// Prepare original stock and the selected release for an installation without a managed catalog.
    /// </summary>
    /// <param name="releaseId">The published target release selector.</param>
    /// <param name="channel">The requested target channel.</param>
    /// <param name="feed">The selected authority snapshot, or null to resolve configured authority.</param>
    /// <param name="installationId">The intended configured installation.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The prepared target and original stock requiring operation-boundary verification.</returns>
    Task<(FullSourcePreparedTarget Target, FullSourceStockPreparation Stock)> FetchInitialInstallAsync(
        string releaseId, string channel, FeedInfo? feed, Guid installationId, CancellationToken ct,
        Action<long, long>? downloadProgress = null) =>
        FetchLegacyMigrationAsync(releaseId, channel, feed, installationId, ct, downloadProgress);

    /// <summary>
    /// Prepare a feed-selected install, update or repair using the plan's pinned release identity.
    /// </summary>
    /// <param name="plan">The feed-selected plan with the expected release values.</param>
    /// <param name="installationId">The intended configured installation.</param>
    /// <param name="hasCatalog">Whether the installation already has a managed catalog.</param>
    /// <param name="installedRelease">The published generation available for update reuse, if any.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The prepared target and any required original stock preparation.</returns>
    async Task<(FullSourcePreparedTarget Target, FullSourceStockPreparation? Stock)> FetchSelectedPlanAsync(
        InstallPlan plan, Guid installationId, bool hasCatalog, FullSourceReleaseIdentity? installedRelease,
        CancellationToken ct, Action<long, long>? downloadProgress = null)
    {
        string releaseId = plan.FullSourceReleaseId ?? throw new InvalidDataException("The plan has no release selector.");
        string channel = plan.FullSourceChannel ?? throw new InvalidDataException("The plan has no release channel.");
        var feed = plan.FullSourceFeed;
        if (!hasCatalog)
            return await FetchInitialInstallAsync(releaseId, channel, feed, installationId, ct, downloadProgress);
        if (plan.IsFullSourceRepair)
            return (await FetchRepairTargetAsync(releaseId, channel, feed, installationId, ct, downloadProgress), null);
        if (installedRelease is not null)
            return (await FetchTargetForInstalledReleaseAsync(releaseId, channel, feed, installationId, installedRelease, ct, downloadProgress), null);
        return (feed is null ? await FetchTargetAsync(releaseId, channel, ct, downloadProgress)
            : await FetchTargetAsync(releaseId, channel, feed, ct, downloadProgress), null);
    }

    /// <summary>
    /// Prepare the selected release and original stock ownership for an existing backup migration.
    /// </summary>
    /// <param name="releaseId">The published target release selector.</param>
    /// <param name="channel">The requested target channel.</param>
    /// <param name="feed">The selected authority snapshot, or null to resolve configured authority.</param>
    /// <param name="installationId">The intended configured installation.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="downloadProgress">Bytes received or reused and the planned byte total.</param>
    /// <returns>The prepared target and original stock requiring operation-boundary verification.</returns>
    async Task<(FullSourcePreparedTarget Target, FullSourceStockPreparation Stock)> FetchLegacyMigrationAsync(
        string releaseId, string channel, FeedInfo? feed, Guid installationId, CancellationToken ct,
        Action<long, long>? downloadProgress = null)
    {
        var target = feed is null ? await FetchTargetAsync(releaseId, channel, ct, downloadProgress)
            : await FetchTargetAsync(releaseId, channel, feed, ct, downloadProgress);
        return (target, await FetchStockAsync(installationId, target, ct));
    }

    /// <summary>
    /// Verify authentication and source preparation without accepting local manifest claims as authority.
    /// </summary>
    /// <param name="target">The release contract and prepared tree.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The task completing only after independent verification.</returns>
    Task VerifyTargetAsync(FullSourcePreparedTarget target, CancellationToken ct);

    /// <summary>
    /// Verify the original storefront baseline and separately known conversion ownership.
    /// </summary>
    /// <param name="preparation">The trusted stock preparation and ownership.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The task completing only after independent verification.</returns>
    Task VerifyStockAsync(FullSourceStockPreparation preparation, CancellationToken ct);
}

/// <summary>
/// Explicit production boundary until authenticated full-source release and stock providers are configured.
/// </summary>
public class UnavailableFullSourcePreparationService : IFullSourcePreparationService
{
    /// <inheritdoc />
    public Task<FullSourceStockPreparation> FetchStockAsync(Guid installationId, CancellationToken ct) =>
        throw new NotSupportedException("Independent original stock preparation is not configured.");

    /// <inheritdoc />
    public Task<FullSourcePreparedTarget> FetchTargetAsync(string releaseId, string channel, CancellationToken ct,
        Action<long, long>? downloadProgress = null) =>
        throw new NotSupportedException("Authenticated full-source release fetching is not configured.");

    /// <inheritdoc />
    public Task VerifyTargetAsync(FullSourcePreparedTarget target, CancellationToken ct) =>
        throw new NotSupportedException("Independent full-source release verification is not configured.");

    /// <inheritdoc />
    public Task VerifyStockAsync(FullSourceStockPreparation preparation, CancellationToken ct) =>
        throw new NotSupportedException("Independent original stock preparation is not configured.");
}

/// <summary>
/// Published managed generation and backup health after reconciliation.
/// </summary>
/// <param name="InstallationId">The configured installation identity.</param>
/// <param name="Generation">The exact published owned file and directory contract.</param>
/// <param name="Release">The authenticated full-source release identity, or null for stock or a retained initial legacy observation.</param>
/// <param name="TransactionResult">The last reconciled engine outcome, or null without a transaction.</param>
/// <param name="ArchiveFailure">The separately reported backup health failure, or null.</param>
public record FullSourceInstallationStatus(Guid InstallationId, StockBaselineManifest Generation,
    FullSourceReleaseIdentity? Release, InstallTransactionResult? TransactionResult, Exception? ArchiveFailure);

/// <summary>
/// Durable full-source operations shared by GUI install plans, startup checks and CLI commands.
/// </summary>
public interface IFullSourceInstallationService
{
    /// <summary>
    /// Retire disposable payloads of the last published Windows transaction under exclusive coordination.
    /// </summary>
    /// <remarks>
    /// Pending, ambiguous and preparation-only operations are retained. The complete original archive,
    /// operation contracts, exact plan, journals and retirement receipts remain available.
    /// </remarks>
    /// <param name="installationId">The configured installation identity.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True if a terminal payload retirement is verified complete, false if no eligible transaction exists.</returns>
    Task<bool> RetireCompletedPayloadsAsync(Guid installationId, CancellationToken ct) =>
        throw new NotSupportedException("Terminal payload retirement is not available from this coordinator.");

    /// <summary>
    /// Determine whether the configured root has a launcher catalog, without reading journal authority.
    /// </summary>
    /// <param name="root">The explicitly configured installation root.</param>
    /// <returns>True if a controlled catalog exists, false otherwise.</returns>
    bool HasCatalog(string root);

    /// <summary>
    /// Read the durable published release identity after reconciliation for installation display.
    /// </summary>
    /// <param name="installationId">The configured installation identity.</param>
    /// <returns>The authenticated full-source release identity, or null before publication or after stock restoration.</returns>
    FullSourceReleaseIdentity? GetPublishedIdentity(Guid installationId);

    /// <summary>
    /// Recover pending work and publish its verified outcome, including after marker cleanup.
    /// </summary>
    /// <remarks>
    /// Pending recovery requires the intended game and installation files to be unused.
    /// Healthy catalog inspection does not impose that mutation precondition.
    /// </remarks>
    /// <param name="installationId">The configured installation identity.</param>
    /// <param name="expectedInstallRoot">The installation directory selected for recovery.</param>
    /// <returns>The published generation and independent archive health, or null without a catalog.</returns>
    /// <exception cref="IOException">Thrown when process use or coordination prevents pending recovery.</exception>
    /// <exception cref="FilesInUseException">Thrown when installation files are held during pending recovery.</exception>
    FullSourceInstallationStatus? Reconcile(Guid installationId, string? expectedInstallRoot = null);

    /// <summary>
    /// Reconcile configured catalogs before launch or legacy mutation and report backup failures.
    /// </summary>
    /// <param name="root">The explicitly configured installation root.</param>
    void EnsureReady(string root);

    /// <summary>
    /// Reserve a configured Windows installation, reconcile pending work and verify owned bytes before a launcher handoff.
    /// </summary>
    /// <remarks>
    /// Hold the lease through handoff until game detection, launch failure or the existing startup timeout.
    /// Catalog-free legacy POSIX and unconfigured roots return null.
    /// </remarks>
    /// <param name="root">The configured intended installation folder.</param>
    /// <param name="ct">Cancellation checked before reservation and during owned-file verification.</param>
    /// <returns>The coordinator lease, or null when no full-source reservation applies.</returns>
    /// <exception cref="IOException">Thrown when another coordinator holds the root or pending recovery is blocked.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is observed before handoff.</exception>
    IDisposable? AcquireLaunchLease(string root, CancellationToken ct = default);

    /// <summary>
    /// Install, update, downgrade or switch channel through one verified target-generation operation.
    /// </summary>
    /// <param name="installationId">The configured installation identity.</param>
    /// <param name="target">The independently verified release and prepared source contract.</param>
    /// <param name="stock">The separately verified initial conversion input, or null for an existing catalog.</param>
    /// <param name="repair">True to observe damaged owned files while preserving the preceding operation's remaining payloads.</param>
    /// <param name="log">The operation progress sink.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="expectedInstallRoot">The selected installation directory when the caller prepared the operation.</param>
    /// <returns>The committed and published generation.</returns>
    /// <exception cref="IOException">Thrown when game process use or coordinator locking prevents mutation.</exception>
    /// <exception cref="FilesInUseException">Thrown when installation files are held before mutation.</exception>
    Task<FullSourceInstallationStatus> ApplyAsync(Guid installationId, FullSourcePreparedTarget target,
        FullSourceStockPreparation? stock, bool repair, Action<string> log, CancellationToken ct,
        string? expectedInstallRoot = null);

    /// <summary>
    /// Restore original stock into a new stage and apply it while retaining the full archive.
    /// </summary>
    /// <param name="installationId">The configured installation identity.</param>
    /// <param name="log">The operation progress sink.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="expectedInstallRoot">The installation directory selected for restoration.</param>
    /// <returns>The committed stock generation.</returns>
    /// <exception cref="IOException">Thrown when game process use or coordinator locking prevents mutation.</exception>
    /// <exception cref="FilesInUseException">Thrown when installation files are held before mutation.</exception>
    Task<FullSourceInstallationStatus> UninstallAsync(Guid installationId, Action<string> log, CancellationToken ct,
        string? expectedInstallRoot = null);
}
