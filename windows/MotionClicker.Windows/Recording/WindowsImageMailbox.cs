namespace MotionClicker.Windows.Recording;

internal sealed record WindowsImageBatch(WindowsVideoFrame? Image,long LossTicks=long.MaxValue,string? LossReason=null);
// One coherent atomic source handoff: image and earliest loss can never cross drains.
internal sealed class WindowsImageMailbox
{
    internal bool Image(WindowsVideoFrame frame)=>true;
    internal void Drop(long ticks,string reason) { }
    internal WindowsImageBatch Drain()=>new(null);
}
