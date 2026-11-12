using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.BundleConversion;

namespace Ksp2Redux.Tools.Common.Restoration;

public partial class StockArchive
{
    private const int MAX_JOURNAL_BYTES = 16 * 1024 * 1024;
    private const int MAX_JOURNAL_RECORDS = 100_000;
    private const string STOCK_JOURNAL = BundleConversionLayout.CONVERSION_ROOT + "/" + BundleConversionLayout.JOURNAL_FILE;
    private static readonly JsonSerializerOptions JOURNAL_JSON = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    private async Task<(FileSystemStream Lease, StockBaselineFile Pin, Dictionary<string, JournalInPlaceFile> Files)>
        ReadStockJournalAsync(string current, StockBaselineManifest stock, CancellationToken ct)
    {
        string path = ContainedPath(current, STOCK_JOURNAL);
        ValidateRecoveryPath(path);
        if (!_fileSystem.File.Exists(path))
            throw new InvalidDataException("conversion journal missing");
        EnsureSafePath(path, false);
        FileSystemStream lease = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (lease.Length is <= 0 or > MAX_JOURNAL_BYTES)
                throw new InvalidDataException("conversion journal exceeds its metadata byte budget or is empty");
            var bytes = new byte[(int)lease.Length];
            await lease.ReadExactlyAsync(bytes, ct);
            if (lease.ReadByte() != -1)
                throw new InvalidDataException("conversion journal changed length while being read");
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            int containers = 0;
            CheckJournalObjects(document.RootElement, ref containers);
            RequireJournalProperties(document.RootElement, "Version", "BundleRoot", "InPlace");
            ConversionJournal journal = JsonSerializer.Deserialize<ConversionJournal>(bytes, JOURNAL_JSON)
                ?? throw new InvalidDataException("conversion journal is null");
            if (journal.Version != ConversionJournal.CURRENT_VERSION || journal.BundleRoot != BundleConversionLayout.BUNDLE_ROOT ||
                journal.InPlace is null || journal.Rewritten is null || journal.Skipped is null ||
                journal.InPlace.Count + (long)journal.Rewritten.Count + journal.Skipped.Count > MAX_JOURNAL_RECORDS)
                throw new InvalidDataException("conversion journal version, bundle scope or record count is invalid");
            if (journal.GameDirectory is null || journal.GameDirectory.Length > 4096 ||
                journal.ShaderManifestSha256 is null || journal.ShaderTargetsSha256 is null ||
                !JournalHash(journal.ShaderManifestSha256, true) || !JournalHash(journal.ShaderTargetsSha256, true))
                throw new InvalidDataException("conversion journal metadata is invalid");
            if (journal.GameDirectory.Length != 0)
            {
                string declaredRoot = DiskPath(journal.GameDirectory);
                if (!string.Equals(declaredRoot, current, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(journal.GameDirectory.TrimEnd('\\', '/'), declaredRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("conversion journal belongs to a different game directory");
                ValidateRecoveryPath(declaredRoot);
            }
            var trusted = stock.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var files = new Dictionary<string, JournalInPlaceFile>(StringComparer.OrdinalIgnoreCase);
            long edits = 0;
            foreach (JsonElement item in document.RootElement.GetProperty("InPlace").EnumerateArray())
            {
                RequireJournalProperties(item, "File", "Size", "Edits");
                foreach (JsonElement edit in item.GetProperty("Edits").EnumerateArray())
                    RequireJournalProperties(edit, "Offset", "Old", "New");
            }
            foreach (JournalInPlaceFile file in journal.InPlace)
            {
                string relative = JournalStockPath(file.File, trusted);
                if (file.Size != trusted[relative].Length || file.Edits is null || file.Edits.Count == 0 ||
                    !files.TryAdd(relative, file) || checked(edits += file.Edits.Count) > MAX_JOURNAL_RECORDS)
                    throw new InvalidDataException($"journal file '{relative}' has an invalid size, duplicate identity or edit count");
                long end = 0;
                foreach (JournalEdit edit in file.Edits.OrderBy(edit => edit.Offset))
                {
                    if (edit.Old is null || edit.New is null || edit.Old.Length == 0 ||
                        edit.Old.Length != edit.New.Length || (edit.Old.Length & 1) != 0 ||
                        !edit.Old.All(Uri.IsHexDigit) || !edit.New.All(Uri.IsHexDigit))
                        throw new InvalidDataException($"journal file '{relative}' has invalid same-size hex payload");
                    long length = edit.Old.Length / 2;
                    if (edit.Offset < end || edit.Offset < 0 || edit.Offset > file.Size || length > file.Size - edit.Offset)
                        throw new InvalidDataException($"journal file '{relative}' has duplicate, overlapping or out-of-bounds ranges");
                    end = checked(edit.Offset + length);
                }
            }
            var rewritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JournalRewrittenFile file in journal.Rewritten)
            {
                string relative = JournalStockPath(file.File, trusted);
                if (files.ContainsKey(relative) || !rewritten.Add(relative) || file.StockSize != trusted[relative].Length ||
                    !JournalHash(file.StockSha256, false) || !JournalHash(file.OutputSha256, true) ||
                    file.Output != BundleConversionLayout.RelativeOutput(file.File))
                    throw new InvalidDataException($"journal converted-copy declaration '{relative}' is ambiguous or invalid");
            }
            foreach (JournalSkipped skipped in journal.Skipped)
            {
                _ = JournalStockPath(skipped.File, trusted);
                if (skipped.Shader is null || skipped.Reason is null || skipped.Shader.Length > 4096 || skipped.Reason.Length > 4096)
                    throw new InvalidDataException("journal skipped metadata exceeds its field budget");
            }
            var pin = new StockBaselineFile(STOCK_JOURNAL, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            return (lease, pin, files);
        }
        catch (Exception failure)
        {
            lease.Dispose();
            if (failure is JsonException or InvalidOperationException or FormatException or OverflowException or NullReferenceException)
                throw new InvalidDataException("conversion journal metadata is malformed or ambiguous", failure);
            throw;
        }
    }

    private static bool JournalHash(string? hash, bool empty) => hash is not null &&
        ((empty && hash.Length == 0) || (hash.Length == 64 && hash.All(Uri.IsHexDigit)));

    private static string JournalStockPath(string? file, Dictionary<string, StockBaselineFile> trusted)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 255 || file.Contains('/') || file.Contains('\\') ||
            StockBaselineManifest.NormalizePath(file) != file)
            throw new InvalidDataException("journal file name leaves the selected bundle scope or is noncanonical");
        string relative = BundleConversionLayout.BUNDLE_ROOT + "/" + file;
        if (!trusted.ContainsKey(relative))
            throw new InvalidDataException($"journal file '{relative}' is foreign to the trusted stock contract");
        return relative;
    }

    private static void RequireJournalProperties(JsonElement item, params string[] names)
    {
        foreach (string name in names)
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out _))
                throw new InvalidDataException($"conversion journal is missing required '{name}' metadata");
    }

    private static void CheckJournalObjects(JsonElement item, ref int containers)
    {
        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array && ++containers > MAX_JOURNAL_RECORDS)
            throw new InvalidDataException("conversion journal exceeds its metadata container count budget");
        if (item.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("conversion journal has duplicate or aliased JSON properties");
                CheckJournalObjects(property.Value, ref containers);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in item.EnumerateArray())
                CheckJournalObjects(child, ref containers);
        }
    }

    private static async Task<IReadOnlyList<KeyValuePair<long, byte[]>>> StockJournalPatchesAsync(Stream source,
        JournalInPlaceFile declaration, StockBaselineFile expected, List<string> ranges, CancellationToken ct)
    {
        if (source.Length != expected.Length || source.Length != declaration.Size)
            throw new InvalidDataException("journal/current/trusted file sizes differ");
        var patches = new List<KeyValuePair<long, byte[]>>();
        foreach (JournalEdit edit in declaration.Edits.OrderBy(edit => edit.Offset))
        {
            byte[] old = Convert.FromHexString(edit.Old);
            byte[] changed = Convert.FromHexString(edit.New);
            var actual = new byte[old.Length];
            source.Position = edit.Offset;
            await source.ReadExactlyAsync(actual, ct);
            string state = actual.AsSpan().SequenceEqual(old) ? "original-or-deferred" :
                actual.AsSpan().SequenceEqual(changed) ? "converted" : "changed";
            ranges.Add($"{expected.Path}@{edit.Offset}+{old.Length}: {state}");
            patches.Add(new KeyValuePair<long, byte[]>(edit.Offset, old));
        }
        return patches;
    }
}
