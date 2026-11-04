using System.Text.Json;

namespace MotionClicker.Fixture;

/// <summary>Serial, target-owned log; never a recorder source-clock authority.</summary>
public sealed class FixtureJournal : IDisposable {
    private FileStream? file;
    private long sequence, lastStamp;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public FixtureJournal(string path, string canvasId, string displayId, long ticksPerSecond) {
        file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try {
            Write(new { RecordType = "header", SchemaVersion = 1, Platform = "Windows", CanvasId = canvasId, DisplayId = displayId,
                HostClock = new { Name = "QueryPerformanceCounter", Units = "ticks", TicksPerSecond = ticksPerSecond },
                CoordinateSpace = "desktopPixelsTopLeft", NativeEventUnits = "PointerPoint.Timestamp microseconds since boot (native UInt64 decimal)" });
        } catch { file.Dispose(); file = null; throw; }
    }
    public void Append(long hostStamp, string kind, FixtureGeometry geometry, int? button = null,
        double? x = null, double? y = null, double deltaX = 0, double deltaY = 0, string? nativeEventStamp = null) {
        if(hostStamp < lastStamp || hostStamp < 0) throw new InvalidDataException("Reversed native host stamp");
        var native = x.HasValue && y.HasValue ? geometry.NativePoint(x.Value, y.Value) : ((double X, double Y)?)null;
        Write(new { RecordType = "sample", Sequence = checked(sequence + 1), HostStamp = hostStamp, Kind = kind, Button = button,
            Geometry = geometry, X = x, Y = y, NativeX = native?.X, NativeY = native?.Y,
            Inside = x.HasValue && y.HasValue && geometry.Contains(x.Value, y.Value), DeltaX = deltaX, DeltaY = deltaY, NativeEventStamp = nativeEventStamp });
        sequence++; lastStamp = hostStamp;
    }
    private void Write<T>(T value) {
        ObjectDisposedException.ThrowIf(file == null, this);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        file!.Write(bytes); file.WriteByte(10);
    }
    public void Dispose() {
        var handle = file; file = null;
        if(handle == null) return;
        try { handle.Flush(flushToDisk: true); } finally { handle.Dispose(); }
    }
}
