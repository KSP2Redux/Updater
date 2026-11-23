# Full-source launcher coordination, version 1

`FullSourceInstallationService` is the production Windows coordinator shared by the
GUI install-plan service, Settings uninstall, CLI `full-source`, startup inspection
and launch guards. Both normal containers register it. Legacy mutation remains guarded.
This moves installation coordination forward. It does not complete Omnipatching or
enable unauthenticated local release installation.

New operations use the canonical engine's version-two changed-file staging policy.
The complete prepared target is still verified. New Windows operations copy only
changed owned inputs into the coordinator's separate source
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
boot.config settings against its publisher trust configuration. It must separately
establish the original complete storefront contract and known initial conversion
ownership. Source file hashes and a local catalog checksum are not authentication.

The production `FullSourcePreparationService` now uses configured HTTPS/GitHub authority metadata
and reconstructs exact packs against publisher-curated stock definitions. Details and authentication
limitations are in `Documentation/Omnipatching/LAUNCHER-PREPARATION.md` in the game checkout.
Local JSON, arbitrary ZIPs and catalog digests never supply release authority. Coordinator fixtures
remain separate from production authentication evidence.

Initial conversion requires verified pristine stock and independently known ownership.
Legacy Redux upgrades run the existing uninstall followed by stock verification and
a fresh install under one coordinator reservation. Complete canonical archives remain
eligible for adoption. See [the backup contract](FullSourceInstallationService.Backup.md).
The trusted stock contract includes StreamingAssets and empty directories. boot.config is in no contract.

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
target contracts, authenticated release contract, archive
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
original stock target. It preserves `uninstall.zip`.

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

`KSP2_x64_Data/boot.config` is outside the coordinator. Parsing a contract drops its
entry, so no hash, file-set check, recipe, stage, backup, journal or catalog covers it,
and any boot.config or none matches stock. `BootConfigSettingsService` edits the file by
key after an install, update or repair, when the graphics jobs option changes and before
each launch. It writes the release's settings from the release descriptor plus the
launcher's own, which win. Release keys overwrite the player's own edits of those keys on
every apply. `build-guid` is never managed.

The release's settings are also published in the catalog (`ReleaseBootConfig`), from the operation
(`BootConfigSettings`) that committed the generation, so they survive a stop between the commit and
the launcher configuration write. `Ksp2InstallService.ApplyActiveInstallBootConfig` copies them into
the installation's record before it applies a Redux build. An empty catalog record means the release
has no settings and clears a stale record. A catalog from an earlier launcher has none (null), and then
the configuration record is left as it is.

The step records each key's stock state on the installation the first time it manages the
key, including keys that already hold the wanted value. A stock file has exactly
`wait-for-native-debugger=0`, `hdr-display-enabled=0`, `gc-max-time-slice=3` and a
`build-guid`, so those three keys are recorded with those values and every other key as
absent. The file's current content is not the original, because an earlier build may have
written to it. The record is saved before the file is written. If it cannot be saved, the
file is left alone until the next try. A key that a release drops is written back to its
stock state, and leaves the record only after that write worked. Uninstall puts every
recorded key back and clears the record. Full-source uninstall reads valid settings from the verified
stock archive's legacy boot.config entry before the engine runs. The committed stock generation
records them in `StockBootConfigRestore` so a restart can replay the restore. The archived values
take precedence over the install profile's undo record. The coordinator clears the pending restore
after the file is restored and any profile undo record is cleared. Changing an
installation's folder clears the record. The step checks no hash and never fails for file content. It reads at most 64 KiB, skips a larger
file with a log line, and replaces the file from a temporary file in its folder that it flushes first, so an
interrupted write leaves the old content. Before a write it deletes the leftover `boot.config.<32 hex digits>.tmp`
files of earlier interrupted writes in that folder, and nothing else. A revert that failed at uninstall keeps its record, and the launcher tries it again
at every start for an installation that is no longer Redux.

Data written before boot.config left the contracts is read without that entry:

- A catalog whose Stock or Ownership lists it is rewritten once in canonical form on its
  first read, after all other checks passed. A release is named everywhere by the digest of its
  normalized contract (`GetNormalizedTargetManifestSha256` on the descriptor), which is what the
  feed plan, the prepared target and the installed identity carry, so an installation and the
  feed agree on a release published in either form. The identity also retains the original published
  digest when different, so update recipes advertised with either digest can use the installed files.
  The catalog's normalized release digest comes from the ownership contract when it is the full release
  contract, or from an operation that committed the recorded release. A later rolled-back operation
  does not replace that release. Migration retries if its committed record is temporarily unavailable.
  Source pins of the original backup that name boot.config are dropped, and so is its journal pin
  when boot.config was the only file recovered through that journal.
- An operation of an earlier build that is still pending is recovered only when it stopped
  while preparing, or while ready before the engine started, with no engine marker or work folder
  (that build wrote ready before entering the engine, which creates the marker first): the game
  folder is verified against the normalized current contract and the existing preparation-only
  reconciliation publishes RolledBack. A ready operation skips the plan digest check, because that
  digest covers contracts listing boot.config, and a game folder that fails verification keeps the
  refusal below. The operation file stays as evidence, and the head is replaced only when its bytes are
  still those the operation was bound to. Once the engine ran, its own journal of that
  operation lists boot.config, so this build cannot recover it. Every action then fails with a
  message that names the way out: restore the original game files (uninstall the game in Steam or
  Epic and delete what is left of the game folder, or replace the portable copy), delete the engine
  marker `.redux-transaction.active` in the game folder if it is still there, delete the
  `.redux-fullsource-<id>` storage folder beside the game folder and the launcher's catalog folder,
  then install Redux again. The message says saves are not affected and carries the real paths. The
  marker is named because a storefront reinstall can leave it, and it blocks every action by itself.
  Nothing is changed on the way. The launcher's startup self-update check shows this message once
  instead of failing startup, and the launch button shows it again.
- A retained terminal operation that lists it is left alone. Payload retirement compares exact
  saved plan bytes, so that operation keeps its payloads.
- An `uninstall.zip` that holds boot.config verifies with that entry ignored, when its embedded
  manifest is the older form of exactly the expected contract. Restoring never extracts the
  file. Every other entry and byte is checked as before.
- Caches under `baselines` and `prepatch-backups` that an earlier build named by the digest of
  a contract listing boot.config are proven by that name and the boot.config they hold, or by the
  older embedded manifest of an archive. When one holds exactly the bytes of the current
  contract it is renamed to the current name before anything needs space to rebuild it. Otherwise
  it is renamed into `trash` in the preparation cache and deleted from there once the cache of
  the same contract verified. Every preparation first finishes the deletions an earlier run left in
  `trash`. A tree or archive that fails its check stays where it is. Nothing outside `baselines`,
  `prepatch-backups` and `trash` is touched. The caches of the original stock are reclaimed when
  that original is next needed, not before.
- A release published in the older form still reads: its artifact drops the boot.config recipe or
  exact copy, and a source contract or update source digest may name either form. Every artifact the
  CI built from 14 November 2026 also lists boot.config as a recipe source. That source is dropped on
  reading and the other sources are renumbered, so such an artifact installs and repairs when no
  recipe reads bytes from it. A recipe that does read from it is refused with "This release needs
  boot.config as a recipe input and cannot be installed by this launcher." The pinned prepatch
  contract is published as pinned, so `PrepatchManifestSha256` and the names derived from it do not
  change. It is read without boot.config.

Journals keep their retired boot derivation fields so older ones still read.

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
