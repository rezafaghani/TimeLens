namespace TimeLens.Ingestion.Grains;

public interface ICoinbaseCandleGrain : IGrainWithStringKey
{
    Task IngestAsync(string messageJson, CancellationToken cancellationToken);
}
