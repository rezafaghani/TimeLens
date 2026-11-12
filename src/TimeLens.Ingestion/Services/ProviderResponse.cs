using System.Text.Json;

namespace TimeLens.Ingestion.Services;

public sealed class ProviderResponse(byte[] rawJson)
{
    public byte[] RawJson { get; } = rawJson;
    public JsonDocument Parse() => JsonDocument.Parse(RawJson);
}
