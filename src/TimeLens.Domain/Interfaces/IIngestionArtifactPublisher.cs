using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IIngestionArtifactPublisher
{
    Task PublishAsync(IngestionArtifactMessage message, CancellationToken cancellationToken = default);
}
