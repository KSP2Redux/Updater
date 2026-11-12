# Windows terminal storage lifecycle

This extends the existing coordinator and transaction engine. It does not scan or
clean arbitrary retained conversion folders. No original installation, preparation
baseline, authoring source or complete `uninstall.zip` is a deletion target.

New Windows source stages copy only changed owned target files and an explicitly
derived boot.config. Complete current/target/release contracts remain in the
operation. The engine already consumes these sparse inputs through version-two
changed-file staging. Input preparation stages have no immutable ownership-transfer
lease, so the coordinator does not borrow or hardlink their mutable files. Their
separate preparation cache remains outside this retirement API.

Retirement runs after successful ownership publication, after pending recovery has
published a healthy terminal outcome, and before the next ordinary apply/uninstall. Healthy
read-only inspection and launch readiness do not initiate retirement. The public
`RetireCompletedPayloadsAsync` API retries it independently. All paths require the
existing exclusive coordinator lease, no pending catalog ID or active engine marker,
the configured game-use checks, and the engine's exclusive installation lease.
The game is never started or stopped.

Explicit repair defers retirement of the preceding operation. It preserves every
remaining prior source, payload and receipt byte while the existing owned-path
observation accepts changed or missing managed files. Ordinary retirement and
ordinary apply still reject that damage. A successfully repaired generation retires
its own terminal payloads normally. The preserved preceding history needs explicit
review after the new head supersedes it.

The head must identify the exact terminal operation and result. Committed ownership
must equal that operation's target with the existing unowned exclusions. Rollback
verifies the actual old observation, including damaged bytes restored by explicit
repair, against its published ownership bounds. The operation's independently saved
plan digest pins the entire canonical engine plan. Only its original exclusion lists
are read from disk, and rebuilding every other field must reproduce all plan bytes.
Original stock identity, complete stock contract and archive reference remain fixed.
The archive reference was originally published only after complete StockArchive
verification. Before deleting, the engine rehashes and holds those exact original ZIP
bytes read-only. A verified completion record grants no further deletion and avoids
another stock-sized archive scan. Apply/recovery/uninstall retain their existing
independent archive-health checks.

New heads use `retain-contracts-v1`. A legacy `retain-all-v1` head remains readable
and is durably transitioned under the lease before retirement authority is saved.
Pending legacy operations are reconciled with their original bytes before this
transition. An absent `SparseSourceStaging` flag preserves complete-source staging.

## Exact retained and retired data

| Root | Retained | Eligible payload |
| --- | --- | --- |
| Configured installation | All published files, user/unowned data, transaction lock, complete original uninstall.zip | None |
| Launcher full-source-v1/installation-ID | installation.json, operation-ID.json, coordinator.lock, operation-ID-retirement.json and unpublished candidates | None |
| Owned operation-ID-work | Exact plan.json, original journal and recovery when present, payload-retirement.json and payload-retirement.complete | Only pinned numbered blobs within target, undo, removed and rollback, then those empty directories |
| Owned operation-ID-source | Exact source contract persists in the operation/receipts | Only manifest files and directories, then the empty root |
| Preparation cache | baselines, artifacts, downloads and stages | None in this API |

`operation-ID-retirement.json` saves the published head bytes, operation digest,
plan digest, source manifest and whether engine storage was observed absent after
terminal rollback. `payload-retirement.json` saves the exact source/work inventories
and authorization digest. The completion file contains that inventory's SHA-256.
These are persisted shapes, not new runtime managers or a cleanup framework.

The full inventory is checked before the first deletion. Every individual deletion
rechecks native physical identity, containment and exact file bytes. Directories are
removed nonrecursively. Unknown entries, changed bytes, named streams, reparse points,
aliases, conflicting journals, new configured installations inside a stage and
unhealthy archives fail while preserving remaining payloads. Native checks use the
existing volume-identity/path validator. No hardlinks are created.

An interruption leaves the durable inventory. Retry permits only already deleted
inventory members to be absent. Unpublished intent and completion records can resume
only exact trusted prefixes. A complete receipt rejects any recreated payload,
including byte-identical files. Cancellation after commit does not undo the published
generation. A later operation uses a new transaction ID, never resumes an intentionally
retired payload. If terminal rollback has no engine storage, independent coordinator
authority witnesses that absence before the engine creates retirement metadata. The
original pinned plan is retained without inventing an engine journal.

Only the transaction actually bound by the current head can newly acquire retirement
authority. Older unbound histories, preparation-only partial stages and unexplained
payloads remain for explicit review. They are not guessed to be disposable. Future
ordinary apply/uninstall operations finish eligible preceding retirement before
replacing that head. Explicit repair preserves the preceding history as described
above. Historical
metadata no longer consumes a protected-root slot for every sibling: new source/work
roots are absent canonical GUID siblings, whose physical disjointness is checked by
the existing native validator. Other configured installations and catalog storage
remain protected. Retained sibling directories are never deleted by that change.

## Focused Windows verification

From ksp2redux.tools:

```powershell
pwsh -File scripts/test-full-source-preparation.ps1 `
  -IsolatedBuild `
  -TestFilter 'FullyQualifiedName~StorageLifecycle' `
  -ParentFeedbackPath '..\..\.agents\omnipatching-work\launcher-windows-storage-lifecycle.feedback.md'
```

Supply the actual absolute parent feedback path when using the runner. It preserves
the exact command, source pins, log, TRX and machine-generated summary in a fresh
ignored Library/Omnipatching/launcher-preparation/tests-* directory. Tests use tiny
synthetic roots. Native cases retain their inputs, manifests, catalog, receipts and
results below Library/Omnipatching/fullsource-coordinator/native-*. No retired
fullscale fixture is resumed. Broad unchanged suites and whole-stock copying/hashing
are unnecessary for this lifecycle verification.

This is Windows standalone evidence. It does not establish hardware power-loss
ordering, a hostile-writer sandbox, native game-process discovery, whole-game delivery,
legacy partial-backup migration, Release GUI/CLI distribution, Unity compilation,
gameplay, Linux/macOS behavior or source-player completion.
