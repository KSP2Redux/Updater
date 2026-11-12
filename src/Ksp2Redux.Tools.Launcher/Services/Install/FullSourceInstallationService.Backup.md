# Windows existing-backup adoption

This is the production preparation/coordinator path for an existing uninstall.zip.
It does not inspect the user's installation during implementation tests, download
proprietary content, or revive the retired fullscale fixture.

The actual legacy CacheService excludes StreamingAssets, mods and UninstallTemp.
Its ZIP entries use native Windows separators and omit empty directory records.
Ksp2Patch keeps operations and final hashes inside patch packages, but the installer
does not persist a complete installed-file ownership catalog. An arbitrary current
folder listing therefore cannot establish ownership of added legacy files.

## Sources and authority

Fresh configured publisher metadata still supplies the complete original
StockBaselineManifest, including identity, provenance, hashes, lengths, directories
and required metadata. An existing canonical ZIP must match that exact contract.
Its bytes can be adopted in place, without rebuilding or renaming it. A canonical
manifest with different provenance, incomplete entries or foreign metadata fails
strict verification and does not silently enter the raw legacy fallback.

For a raw legacy ZIP, StockArchive bounds the actual ZIP directory before allocating
entries. It rejects unsafe names, duplicate aliases, links and conflicting entries.
For each trusted original stock file it prefers an exact ZIP entry, then an exact
hash/length match at that file's current installation path. A directory cannot
substitute for an empty file. Safe unknown ZIP entries are never merged into stock:
the entire original ZIP is retained byte-for-byte. Missing/mismatched originals
produce a complete path-by-path diagnostic and StockArchiveCoverageGaps exception
data. Uncovered changed StreamingAssets remain an explicit blocking gap.

The third source is a read-only original-byte view of an in-place converted bundle.
Only the selected install's Redux/BundleConversion/journal.json is read, with the
canonical StandaloneWindows64 bundle scope and original file names. The existing
OverlayStream supplies retained Old ranges over the held current file. No Revert,
installed-file rewrite, converted-copy output, extra restored tree or hardlink into
mutable source data is used. A converted-copy record alone supplies no original
payload. An unchanged original file remains eligible through the earlier rule.

The .NET-only ReadOnlyByteOverlay bridge is compiled from BundleConversion/Json,
outside the Unity local package root. Its source lease adapter is needed to keep
the caller's actual file handle open when a disposable canonical overlay is consumed
across assemblies. It introduces no bundle parser, planning policy or reverter.

Journal declarations are untrusted. A sixteen-MiB metadata budget, twelve-level
depth budget and 100,000 record/container/edit ceilings bound parsing. Unknown,
duplicate or aliased JSON properties, missing payload fields, null records, unsafe
or foreign paths, wrong scope/game binding, file-size mismatches, invalid or unequal
hex payload lengths, duplicate file identities, overlapping edits and checked range
bounds fail explicitly. Metadata hashes and rewritten-file hashes authenticate
nothing. Each actual range is recorded as converted, original-or-deferred, or
changed. These states are provenance, not authentication. The original-or-deferred
label deliberately does not claim that a journal-before-write edit was applied.
An otherwise valid covered range may reconstruct stock even when current bytes match
neither declaration. Its changed state and actual current hash remain pinned.
Uncovered changes and incorrect original payloads fail the complete byte proof.
Complete reconstructed
length, EOF and independently trusted SHA-256 must match before coverage is admitted.
The shared canonical writer repeats that full proof and the finished ZIP receives
complete canonical readback verification before publication.

StockArchiveSources retains the exact journal hash/length, actual current-file pins
and range observations. This typed record is needed across preparation and the
persisted coordinator boundary. Both boundaries independently reverify it against
fresh stock authority. An old catalog without journal pins cannot newly adopt a
journal-dependent source. Actual journal/current handles stay open with read sharing
only throughout validation and streaming. Native identity and containment checks
apply before opening sources. A journal cannot authorize outside reads, added-file
ownership, storage overlap, a partial canonical archive or an unexplained byte change.

Original stock paths establish the initial managed path scope. Their current bytes
are independently observed for transactional undo, including known modified or
absent stock files. Added legacy installer files require a real publisher-supplied
installed-file contract. Schema-three FullSourceBaselineDefinition optionally carries
LegacyOwnershipManifests, each a bounded canonical StockBaselineManifest from that
same fresh authority. Only an exact matching declared installed contract grants those
additional paths. No metadata means no automatic ownership of additions. Unowned
target collisions name the path and fail before initial catalog publication, so a
fresh legitimate contract can be supplied and retried without deleting user files.
An interrupted conversion preserves the prior legacy version display without
inventing an authenticated full-source release identity.

FullSourceStockPreparation.ExistingArchive pins the original ZIP path, length and
SHA-256. SourceRoot is empty for this path. VerifyStockAsync independently refreshes
authority, coverage, source pins and current ownership before the coordinator uses
them. The coordinator rechecks the same sources under its existing lease and game-use
guards. Normal source-directory preparation and existing canonical catalogs remain
compatible. Optional catalog ExistingBackup and RecoverySources fields are omitted when absent,
preserving their canonical bytes and operation digests.

The saved source observations are provenance evidence, not a future source
requirement. Once the complete canonical backup or a fully verified ready candidate
exists, reconciliation, update and stock restoration use that artifact independently
of the old journal. Lost journals and absent payloads before completion remain
precise per-path gaps. This phase does not invent legacy installed-file contracts.

## Durable promotion and retention

1. Validate target collisions and initial ownership before binding the initial head.
   Save the original backup pin, complete stock authority and pending operation using
   the existing catalog protocol. This intent does not qualify a partial ZIP as stock.
2. Stream exact sources into an owned transaction-ID-stock.zip candidate using the
   same canonical StockArchive writer as ordinary stock archives. Reopen and verify
   every file, the complete entry set and all required metadata. No restored full-game
   tree is created for this conversion.
3. Persist the complete candidate's final uninstall.zip reference in the preparing
   operation before changing archive names. Native identity/containment checks apply
   to sources, candidate, retained original, destinations and temporary files.
   Catalog and archive scratch roots must be disjoint from every configured game
   root before the coordinator creates its lease or writes preparation payloads.
4. Preserve the exact original as legacy-ORIGINAL_SHA256.zip in the installation's
   existing .redux-fullsource-installation-ID storage. Reject a preexisting unowned
   retention collision. A same-volume native rename preserves the original without
   copying it. Hold the verified retained original read-only during final promotion.
5. Publish the independently verified candidate to the absent uninstall.zip path with
   the canonical no-copy native rename. Only then may release application begin.
   Candidate/source/retention conflicts fail and preserve all remaining evidence.

Pending preparation recovery uses the operation's saved candidate pin and original
pin to reconcile the actual names. It finishes verified promotion or leaves the
operation pending on ambiguity. A complete candidate whose ready publication was
interrupted can be reverified against the independently persisted stock contract.
An operation interrupted before candidate creation can roll back its preparation
intent and retry from the pinned original/current sources. It never fabricates
original bytes or promotes a partial ZIP. The canonical full backup remains immutable
across updates, repairs, downgrades and stock restoration.

Retain the original ZIP, stock/current contracts, original pin, canonical archive
reference, operation, plan/journals and retirement receipts. Terminal source/work
payload retirement follows the existing storage lifecycle and never deletes the
preserved legacy ZIP. Incomplete random archive-writer temporary files are not guessed
to be disposable after process loss. They remain reviewable evidence. No new cleanup
framework or automatic historical-root cleanup is introduced.

Windows patch staging uses the recipe's verified input files. It can read them
directly from a configured installation or the preparation cache. Missing cache
files can come from verified local installations or a verified archive. The
coordinator separately requires the complete original stock backup before it
changes the installation.

## Focused verification

From ksp2redux.tools:

```powershell
pwsh -File scripts/test-full-source-preparation.ps1 -IsolatedBuild `
  -TestFilter 'FullyQualifiedName~JournalStock_' `
  -ParentFeedbackPath '..\..\.agents\omnipatching-work\launcher-journal-stock-recovery.feedback.md'
```

Read feedback separately before invoking validation. A recorded feedback hash is
not permission to compile during a parent operation freeze.

The runner records the actual command, exit, log, TRX and current source pins. Native
journal cases create fresh tiny stock/current/target fixtures in short
redux-journal-* subdirectories of the Windows temporary directory and attach
journal-stock-result.json to their TRX cases. The original journal, initial patched
bundle and source observations are retained there. Existing archive-adoption reports
and historical receipts are unchanged. These are fixture-authority checks, not
proof of whole-game delivery, independent storefront signatures, source-player
completion, Unity/gameplay behavior, power-cut durability or Linux/macOS support.
