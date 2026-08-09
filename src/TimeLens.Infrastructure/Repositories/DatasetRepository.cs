using System.Text.Json;
using TimeLens.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace TimeLens.Infrastructure.Repositories;

public class DatasetRepository(TimeLensContext context) : IDatasetRepository
{
    public async Task<DatasetMetadataDto> UpsertAsync(DatasetMetadataDto metadata, CancellationToken cancellationToken = default)
    {
        metadata.Id = string.IsNullOrWhiteSpace(metadata.Id) ? CreateDatasetId(metadata) : metadata.Id;
        metadata.SeriesId = string.IsNullOrWhiteSpace(metadata.SeriesId) ? metadata.Id : metadata.SeriesId;

        var now = DateTimeOffset.UtcNow;
        if (metadata.FirstObservedAt == default)
        {
            metadata.FirstObservedAt = now;
        }

        metadata.LastIngestedAt = now;

        await using var command = context.Postgres.CreateCommand("""
            INSERT INTO market_data_series (
                id, series_id, provider, exchange, symbol, asset_class, base_asset, quote_asset,
                market_data_type, timeframe, currency, time_zone, provider_instrument_id, endpoint,
                unit, calendar, license_info, deprecated, request_parameters, provider_metadata,
                user_metadata, first_available_at, last_available_at, first_observed_at, last_ingested_at)
            VALUES (
                @id, @series_id, @provider, @exchange, @symbol, @asset_class, @base_asset, @quote_asset,
                @market_data_type, @timeframe, @currency, @time_zone, @provider_instrument_id, @endpoint,
                @unit, @calendar, @license_info, @deprecated, @request_parameters, @provider_metadata,
                @user_metadata, @first_available_at, @last_available_at, @first_observed_at, @last_ingested_at)
            ON CONFLICT (id) DO UPDATE SET
                series_id = EXCLUDED.series_id,
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
                last_available_at = EXCLUDED.last_available_at,
                last_ingested_at = EXCLUDED.last_ingested_at
            """);
        AddParameters(command, metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return metadata;
    }

    public async Task<DatasetMetadataDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand($"""
            {SelectSql}
            WHERE id = @id
            LIMIT 1
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ToDto(reader) : null;
    }

    public async Task<List<DatasetMetadataDto>> SearchAsync(DatasetSearchFilter filter, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand();
        var clauses = new List<string>();

        AddFilter(command, clauses, "series_id", filter.SeriesId);
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
                    id ILIKE @search OR
                    series_id ILIKE @search OR
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

    public async Task<DatasetMetadataDto?> SetDeprecatedAsync(string id, bool deprecated, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE market_data_series
            SET deprecated = @deprecated
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("deprecated", deprecated);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetAsync(id, cancellationToken);
    }

    private const string SelectSql = """
        SELECT id, series_id, provider, exchange, symbol, asset_class, base_asset, quote_asset,
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

    private static string CreateDatasetId(DatasetMetadataDto metadata)
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
        command.Parameters.AddWithValue("id", dto.Id);
        command.Parameters.AddWithValue("series_id", dto.SeriesId);
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
        Id = reader.GetString(0),
        SeriesId = reader.GetString(1),
        Provider = reader.GetString(2),
        Exchange = reader.GetString(3),
        Symbol = reader.GetString(4),
        AssetClass = reader.GetString(5),
        BaseAsset = reader.GetString(6),
        QuoteAsset = reader.GetString(7),
        MarketDataType = reader.GetString(8),
        Timeframe = reader.GetString(9),
        Currency = reader.GetString(10),
        TimeZone = reader.GetString(11),
        ProviderInstrumentId = reader.GetString(12),
        Endpoint = reader.GetString(13),
        Unit = reader.GetString(14),
        Calendar = reader.GetString(15),
        LicenseInfo = reader.GetString(16),
        Deprecated = reader.GetBoolean(17),
        RequestParameters = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(18)) ?? [],
        ProviderMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(19)) ?? [],
        UserMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(20)) ?? [],
        FirstAvailableAt = reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
        LastAvailableAt = reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
        FirstObservedAt = reader.GetFieldValue<DateTimeOffset>(23),
        LastIngestedAt = reader.GetFieldValue<DateTimeOffset>(24)
    };
}
