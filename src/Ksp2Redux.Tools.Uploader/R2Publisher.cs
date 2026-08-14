using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Ksp2Redux.Tools.Uploader;

/// <summary>
/// Publishes immutable patch objects and mutable manifests to an R2 bucket.
/// </summary>
public sealed class R2Publisher : IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _publicBaseUrl;

    /// <summary>
    /// Creates an R2 publisher from bucket-scoped S3 credentials.
    /// </summary>
    public R2Publisher(UploadManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.R2Endpoint) ||
            string.IsNullOrWhiteSpace(manifest.R2AccountId) ||
            string.IsNullOrWhiteSpace(manifest.R2Bucket) ||
            string.IsNullOrWhiteSpace(manifest.R2AccessKeyId) ||
            string.IsNullOrWhiteSpace(manifest.R2SecretAccessKey))
        {
            throw new InvalidOperationException("R2 publication requires endpoint, bucket and bucket-scoped credentials.");
        }

        string expectedEuEndpoint = $"https://{manifest.R2AccountId}.eu.r2.cloudflarestorage.com";
        if (!string.Equals(manifest.R2Endpoint.TrimEnd('/'), expectedEuEndpoint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The R2 endpoint must be the EU-jurisdiction endpoint {expectedEuEndpoint}.");

        _bucket = manifest.R2Bucket;
        _publicBaseUrl = manifest.R2PublicBaseUrl.TrimEnd('/');
        var credentials = new BasicAWSCredentials(manifest.R2AccessKeyId, manifest.R2SecretAccessKey);
        _client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = manifest.R2Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        });
    }

    /// <summary>
    /// Gets the public URL for an object key.
    /// </summary>
    public string GetPublicUrl(string key) => $"{_publicBaseUrl}/{key}";

    /// <summary>
    /// Uploads an immutable object, or verifies an identical existing object.
    /// </summary>
    public async Task UploadImmutableAsync(string key, string path, string checksumSha256, CancellationToken ct)
    {
        long size = new FileInfo(path).Length;
        try
        {
            var head = await _client.GetObjectMetadataAsync(_bucket, key, ct);
            string? storedHash = head.Metadata["x-amz-meta-sha256"];
            if (head.ContentLength != size ||
                !string.Equals(storedHash, checksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Immutable R2 object '{key}' already exists with different content.");
            }
            Console.WriteLine($"Verified existing R2 object: {key} ({size} bytes)");
            return;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        await using var stream = File.OpenRead(path);
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/octet-stream",
            // R2 does not implement the streaming SigV4 trailer mode used by
            // recent AWSSDK.S3 versions. These flags make the SDK send a normal
            // fixed-length payload while our SHA-256 metadata and HEAD check
            // provide the immutable-object integrity contract.
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };
        request.Headers.CacheControl = "public, max-age=31536000, s-maxage=31536000, immutable";
        request.Metadata["sha256"] = checksumSha256;
        await _client.PutObjectAsync(request, ct);

        var uploaded = await _client.GetObjectMetadataAsync(_bucket, key, ct);
        if (uploaded.ContentLength != size)
            throw new InvalidOperationException(
                $"R2 reports {uploaded.ContentLength} bytes for '{key}', expected {size}.");
        Console.WriteLine($"Uploaded R2 object: {key} ({size} bytes)");
    }

    /// <summary>
    /// Publishes a mutable manifest with a short browser lifetime and one-day edge lifetime.
    /// </summary>
    public async Task UploadManifestAsync(string key, string json, CancellationToken ct)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/json; charset=utf-8",
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };
        request.Headers.CacheControl = "public, max-age=0, s-maxage=86400, must-revalidate";
        await _client.PutObjectAsync(request, ct);
        Console.WriteLine($"Published R2 manifest: {key}");
    }

    /// <summary>
    /// Deletes retired R2 objects after their replacement manifest is public.
    /// </summary>
    public async Task DeleteObjectsAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            await _client.DeleteObjectAsync(_bucket, key, ct);
            Console.WriteLine($"Deleted retired R2 object: {key}");
        }
    }

    /// <summary>
    /// Reports the bucket object count and stored bytes.
    /// </summary>
    public async Task ReportUsageAsync(CancellationToken ct)
    {
        string? continuationToken = null;
        long objectCount = 0;
        long storedBytes = 0;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                ContinuationToken = continuationToken
            }, ct);
            objectCount += response.S3Objects.Count;
            storedBytes += response.S3Objects.Sum(item => item.Size ?? 0);
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        Console.WriteLine($"R2 usage: {objectCount} objects, {storedBytes} bytes.");
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
