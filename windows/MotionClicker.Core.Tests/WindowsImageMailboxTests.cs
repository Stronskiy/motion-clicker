using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public sealed class WindowsImageMailboxTests
{
    [Fact] public void FullMailboxRetainsFirstImageAndEarliestActualLoss()
    {
        var mailbox=new WindowsImageMailbox();var first=new WindowsVideoFrame([1],100);
        Assert.True(mailbox.Image(first));Assert.False(mailbox.Image(new([2],200)));
        mailbox.Drop(300,"later loss");var batch=mailbox.Drain();
        Assert.Same(first,batch.Image);Assert.Equal(200,batch.LossTicks);Assert.NotNull(batch.LossReason);
        Assert.Null(mailbox.Drain().Image);Assert.Equal(long.MaxValue,mailbox.Drain().LossTicks);
    }
    [Fact] public void NewImageCannotBeSeparatedFromLossByPreviousDrain()
    {
        var mailbox=new WindowsImageMailbox();Assert.True(mailbox.Image(new([1],100)));
        var old=mailbox.Drain();Assert.True(mailbox.Image(new([2],200)));mailbox.Drop(300,"full");
        var next=mailbox.Drain();Assert.Equal(100,old.Image!.SourceTicks);Assert.Equal(long.MaxValue,old.LossTicks);
        Assert.Equal(200,next.Image!.SourceTicks);Assert.Equal(300,next.LossTicks);
    }
}
