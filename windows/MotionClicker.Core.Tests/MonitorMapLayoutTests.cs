using MotionClicker.Core.Runtime;
using MotionClicker.Windows;
using Xunit;
namespace MotionClicker.Core.Tests;

public sealed class MonitorMapLayoutTests {
    private static RuntimeDisplay Display(string id,double x,double y,double w,double h,double scale=1)=>new(id,id,new(new(x/scale,y/scale),new(w/scale,h/scale),scale,0),new(new(x,y),new(w,h)));
    [Fact] public void MixedDPIAdjacentDisplaysStayAdjacentAndPointsStayLocal() {
        var a=Display("a",0,0,1920,1080); var b=Display("b",1920,0,3840,2160,2);
        var map=new MonitorMapLayout([a,b],600,300); var left=map.Frames["a"]; var right=map.Frames["b"];
        Assert.Equal(left.Origin.X+left.Size.Width,right.Origin.X,6);
        Assert.Equal(left.Size.Width*2,right.Size.Width,6);
        var p=map.Project(new(960,540),b); Assert.NotNull(p);
        Assert.Equal(right.Origin.X+right.Size.Width/2,p.X,6); Assert.Equal(right.Origin.Y+right.Size.Height/2,p.Y,6);
    }
    [Fact] public void NegativeOriginsKeepRelativeOrder() {
        var map=new MonitorMapLayout([Display("a",-1600,-900,1600,900),Display("b",0,0,1920,1080)],500,260);
        Assert.True(map.Frames["a"].Origin.X<map.Frames["b"].Origin.X);
        Assert.True(map.Frames["a"].Origin.Y<map.Frames["b"].Origin.Y);
        Assert.True(map.Frames["a"].Origin.X>=0);
        Assert.True(map.Frames["b"].Origin.X+map.Frames["b"].Size.Width<=500);
    }
    [Fact] public void ExtremeFiniteBoundsAndInvalidPointsAreSafe() {
        var d=Display("huge",-1e308,-1e308,1e308,1e308);
        var map=new MonitorMapLayout([d,Display("right",0,0,1e308,1e308)],500,260);
        Assert.True(double.IsFinite(map.Frames["huge"].Origin.X));
        Assert.True(double.IsFinite(map.Frames["huge"].Size.Width));
        Assert.Null(map.Project(new(double.PositiveInfinity,0),d)); Assert.Null(map.Project(new(-1,0),d));
        Assert.Null(map.Project(new(1e308,0),d)); Assert.NotNull(map.Project(new(5e307,5e307),d));
        Assert.Empty(new MonitorMapLayout([],500,260).Frames);
        Assert.Empty(new MonitorMapLayout([d],double.PositiveInfinity,260).Frames);
    }
}
