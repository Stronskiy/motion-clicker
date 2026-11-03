using System.Text.Json;
using System.Text.Json.Serialization;
using MotionClicker.Core.Runtime;

namespace MotionClicker.Core.Recording;

public enum RecordingState { Preparing, Recording, Finalizing, Completed, Interrupted, Failed }
public enum GapKind { Leading, Dropped, Backpressure, SourceFailure, EncoderFailure }
public enum WriterStatus { Succeeded, Failed }
public enum StoreOperation { Append, Flush, MoveMedia, PublishManifest }

public sealed class RecordingException(string message) : Exception(message);
public sealed record RecordingTarget(MonitorSlot Slot, RuntimeDisplay Display);

public sealed record ClockAnchor(long SourceAnchorTicks, long HostAnchorUs, long TicksPerSecond);

public sealed record CapturedPointerEvent(long HostTimeUs, Point NativePosition, ActionKind Kind, MouseButton? Button = null, double? DeltaX = null, double? DeltaY = null, [property: JsonRequired] bool IsOwnInjected = false, [property: JsonRequired] bool IsControl = false);

public sealed record RawPointerRecord(int Sequence, CapturedPointerEvent Event, long TimeUs, RecordedAction? Action = null, string? AtomicGroupId = null);

public sealed record RecordingGap(long StartUs, long EndUs, GapKind Kind, string? Reason = null);

public sealed record StreamManifest(string MonitorSlotId, string RelativePath, ClockAnchor Clock, long? MediaTimeZeroUs, long EncodedDurationUs, long FrameCount, RecordingGap[] Gaps, WriterStatus WriterStatus, string? FailureReason = null);

public sealed record RecordingManifest(int ManifestVersion, string TakeId, RecordingState State, long EpochHostUs, long DurationUs, MonitorSlot[] Targets, StreamManifest[] Streams, long RawEventCount, string[] IncompleteGroupIds, string[] Warnings, string? Reason = null);

public sealed record RecordingResult(RecordingManifest Manifest, Take Take, RecordedAction[] Events);

public sealed record StoreRecovery(string StagingPath, string MediaPath, string ManifestPath, bool MediaPublished, bool ManifestPublished);

public interface IEventJournal { void Append(RawPointerRecord record); void Flush(); void Read(Action<RawPointerRecord> visit); }

public static class RecordingJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ProjectJson.Options);
        options.Converters.Add(new StreamManifestJsonConverter());
        options.Converters.Add(new ExactEnumConverter<RecordingState>());
        options.Converters.Add(new ExactEnumConverter<GapKind>());
        options.Converters.Add(new ExactEnumConverter<WriterStatus>());
        options.Converters.Add(new ExactEnumConverter<StoreOperation>());
        options.MakeReadOnly();
        return options;
    }
    public static T Deserialize<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Null root");
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

// The optional offset precedes required fields in the public constructor. A wire DTO
// keeps it genuinely optional without changing the public API or requiring null keys.
internal sealed class StreamManifestJsonConverter : JsonConverter<StreamManifest>
{
    private sealed record Wire(string MonitorSlotId,string RelativePath,ClockAnchor Clock,
        long EncodedDurationUs,long FrameCount,RecordingGap[] Gaps,WriterStatus WriterStatus,
        long? MediaTimeZeroUs=null,string? FailureReason=null);
    public override StreamManifest Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        var w=JsonSerializer.Deserialize<Wire>(ref reader,options)??throw new JsonException("Null stream");
        return new(w.MonitorSlotId,w.RelativePath,w.Clock,w.MediaTimeZeroUs,w.EncodedDurationUs,w.FrameCount,w.Gaps,w.WriterStatus,w.FailureReason);
    }
    public override void Write(Utf8JsonWriter writer,StreamManifest value,JsonSerializerOptions options)=>
        JsonSerializer.Serialize(writer,new Wire(value.MonitorSlotId,value.RelativePath,value.Clock,value.EncodedDurationUs,
            value.FrameCount,value.Gaps,value.WriterStatus,value.MediaTimeZeroUs,value.FailureReason),options);
}
