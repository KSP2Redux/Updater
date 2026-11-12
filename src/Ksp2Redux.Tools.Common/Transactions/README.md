# Installation transactions, versions 1 and 2

This is the shared engine for the next production launcher integration slice.
The engine remains independent of legacy cache and patch mutation. The launcher now
has a bounded catalog coordinator described in
[`FullSourceInstallationService.md`](../../Ksp2Redux.Tools.Launcher/Services/Install/FullSourceInstallationService.md).
Experiments use synthetic files in mock Windows filesystems or contained native
directories. The 16 October stock-sized matrix reuses the previous complete archive
read-only, one disposable restored installation and retained component outputs.
See Documentation/Omnipatching/LAUNCHER-FULLSCALE.md in the game checkout for exact
inputs, copy/resource measurements, failures and remaining delivery requirements.
The main installation is untouched.

The Windows coordinator now retires exact completed source and transaction payloads
through this engine while retaining the full original archive, contracts, journals
and receipts. The current rules and limits are in
[`FullSourceInstallationService.Storage.md`](../../Ksp2Redux.Tools.Launcher/Services/Install/FullSourceInstallationService.Storage.md).
Preparation caches and unbound historical payloads are outside automatic retirement.

## Contract and ownership

`InstallTransactionPlan` binds a unique transaction ID, canonical absolute local
paths, independent current and target generation contracts, the original stock
contract, a previously verified complete archive reference, and ownership policy.
Current and target identities/provenance are carried by `StockBaselineManifest`.
The full plan is canonical UTF-8 JSON. Its SHA-256 binds every journal
record and the fixed installation active marker.

Version one retains complete target/undo staging and its original canonical bytes.
Version two is selected explicitly with `useChangedFileStaging: true`. It stages
only new or changed owned target files and keeps undo only for changed or removed
current files. Unchanged files and explicitly retained unowned files are freshly
verified in the active installation and never gain staging/undo blobs. Source
files are required only for those changed target outputs. Foreign blobs at skipped
indexes fail before recovery mutation or preparation cleanup. Complete current and
target contracts, hash checks, path validation, archive verification and journal
ordering are unchanged. Disk admission uses the selected staging sets, including
the largest required rollback candidate, rather than both complete game sizes.

New launcher coordinator operations persist this choice. An absent choice in an
existing operation reconstructs the original version-one plan, preserving its
digest and restart behavior. A dedicated compatibility regression executes an
interrupted version-one ready operation through the updated coordinator.

Version three stages the exact Windows stock generation from the pinned archive.
Version four additionally uses the renamed original as undo for files absent from
stock. Before recovery changes any active file, it checks that each such original
exists in exactly one of its active and private locations and matches its contract.
The rename preserves the original file's Windows security descriptor. Recovery
moves it back, and terminal retirement can remove it after a successful restore.
Version five applies the same removal rollback to Windows installs and updates
whose targets come from verified directory stages.
Version six moves new files into native staging from the operation's owned
sibling directory. Preparation recovery returns verified files to the source
when interrupted. Publication keeps destination ACL inheritance and needs room
for only one new file beside the already allocated stage.
The coordinator keeps the original plan version when resuming an older operation.

The launcher must authenticate release contracts and verify `uninstall.zip` with
`StockArchive.VerifyAsync` against the original stock contract before constructing
the archive reference. Apply verifies the reference's complete byte hash
and holds the archive read-only through mutation. It never creates,
overwrites, deletes, or restores over that archive. Archive verification is a
caller contract, not a claim inferred from whatever archive is on disk.

Recovery does not need archive content to restore verified undo. It first
reconciles the transaction, then checks archive health separately.
`RecoverWithArchiveHealth(plan)` returns `(Result, ArchiveFailure)`. A null failure
means the referenced archive bytes and path verified. Missing, changed, truncated,
locked or unreadable backups return the actual exception separately from the
terminal transaction result. The launcher must surface that problem and block
operations requiring stock restoration. It must not replace or delete that archive.
The original `Recover(plan)` method also completes recovery, then raises an
`IOException` for an unhealthy archive with `TransactionRecoveryResult`,
`TransactionArchiveFailure` and `TransactionRoot` in exception `Data`. An ordinary
return therefore still means a verified archive. Future `Apply` always requires
the same valid immutable archive, even after successful undo recovery.

Current files are owned and must have exactly their supplied bytes. Target-only
files must be absent. A preexisting unowned target file is accepted only when
its path is explicitly listed in `RetainedUnownedFiles` and its bytes exactly
match the target. It stays unowned and is never mutated or removed on rollback.
The launcher must exclude those paths from the next generation's managed file
inventory. An unequal unowned collision always fails.

Manifest directories include inferred parents. The caller must carry directory
ownership separately: `ExistingUnownedDirectories` may name current or target
directories and must persist across later plans. Such directories must preexist
and are never deleted. Every other current directory is treated as owned.
Target-only directories must be absent unless this explicit policy lists them.
Old owned directories are removed only when empty, using nonrecursive deletion.
Unknown children survive. Empty target directories are materialized and verified.

Fresh installation uses the independently verified stock file set as current.
Upgrade, downgrade and channel switch use the prior managed set as current.
Uninstall uses a NEW tree restored by `StockArchive` as target source and the
stock contract as target. Repair requires an explicit independently accepted
actual-current contract for damaged files. An ordinary plan rejects current
drift. A missing managed file can be omitted from that repair contract, while
its directory ownership must still be supplied. Unknown files are never scanned
into ownership merely because they happen to be under the installation root.

## Protocol and recovery

1. Validate paths, root relationships, expected current bytes, collisions,
   original archive hash and disk budget. Acquire an exclusive nontruncating
   `.redux-transaction.lock` in the installation. A nonempty lock file is a
   reserved-name conflict. The persistent active marker rejects stale work and
   conflicting work roots, including after a process dies and releases its lock.
2. Create and flush the installation active marker before creating the absent
   transaction root. Flush canonical `plan.json` and the `preparing` journal
   record before staging. Target, undo,
   removed and rollback blobs use fixed ordinal names derived from the trusted
   plan, never paths read from disk metadata.
3. Copy every target file to transaction storage, verify length/SHA-256 and
   flush its bytes. Copy every current file to undo storage with the same checks.
   Reverify current ownership before flushing `prepared` and `mutating` intent.
4. Create planned directories, rename removed files into retained evidence,
   use one same-volume rename for changed existing files and
   additions. There is no delete-then-copy replacement fallback.
5. Verify the entire target file and directory contract and all owned removals,
   then append and flush `committed`. Clear the active marker only afterwards.
   Undo, removed bytes and the complete journal remain on disk.

`Recover(plan)` requires the SAME independently trusted caller plan. It compares
the disk plan byte-for-byte with canonical serialization and validates the active
marker, bounded journal checksum/version and legal state transitions. No journal
supplies path or hash authority. The caller needs a separately trusted plan
catalog to rediscover interrupted work. There is deliberately no API that loads
an arbitrary journal and starts mutating the paths it names.

A complete committed record is authoritative: recovery verifies target bytes
and completes marker cleanup without rolling back. An incomplete trailing
record is accepted only when every existing byte is an exact prefix of a
canonical, legally next record generated from the independently trusted plan.
Complete records must match exactly, including newline, version, plan digest,
state and checksum. Empty lines, foreign bytes, invalid sequences and trailing
data after a terminal record fail. A torn first `preparing` record still means
preparation. A torn `prepared`, `mutating`, `rollingback`, `rolledback` or
`committed` record leaves the last complete state in effect. Actual active bytes
are verified before any recovery writes. In particular a torn commit without a
complete committed record rolls back only under verified old/new/undo contracts.
The incomplete install journal is retained byte-for-byte as evidence.

Recovery writes a separate bounded `recovery` journal with either
`aborting -> aborted` or `rollingback -> rolledback`. An interrupted recovery
append is completed only by appending the missing suffix of its exact expected
record, after reconciling the actual outcome. No truncation, reset or replacement
of either journal is used. The recovery journal cannot override a complete
committed install journal, and conflicting recovery evidence fails. Earlier
complete install journals containing `rollingback` and `rolledback` are supported.

A preparing journal verifies the old generation and cleans only fixed owned
staging names. Before cleanup, every existing target/undo blob must match the
trusted file contract or an exact prefix of independently verified source/current
bytes. A partial target blob therefore needs the verified source file available.
Unexplained staging bytes and any removed/rollback contents during preparation
fail before cleanup. The engine flushes `aborting` before deletion, deletes only
verified fixed staging names, then flushes `aborted` before clearing the marker.
`plan.json`, both journals and the work root remain as ownership evidence.
Repeated recovery verifies old bytes and requires staging directories to stay
absent after `aborted`. Recreated staging is an external conflict, not reacquired
ownership. A retry uses a new transaction identity and absent work root.

Prepared, mutating and rolling-back journals first
validate every original undo hash and every active old/new outcome before any
rollback mutation. Missing removed files require exact retained rename evidence.
Rollback copies and flushes an original undo blob into a fixed candidate before
single-rename publication, so undo remains available across repeated recovery attempts.
Changed owned active files may be absent when recovering earlier nullable-backup
`File.Replace` failures. The documented 1176 state leaves the replacement under
its original name, and 1177 can also leave the old file at another name. Recovery
requires exact verified undo, trusted plan/marker/journal authority and a genuinely
changed current/target contract before restoring that absent name. Surviving staged
new bytes, source bytes, undo and unknown displaced names remain intact. An absent
unchanged file is still an external conflict. This is not authority to reconstruct
unknown files or recover a missing complete-commit target.
An existing interrupted rollback candidate must match an exact prefix of verified
undo before it can be reset. Foreign candidate bytes are retained and fail.
Target-only additions are deleted only after exact target-byte verification.
Every successful rollback verifies the old generation and flushes `rolledback`
in the recovery journal before clearing the marker.

Initialization is also recoverable. When the work root is absent, a reserved
active-marker file containing an exact prefix of the trusted digest can be
completed after verifying the unchanged old generation. The caller must retain
the trusted catalog entry even when the marker is empty. A full marker binds an
empty work root or exact prefix of `plan.json` to that plan. The engine completes
the plan only when the work tree contains at most that regular plan file. The
earlier full-plan-before-marker sequence is recoverable when that complete plan
is the only work entry and the marker is an exact digest prefix. Initial
publication completion appends and flushes only missing suffix bytes.

An absent marker and absent work root is a read-only rollback result after
verifying the original generation. An unexplained existing work root without
the required marker/plan evidence always fails, including an empty unrelated
directory. This repair never attempts to guess ownership after older cleanup
code has already erased both plan and reservation evidence.

Ordinary failures return an exception with `Data["TransactionRoot"]` and retain
resumable work. Cancellation after staging begins invokes recovery without the
cancelled token. Preparing cancellation cleans owned content. Later cancellation
restores the old generation or reports `TransactionRecoveryFailure` and retains
the active transaction. `TransactionRecoveryResult` records a terminal result
when that recovery succeeds. Cancellation arriving after durable commit cannot
undo a committed generation.

## Bounds and rejected cases

- Plan metadata: 48 MiB. Each embedded manifest: 16 MiB. Each journal: 4096 bytes.
  Each manifest/union: at most 100000 entries, 128 GiB total and 64 GiB per file.
  Archive reference: positive length at most 130 GiB. Protected roots: 1024.
  Streaming uses a one MiB copy/hash buffer.
- Free transaction space must be known and exceed target bytes + current undo
  bytes + the largest rollback candidate + two plan copies + 64 MiB reserve.
  Existing source staging and old archives are already allocated. Filesystem
  allocation/metadata overhead can still cause ENOSPC despite preflight.
- Installation, target source and transaction roots must be disjoint. Transaction
  storage must be on the installation volume. Source/work may not overlap the
  archive. Inside the installation only immutable root `uninstall.zip` is allowed
  as the archive reference. Writable roots cannot overlap caller protected roots.
  The launcher must include all other source trees, retained transaction stores,
  archive trees and the user's main installation when operating a disposable copy.
- Traversal, relative roots, volume roots, UNC/device namespaces, ADS path syntax,
  reserved device names, trailing dots/spaces, unpaired surrogates, case aliases
  and file/directory ancestor conflicts fail validation. A path that native Windows
  resolves but validated enumeration cannot explain is rejected, including 8.3 names.
  Real Windows accessed paths and existing ancestors of absent paths must also match
  the normalized DOS path from `GetFinalPathNameByHandleW`, rejecting native root
  aliases before lexical protected-root comparisons can grant access. Existing reparse points
  are rejected along every accessed path, including ancestors.
- Native Windows stream enumeration rejects named streams on accessed leaf
  files/directories, including each managed directory entry. This avoids deleting an apparently empty directory with
  unrecorded stream bytes. Filesystems without stream enumeration fail closed.
  The injectable stream validator exists for virtual filesystems and must enforce
  the same contract in real adapters. The default uses the documented
  [FindFirstStreamW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirststreamw)
  and [WIN32_FIND_STREAM_DATA layout](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-win32_find_stream_data).
  Native queries use validated local paths with the extended `\\?\` prefix,
  including stream enumeration, so they do not depend on MAX_PATH opt-in settings.
- Case-only renames and all file/directory transitions are rejected by combined
  current/target path validation. Supporting a directory-to-file transition
  safely requires additional owned-tree intent and unknown-child handling.
- Unexplained corrupt journals, missing undo, unknown preparation entries,
  foreign markers and externally edited active files are retained and fail
  explicitly. Only exact own trailing-record prefixes and evidenced initial
  publication prefixes are recoverable. Disk metadata never grants path or
  hash authority. Torn commit recovery verifies undo and actual old/new outcomes.
- There is no undo retirement API in this slice. Terminal journals are retained
  deliberately. Production must implement a reviewed retention policy only after
  durable installation identity/ownership publication. Do not delete earlier
  generations or reset unknown storage as a convenience.

## Filesystem guarantees

This is a journaled series of per-file operations, not an atomic directory swap.
Readers can see mixed generations during mutation. The game and editor must be
stopped and the caller must exclude concurrent writes to all participating roots.
The installation lock covers engine instances, not Steam, Epic, antivirus,
user tools or an adversarial directory renamer. Checks are not handle-relative
Windows sandboxing and cannot close every hostile TOCTOU race.

Explicit `Flush(true)` precedes active mutations for plan, journals, staged bytes
and undo. Native Windows publication calls
[`MoveFileExW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)
with flags 0 for absent destinations or `MOVEFILE_REPLACE_EXISTING` (1) for verified
existing files. It never sets `MOVEFILE_COPY_ALLOWED` (2). .NET 10 `File.Move` enables
that copy flag internally, so the real adapter calls the native API directly. Virtual
filesystem adapters use `IFile.Move` and must supply the equivalent contract.
The validated contract is local Windows/NTFS with stable roots and no concurrent
writers. The tests prove observed NTFS rename outcomes and restart reconciliation,
not atomicity of every filesystem or a sequence inside a single .NET API.
The previous
[`ReplaceFileW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
combines multiple steps and explicitly documents partial failures. It is no longer
used for transaction or archive file publication.

`StockArchive` shares the same publication primitive. Its real Linux/macOS archive
path uses native `rename` on sibling paths in an owned stable parent, with no copy
fallback. This narrow archive operation does not adapt the transaction engine to
POSIX. Its existing portable test resolves the owned temporary path's ancestors
before use, including normal macOS `/var` aliases. All genuine Windows native
transaction and Windows destination-lock tests carry NUnit `Platform("Win")`.
Mock Windows contract checks run on every host. Production POSIX transaction
adaptation, mount identity, metadata policy, directory fsync and real host
integration remain required before launcher cutover.

The runtime does not expose a portable durable parent-directory flush here.
Rename/directory metadata ordering and hardware write-cache behavior therefore
remain filesystem-dependent. Unexpected power-loss outcomes fail verification
and retain evidence. The tests simulate process interruption and corrupted disk
metadata, not storage-controller power cuts. Historical byte-only transactions and
stock ZIPs do not record original Windows security descriptors. Timestamps,
hard-link topology, compression/encryption flags and other non-byte metadata are
not restored by the version-one contract. Locked/sharing-violating files and rollback failures
are surfaced, without deleting backup or undo as a fallback.

New real-filesystem Windows transactions qualify the existing file's owner, group,
DACL and descriptor control against their actual undo copy. Existing-file copies
are created as finite siblings in the original destination parent. Security is
supplied at creation. If necessary, only the newly owned candidate's Access section
is persisted through the modern Windows API, followed by exact full descriptor
readback. Original and parent permissions are never changed. Owner/group setters,
privilege changes and descriptor masks are not used. Unsupported descriptors fail
before publication. Qualified siblings transfer to private target, undo or rollback
storage through the same-volume native rename. Replacements retain this descriptor. New files
are created beside their destination to obtain normal destination inheritance.
Existing replacements use their qualified staged file without an additional payload
copy. For new files, one bounded destination candidate is copied, flushed, checked
and atomically renamed at a time. Its staged blob supports interrupted-copy checks until
the rename succeeds. The engine then retires the stage before copying the next file.

The small `windows-publication-v2` record binds both finite sibling roles to the
trusted plan before creation: new-file publication slots use target indexes, and
security-copy slots use changed current-file indexes. One security-copy slot is
reused serially for target, undo and rollback copies of that current path. The
initial guard rejects every occupied or managed slot. Complete v2 authority and
verified original/target preparation prefixes or genuine undo prefixes are required
before deleting an interrupted security copy. Completed aborts reject recreated
siblings. Partial markers cannot own sibling content. Additional disk admission
covers one largest transient copy. No ACL archive or plan/journal encoding change
is introduced. Preparation, recovery and terminal retirement validate or retain
the marker with existing metadata. The genuine v1 reader retains its original
new-file publication role and cannot own v2 security-copy siblings. Unmarked
legacy journals likewise cannot acquire those names. Their recovery can
preserve only the security present on the actual undo file, not reconstruct an
unrecorded historical descriptor. The stock ZIP format remains byte-only.

For marked Windows transactions, byte-equal old content is not sufficient to skip
rollback: its descriptor must also match genuine retained undo. If old bytes are
already present with changed security, restoration explicitly authorizes replacing
those old bytes, including a planned deletion. Full rollback security is checked
before recording success and again on terminal recovery. Unchanged paths without
undo remain untouched. Legacy unmarked journals retain their explicit byte-only
metadata limit. Once undo payloads have been retired, direct engine recovery cannot
re-establish this authority and refuses instead of reporting unverified success.
An aborted preparation never published active content, so its verified terminal
abort can retire disposable data without requiring undo that cleanup already removed.

## Production integration still required

The launcher coordinator implements the Windows catalog, prepared-target operation,
ownership publication, explicit repair, stock-target uninstall, GUI/CLI routing and
startup/launch recovery portions below. These are verified with small contained inputs,
not a full-game cutover. Full-game authenticated feed/recipe/pack delivery verification,
POSIX adaptation, preparation-cache/peak-stage cost reduction and full-game lifecycle
validation remain required. Windows terminal payload retirement is implemented under
the linked storage contract.
Its local catalog digest detects corruption and does not authenticate release data.

1. Persist authenticated current/target/stock contracts, archive reference, root
   policy and transaction identity in a trusted launcher catalog before calling
   the engine. Resolve an active marker to that catalog on startup. Recover before
   another plan, repair, game launch or boot-config mutation.
2. Reconstruct complete release targets into separate NEW verified source roots.
   Verify/create the immutable stock archive once through `StockArchive`, never
   through the old partial-cache flow. Reuse it across releases.
3. In `InstallPlanService`, replace full-source destructive restore/apply steps
   with one ownership-aware `Apply` call. Add progress/background scheduling and
   existing disk-space/log/error adapters. Do not run bundle conversion or legacy
   patch mutation alongside this transaction.
4. Publish the new managed inventory and generation identity only for a committed
   result. Carry unowned file exclusions and directory policy across generations.
   If publication itself fails, startup must reconcile the committed engine result
   with the catalog before another install. Coordinate `Ksp2InstallService`'s
   boot-config writes with recovery and ownership.
5. Integrate uninstall via `RestoreToNewDirectoryAsync` and a stock-target plan.
   Apply the same mechanism for downgrade/channel switching and explicit repair.
   Add reviewed terminal generation retention/retirement. Keep `uninstall.zip`.
6. Parent validation must exercise the complete production flow on disposable
   copies, release identity persistence, cross-process launcher races and bounded
   disk/locked-file scenarios. Unity/game validation remains the parent's work.

## Types and validation

The plan is a persisted contract with its own shape. The engine owns durable
filesystem transaction state shared by launcher operations. The result enum is
the terminal caller contract. The private native structure mirrors the Win32 ABI
required for stream validation, which `IFileSystem` does not expose. Native path
identity uses metadata-only `CreateFileW` handles with `OPEN_EXISTING`, all share
flags, `FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT` and normalized
DOS output from
[`GetFinalPathNameByHandleW`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew).
StockArchive owns the shared internal native file-publication method, avoiding a
second publication mechanism or a new utility type. The plan owns immutable
ordinal current/target dictionaries, canonical serialized bytes and their digest.
Public serialization returns a copy. Every disk path and byte check remains fresh.
Preparation-name lookups use a per-validation dictionary. Directory enumeration
is still repeated, and no representative scale result is claimed. No framework,
mock-only filesystem or new dependency was introduced.

Focused command, run from `ksp2redux.tools`:

```powershell
dotnet test tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj --filter FullyQualifiedName~Transactions --no-restore --verbosity minimal
```

The suite injects before/after failures into filesystem mutations and stream
writes/flushes, restarts new engine instances, and verifies managed hashes plus
literal unknown/archive bytes after recovery and retry. It also covers upgrade,
uninstall, explicit repair, unowned policy, empty directories, corrupt metadata,
protected roots, path aliases, links, cancellation and rollback failure. A real
contained temporary tree checks replacement, locks and native ADS rejection.
No Unity/editor tests or real installation tests are included.

The combined transaction and existing stock archive validation command is:

```powershell
dotnet test tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj --filter 'FullyQualifiedName~Transactions|FullyQualifiedName~Restoration.StockArchiveTest' --no-restore --verbosity minimal
```

Recovery repair final run on 2026-10-13: 422 passed, 0 failed, 0 skipped,
reported duration 1 minute 37 seconds. The byte-prefix loops cover 1431 journal
offsets across all six install phases and four recovery phases, plus 2264
initial plan/marker offsets including the earlier initialization order. They
include a further recovery interruption, fresh engine instances, repeated
recovery and exact active/undo/unknown/archive-byte checks. Separate cases cover
every before/after initialization completion and cleanup boundary, corrupted
commit prefixes, evidenced staging prefixes and recreated terminal work.

Six selected new regression cases were also run against the preserved original
engine in an ignored standalone harness. All six failed for the original torn
journal, missing retained plan and partial initial publication behavior. The
production engine was not swapped out for this check. Evidence is retained under
`Library/Omnipatching/recovery-edges/test-results/`: `recovery-final.trx` and
`original-regressions.trx`. The local harness and baseline copies stay ignored.

Comment and whitespace checks found no violations. Edited C# files retain LF
with a terminal newline. Git reports its normal LF-to-CRLF checkout normalization
warning for this repository. No production service or Unity verification was
performed by this repair worker.

Native recovery correction run on 2026-10-13: 448 passed, 0 failed, 0 skipped
(366 Transactions + 82 StockArchiveTest), reported duration 1 minute 33 seconds.
All previous meaningful cases remain, including 1431 journal truncation offsets,
2264 initialization offsets and the earlier six original-engine regressions.
Six partial-native-outcome cases simulate 1175/1176/1177 inside apply/rollback
publication, with another recovery interruption and retained old/new/unknown bytes.
Four failed against the retained pre-correction engine and all six passed after
the fix. A separate ignored five-case harness covers missing changed names,
missing/changed archives and archive publication loss. All five fail against the
retained pre-correction implementation and pass against the repaired sources.

Real NTFS evidence includes short/long protected-root aliases, descendant short
aliases, file/directory ADS, a directory symbolic link, 397-character paths,
locked/missing/changed backups, destination-lock publication failure 5, and
cross-volume publication failure 17 with both exact files retained. The 1176/1177
partial states are injected, not claimed as naturally reproduced native errors.
Windows-native tests are explicitly constrained to Windows. Linux/macOS execution
was not performed for this correction, so platform skipping is not production
transaction support. No scale measurement, Unity compile or gameplay run occurred.

Evidence stays in ignored `Library/Omnipatching/native-recovery/` at the Unity
checkout root: final-complete.log, test-results/native-recovery-final-complete.trx,
partial-before/after logs and TRX, retained-before/after-fix TRX, native-cross-volume
TRX, baseline source copies and scope hashes. A first destination-lock test assumed
error 32, observed error 5, and failed that error-code assertion. Its log/TRX is
retained. The final test accepts the documented access/sharing failure boundary
and checks literal old archive and source bytes. No production service was switched.
