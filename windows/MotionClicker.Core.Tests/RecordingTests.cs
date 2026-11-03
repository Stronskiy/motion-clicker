using MotionClicker.Core.Recording;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class RecordingTests
{
    private sealed record Timing(long SourceAnchorTicks, long HostAnchorUs, long TicksPerSecond, long EpochHostUs, long SourceTicks, long ExpectedUs);
    [Fact] public void NativeAnchorsLargeIntegersAndFloorFromSharedFixtures()
    {
        var cases = RecordingJson.Deserialize<Timing[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","timing.json")));
        foreach(var c in cases) Assert.Equal(c.ExpectedUs,new MediaClockMapper(c.SourceAnchorTicks,c.HostAnchorUs,c.TicksPerSecond,c.EpochHostUs).Map(c.SourceTicks));
    }
    [Fact] public void InvalidScaleAndOverflowAreVisible()
    {
        foreach(long scale in new long[]{0,-1,2147483648}) Assert.ThrowsAny<Exception>(()=>new MediaClockMapper(0,0,scale,0));
        Assert.ThrowsAny<Exception>(()=>new MediaClockMapper(0,0,1,0).Map(long.MaxValue));
        Assert.ThrowsAny<Exception>(()=>new MediaClockMapper(long.MinValue,0,1000000,0).Map(long.MaxValue));
    }
}
