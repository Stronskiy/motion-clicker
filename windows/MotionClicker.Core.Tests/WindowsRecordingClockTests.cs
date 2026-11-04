using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public class WindowsRecordingClockTests
{
    // A floating-point conversion rounds this source scalar to a different microsecond.
    [Fact] public void QpcAboveDoublePrecisionRetainsExactMicroseconds() =>
        Assert.Equal(9_007_199_254_740_999L,WindowsRecordingClock.CounterToHostUs(90_071_992_547_409_999L,10_000_000));

    [Theory]
    [InlineData(-1L,10_000_000L,-1L)]
    [InlineData(-11L,10_000_000L,-2L)]
    [InlineData(long.MinValue,10_000_000L,-922_337_203_685_477_581L)]
    [InlineData(long.MaxValue,10_000_000L,922_337_203_685_477_580L)]
    [InlineData(3L,2L,1_500_000L)]
    public void ConversionFloorsSignedFractionsWithoutIntermediateOverflow(long counter,long frequency,long expected) =>
        Assert.Equal(expected,WindowsRecordingClock.CounterToHostUs(counter,frequency));

    [Theory]
    [InlineData(0L)] [InlineData(-1L)]
    public void InvalidFrequencyRejectsInsteadOfGuessingDomain(long frequency) =>
        Assert.Throws<ArgumentOutOfRangeException>(()=>WindowsRecordingClock.CounterToHostUs(1,frequency));

    [Fact] public void UnrepresentableHostTimeRejectsInsteadOfSaturating() =>
        Assert.Throws<OverflowException>(()=>WindowsRecordingClock.CounterToHostUs(long.MaxValue,1));
}
