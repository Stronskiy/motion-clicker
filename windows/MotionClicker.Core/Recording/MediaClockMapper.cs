namespace MotionClicker.Core.Recording;
public sealed class MediaClockMapper
{
    public MediaClockMapper(long sourceAnchorTicks, long hostAnchorUs, long ticksPerSecond, long epochHostUs) { }
    public long Map(long sourceTicks) => throw new RecordingException("Not implemented");
}
