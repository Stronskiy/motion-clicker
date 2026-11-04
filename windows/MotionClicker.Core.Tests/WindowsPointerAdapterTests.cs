using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public class WindowsPointerAdapterTests
{
    private static WindowsPointerSample Sample(ActionKind kind=ActionKind.Move,MouseButton? button=null,
        long observed=150,uint packet=uint.MaxValue,double x=20,nuint tag=0) => new(observed,packet,new(x,10),kind,button,null,null,tag);
    private static WindowsPointerAdapter Adapter(Func<WindowsPointerSample,bool>? classifier=null)=>new(new(42,classifier??(_=>false)));

    [Fact] public void ImmutableHookObservationSurvivesClassifierAndOwnerDelay()
    {
        long classifierSaw=0;
        var adapter=Adapter(sample=> { classifierSaw=sample.ObservationHostUs;return false; });
        var result=adapter.Convert(Sample(),100,950)!;
        Assert.Equal(150,result.HostTimeUs);
        Assert.Equal(150,classifierSaw);
        Assert.Equal(800,result.Timing!.OwnerDelayUs);
        Assert.Equal("4294967295",result.Timing.PacketTimestamp);
        Assert.Equal(PointerHostClock.QueryPerformanceCounter,result.Timing.HostClock);
        Assert.Equal(new Point(20,10),result.NativePosition);
    }

    [Fact] public void PacketWrapChangesDiagnosticsOnly()
    {
        var adapter=Adapter();
        var before=adapter.Convert(Sample(packet:uint.MaxValue),100,200)!;
        var after=adapter.Convert(Sample(packet:0),100,200)!;
        Assert.Equal(before.HostTimeUs,after.HostTimeUs);
        Assert.Equal("4294967295",before.Timing!.PacketTimestamp);
        Assert.Equal("0",after.Timing!.PacketTimestamp);
    }

    [Fact] public void ControlPressSuppressesItsReleaseOutsideControlRegion()
    {
        var adapter=Adapter(sample=>sample.NativePosition.X<10);
        Assert.Null(adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Left,x:1),100,200));
        Assert.Null(adapter.Convert(Sample(x:100),100,200));
        Assert.Null(adapter.Convert(Sample(ActionKind.ButtonUp,MouseButton.Left,x:100),100,200));
        Assert.NotNull(adapter.Convert(Sample(x:100),100,200));
    }

    [Fact] public void RecordedHoldRetainsRealReleaseInsideControlRegion()
    {
        var adapter=Adapter(sample=>sample.NativePosition.X<10);
        Assert.NotNull(adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Left,x:100),100,200));
        Assert.NotNull(adapter.Convert(Sample(x:1),100,200));
        Assert.Equal(ActionKind.ButtonUp,adapter.Convert(Sample(ActionKind.ButtonUp,MouseButton.Left,x:1),100,200)!.Kind);
    }

    [Fact] public void PreEpochAndOwnInjectedPressCannotTakeOwnership()
    {
        var adapter=Adapter(sample=>sample.NativePosition.X<10);
        Assert.Null(adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Left,observed:99,x:1),100,200));
        Assert.Null(adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Left,tag:42),100,200));
        Assert.NotNull(adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Left),100,200));
    }

    [Fact] public void ConflictingControlOwnershipFailsInsteadOfInventingARelease()
    {
        var adapter=Adapter(_=>true);
        adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Left),100,200);
        Assert.Throws<RecordingException>(()=>adapter.Convert(Sample(ActionKind.ButtonDown,MouseButton.Right),100,200));
    }

    [Fact] public void ReversedOwnerClockFailsInsteadOfClampingLatency()
    {
        Assert.Throws<RecordingException>(()=>Adapter().Convert(Sample(),100,149));
    }
}
