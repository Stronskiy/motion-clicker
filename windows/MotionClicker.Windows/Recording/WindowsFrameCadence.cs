using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

// Serial source-QPC policy; native epoch gates admission, fixed source zero sets buckets.
internal sealed class WindowsFrameCadence
{
    private readonly Int128 epochTicks;
    private readonly object gate=new();
    private long? lastSource;
    private Int128? lastBucket;
    internal WindowsFrameCadence(long epochHostUs)
    {
        if(epochHostUs<0)throw new ArgumentOutOfRangeException(nameof(epochHostUs));
        epochTicks=(Int128)epochHostUs*10;
    }
    internal bool Select(long sourceTicks)
    {
        lock(gate)
        {
            if(sourceTicks<0)throw new RecordingException("Negative capture source time");
            if((Int128)sourceTicks<epochTicks)return false;
            if(lastSource is long previous&&sourceTicks<=previous)
                throw new RecordingException("Reversed or duplicate capture source time");
            lastSource=sourceTicks;
            Int128 bucket=(Int128)sourceTicks*30/10_000_000;
            if(lastBucket==bucket)return false;
            lastBucket=bucket;return true;
        }
    }
}
