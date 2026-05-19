using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anjal.Api;

/// <summary>
/// JSON codec used by the API. Single source of truth for serialization
/// options so request and response bodies stay symmetric.
/// </summary>
public static class ApiJson
{
    /// <summary>
    /// The JSON options the API uses. Camel-case property names, ignore
    /// null values on output, case-insensitive on input.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Serialise a value to a UTF-8 string.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to serialise.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    /// <summary>
    /// Deserialise a JSON string into a value of the given type. Returns
    /// <see langword="null"/> on empty input.
    /// </summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="json">JSON text.</param>
    /// <returns>The parsed value, or <see langword="null"/> if input is empty.</returns>
    /// <exception cref="JsonException">If the JSON is malformed.</exception>
    public static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
