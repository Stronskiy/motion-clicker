namespace MotionClicker.Core.Recording;
internal static class RecordingCheck
{
    public static void Require(bool condition,string message) { if(!condition)throw new RecordingException(message); }
}
public sealed class MediaClockMapper
{
    private readonly long anchor, offset, scale;
    public MediaClockMapper(long sourceAnchorTicks,long hostAnchorUs,long ticksPerSecond,long epochHostUs)
    {
        RecordingCheck.Require(ticksPerSecond>0 && ticksPerSecond<=int.MaxValue,"Invalid source clock scale");
        anchor=sourceAnchorTicks;scale=ticksPerSecond;offset=checked(hostAnchorUs-epochHostUs);
    }
    public long Map(long sourceTicks)
    {
        checked
        {
            var delta=sourceTicks-anchor;
            var whole=(delta/scale)*1000000;
            var numerator=(delta%scale)*1000000;
            var fraction=numerator/scale-(numerator<0 && numerator%scale!=0?1:0);
            return offset+(whole+fraction);
        }
    }
}
