using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Tests;

/// <summary>
/// Versioned local probe inputs shared by publication and lifecycle qualification.
/// </summary>
/// <remarks>
/// This persisted shape is test infrastructure. Diagnostic qualification never grants release eligibility.
/// Every supplied target file is included, including build evidence if it remains in the supplied root.
/// </remarks>
internal class FullPlayerProbeBinding
{
    private readonly JsonElement _json;
    private readonly IFileSystem _fs;
    private readonly string _bindingPath;

    private FullPlayerProbeBinding(IFileSystem fs, JsonElement json, string hash, string path)
    {
        _fs = fs;
        _json = json;
        BindingSha256 = hash;
        _bindingPath = path;
    }

    internal string BindingSha256 { get; }
    internal string PublicationRoot => Text("publicationRoot");
    internal string FixtureRoot => Text("fixtureRoot");
    internal string TargetRoot => Text("targetRoot");
    internal string StockRoot => Text("stockRoot");
    internal string ReleaseId => Text("releaseId");
    internal string Channel => Text("channel");
    internal string Version => Text("version");
    internal string ReceiptPath => PinPath("playerReceipt");
    internal string ArchivePath => PinPath("stockArchive");
    internal string ArchiveSha256 => _json.GetProperty("stockArchive").GetProperty("sha256").GetString()!;
    internal StockBaselineManifest Target { get; private set; } = null!;
    internal StockBaselineManifest Stock { get; private set; } = null!;
    internal IReadOnlyList<ReleaseRecipeSource> Sources =>
        JsonSerializer.Deserialize<List<ReleaseRecipeSource>>(_json.GetProperty("sources").GetRawText())!;

    /// <summary>
    /// Read pinned controls and genuine receipt fields without reading player or archive payload bytes.
    /// </summary>
    /// <param name="fs">The filesystem used by both probes.</param>
    /// <param name="path">The canonical absolute binding path.</param>
    /// <returns>The validated diagnostic-only binding.</returns>
    internal static FullPlayerProbeBinding Read(IFileSystem fs, string path)
    {
        byte[] bytes = ReadBounded(fs, path, 1024 * 1024);
        using var document = JsonDocument.Parse(bytes);
        JsonElement json = document.RootElement.Clone();
        RejectDuplicates(json);
        string[] fields = { "schema", "mode", "publicationRoot", "fixtureRoot", "targetRoot", "stockRoot",
            "targetManifest", "stockManifest", "playerReceipt", "sourceContractSha256", "stockArchive",
            "stockGeneration", "stockRelocation", "expectedTargetIdentity", "releaseId", "channel", "version",
            "sources", "protectedRoots", "successorBinding", "parentBinding", "changedPayloadPath" };
        Require(json.ValueKind == JsonValueKind.Object && json.EnumerateObject().All(field => fields.Contains(field.Name)),
            "Unknown full-player binding field.");
        var binding = new FullPlayerProbeBinding(fs, json, Digest(bytes), path);
        binding.Canonical(path);
        Require(binding.Text("schema") == "omnipatching.full-player-probe-binding.v1" &&
            binding.Text("mode") == "diagnostic-only-full-player-v1",
            "Only explicit diagnostic-only full-player qualification is supported. Production gates remain required.");
        // A pins B's complete bytes. B locates A by path, then verifies A's pin back to B.
        // The two roles have no recursive links or cyclic JSON hash requirements.
        JsonElement parent = json.GetProperty("parentBinding");
        JsonElement successor = json.GetProperty("successorBinding");
        Require((parent.ValueKind == JsonValueKind.Null && successor.ValueKind == JsonValueKind.Object) ||
            (parent.ValueKind == JsonValueKind.String && successor.ValueKind == JsonValueKind.Null),
            "A pair requires one pinned successor and one terminal parent path. Cycles are refused.");
        if (parent.ValueKind == JsonValueKind.String)
            binding.Canonical(binding.Text("parentBinding"));
        foreach (string field in new[] { "publicationRoot", "fixtureRoot", "targetRoot", "stockRoot" })
        {
            binding.Canonical(binding.Text(field));
        }
        binding.Target = StockBaselineManifest.FromJson(binding.ReadPin("targetManifest", ReleaseRecipePipeline.MAX_MANIFEST_BYTES));
        binding.Stock = StockBaselineManifest.FromJson(binding.ReadPin("stockManifest", ReleaseRecipePipeline.MAX_MANIFEST_BYTES));
        Require(binding.Target.BaselineIdentity == binding.Text("expectedTargetIdentity"), "Unexpected complete target identity.");
        Require(binding.Target.Files.Any(file => file.Path == "KSP2_x64.exe") &&
            binding.Target.Files.Any(file => file.Path == "KSP2_x64_Data/boot.config") &&
            binding.Target.Files.Any(file => file.Path == "KSP2_x64_Data/StreamingAssets/aa/catalog.json") &&
            binding.Target.Files.Any(file => file.Path.EndsWith(".bundle", StringComparison.Ordinal)),
            "A complete actual player contract is required.");
        using var receipt = JsonDocument.Parse(binding.ReadPin("playerReceipt", 128 * 1024 * 1024));
        JsonElement result = receipt.RootElement;
        Require(result.GetProperty("schema").GetString() == "omnipatching.source-player-diagnostic-build.v1" &&
            result.GetProperty("success").GetBoolean() && result.GetProperty("sourceOwnedPlayerBuilt").GetBoolean() &&
            result.GetProperty("facadesAbsentFromOutput").GetBoolean() &&
            result.GetProperty("platform").GetString() == "StandaloneWindows64" &&
            !result.GetProperty("finalBuildReady").GetBoolean() && !result.GetProperty("releaseEligible").GetBoolean() &&
            !result.GetProperty("fullGoalComplete").GetBoolean() &&
            EqualHash(result.GetProperty("contractSha256").GetString()!, binding.Text("sourceContractSha256")),
            "Missing, failed, foreign or relabeled diagnostic build receipt.");
        Require(binding.ReceiptPath == fs.Path.Combine(binding.TargetRoot, "source-player-diagnostic.json"),
            "The receipt must belong to the supplied actual player root.");
        Require(binding.Sources.Count <= 16 && binding.Sources.Select(source => source.Path).Distinct(StringComparer.Ordinal).Count() == binding.Sources.Count &&
            binding.Sources.All(source => binding.Stock.Files.Any(file => file.Path == source.Path) &&
                source.Kind is BundleRecipeSourceKind.RawFile or BundleRecipeSourceKind.UnityFsData),
            "Selected recipe sources must be distinct stock files within the existing sixteen-source bound.");
        using var generation = JsonDocument.Parse(binding.ReadPin("stockGeneration", 1024 * 1024));
        using var relocation = JsonDocument.Parse(binding.ReadPin("stockRelocation", 1024 * 1024));
        JsonElement original = generation.RootElement;
        JsonElement moved = relocation.RootElement;
        Require(original.GetProperty("status").GetString() == "complete" &&
            EqualHash(original.GetProperty("expectedManifestSha256").GetString()!, binding.PinHash("stockManifest")) &&
            original.GetProperty("expectedFileCount").GetInt32() == binding.Stock.Files.Count &&
            original.GetProperty("expectedDirectoryCount").GetInt32() == binding.Stock.Directories.Count &&
            original.GetProperty("expectedBytes").GetInt64() == binding.Stock.TotalBytes &&
            EqualHash(original.GetProperty("archiveSha256").GetString()!, binding.ArchiveSha256) &&
            moved.GetProperty("schema").GetString() == "omnipatching.stock-archive-relocation.v1" &&
            moved.GetProperty("state").GetString() == "complete" &&
            moved.GetProperty("source").GetString() == original.GetProperty("archivePath").GetString() &&
            moved.GetProperty("destination").GetString() == binding.ArchivePath &&
            EqualHash(moved.GetProperty("generationSha256").GetString()!, binding.PinHash("stockGeneration")) &&
            EqualHash(moved.GetProperty("expectedSha256").GetString()!, binding.ArchiveSha256) &&
            moved.GetProperty("expectedBytes").GetInt64() == binding._json.GetProperty("stockArchive").GetProperty("length").GetInt64() &&
            original.GetProperty("archiveLength").GetInt64() == moved.GetProperty("expectedBytes").GetInt64(),
            "Complete stock generation or original archive relocation lineage differs.");
        Require(binding._json.GetProperty("protectedRoots").GetArrayLength() > 0,
            "Explicit user-game, profile and source protections are required.");
        foreach (string field in new[] { "releaseId", "channel", "version" })
        {
            _ = binding.Text(field);
        }
        return binding;
    }

    /// <summary>
    /// Resolve the pinned pair and validate every output against both generations before output creation.
    /// </summary>
    /// <remarks>
    /// Either binding can publish independently. Only the selected publication must be fresh.
    /// Both complete control envelopes and the changed runtime witness are required before either publication.
    /// </remarks>
    /// <param name="checkout">The current checkout root.</param>
    /// <param name="publication">True for fresh publication output, false for a fresh lifecycle fixture.</param>
    internal void ValidateOutput(string checkout, bool publication)
    {
        FullPlayerProbeBinding first = _json.GetProperty("parentBinding").ValueKind == JsonValueKind.Null
            ? this : Read(_fs, Text("parentBinding"));
        FullPlayerProbeBinding next = first.ReadSuccessor();
        Require((_bindingPath == first._bindingPath && EqualHash(BindingSha256, first.BindingSha256)) ||
            (_bindingPath == next._bindingPath && EqualHash(BindingSha256, next.BindingSha256)),
            "Publication binding does not belong to the pinned pair.");
        first.ValidatePairLayout(checkout, next);
        RequireFresh(publication ? PublicationRoot : FixtureRoot);
    }

    private IEnumerable<string> InputPaths()
    {
        yield return _bindingPath;
        yield return TargetRoot;
        yield return StockRoot;
        foreach (string field in new[] { "stockArchive", "playerReceipt", "stockManifest", "targetManifest", "stockGeneration", "stockRelocation" })
        {
            yield return PinPath(field);
        }
        foreach (JsonElement input in _json.GetProperty("protectedRoots").EnumerateArray())
        {
            yield return Canonical(input.GetString()!);
        }
    }

    private void ValidatePairLayout(string checkout, FullPlayerProbeBinding next)
    {
        // Mock paths have no native streams. Real filesystems retain the native validator.
        var guard = new InstallTransactionEngine(_fs, validateFileStreams: _fs is MockFileSystem ? _ => { } : null);
        string allowed = Canonical(checkout) + _fs.Path.DirectorySeparatorChar +
            _fs.Path.Combine("Library", "Omnipatching") + _fs.Path.DirectorySeparatorChar;
        string[] outputs = { PublicationRoot, next.PublicationRoot, FixtureRoot };
        string[] inputs = InputPaths().Concat(next.InputPaths()).Distinct(StringComparer.Ordinal).ToArray();
        for (int index = 0; index < outputs.Length; index++)
        {
            string output = Canonical(outputs[index]);
            guard.ValidateControlledPath(output);
            Require(output.StartsWith(allowed, StringComparison.Ordinal),
                "Every private output must be below this checkout's Library/Omnipatching.");
            foreach (string input in inputs)
            {
                Require(!guard.PathsOverlap(output, Canonical(input)),
                    "Probe output overlaps a pair input or protected root.");
            }
            foreach (string other in outputs.Skip(index + 1))
            {
                Require(!guard.PathsOverlap(output, other), "Pair outputs must be separate.");
            }
        }
        Require(!guard.PathsOverlap(TargetRoot, next.TargetRoot), "Target generations must be separate.");
    }

    private void RequireFresh(string output)
    {
        Require(!_fs.Directory.Exists(output) && !_fs.File.Exists(output), "The private probe output must be fresh.");
    }

    /// <summary>
    /// Verify every actual target and baseline file and the independent complete original ZIP before dispatch.
    /// </summary>
    internal void VerifyInputs()
    {
        VerifyTree(TargetRoot, Target);
        VerifyTree(StockRoot, Stock);
        var guard = new InstallTransactionEngine(_fs);
        guard.ValidateControlledPath(ArchivePath);
        Require(_fs.FileInfo.New(ArchivePath).Length == _json.GetProperty("stockArchive").GetProperty("length").GetInt64() &&
            EqualHash(HashFile(ArchivePath), ArchiveSha256), "The complete original ZIP changed.");
        Require(!guard.PathsOverlap(ArchivePath, TargetRoot) && !guard.PathsOverlap(ArchivePath, StockRoot),
            "Canonical archive and read-only source roots must be separate.");
    }

    /// <summary>
    /// Read an explicitly pinned successor and reject version-only or control-only updates.
    /// </summary>
    /// <returns>The genuine changed-payload successor.</returns>
    internal FullPlayerProbeBinding ReadSuccessor()
    {
        Require(_json.GetProperty("parentBinding").ValueKind == JsonValueKind.Null,
            "Only the first binding can select a successor.");
        _ = ReadPin("successorBinding", 1024 * 1024);
        FullPlayerProbeBinding next = Read(_fs, PinPath("successorBinding"));
        ValidateSuccessor(next);
        return next;
    }

    private void ValidateSuccessor(FullPlayerProbeBinding next)
    {
        Require(_json.GetProperty("parentBinding").ValueKind == JsonValueKind.Null &&
            next._json.GetProperty("successorBinding").ValueKind == JsonValueKind.Null &&
            next.Text("parentBinding") == _bindingPath && next._bindingPath == PinPath("successorBinding") &&
            EqualHash(next.BindingSha256, PinHash("successorBinding")) && next.FixtureRoot == FixtureRoot &&
            next.PublicationRoot != PublicationRoot && next.TargetRoot != TargetRoot && next.ReleaseId != ReleaseId &&
            next.Channel == Channel && EqualHash(next.PinHash("stockManifest"), PinHash("stockManifest")) &&
            next.StockRoot == StockRoot && next.ArchivePath == ArchivePath &&
            EqualHash(next.ArchiveSha256, ArchiveSha256) &&
            !EqualHash(next.PinHash("playerReceipt"), PinHash("playerReceipt")) &&
            !EqualHash(next.Text("sourceContractSha256"), Text("sourceContractSha256")),
            "Successor must bind a distinct actual source build on the same channel with the same original stock and fixture.");
        string changed = Text("changedPayloadPath");
        Require(IsRuntimePayload(changed),
            "Select a built Addressables bundle, the managed game assembly or Windows soundbank/media payload. Identity, debug and control files are refused.");
        StockBaselineFile? before = Target.Files.SingleOrDefault(file => file.Path == changed);
        StockBaselineFile? after = next.Target.Files.SingleOrDefault(file => file.Path == changed);
        Require(before is not null && after is not null && before.Length > 0 && after.Length > 0 &&
            !EqualHash(before.Sha256, after.Sha256), "Update requires a real changed payload present in both players.");
    }

    private static bool IsRuntimePayload(string path)
        => path == "KSP2_x64_Data/Managed/Ksp2Redux.dll" ||
            (IsDirectPayload(path, "Redux/Addressables/StandaloneWindows64/") && path.EndsWith(".bundle", StringComparison.Ordinal)) ||
            (IsDirectPayload(path, "KSP2_x64_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/") &&
                (path.EndsWith(".bnk", StringComparison.Ordinal) || path.EndsWith(".wem", StringComparison.Ordinal)));

    private static bool IsDirectPayload(string path, string directory)
        => path.StartsWith(directory, StringComparison.Ordinal) && path.Length > directory.Length &&
            !path[directory.Length..].Contains('/');

    internal string ChangedPayloadPath => Text("changedPayloadPath");

    /// <summary>
    /// Validate both publication roots and the fresh fixture against all pair inputs before initialization.
    /// </summary>
    /// <param name="checkout">The current checkout.</param>
    /// <param name="next">The pinned genuine successor.</param>
    internal void ValidatePairOutput(string checkout, FullPlayerProbeBinding next)
    {
        ValidateSuccessor(next);
        ValidatePairLayout(checkout, next);
        RequireFresh(FixtureRoot);
        Require(!Stock.Files.Any(file => file.Path == "mods/user.txt") &&
            !Target.Files.Any(file => file.Path == "mods/user.txt") && !next.Target.Files.Any(file => file.Path == "mods/user.txt"),
            "The unknown-user sentinel must not collide with a real payload.");
    }

    /// <summary>
    /// Read publication measurements only when they bind this exact player and complete stock contract.
    /// </summary>
    /// <returns>The already completed local publication.</returns>
    internal ReleaseRecipePublication ReadPublication()
    {
        using var report = JsonDocument.Parse(ReadBounded(_fs, _fs.Path.Combine(PublicationRoot, "result.json"), 1024 * 1024));
        Require(report.RootElement.GetProperty("BindingSha256").GetString() == BindingSha256 &&
            report.RootElement.GetProperty("DiagnosticOnly").GetBoolean() &&
            !report.RootElement.GetProperty("ReleaseEligible").GetBoolean(), "Publication belongs to another binding or mode.");
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(report.RootElement.GetProperty("Publication").GetRawText())!;
        var manifest = new ReleaseRecipePipeline(_fs).Inspect(_fs.Path.Combine(PublicationRoot, "artifact"),
            publication.ArtifactSha256, publication.TargetManifestSha256);
        Require(manifest.TargetManifest.AsSpan().SequenceEqual(Target.ToJson()) && manifest.Variants.Count == 1 &&
            manifest.Variants[0].SourceManifest.AsSpan().SequenceEqual(Stock.ToJson()), "Published complete contracts differ.");
        return publication;
    }

    /// <summary>
    /// Verify exact files and directories with bounded streaming reads and the existing native path guards.
    /// </summary>
    /// <param name="root">The read-only complete tree.</param>
    /// <param name="manifest">The independently pinned complete file contract.</param>
    internal void VerifyTree(string root, StockBaselineManifest manifest)
    {
        var guard = new InstallTransactionEngine(_fs);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            guard.ValidateControlledPath(directory);
            foreach (string entry in _fs.Directory.EnumerateFileSystemEntries(directory))
            {
                guard.ValidateControlledPath(entry);
                string relative = _fs.Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (_fs.Directory.Exists(entry))
                {
                    directories.Add(relative);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(relative);
                }
            }
        }
        Require(files.SetEquals(manifest.Files.Select(file => file.Path)) && directories.SetEquals(manifest.Directories),
            "The file contract must cover the entire actual tree, including empty directories and all delivered aliases.");
        foreach (StockBaselineFile file in manifest.Files)
        {
            string path = _fs.Path.Combine(root, file.Path.Replace('/', _fs.Path.DirectorySeparatorChar));
            Require(_fs.FileInfo.New(path).Length == file.Length && EqualHash(HashFile(path), file.Sha256),
                $"Complete tree bytes differ: {file.Path}");
        }
    }

    private string Text(string field)
    {
        string? value = _json.GetProperty(field).GetString();
        Require(!string.IsNullOrWhiteSpace(value), $"Missing binding value: {field}");
        return value!;
    }

    private string PinPath(string field) => Canonical(_json.GetProperty(field).GetProperty("path").GetString()!);
    private string PinHash(string field) => _json.GetProperty(field).GetProperty("sha256").GetString()!.ToUpperInvariant();

    private byte[] ReadPin(string field, int limit)
    {
        byte[] bytes = ReadBounded(_fs, PinPath(field), limit);
        Require(EqualHash(Digest(bytes), PinHash(field)), $"Pinned control changed: {field}");
        return bytes;
    }

    private string Canonical(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && _fs.Path.IsPathFullyQualified(path) &&
            _fs.Path.GetFullPath(path) == path && _fs.Path.TrimEndingDirectorySeparator(path) == path,
            "Probe paths must be canonical absolute paths.");
        return path;
    }

    private static byte[] ReadBounded(IFileSystem fs, string path, int limit)
    {
        using Stream input = fs.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length <= limit, "Probe control exceeds its byte bound.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private string HashFile(string path)
    {
        using Stream input = _fs.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool EqualHash(string first, string second) => first.Length == 64 && second.Length == 64 &&
        first.All(Uri.IsHexDigit) && second.All(Uri.IsHexDigit) && first.Equals(second, StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate binding JSON property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in value.EnumerateArray())
            {
                RejectDuplicates(element);
            }
        }
    }
}
