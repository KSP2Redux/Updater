# Original backup adoption and legacy upgrades

Existing full-source catalogs retain their immutable original stock archive across
updates, repairs and uninstall. A complete canonical uninstall.zip can also be
adopted after verification against the selected publisher-pinned stock contract.
A partial legacy ZIP is never admitted as a canonical archive.

A feed-selected legacy upgrade is one install-plan job. InstallPlanService invokes
the existing UninstallStep through IInstallPlanService, then prepares and applies
a fresh full-source generation. FullSourceInstallationService holds its exclusive
coordinator lease throughout uninstall, preparation and application. GUI and CLI
use that same plan route; launch reservations fail while it holds the lease.

Legacy uninstall reverts BundleConversion through its journal and unrecorded-edit
scan before restoring uninstall.zip, removes legacy additions and preserves mods.
Initial preparation requires the restored installation to match the complete pinned
stock contract before downloading the release recipe. It never downloads replacement
stock to repair a failed legacy restoration. Failure leaves the restored installation
for Steam's Verify integrity of game files or Epic's Verify, followed by a retry.
Unknown files never acquire ownership. The first full-source generation becomes the
baseline for subsequent updates.

New initial preparation no longer assembles a stock archive from legacy ZIP entries,
current installed bytes or journal overlays, and publishers no longer provide
LegacyOwnershipManifests. The generic archive readers and persisted coordinator
recovery shapes remain for interrupted operations created by older launchers;
they do not authorize a new legacy adoption.

Standalone regressions cover canonical adoption, queued uninstall ordering, failed
stock verification without recipe download, mod preservation, and native Windows
launch exclusion throughout uninstall and preparation. Whole-game timing and runtime
acceptance require an independently verified stock tree.
