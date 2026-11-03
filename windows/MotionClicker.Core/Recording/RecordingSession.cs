namespace MotionClicker.Core.Recording;
/// <summary>All calls must be confined to one serial owner. Marshal native callbacks here.</summary>
public sealed class RecordingSession
{
    public RecordingState State { get; private set; } = RecordingState.Preparing;
    public RecordingManifest? Result { get; private set; }
    public RecordingSession(string takeId, RecordingTarget[] targets, IEventJournal journal) { }
    public void SourceReady(string monitorSlotId, ClockAnchor clock) { }
    public void EventTapReady() { }
    public void Begin(long epochHostUs) { }
    public void PreparationFailed(string reason) { }
    public void Receive(CapturedPointerEvent e) { }
    public void AcceptedFrame(string monitorSlotId, long sourceTicks, long encodedTimeUs, long durationUs) { }
    public void Idle(string monitorSlotId, long sourceTicks) { }
    public void ReportGap(string monitorSlotId, long startTicks, long endTicks, GapKind kind, string? reason = null) { }
    public void SourceFailed(string monitorSlotId, long hostTimeUs, string reason) { }
    public void Stop(long hostTimeUs, string? reason = null) { }
    public void WriterFinished(string monitorSlotId, WriterStatus status, long encodedDurationUs, string? reason = null) { }
    public RecordingResult Project() => throw new RecordingException("Not implemented");
}
