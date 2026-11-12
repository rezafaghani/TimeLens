namespace TimeLens.Domain.Interfaces;

public interface IObjectStorage
{
    Task EnsureBucketAsync(CancellationToken cancellationToken = default);
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default);
    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default);
}
