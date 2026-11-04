namespace MotionClicker.Windows.Recording;
public sealed record NativeVideoEvidence(long CodedFrames,long DecodedFrames,long OriginTicks,long DurationTicks);
/// <summary>Native codec evidence, distinct from portable bundle path validation.</summary>
public static class NativeVideoInspector {
    public static Task<NativeVideoEvidence> InspectAsync(string path,long frameCount,long durationUs,int pixelWidth,int pixelHeight,CancellationToken cancellation=default)=>Task.FromException<NativeVideoEvidence>(new NotImplementedException("Native coded/decoded inspection not implemented"));
}
