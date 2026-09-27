using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Instella.Server.Storage;

/// <summary>
/// S3-compatible storage provider.
/// Works with AWS S3, MinIO, Cloudflare R2, Backblaze B2, DigitalOcean Spaces, etc.
/// </summary>
public class S3StorageProvider : IStorageProvider, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    /// <summary>
    /// Default expiry time for pre-signed URLs.
    /// </summary>
    public static readonly TimeSpan DefaultUrlExpiry = TimeSpan.FromHours(1);

    public S3StorageProvider(string endpoint, string bucket, string accessKey, string secretKey, string region)
    {
        _bucket = bucket;

        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = true // Required for most S3-compatible services
        };

        // Use custom endpoint if provided
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            config.ServiceURL = endpoint;
        }

        _client = new AmazonS3Client(accessKey, secretKey, config);
    }

    public async Task<StorageResult> UploadAsync(string key, Stream content, CancellationToken ct = default)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = content
            };

            await _client.PutObjectAsync(request, ct);

            // Get the uploaded file size
            var info = await GetInfoAsync(key, ct);
            return StorageResult.Ok(key, info?.Size ?? 0);
        }
        catch (Exception ex)
        {
            return StorageResult.Fail($"S3 upload failed: {ex.Message}");
        }
    }

    public async Task<Stream?> DownloadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            };

            // Stream straight from S3: buffering whole objects in memory does not
            // scale to large files. The wrapper disposes the response with the stream.
            var response = await _client.GetObjectAsync(request, ct);
            return new ResponseOwningStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes <paramref name="key"/>; false only when it was not there. Any other failure
    /// (credentials, network, the bucket) throws, so callers never mistake it for success.
    /// </summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="key"/> exists: false only for a 404. Any other failure throws,
    /// because "cannot tell" is not "not there" is not "not there", which would, for example, re-upload content.
    /// </summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// "Test Connection": writes a 12-byte object under <c>__instella_connection_test__/</c>,
    /// reads its metadata and deletes it, which proves the credentials can do what uploads need.
    /// A failure message is the HTTP status and S3 error code only, never a raw exception text.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var key = $"__instella_connection_test__/{Guid.NewGuid():N}";
        try
        {
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentBody = "instella ok\n" }, ct);
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct);
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
            return (true, "Connection successful: a test object was written, read and deleted.");
        }
        catch (AmazonS3Exception ex)
        {
            var code = string.IsNullOrEmpty(ex.ErrorCode) ? "" : $" ({ex.ErrorCode})";
            return (false, $"the storage answered HTTP {(int)ex.StatusCode} {ex.StatusCode}{code}");
        }
        catch (HttpRequestException)
        {
            return (false, "the endpoint could not be reached");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "the endpoint did not answer in time");
        }
    }

    public async Task<StorageInfo?> GetInfoAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            };

            var response = await _client.GetObjectMetadataAsync(request, ct);

            return new StorageInfo
            {
                Key = key,
                Size = response.ContentLength,
                LastModified = response.LastModified
            };
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> GetPresignedUrlAsync(string key, TimeSpan expiry, string? fileName = null, CancellationToken ct = default)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Expires = DateTime.UtcNow.Add(expiry),
            Verb = HttpVerb.GET
        };

        if (!string.IsNullOrEmpty(fileName))
        {
            request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        }

        return Task.FromResult<string?>(_client.GetPreSignedURL(request));
    }

    public async Task<Stream?> DownloadRangeAsync(string key, long from, long to, CancellationToken ct = default)
    {
        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                ByteRange = new ByteRange(from, to)
            };

            // Stream straight from S3: buffering whole objects in memory does not
            // scale to large files. The wrapper disposes the response with the stream.
            var response = await _client.GetObjectAsync(request, ct);
            return new ResponseOwningStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async IAsyncEnumerable<StorageInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);
            foreach (var o in response.S3Objects ?? [])
                yield return new StorageInfo { Key = o.Key, Size = o.Size, LastModified = o.LastModified.ToUniversalTime() };
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated);
    }

    /// <summary>A read stream that disposes the S3 response that owns it.</summary>
    private sealed class ResponseOwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
