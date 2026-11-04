using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public sealed class WindowsFrameCadenceTests
{
    [Theory] [InlineData(60)] [InlineData(120)]
    public void OneSecondHighRefreshSelectsThirtyOriginalImagesWithoutFalseLoss(int hz)
    {
        const long epoch=1_000_000;var cadence=new WindowsFrameCadence(epoch);
        var accepted=new List<WindowsTimedFrame>();var gaps=new List<(long,long)>();
        var timeline=new WindowsStreamTimeline(epoch,f=>{accepted.Add(f);return true;},(a,b,_,_)=>gaps.Add((a,b)));
        var original=Enumerable.Range(0,hz).Select(i=>10_000_000L+i*10_000_000L/hz).ToArray();
        var selected=original.Where(cadence.Select).ToArray();
        foreach(var ticks in selected)timeline.Image(new([1],ticks));timeline.Stop(20_000_000);
        Assert.Equal(30,selected.Length);Assert.Equal(selected,accepted.Select(f=>f.SourceTicks));Assert.Empty(gaps);
        Assert.Equal(0,accepted[0].EncodedTicks);Assert.Equal(10_000_000,accepted[^1].EncodedTicks+accepted[^1].DurationTicks);
        // Fixed source-domain bucket, earliest genuine source frame, exact integer floor.
        Assert.Equal(original.GroupBy(t=>(Int128)t*30/10_000_000).Select(g=>g.First()),selected);
    }
    [Fact] public void PreEpochDoesNotOccupyBucketAndLateVfrTimeIsNeverRebased()
    {
        var cadence=new WindowsFrameCadence(1_005_000);
        Assert.False(cadence.Select(10_000_000));Assert.True(cadence.Select(10_060_000));
        Assert.False(cadence.Select(10_070_000));Assert.True(cadence.Select(19_000_000));
        var encoded=new List<WindowsTimedFrame>();
        var timeline=new WindowsStreamTimeline(1_005_000,f=>{encoded.Add(f);return true;},(_,_,_,_)=>throw new Exception("False VFR gap"));
        timeline.Image(new([1],10_060_000));timeline.Image(new([2],19_000_000));timeline.Stop(20_000_000);
        Assert.Null(timeline.Failure);
        Assert.Equal(new long[]{10_060_000,19_000_000},encoded.Select(f=>f.SourceTicks));
        Assert.Equal(1000,new MediaClockMapper(0,0,10_000_000,1_005_000).Map(encoded[0].SourceTicks));
        Assert.True(new WindowsFrameCadence(1_005_000).Select(10_070_000)); // New generation has fresh admission state.
    }
    [Fact] public void ExtremeSourceClockUsesInt128AndReversedOrDuplicateTimeRejects()
    {
        var cadence=new WindowsFrameCadence(0);
        Assert.True(cadence.Select(long.MaxValue-10_000_000));Assert.True(cadence.Select(long.MaxValue));
        Assert.Throws<RecordingException>(()=>cadence.Select(long.MaxValue));
        Assert.Throws<RecordingException>(()=>new WindowsFrameCadence(0).Select(-1));
        var reversed=new WindowsFrameCadence(0);Assert.True(reversed.Select(20_000_000));
        Assert.Throws<RecordingException>(()=>reversed.Select(19_999_999));
    }
    [Fact] public void IntentionalSelectionDoesNotHideActualLossOrStretchItsPrefix()
    {
        var cadence=new WindowsFrameCadence(1_000_000);var encoded=new List<WindowsTimedFrame>();var gaps=new List<(long,long)>();
        var timeline=new WindowsStreamTimeline(1_000_000,f=>{encoded.Add(f);return true;},(a,b,_,_)=>gaps.Add((a,b)));
        Assert.True(cadence.Select(10_000_000));timeline.Image(new([1],10_000_000));
        Assert.False(cadence.Select(10_100_000));Assert.True(cadence.Select(10_500_000));
        timeline.Drop(10_500_000,"real admitted-frame loss");timeline.Stop(12_000_000);
        Assert.Single(encoded);Assert.Equal(500_000,encoded[0].DurationTicks);Assert.Equal([(10_500_000L,12_000_000L)],gaps);
    }
}
