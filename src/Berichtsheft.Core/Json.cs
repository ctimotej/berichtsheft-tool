using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berichtsheft.Core;

/// <summary>Shared JSON settings: snake_case names (compatible with the data of earlier versions), readable umlauts,
/// unknown fields are ignored so settings of older/newer versions still load.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json, T? fallback = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? fallback; }
        catch (JsonException) { return fallback; }
        catch (NotSupportedException) { return fallback; }
    }
}
