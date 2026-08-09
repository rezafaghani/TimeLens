using TimeLens.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace TimeLens.API.Controllers;

[ApiController]
[Route("api/datasets")]
public class DatasetsController(
    IDatasetRepository datasetRepository,
    ITimeSeriesRepository timeSeriesRepository) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<DatasetMetadataDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] string? seriesId,
        [FromQuery] string? provider,
        [FromQuery] string? exchange,
        [FromQuery] string? symbol,
        [FromQuery] string? assetClass,
        [FromQuery] string? baseAsset,
        [FromQuery] string? quoteAsset,
        [FromQuery] string? marketDataType,
        [FromQuery] string? timeframe,
        [FromQuery] string? endpoint,
        CancellationToken cancellationToken)
    {
        var datasets = await datasetRepository.SearchAsync(new DatasetSearchFilter
        {
            Search = search,
            SeriesId = seriesId,
            Provider = provider,
            Exchange = exchange,
            Symbol = symbol,
            AssetClass = assetClass,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            MarketDataType = marketDataType,
            Timeframe = timeframe,
            Endpoint = endpoint,
        }, cancellationToken);

        return Ok(datasets);
    }

    [HttpGet("{id}/metadata")]
    [ProducesResponseType(typeof(DatasetMetadataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Metadata([FromRoute] string id, CancellationToken cancellationToken)
    {
        var dataset = await datasetRepository.GetAsync(id, cancellationToken);
        return dataset is null ? NotFound() : Ok(dataset);
    }

    [HttpPatch("{id}/deprecated")]
    [ProducesResponseType(typeof(DatasetMetadataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDeprecated([FromRoute] string id, [FromBody] SetDatasetDeprecatedRequest request, CancellationToken cancellationToken)
    {
        var dataset = await datasetRepository.SetDeprecatedAsync(id, request.Deprecated, cancellationToken);
        return dataset is null ? NotFound() : Ok(dataset);
    }

    [HttpGet("{id}/series")]
    [ProducesResponseType(typeof(List<TimeSeriesPointDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Series(
        [FromRoute] string id,
        [FromQuery] string start,
        [FromQuery] string end,
        [FromQuery] string? asOf,
        [FromQuery] string? timeZone,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !DateTimeExpression.TryResolve(start, out var startTime, timeZone)
            || !DateTimeExpression.TryResolve(end, out var endTime, timeZone)
            || startTime >= endTime)
        {
            return BadRequest("A valid dataset id and date range are required.");
        }

        if (limit is < 1 or > 10000)
        {
            return BadRequest("Limit must be between 1 and 10000.");
        }

        DateTimeOffset? versionTime = null;
        if (!string.IsNullOrWhiteSpace(asOf))
        {
            if (!DateTimeExpression.TryResolve(asOf, out var parsedAsOf, timeZone))
            {
                return BadRequest("A valid asOf version time is required.");
            }

            versionTime = parsedAsOf;
        }

        var points = await timeSeriesRepository.GetSeriesAsync(id, startTime, endTime, versionTime, cancellationToken, limit);
        return Ok(points);
    }
}

public record SetDatasetDeprecatedRequest(bool Deprecated);
