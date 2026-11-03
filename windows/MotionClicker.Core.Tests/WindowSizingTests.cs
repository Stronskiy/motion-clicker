using MotionClicker.Windows;
using Xunit;
namespace MotionClicker.Core.Tests;

public sealed class WindowSizingTests {
    [Theory]
    [InlineData(1,980,720,1180,800)]
    [InlineData(1.5,1470,1080,1770,1200)]
    [InlineData(2,1960,1440,2360,1600)]
    public void LogicalMinimumAndDefaultSurviveDisplayScale(double scale,int minWidth,int minHeight,int defaultWidth,int defaultHeight) {
        Assert.True(WindowSizing.TryCalculate(980,720,scale,16,39,out var minimum));
        Assert.Equal(minWidth,minimum.ClientWidth); Assert.Equal(minHeight,minimum.ClientHeight);
        // Non-client extents are already physical pixels: never multiply them again.
        Assert.Equal(minWidth+16,minimum.OuterWidth); Assert.Equal(minHeight+39,minimum.OuterHeight);
        Assert.True(WindowSizing.TryCalculate(1180,800,scale,16,39,out var normal));
        Assert.Equal(defaultWidth,normal.ClientWidth); Assert.Equal(defaultHeight,normal.ClientHeight);
    }
    [Fact] public void FractionalClientExtentRoundsUpSoMinimumNeverShrinks() {
        Assert.True(WindowSizing.TryCalculate(980.25,720.25,1.5,24,58,out var size));
        Assert.Equal(1471,size.ClientWidth); Assert.Equal(1081,size.ClientHeight);
        Assert.Equal(1495,size.OuterWidth); Assert.Equal(1139,size.OuterHeight);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidScaleCannotReachNativeIntegerSizing(double scale) {
        Assert.False(WindowSizing.TryCalculate(980,720,scale,16,39,out _));
    }
    [Fact] public void InvalidDimensionsAndOverflowCannotReachNativeSizing() {
        Assert.False(WindowSizing.TryCalculate(double.MaxValue,720,2,16,39,out _));
        Assert.False(WindowSizing.TryCalculate(double.NaN,720,1,16,39,out _));
        Assert.False(WindowSizing.TryCalculate(980,0,1,16,39,out _));
        Assert.False(WindowSizing.TryCalculate(980,720,1,-1,39,out _));
        Assert.False(WindowSizing.TryCalculate(int.MaxValue,720,1,16,39,out _));
    }
}
