namespace MotionClicker.Windows.Recording;

internal sealed record WindowsImageBatch(WindowsVideoFrame? Image,long LossTicks=long.MaxValue,string? LossReason=null);
// One coherent atomic source handoff: image and earliest loss can never cross drains.
internal sealed class WindowsImageMailbox
{
    private static readonly WindowsImageBatch empty=new(null);
    private WindowsImageBatch batch=empty;
    internal bool Image(WindowsVideoFrame frame)
    {
        while(true)
        {
            var previous=Volatile.Read(ref batch);bool accepted=previous.Image is null;
            var next=accepted?previous with {Image=frame}:Loss(previous,frame.SourceTicks,"Capture image mailbox full");
            if(ReferenceEquals(Interlocked.CompareExchange(ref batch,next,previous),previous))return accepted;
        }
    }
    internal void Drop(long ticks,string reason)
    {
        while(true)
        {
            var previous=Volatile.Read(ref batch);var next=Loss(previous,ticks,reason);
            if(ReferenceEquals(Interlocked.CompareExchange(ref batch,next,previous),previous))return;
        }
    }
    private static WindowsImageBatch Loss(WindowsImageBatch previous,long ticks,string reason)=>
        ticks<previous.LossTicks?previous with {LossTicks=ticks,LossReason=reason}:previous;
    internal WindowsImageBatch Drain()=>Interlocked.Exchange(ref batch,empty);
}
