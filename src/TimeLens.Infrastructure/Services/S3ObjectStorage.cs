using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using TimeLens.Domain.Interfaces;

namespace TimeLens.Infrastructure.Services;

public class S3ObjectStorage(IAmazonS3 client, IOptions<ObjectStorageOptions> options) : IObjectStorage
{
    private readonly string _bucketName = options.Value.BucketName;

    public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetBucketLocationAsync(_bucketName, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = _bucketName }, cancellationToken);
            }
            catch (AmazonS3Exception createException) when (
                createException.ErrorCode is "BucketAlreadyExists" or "BucketAlreadyOwnedByYou")
            {
            }
        }
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false
        }, cancellationToken);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetObjectAsync(_bucketName, key, cancellationToken);
        await using var stream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
