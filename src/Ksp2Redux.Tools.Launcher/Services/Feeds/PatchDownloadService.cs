using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

/// <summary>
/// Downloads, resumes, verifies and reconstructs release patches.
/// </summary>
public interface IPatchDownloadService
{
    /// <summary>
    /// Enqueues every request with one shared transfer limit.
    /// </summary>
    /// <param name="requests">The ordered logical patch requests.</param>
    /// <param name="source">The selected public source.</param>
    /// <param name="maxConcurrency">The maximum active transfers.</param>
    /// <param name="log">The user-facing log callback.</param>
    /// <param name="progress">The aggregate byte-progress callback.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Tasks aligned with the supplied request order.</returns>
    IReadOnlyList<Task<string>> EnqueueAll(
        IReadOnlyList<PatchDownloadRequest> requests,
        PatchDownloadSource source,
        int maxConcurrency,
        Action<string> log,
        Action<long, long> progress,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class PatchDownloadService(
    IFileSystem fileSystem,
    IManifestReleasesFeedProviderService provider,
    ILogService serviceLog,
    IDiskSpaceService diskSpaceService) : IPatchDownloadService
{
    private const int MAX_ATTEMPTS = 3;
    private const int BUFFER_SIZE = 1024 * 1024;

    /// <inheritdoc />
    public IReadOnlyList<Task<string>> EnqueueAll(
        IReadOnlyList<PatchDownloadRequest> requests,
        PatchDownloadSource source,
        int maxConcurrency,
        Action<string> log,
        Action<long, long> progress,
        CancellationToken ct)
    {
        int concurrency = Math.Clamp(maxConcurrency, 1, 8);
        var transferLimit = new SemaphoreSlim(concurrency, concurrency);
        var aggregate = new AggregateDownloadProgress(requests.Sum(request => request.Patch.Size), progress);
        var stagingSpace = new StagingSpaceCoordinator(diskSpaceService);
        progress(0, aggregate.TotalBytes);

        return requests
            .Select(request => DownloadPatchAsync(request, source, transferLimit, stagingSpace, aggregate, log, ct))
            .ToList();
    }

    private async Task<string> DownloadPatchAsync(
        PatchDownloadRequest request,
        PatchDownloadSource selectedSource,
        SemaphoreSlim transferLimit,
        StagingSpaceCoordinator stagingSpace,
        AggregateDownloadProgress aggregate,
        Action<string> log,
        CancellationToken ct)
    {
        ReleaseManifestValidator.ValidatePatch(request.Patch);
        fileSystem.Directory.CreateDirectory(request.StorageDirectory);

        string patchHash = request.Patch.ChecksumSha256.ToUpperInvariant();
        string patchDirectory = fileSystem.Path.Combine(request.StorageDirectory, "patches");
        fileSystem.Directory.CreateDirectory(patchDirectory);
        string completedPatchPath = fileSystem.Path.Combine(patchDirectory, $"{patchHash}.patch");

        if (await MatchesChecksumAsync(completedPatchPath, request.Patch.Size, patchHash, ct))
        {
            aggregate.Complete($"patch:{patchHash}", request.Patch.Size);
            log($"Using verified cached patch for {request.Patch.Version}.");
            return completedPatchPath;
        }

        if (fileSystem.File.Exists(completedPatchPath))
            fileSystem.File.Delete(completedPatchPath);

        if (request.Patch.Chunks is null)
        {
            string legacyUrl = request.Patch.Url!;
            var source = InferLegacySource(legacyUrl, selectedSource);
            if (selectedSource == PatchDownloadSource.R2 &&
                source == PatchDownloadSource.GitHub &&
                !string.IsNullOrWhiteSpace(request.Feed.R2ManifestUrl))
            {
                throw new PatchDownloadException(
                    PatchDownloadSource.R2,
                    "This legacy patch is available only from GitHub. Select the GitHub backup to continue.");
            }
            log($"Downloading legacy patch for {request.Patch.Version}.");
            await DownloadVerifiedFileAsync(
                request.Feed,
                legacyUrl,
                completedPatchPath,
                request.Patch.Size,
                patchHash,
                $"patch:{patchHash}",
                source,
                false,
                transferLimit,
                stagingSpace,
                aggregate,
                ct);
            return completedPatchPath;
        }

        string chunkDirectory = fileSystem.Path.Combine(request.StorageDirectory, "chunks", patchHash);
        fileSystem.Directory.CreateDirectory(chunkDirectory);
        var chunks = request.Patch.Chunks.OrderBy(chunk => chunk.Index).ToList();
        var chunkTasks = chunks.Select(chunk =>
        {
            string chunkHash = chunk.ChecksumSha256.ToUpperInvariant();
            string chunkPath = fileSystem.Path.Combine(chunkDirectory, $"{chunk.Index:D4}-{chunkHash}.bin");
            string url = selectedSource == PatchDownloadSource.R2 ? chunk.R2Url : chunk.GitHubUrl;
            return DownloadVerifiedFileAsync(
                request.Feed,
                url,
                chunkPath,
                chunk.Size,
                chunkHash,
                $"{patchHash}:{chunk.Index}",
                selectedSource,
                true,
                transferLimit,
                stagingSpace,
                aggregate,
                ct);
        }).ToList();

        log($"Downloading {chunks.Count} part(s) for patch {request.Patch.Version} from {selectedSource}.");
        await Task.WhenAll(chunkTasks);

        string temporaryPatchPath = completedPatchPath + ".tmp";
        if (fileSystem.File.Exists(temporaryPatchPath))
            fileSystem.File.Delete(temporaryPatchPath);

        try
        {
            await using (var output = fileSystem.FileStream.New(
                             temporaryPatchPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BUFFER_SIZE, true))
            {
                foreach (string chunkPath in chunkTasks.Select(task => task.Result))
                {
                    await using var input = fileSystem.FileStream.New(
                        chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, true);
                    await input.CopyToAsync(output, BUFFER_SIZE, ct);
                }
                await output.FlushAsync(ct);
            }

            if (!await MatchesChecksumAsync(temporaryPatchPath, request.Patch.Size, patchHash, ct))
                throw new InvalidDataException(
                    $"Reconstructed patch {request.Patch.Version} did not match its expected checksum.");

            fileSystem.File.Move(temporaryPatchPath, completedPatchPath);
            foreach (string chunkPath in chunkTasks.Select(task => task.Result))
            {
                if (fileSystem.File.Exists(chunkPath))
                    fileSystem.File.Delete(chunkPath);
            }
            TryDeleteEmptyDirectory(chunkDirectory);
            log($"Patch {request.Patch.Version} was reconstructed and verified.");
            return completedPatchPath;
        }
        catch
        {
            if (fileSystem.File.Exists(temporaryPatchPath))
                fileSystem.File.Delete(temporaryPatchPath);
            throw;
        }
    }

    private async Task<string> DownloadVerifiedFileAsync(
        FeedInfo feed,
        string url,
        string completedPath,
        long expectedSize,
        string expectedHash,
        string progressKey,
        PatchDownloadSource source,
        bool canSwitchSource,
        SemaphoreSlim transferLimit,
        StagingSpaceCoordinator stagingSpace,
        AggregateDownloadProgress aggregate,
        CancellationToken ct)
    {
        if (await MatchesChecksumAsync(completedPath, expectedSize, expectedHash, ct))
        {
            aggregate.Complete(progressKey, expectedSize);
            return completedPath;
        }

        if (fileSystem.File.Exists(completedPath))
            fileSystem.File.Delete(completedPath);

        string partialPath = completedPath + ".partial";
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            long currentOffset = fileSystem.File.Exists(partialPath) ? fileSystem.FileInfo.New(partialPath).Length : 0;
            using var reservation = await stagingSpace.ReserveAsync(
                fileSystem.Path.GetDirectoryName(partialPath) ?? partialPath,
                Math.Max(0, expectedSize - currentOffset), ct);
            await transferLimit.WaitAsync(ct);
            try
            {
                long offset = fileSystem.File.Exists(partialPath) ? fileSystem.FileInfo.New(partialPath).Length : 0;
                if (offset > expectedSize)
                {
                    fileSystem.File.Delete(partialPath);
                    offset = 0;
                }
                aggregate.Report(progressKey, offset);

                using var response = await provider.DownloadFileAsync(feed, url, offset, ct);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (await MatchesChecksumAsync(partialPath, expectedSize, expectedHash, ct))
                    {
                        fileSystem.File.Move(partialPath, completedPath);
                        aggregate.Complete(progressKey, expectedSize);
                        return completedPath;
                    }

                    if (fileSystem.File.Exists(partialPath))
                        fileSystem.File.Delete(partialPath);
                    throw new HttpRequestException("The server rejected the resume offset.", null, response.StatusCode);
                }

                bool isValidResume = offset > 0 &&
                                     response.StatusCode == HttpStatusCode.PartialContent &&
                                     response.Content.Headers.ContentRange?.From == offset &&
                                     response.Content.Headers.ContentRange?.Length == expectedSize;
                if (offset > 0 && !isValidResume)
                {
                    fileSystem.File.Delete(partialPath);
                    offset = 0;
                    aggregate.Report(progressKey, 0);
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Download returned HTTP {(int)response.StatusCode} {response.StatusCode}.",
                        null,
                        response.StatusCode);

                FileMode mode = isValidResume ? FileMode.Append : FileMode.Create;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                {
                    await using var output = fileSystem.FileStream.New(
                        partialPath, mode, FileAccess.Write, FileShare.None, BUFFER_SIZE, true);
                    var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                    try
                    {
                        long written = offset;
                        var updateTimer = Stopwatch.StartNew();
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            written += read;
                            if (written > expectedSize)
                                throw new InvalidDataException("The downloaded file is larger than the manifest declares.");

                            if (updateTimer.ElapsedMilliseconds >= 100)
                            {
                                aggregate.Report(progressKey, written);
                                updateTimer.Restart();
                            }
                        }
                        await output.FlushAsync(ct);
                        aggregate.Report(progressKey, written);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                if (!await MatchesChecksumAsync(partialPath, expectedSize, expectedHash, ct))
                {
                    fileSystem.File.Delete(partialPath);
                    throw new InvalidDataException("The downloaded file did not match its expected checksum.");
                }

                fileSystem.File.Move(partialPath, completedPath);
                aggregate.Complete(progressKey, expectedSize);
                return completedPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                bool retryable = IsRetryable(ex);
                serviceLog.Warn($"Download attempt {attempt}/{MAX_ATTEMPTS} failed for {url}: {ex.Message}");
                if (!retryable || attempt == MAX_ATTEMPTS)
                    break;
            }
            finally
            {
                transferLimit.Release();
            }

            int delayMilliseconds = (int)Math.Pow(2, attempt - 1) * 500 + Random.Shared.Next(0, 250);
            await Task.Delay(delayMilliseconds, ct);
        }

        throw new PatchDownloadException(
            source,
            $"Could not download a verified patch part from {source}.",
            lastFailure,
            canSwitchSource);
    }

    private async Task<bool> MatchesChecksumAsync(string path, long expectedSize, string expectedHash, CancellationToken ct)
    {
        if (!fileSystem.File.Exists(path) || fileSystem.FileInfo.New(path).Length != expectedSize)
            return false;

        await using var stream = fileSystem.FileStream.New(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetryable(Exception exception)
    {
        if (exception is IOException or InvalidDataException)
            return true;
        if (exception is not HttpRequestException requestException)
            return false;
        if (requestException.StatusCode is null)
            return true;

        int statusCode = (int)requestException.StatusCode.Value;
        return requestException.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               statusCode >= 500;
    }

    private static PatchDownloadSource InferLegacySource(string url, PatchDownloadSource fallback)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return fallback;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return PatchDownloadSource.GitHub;
        if (uri.Host.Equals("download.ksp2redux.org", StringComparison.OrdinalIgnoreCase))
            return PatchDownloadSource.R2;
        return fallback;
    }

    private void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (fileSystem.Directory.Exists(path) && !fileSystem.Directory.EnumerateFileSystemEntries(path).Any())
                fileSystem.Directory.Delete(path);
        }
        catch (IOException)
        {
            // A concurrent cleanup or scanner can keep an empty cache directory alive without affecting the patch.
        }
    }

    private sealed class AggregateDownloadProgress(long totalBytes, Action<long, long> callback)
    {
        private readonly ConcurrentDictionary<string, long> _bytesByItem = new();
        private long _lastReported;

        public long TotalBytes { get; } = totalBytes;

        public void Report(string key, long bytes)
        {
            _bytesByItem.AddOrUpdate(key, bytes, (_, previous) => Math.Max(previous, bytes));
            long current = Math.Min(TotalBytes, _bytesByItem.Values.Sum());
            long monotonic = Math.Max(Interlocked.Read(ref _lastReported), current);
            Interlocked.Exchange(ref _lastReported, monotonic);
            callback(monotonic, TotalBytes);
        }

        public void Complete(string key, long bytes) => Report(key, bytes);
    }

    private sealed class StagingSpaceCoordinator(IDiskSpaceService diskSpaceService)
    {
        private const long SAFETY_BYTES = 512L * 1024 * 1024;
        private long _reservedBytes;

        public async Task<IDisposable> ReserveAsync(string path, long bytes, CancellationToken ct)
        {
            if (bytes <= 0)
                return new Reservation(this, 0);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long? available = diskSpaceService.GetAvailableFreeSpace(path);
                long reserved = Interlocked.Read(ref _reservedBytes);
                if (available is null || available.Value - reserved >= bytes + SAFETY_BYTES)
                {
                    if (Interlocked.CompareExchange(ref _reservedBytes, reserved + bytes, reserved) == reserved)
                        return new Reservation(this, bytes);
                    continue;
                }
                await Task.Delay(500, ct);
            }
        }

        private sealed class Reservation(StagingSpaceCoordinator owner, long bytes) : IDisposable
        {
            private long _bytes = bytes;

            public void Dispose()
            {
                long value = Interlocked.Exchange(ref _bytes, 0);
                if (value > 0)
                    Interlocked.Add(ref owner._reservedBytes, -value);
            }
        }
    }
}
