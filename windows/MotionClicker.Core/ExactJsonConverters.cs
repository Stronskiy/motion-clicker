using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MotionClicker.Core;

/// <summary>Accept mathematically integral JSON numbers, including decimal/exponent forms,
/// exactly as Swift Codable does, without ever converting an Int64 through binary64.</summary>
internal sealed class ExactInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected Int64 number.");
        if (reader.TryGetInt64(out var direct)) return direct;
        using var token = JsonDocument.ParseValue(ref reader);
        var raw = token.RootElement.GetRawText();
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var negative = mantissa[0] == '-';
        if (negative) mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.');
        var fractionalDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return 0;
        long exponent = 0;
        if (exponentIndex >= 0 && !long.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent)) throw new JsonException("Int64 exponent out of range.");
        // Bound exponent before subtraction or allocation. Huge exponents cannot produce a nonzero Int64.
        if (exponent > (long)fractionalDigits + 19 || exponent < -(long)digits.Length)
            throw new JsonException("Not an exact Int64.");
        var power = exponent - fractionalDigits;
        if (power >= 0)
        {
            if (digits.Length + power > 19) throw new JsonException("Int64 overflow.");
            digits += new string('0', (int)power);
        }
        else
        {
            var remove = -power;
            if (remove > digits.Length || digits.AsSpan(digits.Length - (int)remove).ContainsAnyExcept('0'))
                throw new JsonException("Expected an integral number.");
            digits = digits[..(digits.Length - (int)remove)];
        }
        if (!long.TryParse(negative ? "-" + digits : digits, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value)) throw new JsonException("Int64 overflow.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

internal sealed class ExactInt32Converter : JsonConverter<int>
{
    private static readonly ExactInt64Converter Int64 = new();
    public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var value = Int64.Read(ref reader, typeof(long), options);
        if (value is < int.MinValue or > int.MaxValue) throw new JsonException("Int32 overflow.");
        return (int)value;
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

// RespectNullableAnnotations does not cover collection elements. Every array in this wire
// contract has non-null elements, so enforce that boundary explicitly rather than allowing
// a null to reach semantic validation as a NullReferenceException.
internal sealed class NonNullArrayConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsArray && type.GetArrayRank() == 1;
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(NonNullArrayConverter<>).MakeGenericType(type.GetElementType()!))!;
    private sealed class NonNullArrayConverter<T> : JsonConverter<T[]>
    {
        public override bool HandleNull => true;
        public override T[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected non-null array.");
            var result = new List<T>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return result.ToArray();
                if (reader.TokenType == JsonTokenType.Null) throw new JsonException("Array elements cannot be null.");
                result.Add(JsonSerializer.Deserialize<T>(ref reader, options) ?? throw new JsonException("Array element cannot be null."));
            }
            throw new JsonException("Unterminated array.");
        }
        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
        {
            if (value is null) throw new JsonException("Array cannot be null.");
            writer.WriteStartArray();
            foreach (var element in value)
            {
                if (element is null) throw new JsonException("Array element cannot be null.");
                JsonSerializer.Serialize(writer, element, options);
            }
            writer.WriteEndArray();
        }
    }
}

internal sealed class ProjectErrorJsonConverter : JsonConverter<ProjectError>
{
    private sealed record ErrorWire(ErrorCode Code, string Path, string Message);
    public override ProjectError Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<ErrorWire>(ref reader, options) ?? throw new JsonException("Expected error object.");
        return new(wire.Code, wire.Path, wire.Message);
    }
    public override void Write(Utf8JsonWriter writer, ProjectError value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new ErrorWire(value.Code, value.Path, value.Message), options);
}
