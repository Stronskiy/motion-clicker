using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public class WindowsStreamTimelineTests
{
    private static WindowsVideoFrame Frame(long pts)=>new([1,2,3,4],pts);
    private static WindowsStreamTimeline Timeline(List<WindowsTimedFrame> frames,List<(long,long)> gaps,Func<WindowsTimedFrame,bool>? submit=null) =>
        new(100,submit??(f=> {frames.Add(f);return true;}),(start,end,_,_)=>gaps.Add((start,end)));

    [Fact] public void PreEpochImageDoesNotEraseRealLateOffsetOrSourceIntervals()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];
        var timeline=Timeline(frames,gaps);
        timeline.Image(Frame(999));timeline.Image(Frame(1500));timeline.Image(Frame(2500));timeline.Stop(3500);
        Assert.Equal(new[]{(1500L,0L,1000L),(2500L,1000L,1000L)},frames.Select(f=>(f.SourceTicks,f.EncodedTicks,f.DurationTicks)));
        Assert.Empty(gaps);
    }

    [Fact] public void StaticTailRetainsOneGenuineImageUntilStop()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];
        var timeline=Timeline(frames,gaps);timeline.Image(Frame(2000));timeline.Stop(22000);
        var frame=Assert.Single(frames);Assert.Equal(20000,frame.DurationTicks);Assert.Equal(0,frame.EncodedTicks);Assert.Empty(gaps);
    }

    [Fact] public void KnownLossSpansUntilNextGenuineImageAndRejectsSparseContinuation()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];
        var timeline=Timeline(frames,gaps);timeline.Image(Frame(1000));timeline.Drop(2000,"source backlog");
        Assert.Throws<RecordingException>(()=>timeline.Image(Frame(5000)));
        Assert.Equal(new[]{(2000L,5000L)},gaps);Assert.Single(frames);Assert.NotNull(timeline.Failure);
    }

    [Fact] public void TrailingLossEndsAtStopRatherThanOneFrameDuration()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];
        var timeline=Timeline(frames,gaps);timeline.Image(Frame(1000));timeline.Drop(2000,"source backlog");timeline.Stop(9000);
        Assert.Equal(new[]{(2000L,9000L)},gaps);Assert.Equal(1000,Assert.Single(frames).DurationTicks);
    }

    [Fact] public void RejectedAdmissionStartsLossAtTheUnencodedPendingImage()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];
        var timeline=Timeline(frames,gaps,_=>false);timeline.Image(Frame(1000));
        Assert.Throws<RecordingException>(()=>timeline.Image(Frame(2000)));
        timeline.Stop(7000);Assert.Equal(new[]{(1000L,7000L)},gaps);Assert.NotNull(timeline.Failure);
    }

    [Fact] public void DropSubmissionFailureIsRetainedThroughStopWithoutRetryingPixels()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];int attempts=0;
        var timeline=Timeline(frames,gaps,_=> {attempts++;throw new IOException("encoder disk failure");});
        timeline.Image(Frame(1000));Assert.Throws<IOException>(()=>timeline.Drop(2000,"drop"));
        timeline.Stop(9000);Assert.Equal(1,attempts);Assert.Equal(new[]{(1000L,9000L)},gaps);Assert.Contains("disk failure",timeline.Failure);
    }

    [Fact] public void StopIsIdempotentAndLateImagesCannotReopenTheStream()
    {
        List<WindowsTimedFrame> frames=[];List<(long,long)> gaps=[];
        var timeline=Timeline(frames,gaps);timeline.Image(Frame(1000));timeline.Stop(2000);timeline.Stop(3000);timeline.Image(Frame(4000));
        Assert.Single(frames);Assert.Equal(1000,frames[0].DurationTicks);
    }
}
