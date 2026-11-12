namespace Ksp2Redux.Tools.Common.Restoration;

/// <summary>
/// Caller-selected bounds for stock archive memory, disk and content consumption.
/// </summary>
public record StockArchiveLimits
{
    /// <summary>
    /// Gets the maximum uncompressed total file bytes.
    /// </summary>
    public long MaxTotalBytes { get; init; } = 128L * 1024 * 1024 * 1024;
    /// <summary>
    /// Gets the maximum uncompressed bytes in one file.
    /// </summary>
    public long MaxFileBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    /// <summary>
    /// Gets the maximum ZIP bytes, including metadata and container overhead.
    /// </summary>
    public long MaxArchiveBytes { get; init; } = 130L * 1024 * 1024 * 1024;
    /// <summary>
    /// Gets the maximum number of files, directories and metadata entries.
    /// </summary>
    public int MaxEntries { get; init; } = 100_000;
    /// <summary>
    /// Gets the maximum UTF-8 manifest bytes held in memory.
    /// </summary>
    public int MaxManifestBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>
    /// Gets the maximum ZIP central-directory bytes accepted before the ZIP service allocates entries.
    /// </summary>
    public int MaxCentralDirectoryBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>
    /// Gets the reusable streaming buffer size, bounded to sixteen MiB.
    /// </summary>
    public int BufferBytes { get; init; } = 256 * 1024;
    /// <summary>
    /// Gets the additional free disk bytes required before creation or restore.
    /// </summary>
    public long DiskReserveBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate(StockBaselineManifest manifest)
    {
        if (MaxTotalBytes < 0 || MaxFileBytes < 0 || MaxArchiveBytes <= 0 || MaxEntries <= 0 ||
            MaxManifestBytes <= 0 || MaxCentralDirectoryBytes <= 0 ||
            BufferBytes is <= 0 or > 16 * 1024 * 1024 || DiskReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(StockArchiveLimits), "Invalid stock archive resource budget.");
        if (manifest.TotalBytes > MaxTotalBytes || manifest.Files.Any(file => file.Length > MaxFileBytes) ||
            (long)manifest.Files.Count + manifest.Directories.Count + 1 > MaxEntries)
            throw new InvalidDataException("Baseline exceeds the stock archive resource budget.");
    }
}
