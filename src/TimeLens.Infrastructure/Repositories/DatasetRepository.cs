using System.Text.Json;
using TimeLens.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace TimeLens.Infrastructure.Repositories;

public class DatasetRepository(TimeLensContext context) : IDatasetRepository
{
    public async Task<DatasetMetadataDto> UpsertAsync(DatasetMetadataDto metadata, CancellationToken cancellationToken = default)
    {
        var definition = CanonicalSeriesRegistry.Resolve(metadata.Provider, metadata.ProviderSeriesId, metadata.Timeframe);
        if (metadata.SeriesId != 0 && metadata.SeriesId != definition.SeriesId)
        {
            throw new InvalidOperationException(
                $"Series definition '{metadata.ProviderSeriesId}' maps to ID {definition.SeriesId}, not {metadata.SeriesId}.");
        }

        metadata.SeriesId = definition.SeriesId;
        metadata.DefinitionId = string.IsNullOrWhiteSpace(metadata.DefinitionId) ? CreateDefinitionId(metadata) : metadata.DefinitionId;

        var now = DateTimeOffset.UtcNow;
        if (metadata.FirstObservedAt == default)
        {
            metadata.FirstObservedAt = now;
        }

        metadata.LastIngestedAt = now;

        await using var command = context.Postgres.CreateCommand("""
            INSERT INTO market_data_series (
                definition_id, series_id, provider_series_id, provider, exchange, symbol, asset_class, base_asset, quote_asset,
                market_data_type, timeframe, currency, time_zone, provider_instrument_id, endpoint,
                unit, calendar, license_info, deprecated, request_parameters, provider_metadata,
                user_metadata, first_available_at, last_available_at, first_observed_at, last_ingested_at)
            VALUES (
                @definition_id, @series_id, @provider_series_id, @provider, @exchange, @symbol, @asset_class, @base_asset, @quote_asset,
                @market_data_type, @timeframe, @currency, @time_zone, @provider_instrument_id, @endpoint,
                @unit, @calendar, @license_info, @deprecated, @request_parameters, @provider_metadata,
                @user_metadata, @first_available_at, @last_available_at, @first_observed_at, @last_ingested_at)
            ON CONFLICT (series_id) DO UPDATE SET
                definition_id = EXCLUDED.definition_id,
                provider_series_id = EXCLUDED.provider_series_id,
                provider = EXCLUDED.provider,
                exchange = EXCLUDED.exchange,
                symbol = EXCLUDED.symbol,
                asset_class = EXCLUDED.asset_class,
                base_asset = EXCLUDED.base_asset,
                quote_asset = EXCLUDED.quote_asset,
                market_data_type = EXCLUDED.market_data_type,
                timeframe = EXCLUDED.timeframe,
                currency = EXCLUDED.currency,
                time_zone = EXCLUDED.time_zone,
                provider_instrument_id = EXCLUDED.provider_instrument_id,
                endpoint = EXCLUDED.endpoint,
                unit = EXCLUDED.unit,
                calendar = EXCLUDED.calendar,
                license_info = EXCLUDED.license_info,
                deprecated = EXCLUDED.deprecated,
                request_parameters = EXCLUDED.request_parameters,
                provider_metadata = EXCLUDED.provider_metadata,
                first_available_at = LEAST(market_data_series.first_available_at, EXCLUDED.first_available_at),
                last_available_at = GREATEST(market_data_series.last_available_at, EXCLUDED.last_available_at),
                last_ingested_at = EXCLUDED.last_ingested_at
            """);
        AddParameters(command, metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return metadata;
    }

    public async Task<DatasetMetadataDto?> GetAsync(long seriesId, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand($"""
            {SelectSql}
            WHERE series_id = @series_id
            LIMIT 1
            """);
        command.Parameters.AddWithValue("series_id", seriesId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ToDto(reader) : null;
    }

    public async Task<List<DatasetMetadataDto>> SearchAsync(DatasetSearchFilter filter, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand();
        var clauses = new List<string>();

        if (filter.SeriesId.HasValue)
        {
            clauses.Add("series_id = @series_id");
            command.Parameters.AddWithValue("series_id", filter.SeriesId.Value);
        }
        AddFilter(command, clauses, "provider", filter.Provider);
        AddFilter(command, clauses, "exchange", filter.Exchange);
        AddFilter(command, clauses, "symbol", filter.Symbol);
        AddFilter(command, clauses, "asset_class", filter.AssetClass);
        AddFilter(command, clauses, "base_asset", filter.BaseAsset);
        AddFilter(command, clauses, "quote_asset", filter.QuoteAsset);
        AddFilter(command, clauses, "market_data_type", filter.MarketDataType);
        AddFilter(command, clauses, "timeframe", filter.Timeframe);
        AddFilter(command, clauses, "endpoint", filter.Endpoint);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("""
                (
                    series_id::text ILIKE @search OR
                    definition_id ILIKE @search OR
                    provider_series_id ILIKE @search OR
                    provider ILIKE @search OR
                    exchange ILIKE @search OR
                    symbol ILIKE @search OR
                    asset_class ILIKE @search OR
                    base_asset ILIKE @search OR
                    quote_asset ILIKE @search OR
                    market_data_type ILIKE @search OR
                    timeframe ILIKE @search
                )
                """);
            command.Parameters.AddWithValue("search", $"%{filter.Search.Trim()}%");
        }

        command.CommandText = $"""
            {SelectSql}
            {(clauses.Count == 0 ? "" : $"WHERE {string.Join(" AND ", clauses)}")}
            ORDER BY last_ingested_at DESC
            LIMIT 500
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DatasetMetadataDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ToDto(reader));
        }

        return result;
    }

    public async Task<DatasetMetadataDto?> SetDeprecatedAsync(long seriesId, bool deprecated, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE market_data_series
            SET deprecated = @deprecated
            WHERE series_id = @series_id
            """);
        command.Parameters.AddWithValue("series_id", seriesId);
        command.Parameters.AddWithValue("deprecated", deprecated);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetAsync(seriesId, cancellationToken);
    }

    private const string SelectSql = """
        SELECT series_id, definition_id, provider_series_id, provider, exchange, symbol, asset_class, base_asset, quote_asset,
               market_data_type, timeframe, currency, time_zone, provider_instrument_id, endpoint,
               unit, calendar, license_info, deprecated, request_parameters::text, provider_metadata::text,
               user_metadata::text, first_available_at, last_available_at, first_observed_at, last_ingested_at
        FROM market_data_series
        """;

    private static void AddFilter(NpgsqlCommand command, List<string> clauses, string column, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        clauses.Add($"{column} = @{column}");
        command.Parameters.AddWithValue(column, value);
    }

    private static string CreateDefinitionId(DatasetMetadataDto metadata)
    {
        var parts = new[]
        {
            metadata.Provider,
            metadata.Exchange,
            metadata.Symbol,
            metadata.MarketDataType,
            metadata.Timeframe
        };

        return string.Join(':', parts
            .Select(x => string.IsNullOrWhiteSpace(x) ? "-" : x.Trim().ToLowerInvariant().Replace(' ', '-')));
    }

    private static void AddParameters(NpgsqlCommand command, DatasetMetadataDto dto)
    {
        command.Parameters.AddWithValue("definition_id", dto.DefinitionId);
        command.Parameters.AddWithValue("series_id", dto.SeriesId);
        command.Parameters.AddWithValue("provider_series_id", dto.ProviderSeriesId);
        command.Parameters.AddWithValue("provider", dto.Provider);
        command.Parameters.AddWithValue("exchange", dto.Exchange);
        command.Parameters.AddWithValue("symbol", dto.Symbol);
        command.Parameters.AddWithValue("asset_class", dto.AssetClass);
        command.Parameters.AddWithValue("base_asset", dto.BaseAsset);
        command.Parameters.AddWithValue("quote_asset", dto.QuoteAsset);
        command.Parameters.AddWithValue("market_data_type", dto.MarketDataType);
        command.Parameters.AddWithValue("timeframe", dto.Timeframe);
        command.Parameters.AddWithValue("currency", dto.Currency);
        command.Parameters.AddWithValue("time_zone", dto.TimeZone);
        command.Parameters.AddWithValue("provider_instrument_id", dto.ProviderInstrumentId);
        command.Parameters.AddWithValue("endpoint", dto.Endpoint);
        command.Parameters.AddWithValue("unit", dto.Unit);
        command.Parameters.AddWithValue("calendar", dto.Calendar);
        command.Parameters.AddWithValue("license_info", dto.LicenseInfo);
        command.Parameters.AddWithValue("deprecated", dto.Deprecated);
        command.Parameters.Add(new NpgsqlParameter("request_parameters", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(dto.RequestParameters) });
        command.Parameters.Add(new NpgsqlParameter("provider_metadata", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(dto.ProviderMetadata) });
        command.Parameters.Add(new NpgsqlParameter("user_metadata", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(dto.UserMetadata) });
        command.Parameters.AddWithValue("first_available_at", DbValue.From(dto.FirstAvailableAt));
        command.Parameters.AddWithValue("last_available_at", DbValue.From(dto.LastAvailableAt));
        command.Parameters.AddWithValue("first_observed_at", dto.FirstObservedAt);
        command.Parameters.AddWithValue("last_ingested_at", dto.LastIngestedAt);
    }

    private static DatasetMetadataDto ToDto(NpgsqlDataReader reader) => new()
    {
        SeriesId = reader.GetInt64(0),
        DefinitionId = reader.GetString(1),
        ProviderSeriesId = reader.GetString(2),
        Provider = reader.GetString(3),
        Exchange = reader.GetString(4),
        Symbol = reader.GetString(5),
        AssetClass = reader.GetString(6),
        BaseAsset = reader.GetString(7),
        QuoteAsset = reader.GetString(8),
        MarketDataType = reader.GetString(9),
        Timeframe = reader.GetString(10),
        Currency = reader.GetString(11),
        TimeZone = reader.GetString(12),
        ProviderInstrumentId = reader.GetString(13),
        Endpoint = reader.GetString(14),
        Unit = reader.GetString(15),
        Calendar = reader.GetString(16),
        LicenseInfo = reader.GetString(17),
        Deprecated = reader.GetBoolean(18),
        RequestParameters = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(19)) ?? [],
        ProviderMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(20)) ?? [],
        UserMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(21)) ?? [],
        FirstAvailableAt = reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
        LastAvailableAt = reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
        FirstObservedAt = reader.GetFieldValue<DateTimeOffset>(24),
        LastIngestedAt = reader.GetFieldValue<DateTimeOffset>(25)
    };
}
