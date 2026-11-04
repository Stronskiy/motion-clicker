using MotionClicker.Core;
using MotionClicker.Windows.RecordingUI;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class ControlRegionTests {
    [Fact] public void PhysicalNegativeDesktopRegionsAreImmutableAndHalfOpen() {
        var regions=new ControlRegions();var rectangles=new[]{new Rect(new(-1920,200),new(980,720))};regions.Update(rectangles);rectangles[0]=new(new(0,0),new(1,1));
        Assert.True(regions.Contains(new(-1920,200)));Assert.True(regions.Contains(new(-941,919)));
        Assert.False(regions.Contains(new(-940,200)));Assert.False(regions.Contains(new(-1920,920)));Assert.False(regions.Contains(new(0,0)));
        regions.Update([]);Assert.False(regions.Contains(new(-1920,200)));
    }
    [Fact] public void OneOperationLeaseKeepsPausedExecutionAndOpeningMutuallyExclusive() {
        var gate=new AppOperationGate();using(var run=gate.TryEnter(AppOperation.Execution)) { Assert.NotNull(run);Assert.Null(gate.TryEnter(AppOperation.Recording));Assert.Null(gate.TryEnter(AppOperation.Opening));Assert.True(gate.Busy); }
        using(var recording=gate.TryEnter(AppOperation.Recording)){Assert.NotNull(recording);Assert.Null(gate.TryEnter(AppOperation.Execution));}
        Assert.False(gate.Busy);
    }
}
