# Full-source launcher coordination, version 1

`FullSourceInstallationService` is the production Windows coordinator shared by the
GUI install-plan service, Settings uninstall, CLI `full-source`, startup inspection
and launch guards. Both normal containers register it. Legacy mutation remains guarded.
This moves installation coordination forward. It does not complete Omnipatching or
enable unauthenticated local release installation.

New operations use the canonical engine's version-two changed-file staging policy.
The complete prepared target is still verified. New Windows operations copy only
changed owned inputs and derived boot.config into the coordinator's separate source
stage. Only changed owned target files and necessary undo are then
copied into engine storage. Existing operations without the optional
`ChangedFileStaging` field retain version-one plan digests and recovery. The false
default is omitted during canonical serialization so old envelopes still round-trip.
Windows terminal source/engine payload retirement now follows the existing coordinator
and engine lifecycle. See [the storage contract](FullSourceInstallationService.Storage.md).
Preparation caches and stock restore staging remain separate peak disk costs.

## Authentication and inputs

`IFullSourcePreparationService` is a required independent boundary. A production
implementation must authenticate release identity, channel, the complete manifest and
boot derivation permission against its publisher trust configuration. It must separately
establish the original complete storefront contract and known initial conversion
ownership. Source file hashes and a local catalog checksum are not authentication.

The production `FullSourcePreparationService` now uses configured HTTPS/GitHub authority metadata
and reconstructs exact packs against publisher-curated stock definitions. Details and authentication
limitations are in `Documentation/Omnipatching/LAUNCHER-PREPARATION.md` in the game checkout.
Local JSON, arbitrary ZIPs and catalog digests never supply release authority. Coordinator fixtures
remain separate from production authentication evidence.

Initial conversion accepts separately verified pristine stock input and independently
known current ownership. They can differ, allowing preparation from a separately restored
stock tree. Windows existing-backup adoption now accepts an independently verified
canonical archive or recovers a complete candidate from exact legacy ZIP/current-file
sources. The original legacy ZIP remains independently preserved. See
[the backup contract](FullSourceInstallationService.Backup.md). A partial ZIP is
never qualified as a complete archive. The trusted stock contract must include all
original files, StreamingAssets, boot.config and empty directories.

## Authority, storage and bounds

The only recovery identities are entries already configured in `LauncherConfig`.
Roots must be canonical local paths and match their configured executable. Duplicate or
overlapping configured roots fail. Serialized roots never redirect recovery. No active
marker, journal or engine `plan.json` is consulted to discover installation authority.

The canonical catalog paths are:

- `<launcher storage>/full-source-v1/<installation GUID>/installation.json`
- `<launcher storage>/full-source-v1/<installation GUID>/<transaction GUID>.json`
- `<installation parent>/.redux-fullsource-<installation GUID>/<transaction GUID>-source`
- `<installation parent>/.redux-fullsource-<installation GUID>/<transaction GUID>-work`
- `<installation>/uninstall.zip`

The head persists the exact original stock contract, published owned tree and release
identity, archive reference, configured root/identity, ownership exclusions, retention
policy and pending transaction ID. The operation persists exact observed current and
target contracts, authenticated release contract, derived boot preferences, archive
reference, source/work locations, protected roots, transaction ID and plan digest.
The ready operation is saved before `Apply`. The original stock identity and archive
reference remain immutable across generations.

Version 1 uses canonical System.Text.Json bytes in a digest envelope. Unknown fields,
versions, duplicate properties, noncanonical bytes, bad digests, invalid ownership paths,
root aliases and changed evidence are rejected and retained. Each envelope is bounded
to 96 MiB, with the Common manifest/plan limits still applying. Base64 envelopes add
metadata overhead, so a plan near Common's maximum can hit the catalog limit first.
There is no unbounded in-memory operation history. At most 1024 protected roots are
accepted. New Windows GUID siblings do not accumulate a slot for every retained
transaction directory. Retirement never recursively deletes unknown storage.

The same transaction path validator enforces native Windows path identity, aliases,
reparse points and named streams on catalog and stage paths. `ValidateControlledPath`
is the narrow new Common API for that purpose. `StockArchive.PublishFile` is now public
so durable catalog publication uses the existing same-volume native no-copy rename.
New paths require absence. Replacements recheck the exact prior catalog bytes immediately
before publication. A conflicting head or operation file is not overwritten.

A nontruncating exclusive coordinator lease covers preparation, engine calls, recovery,
publication and launcher-managed startup. It excludes other coordinator instances for
the same configured ID and launcher storage. The engine additionally takes its installation
lease. GUI and CLI launch acquire the coordinator lease before the actual native or Steam
handoff, including before a catalog exists, and hold it until the intended game is detected,
the handoff fails, waiting is cancelled or the existing startup timeout expires. Native
Windows direct startup uses 30 seconds, Steam and legacy Wine use 120 seconds. A game-session
exit wait does not retain the lease. Unrelated configured roots have independent leases.
Catalog-free legacy POSIX launch returns no full-source lease and retains its old path.
Cataloged POSIX full-source launch and installation remain unsupported.

Owned-file verification is synchronous and accepts the caller's cancellation token.
GUI launch performs lease acquisition on a worker task, keeps its existing Cancel action
available during verification, then resumes on the UI context before process handoff.
CLI launch passes its existing interrupt token directly. Both check cancellation again
inside the acquired lease before dispatch. A cancelled verification disposes the lease
and does not start a process. Once handed off, the existing startup timeout, process
detection, cancellation and game-session behavior still apply. The file-stream coordinator
lease can be released on the resumed caller thread.

Cancellation is checked before reservation, during the existing one-MiB digest loop and
before returning the lease. A synchronous filesystem read and any already-required pending
transaction recovery retain their existing completion boundaries. EnsureReady and
GetPublishedIdentity keep their existing paths. Reconcile retains its original archive
health check and adds no owned-tree hash.
No cache, timestamp shortcut, background repair or permission change is added.

Apply, explicit repair and uninstall enforce the precondition inside the production
coordinator under that lease. They use the existing game process service to check the
configured intended Windows executable, and the existing file-lock service to probe all
installation files, including mods and uninstall staging folders that legacy operations
skip. Identifiable unrelated games do not block this root. Inaccessible game process
identity or failed process discovery rejects mutation rather than assuming safety. The
game is never force-stopped. Checks run before original archive, operation or catalog
publication, and again after preparation before beginning a generation. Pending reconciliation
checks the same precondition before recovery can roll back files or publish catalog state.
Rejection retains installation, original archive, unknown files and prior recovery evidence.
The empty coordinator lock may be created to establish exclusion, but is never truncated.

Launch lease acquisition verifies the current published ownership manifest after reconciliation
and before returning the reservation. It reuses the generation verifier's controlled paths,
file lengths and SHA-256 hashes. Changed, missing, unreadable or unsafe owned paths fail through
the existing launch failure handling. Explicit repair remains a separate requested operation.
The already read catalog is reused on the healthy path. Only pending recovery rereads the
published catalog, so a repair rollback is checked against the desired published ownership.

The content check runs once per launch reservation. Ordinary inspection, startup reconciliation
and EnsureReady keep their existing metadata path. Verification reads only manifest-owned files
and checks expected directories, with no recursive game scan or stock archive payload read.
There is no trusted content cache. Timestamps cannot replace the expected hashes. Large owned
generations still add IO latency before handoff. The GUI remains responsive during that work
and can cancel it through the lease API's caller token.

Healthy catalog inspection, published identity and reconciliation with no pending operation
do not run mutation preconditions and remain usable while the game runs. Startup reports
blocked pending recovery and preserves it for a later retry. Inspecting a catalog or a
transaction marker alone never authorizes rollback.

The reservation closes the launcher's own readiness-check/handoff gap for consumers using
this configured coordinator. File-use probes are observations, not retained handles on every
installation file. They do not sandbox Steam, external tools, another launcher configuration
with different storage, a legacy operation already in progress or user-started games. A late
Steam/game start after timeout or cancellation can still race an external writer. Source input
writers also remain an external exclusion requirement, with exact hash checks detecting changed
input. No broader polling or automatic process termination is introduced.

## Durable sequence and restart

1. Authenticate and verify prepared input. Persist initial stock/ownership authority
   when needed. Reject current edits and unowned collisions before beginning a plan.
2. Persist the preparing operation, then the head's pending ID before creating archive
   or source-stage bytes. Unreferenced operation files cannot have called the engine.
3. Create the complete archive through `StockArchive`, verify against the original
   contract and record its complete SHA-256/length. Existing referenced archives are
   checked and reused. They are never regenerated during an upgrade or uninstall.
4. Stage and verify a complete target under a new owned source root. Persist the ready
   operation with archive reference and complete plan digest before calling `Apply`.
5. Save the verified engine result, then atomically publish ownership and release
   identity. Configuration version/channel fields are a retryable UI projection of
   the durable catalog. The installed identity can be read without facade assembly
   reflection. A future selected channel preference remains distinct from installed
   version metadata.

Startup reconciles all configured roots through `Ksp2InstallService`. Launch and install
guards reconcile the actual configured root. They inspect the catalog even when the
engine has already cleared its marker. Early self-update stages, which lack the DI
coordinator, read bounded catalog intent only as a protection signal and block pending
or corrupt metadata without authorizing any recovery.

Preparing-only records verify the old observed generation, retain all stage/candidate
evidence and publish rollback. A created complete archive can be verified against the
persisted stock contract and retained even if its reference had not yet been published.
Ready and terminal operations call `RecoverWithArchiveHealth` with the independently
persisted plan. A complete committed engine record publishes the new generation.
Rollback preserves prior managed ownership and identity. Archive failure is reported
separately after that generation has been published, and blocks subsequent apply or
stock restoration. It never replaces the archive or hides a committed outcome.

Catalog candidates are written with `CreateNew`, flushed to disk, and published by
native rename. Unpublished candidates remain as evidence and never supply authority.
Terminal plans, journals, operation contracts and retirement receipts remain. Published
Windows transactions retire only exact disposable source and engine payloads under the
storage contract. No unknown storage is recursively deleted. Durable directory metadata and storage
controller power-loss ordering remain filesystem-dependent, as in the Common engine.
Tests simulate process interruption, not hardware power cuts.

## Operations and ownership

GUI callers supply `InstallPlan.FullSourceTarget`, its configured installation ID,
optional original stock preparation and the explicit repair flag. Such a plan cannot
contain legacy steps. Full-source uninstall and plain revert plans route to the same
coordinator. Settings' direct uninstall does so too. CLI entry points are:

```powershell
redux-launcher-cli full-source apply <release-id> --channel <channel> --install <configured-id>
redux-launcher-cli full-source repair <release-id> --channel <channel> --install <configured-id>
redux-launcher-cli full-source recover --install <configured-id>
redux-launcher-cli full-source uninstall --install <configured-id> --yes
redux-launcher-cli uninstall --install <configured-id> --yes
```

Apply, update, downgrade and channel switching share the target-generation operation.
Uninstall uses `StockArchive.RestoreToNewDirectoryAsync`, a new stock stage and the
original stock target. It preserves `uninstall.zip` and exact original boot.config.

Ordinary update rejects changed or absent owned files. Explicit repair observes bytes
only at previously owned paths, supplies those actual bytes for verified undo, and
omits absent owned files from the actual-current contract. Unknown disk paths never
become managed ownership. On repair rollback the known desired ownership stays old,
while the engine restores the independently observed damaged state. That state still
needs repair. An unchanged old release identity is not a claim that damage disappeared.

Retained unowned files are excluded from published ownership even when target hashes
match. Their exclusions and preexisting unowned directory policy survive later plans.
Previously owned directories that remain because unknown children prevented empty
removal are carried forward as explicit unowned directory exclusions. This permits
later stock restoration without adopting or deleting the unknown children.

Boot preferences enter the target contract only when independently authenticated
metadata explicitly permits the version-one boot derivation. The signed source
boot.config is verified before derivation in the owned stage. The resulting file hash
and generation identity are persisted before engine mutation. All other release files
remain exact. Legacy `ApplyActiveInstallBootConfig` remains blocked for cataloged roots.
An initial UTF-8 signature is consumed during derivation so the first setting cannot
be hidden or duplicated. Stock uninstall restores that original signature byte-for-byte.
Changing a preference after commit does not silently rewrite a signed/managed file.
Applying the preference requires another verified target-generation operation.

## Parent disposable validation

Run from `ksp2redux.tools`, with no game or editor control:

```powershell
dotnet test tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj --no-restore --filter FullyQualifiedName~NativeContainedIntegration --logger "trx;LogFileName=parent-contained.trx" --results-directory "../Library/Omnipatching/fullsource-coordinator/test-results" --verbosity normal

dotnet test tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj --no-restore --filter FullyQualifiedName~FullSourceInstallationServiceTest --logger "trx;LogFileName=parent-coordinator.trx" --results-directory "../Library/Omnipatching/fullsource-coordinator/test-results" --verbosity minimal
```

The native integration creates and retains small real Windows stock/release-two/
release-three inputs, exact manifests, `inputs.json`, catalog, stages, original archive
and terminal engine evidence under the Unity checkout's ignored
`Library/Omnipatching/fullsource-coordinator/native-<GUID>/`. The TRX attaches its exact
input location. It runs apply, update/channel change, rejected ordinary drift, explicit
repair/downgrade and uninstall. No main installation or 33 GB archive is used.
The release provider is an independently pinned test authority, not a production
authentication claim. CLI apply with default DI now requires supported schema-three metadata
from its configured online authority and a complete supported baseline.

Live-use evidence is under the checkout's ignored
`Library/Omnipatching/fullsource-live-use/`. The before-guard run executes real coordinator
and CLI entry points with injected process use and real mock-filesystem file handles.
It records seven causal failures and one healthy read-only pass before the guards exist.
The original source snapshots and SHA-256 manifest are retained there. No production file
was replaced with an unfixed copy to obtain those failures.

The focused regressions use actual coordinator apply/uninstall/reconcile, shared GUI
install-plan, Settings uninstall, startup load, CLI full-source, CLI Steam and GUI native
launch paths. Process handoffs/detection are mocked, so no game, Steam or Wine is started
and no process is stopped. Tests check same-root startup/install exclusion, unrelated-root
progress, held managed files/archive, no authority/evidence loss on rejection, healthy
read-only use, interruption rollback and normal apply/recovery. The existing native contained
tests check small disposable Windows filesystem publication and retain their late-arriving
file-reader rollback coverage. They do not validate native game process discovery or launch.

Parent-only contained integration commands from `ksp2redux.tools`:

```powershell
dotnet test tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj --no-restore --filter FullyQualifiedName~NativeContainedIntegration --logger "trx;LogFileName=parent-live-use-contained.trx" --results-directory "../Library/Omnipatching/fullsource-live-use/test-results" --verbosity minimal

dotnet test tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj --no-restore --filter FullyQualifiedName~LiveUse --logger "trx;LogFileName=parent-live-use.trx" --results-directory "../Library/Omnipatching/fullsource-live-use/test-results" --verbosity minimal
```

## Remaining cutover work

Configured online release preparation and recipe/pack fetching are now implemented.
Independent storefront/offline authentication, whole-game publication, POSIX native transaction, durable directory flush,
mount/metadata policy and genuine Linux/macOS launcher-host validation remain required.
New full-source operations explicitly reject unsupported hosts. Catalog-free legacy
Linux/macOS paths keep their existing behavior, including Wine support on macOS.

New Windows operations use changed-only source/engine staging and retire verified
terminal payloads. Preparation-cache retention, peak stock-restoration staging,
representative full-game lifecycle disk/time measurements, cancellation latency/resource
tuning and unsupported legacy source/ownership gaps remain work. Provable Windows
existing-backup adoption follows the linked backup contract.
There is no automatic retirement or migration between configured installation identities
or roots. Full asset/source reconstruction, game startup, save/mod generation cutover and
Unity/gameplay fidelity validation are separate remaining gates. No full-game installation,
Unity compilation or gameplay success is claimed by these standalone tests.

The coordinator has real state shared by independent GUI/CLI/startup consumers. Its
catalog/operation/envelope records are distinct persisted shapes. Release/preparation/status
records are the shared public contract, and the unavailable provider supplies the explicit
production dependency boundary. These are the concrete reasons for the new types.
