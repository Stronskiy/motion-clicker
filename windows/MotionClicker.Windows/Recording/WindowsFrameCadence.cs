namespace MotionClicker.Windows.Recording;

// Serial source-QPC policy; native epoch gates admission, fixed source zero sets buckets.
internal sealed class WindowsFrameCadence(long epochHostUs)
{
    internal bool Select(long sourceTicks)=>true;
    internal long EpochHostUs=>epochHostUs;
}
