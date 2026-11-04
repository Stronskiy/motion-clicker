using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

internal sealed record WindowsVideoFrame(byte[] Bgra,long SourceTicks);
internal sealed record WindowsTimedFrame(byte[] Bgra,long SourceTicks,long EncodedTicks,long DurationTicks);
internal sealed class WindowsStreamTimeline(long epochHostUs,Func<WindowsTimedFrame,bool> submit,
    Action<long,long,GapKind,string> reportGap)
{
    internal string? Failure { get; private set; }
    internal void Image(WindowsVideoFrame frame) { _=epochHostUs;_=submit;_=reportGap;Failure=null;throw new NotImplementedException(); }
    internal void Drop(long sourceTicks,string reason)=>throw new NotImplementedException();
    internal void Stop(long sourceTicks)=>throw new NotImplementedException();
}
