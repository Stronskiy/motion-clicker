namespace MotionClicker.Windows.Recording;

internal static class WindowsRecordingClock
{
    internal static long CounterToHostUs(long counter,long frequency)
    {
        if(frequency<=0)throw new ArgumentOutOfRangeException(nameof(frequency));
        Int128 numerator=(Int128)counter*1_000_000;
        var result=numerator/frequency;
        if(numerator<0 && numerator%frequency!=0)result--;
        return checked((long)result);
    }
}
