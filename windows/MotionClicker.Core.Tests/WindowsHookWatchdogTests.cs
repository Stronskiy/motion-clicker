using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public sealed class WindowsHookWatchdogTests
{
    [Fact] public void ResponsivePumpWithoutMouseTrafficIsHealthy()
    {
        var watch=new WindowsHookWatchdog(4);
        for(long now=0;now<5_000_000;now+=100_000)
        {
            watch.Probe(4,now);watch.Acknowledge(4,now);
            Assert.Null(watch.Check(4,now+99_999));
        }
    }
    [Fact] public void UnacknowledgedPumpInterruptsEvenWithoutAnyMouseTraffic()
    {
        var watch=new WindowsHookWatchdog(4);watch.Probe(4,10);
        Assert.Null(watch.Check(4,250_009));
        Assert.Equal("Pointer hook pump deadline exceeded",watch.Check(4,250_010));
    }
    [Fact] public void StalledCallbackInterruptsIndependentlyOfPump()
    {
        var watch=new WindowsHookWatchdog(4);watch.CallbackEntered(4,7);
        Assert.Equal("Pointer hook callback budget exceeded",watch.Check(4,100_007));
        watch.CallbackExited(4,100_008);
        Assert.Equal("Pointer hook callback budget exceeded",watch.Check(4,100_009));
    }
    [Fact] public void OverflowRetainsFirstReasonAndIgnoresStaleGeneration()
    {
        var watch=new WindowsHookWatchdog(4);
        watch.Lost(3,"stale overflow");Assert.Null(watch.Check(4,100));
        watch.Lost(4,"Pointer mailbox overflow");watch.Lost(4,"thread exit");
        Assert.Equal("Pointer mailbox overflow",watch.Check(4,100));
        Assert.Null(watch.Check(3,100));
    }
}
