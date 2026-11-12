using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IDatasetRepository
{
    Task<DatasetMetadataDto> UpsertAsync(DatasetMetadataDto metadata, CancellationToken cancellationToken = default);
    Task<DatasetMetadataDto?> GetAsync(long seriesId, CancellationToken cancellationToken = default);
    Task<List<DatasetMetadataDto>> SearchAsync(DatasetSearchFilter filter, CancellationToken cancellationToken = default);
    Task<DatasetMetadataDto?> SetDeprecatedAsync(long seriesId, bool deprecated, CancellationToken cancellationToken = default);
}
