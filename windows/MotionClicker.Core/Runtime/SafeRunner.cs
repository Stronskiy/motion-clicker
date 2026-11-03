namespace MotionClicker.Core.Runtime;

public sealed record RuntimeDisplay(string Id, string Name, DisplayGeometry Geometry, Rect NativeBounds);
public interface IMonitorProvider { IReadOnlyList<RuntimeDisplay> Displays(); }
public interface IMonotonicClock { long NowUs { get; } }
public interface IInputBackend {
    bool Ready { get; }
    ulong Interference { get; }
    Point Position();
    void Send(RuntimeStep step, Point point);
    void Release(MouseButton button, Point point);
}
public sealed record RuntimeStep(long TimeUs, string Slot, Point? Point, ActionKind Kind,
    MouseButton? Button = null, int ClickCount = 1, double DeltaX = 0, double DeltaY = 0);
public sealed class RunProgram {
    public IReadOnlyList<RuntimeStep> Steps { get; }
    public IReadOnlyList<MonitorSlot> Slots { get; }
    public long? PeriodUs { get; }
    public long? Count { get; }
    public long? DurationUs { get; }
    public RunProgram(IEnumerable<RuntimeStep> steps, IEnumerable<MonitorSlot> slots, long? periodUs = null, long? count = 1, long? durationUs = null) {
        Steps=Array.AsReadOnly(steps.ToArray()); Slots=Array.AsReadOnly(slots.ToArray()); PeriodUs=periodUs; Count=count; DurationUs=durationUs;
    }
}
public enum RunnerState { Idle, Armed, Running, Paused, Stopped, Failed }
public sealed record RunnerSnapshot(RunnerState State, string Reason, long Dispatched, long MaxLagUs);
public sealed class SafeRunner(IMonotonicClock clock, IMonitorProvider monitors, IInputBackend backend) {
    public RunnerSnapshot Snapshot => new(RunnerState.Idle, "", 0, 0);
    public void Start(RunProgram program, IReadOnlyDictionary<string,string> mapping, long delayUs=0, bool hotkeyReady=false) { _ = clock; _ = monitors; _ = backend; }
    public void Pump() { }
    public void Stop(string reason="Stopped", bool paused=false) { }
}
public static class PreviewRunner {
    public static IReadOnlyList<RuntimeStep> Events(RunProgram program,int limit=200) => [];
}
