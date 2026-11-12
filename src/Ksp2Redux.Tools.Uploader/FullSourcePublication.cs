using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Uploader;

internal static class FullSourcePublication
{
    private const string RECEIPT = "source-player-build.json";
    private const string EXECUTABLE = "KSP2_x64.exe";
    private const string GAME_ASSEMBLY = "KSP2_x64_Data/Managed/Ksp2Redux.dll";
    private const int MAX_CONTRACT_BYTES = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JSON_OPTIONS = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<BundleConversion.Bundles.BundleRecipeSourceKind>(allowIntegerValues: false) },
    };

    internal static int CaptureTarget(string playerRoot, string targetManifest, string releaseId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Full-source Windows publication requires Windows.");

        string player = Path.TrimEndingDirectorySeparator(Path.GetFullPath(playerRoot));
        string output = Path.GetFullPath(targetManifest);
        var paths = new InstallTransactionEngine(new RealFileSystem());
        if (paths.PathsOverlap(player, output))
            throw new InvalidDataException("The target manifest must be outside the player output.");
        string outputDirectory = Path.GetDirectoryName(output)!;
        if (Path.Exists(output) || !Directory.Exists(outputDirectory))
            throw new IOException("Target capture requires a new manifest path with an existing parent.");

        string receiptPath = Path.Combine(player, RECEIPT);
        paths.ValidateControlledPath(receiptPath);
        byte[] receiptBytes = ReadContract(receiptPath);
        using var receipt = JsonDocument.Parse(receiptBytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        JsonElement ownership = ReadOutputOwnership(receipt.RootElement, player, allowDevelopment: true);

        var files = new List<StockBaselineFile>();
        var directories = new List<string>();
        foreach (var (path, relative, isDirectory) in TargetEntries(player))
        {
            if (isDirectory)
            {
                directories.Add(relative);
                continue;
            }
            using var stream = File.OpenRead(path);
            files.Add(new StockBaselineFile(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
        }
        var target = new StockBaselineManifest(releaseId, "Unity Windows source player output", files, directories);
        var byPath = target.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (!byPath.TryGetValue(RECEIPT, out var pin) || pin.Length != receiptBytes.Length ||
            pin.Sha256 != Convert.ToHexString(SHA256.HashData(receiptBytes)))
            throw new InvalidDataException("The source-player receipt changed during target capture.");
        ValidateOutputPins(ownership, byPath);
        ValidateTargetTree(paths, player, target);

        byte[] manifest = target.ToJson(MAX_CONTRACT_BYTES);
        string temporary = Path.Combine(outputDirectory, Path.GetRandomFileName());
        var outputStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (outputStream)
            {
                outputStream.Write(manifest);
                outputStream.Flush(flushToDisk: true);
            }
            File.Move(temporary, output);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        Console.WriteLine($"Captured {target.Files.Count} files and {target.Directories.Count} directories in {output}");
        return 0;
    }

    internal static int Prepare(string optionsPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Full-source Windows publication requires Windows.");

        optionsPath = Path.GetFullPath(optionsPath);
        string optionsDirectory = Path.GetDirectoryName(optionsPath)!;
        string Resolve(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, optionsDirectory));

        var options = JsonSerializer.Deserialize<PublicationOptions>(ReadContract(optionsPath), JSON_OPTIONS)
            ?? throw new InvalidDataException("Publication options are empty.");
        if ((options.SourceManifestSha256 is not null || options.PlayerBuildReceiptSha256 is not null) &&
            (!IsSha256(options.SourceManifestSha256) || !IsSha256(options.PlayerBuildReceiptSha256)))
            throw new InvalidDataException("Publication admission requires both the source-manifest and player build-receipt SHA-256 digests.");
        string player = Resolve(options.PlayerRoot);
        string stockRoot = Resolve(options.StockRoot);
        string output = Resolve(options.OutputRoot);
        string targetPath = Resolve(options.TargetManifest);
        string stockPath = Resolve(options.StockManifest);
        string[] legacyPaths = (options.LegacyOwnershipManifests ?? []).Select(Resolve).ToArray();
        var updates = (options.UpdateSources ?? [])
            .Select(source => (Root: Resolve(source.Root), ManifestPath: Resolve(source.Manifest))).ToArray();
        if (updates.Length > 15)
            throw new InvalidDataException("A hosted release supports at most fifteen prior Redux source contracts.");
        var paths = new InstallTransactionEngine(new RealFileSystem());
        foreach (string input in new[] { player, stockRoot, targetPath, stockPath, optionsPath }.Concat(legacyPaths)
                     .Concat(updates.SelectMany(source => new[] { source.Root, source.ManifestPath })))
        {
            if (paths.PathsOverlap(output, input))
                throw new InvalidDataException("Publication output overlaps an input.");
        }

        byte[] targetBytes = ReadContract(targetPath);
        var target = StockBaselineManifest.FromJson(targetBytes);
        if (target.HasUnixMetadata)
            throw new InvalidDataException("Windows publication requires Windows target and stock contracts.");
        ReleaseManifestValidator.ValidateHostedGameContract(target);
        var targetFiles = target.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (options.PlayerBuildReceiptSha256 is { } expectedReceipt &&
            (!targetFiles.TryGetValue(RECEIPT, out var receiptPin) ||
             !receiptPin.Sha256.Equals(expectedReceipt, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The captured target does not contain the player build receipt admitted for publication.");
        using (Stream receiptStream = OpenTargetFile(paths, player, target, RECEIPT, MAX_CONTRACT_BYTES))
        using (var receipt = JsonDocument.Parse(receiptStream, new JsonDocumentOptions { AllowDuplicateProperties = false }))
        {
            JsonElement ownership = ReadOutputOwnership(receipt.RootElement, player, allowDevelopment: false);
            if (options.SourceManifestSha256 is { } expectedSource &&
                (!ownership.TryGetProperty("sourceManifestSha256AtBuild", out var source) || source.ValueKind != JsonValueKind.String ||
                 !string.Equals(source.GetString(), expectedSource, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The player build receipt names a different source manifest than the one admitted for publication.");
            ValidateOutputPins(ownership, targetFiles);
        }

        byte[] stockBytes = ReadContract(stockPath);
        var stock = StockBaselineManifest.FromJson(stockBytes);
        if (stock.HasUnixMetadata)
            throw new InvalidDataException("Windows publication requires Windows target and stock contracts.");
        ReleaseManifestValidator.ValidateHostedGameContract(stock);

        List<byte[]>? legacy = legacyPaths.Length == 0 ? null : legacyPaths.Select(ReadContract).ToList();
        var baseline = new FullSourceBaselineDefinition(stockBytes, options.SteamDepots ?? [], legacy);
        var updateRecipes = new List<(ReleaseRecipeSourceTree Source, FullSourceUpdateArtifact Artifact)>();
        foreach (var update in updates)
        {
            byte[] bytes = ReadContract(update.ManifestPath);
            var contract = StockBaselineManifest.FromJson(bytes);
            if (contract.HasUnixMetadata || !contract.ToJson().AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("A prior Redux update source must have a canonical Windows target contract.");
            ReleaseManifestValidator.ValidateHostedGameContract(contract);
            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            string markerName = $"update-{digest.ToLowerInvariant()}-release.json";
            var draft = new FullSourceUpdateArtifact(digest, digest, 1, 1,
                new Uri(new Uri(options.R2ArtifactUrl), markerName).AbsoluteUri,
                new Uri(new Uri(options.GitHubArtifactUrl), markerName).AbsoluteUri);
            updateRecipes.Add((new ReleaseRecipeSourceTree(update.Root, contract, []), draft));
        }
        // The existing validator also checks canonical contracts, version and HTTPS URLs before any packs are written.
        string targetHash = Convert.ToHexString(SHA256.HashData(targetBytes));
        var preview = new ReleasePatch
        {
            Version = options.Version,
            ChecksumSha256 = targetHash,
            Size = 1,
            ReleasedAt = DateTime.UnixEpoch,
            Requires = new PatchRequirement { Version = null },
            FullSource = new FullSourceReleaseDescriptor(options.ReleaseId, options.Channel, options.Version,
                targetBytes, targetHash, options.R2ArtifactUrl, options.GitHubArtifactUrl, [baseline], false, 1)
            {
                UpdateArtifacts = updateRecipes.Count == 0 ? null : updateRecipes.Select(recipe => recipe.Artifact).ToList(),
            },
        };
        ReleaseManifestValidator.ValidatePatch(preview);

        using (Stream assembly = OpenTargetFile(paths, player, target, GAME_ASSEMBLY))
        using (var pe = new PEReader(assembly))
        {
            MetadataReader metadata = pe.GetMetadataReader();
            TypeDefinition version = metadata.GetTypeDefinition(metadata.TypeDefinitions.Single(handle =>
                metadata.StringComparer.Equals(metadata.GetTypeDefinition(handle).Name, "VersionID") &&
                metadata.GetTypeDefinition(handle).Namespace.IsNil));
            if (ReadVersionField(metadata, version, "VERSION_TEXT") != options.Version ||
                ReadVersionField(metadata, version, "CHANNEL_NAME") != options.Channel)
                throw new InvalidDataException("Publication version or channel differs from the compiled player.");
        }

        ValidateTargetTree(paths, player, target);
        if (Path.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
            throw new IOException("Publication requires a new output directory with an existing parent.");
        Directory.CreateDirectory(output);
        paths.ValidateControlledPath(output);
        using var owner = File.Open(Path.Combine(output, "publication.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        string artifact = Path.Combine(output, "artifact");
        var pipeline = new ReleaseRecipePipeline(new RealFileSystem());
        long stockStarted = Stopwatch.GetTimestamp();
        ReleaseRecipePublication publication = pipeline.PublishHosted(player, target,
            [new ReleaseRecipeSourceTree(stockRoot, stock, options.Sources ?? [])], artifact);
        long stockPreparationMilliseconds = (long)Stopwatch.GetElapsedTime(stockStarted).TotalMilliseconds;
        int assetCount = Directory.EnumerateFiles(artifact).Count();
        var updateArtifacts = new List<FullSourceUpdateArtifact>();
        var routes = new List<PublicationRoute>(updateRecipes.Count + 1);
        foreach (var (source, draft) in updateRecipes)
        {
            long updateStarted = Stopwatch.GetTimestamp();
            string updateRoot = Path.Combine(output, "updates", draft.SourceDigest.ToLowerInvariant());
            Directory.CreateDirectory(updateRoot);
            string updateArtifactRoot = Path.Combine(updateRoot, "artifact");
            var published = pipeline.PublishHosted(player, target, [source], updateArtifactRoot);
            long markerSize = new FileInfo(Path.Combine(updateArtifactRoot, "release.json")).Length;
            var updateArtifact = draft with { MarkerSize = markerSize, MarkerSha256 = published.ArtifactSha256,
                DownloadBytes = checked(markerSize + published.StoredPackBytes) };
            var inspected = pipeline.Inspect(updateArtifactRoot, updateArtifact.MarkerSha256, targetHash);
            ReleaseRecipePipeline.ValidateUpdateFeedArtifact(inspected, targetBytes, updateArtifact);
            assetCount = checked(assetCount + inspected.Packs.Count + 1);
            if (assetCount > ReleaseRecipePipeline.MAX_HOSTED_PACKS + 1)
                throw new InvalidDataException("The combined stock and update assets exceed the hosted release asset count.");
            updateArtifacts.Add(updateArtifact);
            routes.Add(new PublicationRoute("update", source.Manifest.BaselineIdentity, draft.SourceDigest,
                markerSize, published.StoredPackBytes, updateArtifact.DownloadBytes, published.SourceReusedBytes,
                (long)Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds));
        }

        DateTime releasedAt = DateTime.UtcNow;
        ReleasePatch patch = pipeline.CreateFeedEntry(artifact, publication, options.ReleaseId, options.Channel,
            options.Version, options.R2ArtifactUrl, options.GitHubArtifactUrl, [baseline], false, releasedAt);
        if (updateArtifacts.Count != 0)
            patch = patch with { FullSource = patch.FullSource! with { UpdateArtifacts = updateArtifacts } };
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = options.Channel, GeneratedAt = releasedAt, Patches = [patch] };
        ReleaseManifestValidator.Validate(feed);
        routes.Insert(0, new PublicationRoute("stock", stock.BaselineIdentity, Convert.ToHexString(SHA256.HashData(stockBytes)),
            patch.Size, publication.StoredPackBytes, patch.FullSource!.DownloadBytes, publication.SourceReusedBytes,
            stockPreparationMilliseconds));
        using (Stream manifest = File.Open(Path.Combine(output, "release-manifest.json"), FileMode.CreateNew))
        {
            JsonSerializer.Serialize(manifest, feed, new JsonSerializerOptions { WriteIndented = true });
        }
        // The lifecycle runner uses the final receipt to locate the verified publication measurements.
        using (Stream result = File.Open(Path.Combine(output, "publication-result.json"), FileMode.CreateNew))
        {
            JsonSerializer.Serialize(result, new { Publication = publication, TargetFileBytes = target.TotalBytes, Routes = routes },
                new JsonSerializerOptions { WriteIndented = true });
        }
        Console.WriteLine($"Target player files: {target.TotalBytes} bytes.");
        foreach (var route in routes)
        {
            Console.WriteLine($"{route.Kind} {route.SourceIdentity}: {route.DownloadBytes} download bytes, {route.SourceReusedBytes} reused bytes.");
        }
        Console.WriteLine($"Prepared local full-source publication in {output}");
        return 0;
    }

    private static void ValidateTargetTree(InstallTransactionEngine paths, string player, StockBaselineManifest target)
    {
        paths.ValidateControlledPath(player);
        var files = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var directories = target.Directories.ToHashSet(StringComparer.Ordinal);
        foreach (var (_, relative, isDirectory) in TargetEntries(player))
        {
            if (!(isDirectory ? directories : files).Remove(relative))
                throw new InvalidDataException($"The target contract omits {(isDirectory ? "directory" : "file")} '{relative}'.");
        }
        if (files.Count != 0 || directories.Count != 0)
        {
            string missing = files.FirstOrDefault() ?? directories.First();
            throw new InvalidDataException($"The player output is missing target entry '{missing}'.");
        }
    }

    private static IEnumerable<(string Path, string Relative, bool IsDirectory)> TargetEntries(string player)
    {
        var pending = new Stack<string>();
        pending.Push(player);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                string relative = Path.GetRelativePath(player, entry).Replace('\\', '/');
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"A linked entry cannot belong to the publication target: '{relative}'.");
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory)
                    pending.Push(entry);
                yield return (entry, relative, isDirectory);
            }
        }
    }

    private static JsonElement ReadOutputOwnership(JsonElement build, string player, bool allowDevelopment)
    {
        string? schema = build.GetProperty("schema").GetString();
        bool recognized = schema == "omnipatching.source-player-build.v1" ||
            (allowDevelopment && schema == "omnipatching.source-player-development-build.v1");
        if (!recognized || !build.GetProperty("success").GetBoolean() ||
            !build.GetProperty("sourceOwnedPlayerBuilt").GetBoolean() ||
            !build.GetProperty("facadesAbsentFromOutput").GetBoolean() ||
            build.GetProperty("platform").GetString() != "StandaloneWindows64")
            throw new InvalidDataException(allowDevelopment
                ? "A successful Windows source-player receipt is required."
                : "A successful Windows production source-player receipt is required.");

        var ownership = build.GetProperty("outputOwnership");
        if (ownership.GetProperty("schema").GetString() != "omnipatching.production-output-ownership.v1" ||
            !ownership.GetProperty("unityBuildSucceeded").GetBoolean() ||
            !player.Equals(ownership.GetProperty("outputRoot").GetString(), StringComparison.OrdinalIgnoreCase) ||
            ownership.GetProperty("executableName").GetString() != EXECUTABLE)
            throw new InvalidDataException("The source-player receipt belongs to another player output.");
        return ownership;
    }

    private static void ValidateOutputPins(JsonElement ownership, IReadOnlyDictionary<string, StockBaselineFile> files)
    {
        void Validate(string path, JsonElement pin)
        {
            if (!files.TryGetValue(path, out var file) || file.Length != pin.GetProperty("bytes").GetInt64() ||
                !file.Sha256.Equals(pin.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The target contract differs from the source-player receipt at '{path}'.");
        }
        Validate(EXECUTABLE, ownership.GetProperty("executable"));
        foreach (var entry in ownership.GetProperty("generatedFiles").EnumerateObject())
            Validate(entry.Name, entry.Value);
        foreach (var entry in ownership.GetProperty("supportFiles").EnumerateObject())
            Validate(entry.Name, entry.Value);
    }

    private static bool IsSha256(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static byte[] ReadContract(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MAX_CONTRACT_BYTES)
            throw new InvalidDataException($"Publication input exceeds its byte limit: {path}");
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static Stream OpenTargetFile(InstallTransactionEngine paths, string player, StockBaselineManifest target, string name,
        long maxLength = long.MaxValue)
    {
        var entry = target.Files.SingleOrDefault(file => file.Path == name)
            ?? throw new InvalidDataException($"The target contract omits {name}.");
        string path = Path.GetFullPath(Path.Combine(player, name));
        paths.ValidateControlledPath(path);
        var stream = File.OpenRead(path);
        try
        {
            if (stream.Length > maxLength || stream.Length != entry.Length ||
                Convert.ToHexString(SHA256.HashData(stream)) != entry.Sha256)
                throw new InvalidDataException($"The target contract differs from {name}.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string ReadVersionField(MetadataReader metadata, TypeDefinition version, string name)
    {
        foreach (FieldDefinitionHandle handle in version.GetFields())
        {
            FieldDefinition field = metadata.GetFieldDefinition(handle);
            if (!metadata.StringComparer.Equals(field.Name, name) || field.GetDefaultValue().IsNil)
                continue;
            Constant value = metadata.GetConstant(field.GetDefaultValue());
            if (value.TypeCode != ConstantTypeCode.String)
                break;
            BlobReader text = metadata.GetBlobReader(value.Value);
            return text.ReadUTF16(text.Length);
        }
        throw new InvalidDataException($"VersionID has no string constant {name}.");
    }

    private sealed record PublicationOptions(string PlayerRoot, string TargetManifest, string StockRoot,
        string StockManifest, string OutputRoot, string ReleaseId, string Channel, string Version, string R2ArtifactUrl,
        string GitHubArtifactUrl, List<FullSourceSteamDepot>? SteamDepots, List<string>? LegacyOwnershipManifests,
        List<ReleaseRecipeSource>? Sources, List<UpdateSourceOptions>? UpdateSources,
        string? SourceManifestSha256, string? PlayerBuildReceiptSha256);

    private sealed record UpdateSourceOptions(string Root, string Manifest);

    private sealed record PublicationRoute(string Kind, string SourceIdentity, string SourceManifestSha256,
        long MarkerBytes, long StoredPackBytes, long DownloadBytes, long SourceReusedBytes, long PreparationMilliseconds);
}
