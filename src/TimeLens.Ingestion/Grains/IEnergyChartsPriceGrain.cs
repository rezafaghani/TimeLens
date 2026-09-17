namespace TimeLens.Ingestion.Grains;

public interface IEnergyChartsPriceGrain : IGrainWithStringKey
{
    Task IngestAsync(string messageJson, CancellationToken cancellationToken);
}
