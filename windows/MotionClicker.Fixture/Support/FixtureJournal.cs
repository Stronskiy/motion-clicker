namespace MotionClicker.Fixture;

public sealed class FixtureJournal : IDisposable {
    public FixtureJournal(string path, string canvasId, string displayId, long ticksPerSecond) { }
    public void Append(long hostStamp, string kind, FixtureGeometry geometry, int? button = null,
        double? x = null, double? y = null, double deltaX = 0, double deltaY = 0, string? nativeEventStamp = null) { }
    public void Dispose() { }
}
