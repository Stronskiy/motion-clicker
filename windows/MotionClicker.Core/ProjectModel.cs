using System.Text.Json;
using System.Text.Json.Serialization;

namespace MotionClicker.Core;

public sealed record Point(double X, double Y);
public sealed record Size(double Width, double Height);
public sealed record Rect(Point Origin, Size Size);
public sealed record DisplayGeometry(Point Origin, Size Size, double ScaleFactor, int RotationDegrees);
public sealed record MonitorSlot(string Id, string Name, DisplayGeometry Geometry, string? DeviceHint = null);
public sealed record TakeMedia(string MonitorSlotId, string RelativePath);
public sealed record Take(string Id, long DurationUs, string[] MonitorSlotIds, TakeMedia[] Media);
public sealed record Track(string Id, string Name, string MonitorSlotId, int Order);
public sealed record TimelineClip(string Id, string TrackId, string TakeId, long SourceInUs, long SourceOutUs,
    long TimelineStartUs, double Speed, string? LinkGroupId = null);
public sealed record RecordedAction(string Id, string TakeId, string MonitorSlotId, long TimeUs, int Order,
    ActionKind Kind, double X, double Y, MouseButton? Button = null, string? AtomicGroupId = null,
    double? DeltaX = null, double? DeltaY = null);
public sealed record MotionProject(int SchemaVersion, string Id, long Revision, MonitorSlot[] MonitorSlots,
    Take[] Takes, Track[] Tracks, TimelineClip[] Clips, RecordedAction[] Events);
public sealed record PlannedAction(string Id, string SourceEventId, string ClipId, string TakeId,
    string MonitorSlotId, long TimeUs, long SourceTimeUs, int TrackOrder, int EventOrder,
    ActionKind Kind, double X, double Y, MouseButton? Button = null, string? AtomicGroupId = null,
    double? DeltaX = null, double? DeltaY = null);

public enum ActionKind { Move, ButtonDown, ButtonUp, Scroll }
public enum MouseButton { Left, Right, Middle }
public enum ErrorCode
{
    UnsupportedVersion, InvalidID, DuplicateID, MissingReference, InvalidRange, InvalidGeometry,
    InvalidCoordinate, InvalidAction, InvalidAtomicGroup, AtomicGroupSplit, MouseConflict, UnsafeMediaPath
}

public sealed class ProjectError(ErrorCode code, string path, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
    public string Path { get; } = path;
}

/// <summary>Exact, case-sensitive wire codec. Required constructor fields have no wire defaults.</summary>
public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new ProjectErrorJsonConverter());
        options.Converters.Add(new ExactInt64Converter());
        options.Converters.Add(new ExactInt32Converter());
        options.Converters.Add(new NonNullArrayConverterFactory());
        options.Converters.Add(new ExactEnumConverter<ActionKind>());
        options.Converters.Add(new ExactEnumConverter<MouseButton>());
        options.Converters.Add(new ExactEnumConverter<ErrorCode>());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    public static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Root cannot be null.");
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> Values = Enum.GetValues<T>()
        .ToDictionary(v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()), v => v, StringComparer.Ordinal);
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value)
            ? value : throw new JsonException($"Unknown {typeof(T).Name}.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException($"Unknown {typeof(T).Name}.");
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}
