using MotionClicker.Core.Recording;
namespace MotionClicker.Windows.RecordingUI;
public sealed record SourceGapWindow(string MonitorSlotId,long StartUs,long EndUs,GapKind? Kind) {
    public bool Contains(string monitor,long sourceUs)=>MonitorSlotId==monitor&&sourceUs>=StartUs&&sourceUs<EndUs;
}
public sealed class CompleteGapLookup {
    public CompleteGapLookup(string path,long durationUs,StreamManifest[] streams) { }
    public Task<SourceGapWindow> WindowAsync(string monitor,long sourceUs,CancellationToken cancellation=default)=>Task.FromException<SourceGapWindow>(new NotImplementedException("Complete journal lookup not implemented"));
}
public static class SourcePlayback {
    public static bool TryRange(RecordedBundleMedia media,SourceGapWindow? window,long sourceUs,out TimeSpan position,out TimeSpan end) { position=end=default;return false; }
}
