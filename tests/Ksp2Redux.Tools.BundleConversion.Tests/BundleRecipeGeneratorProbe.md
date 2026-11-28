# Bundle and exact-file recipe generation probe

The generator is a callable tools-library API. It canonicalizes a UnityFS target with
`BundleRewriter.WriteBundle`, preserving format version, Unity version strings, entry
order, names and flags. It records the exact canonical SHA-256 and length. It does not
promise the original Unity compressor's container bytes.
`BundleRecipe.GenerateRaw` uses the same source validation, CDC/hint matcher, literal
catalog and range reconstruction for complete ordinary files. Soundbanks, WEM/video,
assemblies and player data are opaque bytes under this contract. It does not parse,
wrap, recompress, normalize or canonicalize a raw target, even if it is a UnityFS file.
Raw generation needs only a literal-pack output, without a second target copy.
The current reader supports UnityFS containers with raw or LZ4/LZ4HC metadata and storage
blocks. LZMA containers are rejected. Use Unity's chunk-based compression for rebuilt probes.

```csharp
BundleRecipeSource descriptor = BundleRecipe.DescribeSource(
    sourceStream, BundleRecipeSourceKind.UnityFsData);
BundleRecipeGenerationResult generated = BundleRecipe.Generate(
    descriptors, sourceStreams, targetStream, literalPackStream,
    canonicalTargetStream, options, rangeHints);
generated.Recipe.Reconstruct(sourceStreams, literalPackStream, reconstructionStream);
```

```csharp
BundleRecipeGenerationResult raw = BundleRecipe.GenerateRaw(
    descriptors, sourceStreams, targetFileStream, literalPackStream, options, rangeHints);
raw.Recipe.Reconstruct(sourceStreams, literalPackStream, reconstructionStream);
```

## Persisted target contract

Version 1 remains canonical UnityFS with
`unityfs-128k-lz4hc-assetstools-3.0.5-v1`, its existing header and named directory
entries. Existing v1 JSON shape, writer, CDC and hint goldens are preserved.
`CURRENT_VERSION` retains the value 1 for that generator.

Version 2 (`RAW_TARGET_VERSION`) exclusively means an exact raw file with
`raw-file-exact-v1` (`RAW_PACKING_PROFILE`). It uses the existing range-entry shape:
`TargetHeader` must be null, and `Entries` must contain exactly one content entry with
`Name: ""`, `Flags: 0` and `Length` equal to `TargetLength`. Its segment lengths must
sum to that length. Zero-length files use a zero-length content entry with no generated
segments and the SHA-256 of empty bytes. Version/profile mismatches, bundle metadata in
a raw recipe, named/flagged/multiple raw entries and incomplete/overflowing ranges fail
before output writes. Version 2 is not a reinterpretation of version 1.

Both profiles verify exact output length and SHA-256. Raw reconstruction streams the
single content entry directly to the output. It supports either source coordinate kind,
no sources, repeated literals and checked hints. Input ownership, positions, sparse
unused-source behavior and discard-on-failure semantics are shared with v1.

Use `RawFile` for native bytes, including media or resources. An existing trusted
`BundleRecipeSource` inventory can replace `DescribeSource`. Generation verifies every
supplied source identity, including unused sources. Reconstruction retains its sparse
unused-source policy. Streams stay open and input positions restore. Generation rejects
duplicate stream objects. Output storage must also be separate from all inputs. Outputs
start empty at position zero and end at their lengths on success. Discard both generator
outputs on failure. No filesystem locations occur in a recipe.

## Matching contract

`gear64-sm64-4f4d4e4950415443-min8k-mask15-max128k-v1` fixes a 64-bit Gear hash,
SplitMix64 table generation seed and byte order, an 8 KiB minimum, low-15-bit boundary
mask and 128 KiB maximum. The nominal unconstrained boundary interval is 32 KiB, not a
measured average chunk length. Hash state resets at each chunk and each UnityFS entry.
Raw-file CDC spans that file's entire native coordinate space. Names do not participate
in matching. SHA-256 identifies chunks and an exact range comparison verifies each
duplicate or matched candidate. Duplicate sources select source index, then coordinate
offset. Adjacent ranges from the same storage coordinates merge.

One newly written literal pack belongs to each generation. Identical literal chunks
within it reuse their offsets. `LiteralChunks` exports SHA-256, offset and length in pack
order so a later generator layer can aggregate/remap packs. Cross-recipe storage,
content-addressed hosting, persistent corpus indexes and CDC installer support are
pending. This primitive does not install files or create a production baseline manifest.

`MaxIndexedChunks` defaults to 262144 unique records across source and literal indexes.
`MaxRecipeSegments` defaults to 1000000 merged segments. Zero supports empty content.
Budget exhaustion throws a clear `InvalidOperationException`. Indexes retain hashes and
ranges, not source chunk arrays. CDC buffers are one 128 KiB chunk and two 64 KiB buffers.
The shared writer uses 128 KiB blocks. These bounds are not a total memory budget:
each open UnityFS reader also retains directory/block metadata and up to eight source
storage blocks of native size. Source block sizes are not capped by the CDC profile.

Metrics record scanned source/target chunks and logical bytes, reused target bytes,
logical literal bytes, literal deduplication, physical pack bytes, unique index records,
merged segments and elapsed milliseconds. Timing is excluded from persisted recipes.
Native source hashes are checked before indexing and again before return. Native target
and canonical target identity are also checked before return. The four existing canonical
writer hashes and the generated CDC recipe hash guard deliberate profile changes.
Raw generation checks the native target identity again before return. An empty raw
target or fully hinted target does not build an unused source chunk index, while source
identities remain verified. Literal byte counts are uncompressed pack/accounting counts,
not a final compressed release download size.

## Verified payload ranges

The optional final `rangeHints` argument is an `IReadOnlyList<BundleRecipeRangeHint>`.
Omitting it, passing null, or passing an empty list preserves the original CDC profile
and deterministic recipe bytes. A nonempty list records the profile
`gear64-sm64-4f4d4e4950415443-min8k-mask15-max128k-v1+verified-ranges-v1`.
The recipe schema and reconstruction primitive are unchanged.

Each hint specifies `TargetEntryIndex`, entry-relative `TargetOffset`, `SourceIndex`,
source-coordinate `SourceOffset`, positive `Length` and the expected exact-range `Sha256`.
For `UnityFsData`, source coordinates address the entire decompressed bundle data stream,
including directory offsets. For `RawFile`, they address the native file. Entry names
do not identify either side. A future recovery-ledger/type-aware locator supplies these
measured coordinates and hashes. The generator does not discover semantic payloads here.
For a raw target, every hint uses `TargetEntryIndex: 0`, and `TargetOffset` is relative
to the beginning of the complete native target file.

The generator copies and sorts hints by target entry index and relative offset. Invalid
indices, negative/zero lengths, malformed hashes, out-of-bounds or overflowing ranges,
duplicates and overlapping target ranges fail explicitly. Adjacent target ranges are
valid. Source ranges may overlap because repeated target data can reuse the same bytes.
Bad provenance claims never silently fall back to literals.

Before writes, each hinted source and target range is SHA-256 hashed and compared byte
for byte through two reusable 64 KiB buffers. It is checked again against canonical
target bytes before emission. No hint-sized array is allocated. A verified hint selects
its explicit source coordinate, while ordinary CDC candidates keep their existing
index/offset tie rule. CDC runs on the remaining target gaps and resets at each gap edge.
Adjacent copied ranges merge under the existing segment budget. A fully hinted target
skips the unused CDC index, but native source and target identities are still checked.

`MaxRangeHints` defaults to 65536 supplied metadata records and may be configured explicitly.
It bounds the copied/sorted hint list, not total process memory or native reader block
caches. Index and segment budgets still apply. `ReusedBytes` includes both reuse paths,
`HintedReusedBytes` records verified-range reuse, `CdcReusedBytes` excludes hints, and
`VerifiedRangeHints` counts accepted hints before segment merging. `TargetChunksScanned`
counts CDC gap chunks, while `TargetDataBytes` includes hinted bytes as well.

## Standalone real-file invocation

Parent integration owns this probe. Use copies of stock/extracted files and a newly
built target, never a live installation as an output location. Save this configuration
under ignored `reference/omnipatching` or `Library/Omnipatching`. Paths are caller-controlled
probe inputs, not recipe paths. Choose a fresh output subdirectory below one of those
two roots in this checkout. Existing output directories are refused.

```json
{
  "Sources": [
    { "Path": "C:/scratch/stock-copy/first.bundle", "Kind": "UnityFsData" },
    { "Path": "C:/scratch/stock-copy/second.bundle", "Kind": "UnityFsData" },
    { "Path": "C:/scratch/stock-copy/resource.bin", "Kind": "RawFile" }
  ],
  "Target": "C:/scratch/native-rebuild/texture.bundle",
  "OutputDirectory": "C:/Games/KSP Stuff/KSP 2 Modding/SDK/Ksp2Redux2/reference/omnipatching/recipe-probe/run-001",
  "Options": { "MaxIndexedChunks": 262144, "MaxRecipeSegments": 1000000, "MaxRangeHints": 65536 },
  "RangeHints": []
}
```

For measured payload reuse, populate `RangeHints` with records of this shape. Replace
every example coordinate, length and hash placeholder with locator evidence for the
configured source/target copies. These numbers are illustrative, not texture offsets.

```json
{
  "TargetEntryIndex": 0,
  "TargetOffset": 4096,
  "SourceIndex": 0,
  "SourceOffset": 8192,
  "Length": 65536,
  "Sha256": "REPLACE_WITH_64_HEXADECIMAL_CHARACTERS_FROM_THE_EXACT_RANGE"
}
```

Run from the Redux checkout root. The standalone test runner compiles only the tools
library/test project. It does not control Unity.

```powershell
$env:KSP2REDUX_BUNDLE_RECIPE_PROBE = 'C:/Games/KSP Stuff/KSP 2 Modding/SDK/Ksp2Redux2/reference/omnipatching/recipe-probe/probe.json'
dotnet test ksp2redux.tools/tests/Ksp2Redux.Tools.BundleConversion.Tests/Ksp2Redux.Tools.BundleConversion.Tests.csproj --no-restore --filter FullyQualifiedName~GenerateAndReconstructConfiguredCopiedBundles --verbosity minimal
Remove-Item Env:KSP2REDUX_BUNDLE_RECIPE_PROBE
```

The probe opens inputs read-only and creates `canonical.bundle`, `literals.pack`,
`recipe.json`, `reconstructed.bundle` and `generation.json`. It deserializes the persisted
recipe and requires reconstruction to match the canonical SHA-256 and length.
`generation.json` retains native/canonical target identities, recipe/pack hashes, metrics
and the literal chunk catalog. Failed artifacts remain for diagnosis and are unverified.
The probe does not validate Unity runtime asset fidelity or certify pristine-stock provenance.

### Exact ordinary-file invocation

Set `TargetKind` to `RawFile`. The omitted/default kind is `UnityFS`, preserving existing
bundle probe configurations. For a raw probe, the configuration, every source, target
and new output directory must be contained below this checkout's `reference/omnipatching`
or `Library/Omnipatching`. All probe output paths and raw input paths reject symlink/junction
ancestors. Sources are read-only, and existing output directories are refused.

```json
{
  "TargetKind": "RawFile",
  "Sources": [
    { "Path": "C:/Games/KSP Stuff/KSP 2 Modding/SDK/Ksp2Redux2/reference/omnipatching/copied-media/source.wem", "Kind": "RawFile" }
  ],
  "Target": "C:/Games/KSP Stuff/KSP 2 Modding/SDK/Ksp2Redux2/reference/omnipatching/copied-media/staged-change.wem",
  "OutputDirectory": "C:/Games/KSP Stuff/KSP 2 Modding/SDK/Ksp2Redux2/reference/omnipatching/raw-probe/run-001",
  "RangeHints": []
}
```

Use the same environment variable and standalone test filter shown above. Raw probes
create `literals.pack`, `recipe.json`, `reconstructed.file` and `generation.json`, without
`canonical.bundle`. They deserialize the persisted recipe, reconstruct, and independently
compare every output byte with the configured target through two 64 KiB comparison buffers.
Bundle probes also compare bytes against their canonical target. Evidence retains source
descriptors, schema/profile identifiers, target/reconstruction and recipe/pack hashes,
entry/segment/chunk counts, generation/reconstruction milliseconds, generation-thread
allocated bytes and the process working set sampled after generation. These memory
measurements include runtime effects and are not peak measurements or memory guarantees.

The contained 2026-10-13 WEM proof is under
`reference/omnipatching/raw-recipe-proof-20261013` in the parent checkout. Its preparation
script independently verifies isolated `1000486374.wem` against the baseline receipt,
then stages a RIFF size change and 56-byte JUNK metadata insertion before its native
chunks. Every original encoded-media byte remains unchanged but moves. This is a staged
metadata edit, not a native Wwise regeneration or an audio-playback fidelity test.

Source length 2668732, SHA-256
`4a1620f2528b59f3ada4f13790090d368bd7514348c44167150e6a96842328c5`.
Staged target/reconstruction length 2668788, SHA-256
`cd8fd32e35c417947d0e7659341dcf2137a9bc5b379a7f5b07c89dceca749527`.
The source agrees with the recorded local Steam baseline receipt. Its upstream manifest
signature is not independently authenticated by this probe.

| Run | Reused bytes | Literal pack bytes | Segments | Source/target CDC chunks | Generation / reconstruction |
| --- | ---: | ---: | ---: | --- | --- |
| `cdc/generation.json` | 2648030 | 20758 | 2 | 76 / 76 | 80.1913 / 16.7321 ms |
| `hinted/generation.json` | 2668720 | 68 | 2 | 76 / 1 | 64.9161 / 10.3530 ms |

Both persisted recipes are version 2 with one content entry, one unique literal chunk,
76 unique source chunks and 77 total index records. The hinted run verifies one source
range at offset 12, target offset 68, length 2668720. Both runs passed independent byte
comparison. The recorded generation-thread allocations are 342952/318464 bytes and
post-generation process working sets 63758336/63623168 bytes respectively. Timing is
one observed local run, not a performance guarantee. The 68 literal bytes are not the
compressed download size: recipe metadata and future pack/container costs also matter.

Shared compressed packs, representation cost selection, persistent corpus indexes,
release manifests/provider catalogs, metadata/native-block/total resource budgets and
production launcher transactions are separate remaining layers. This exact-file primitive
does not publish or install a complete release.

Routine synthetic validation excludes the configured real-file probe:

```powershell
dotnet test ksp2redux.tools/tests/Ksp2Redux.Tools.BundleConversion.Tests/Ksp2Redux.Tools.BundleConversion.Tests.csproj --no-restore --filter 'TestCategory!=Integration' --verbosity minimal
```
