using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

internal sealed record WindowsVideoFrame(byte[] Bgra,long SourceTicks);
internal sealed record WindowsTimedFrame(byte[] Bgra,long SourceTicks,long EncodedTicks,long DurationTicks);

// Serial recording owner only. Exactly one pending image determines VFR duration
// from the next genuine source image, known loss boundary, or explicit stop.
internal sealed class WindowsStreamTimeline(long epochHostUs,Func<WindowsTimedFrame,bool> submit,
    Action<long,long,GapKind,string> reportGap)
{
    private WindowsVideoFrame? pending;
    private long? zero,lastSource,lossStart;
    private string lossReason="Capture backlog";
    private bool stopped;
    internal string? Failure { get; private set; }
    internal bool HasImage { get; private set; }

    internal void Image(WindowsVideoFrame frame)
    {
        if(stopped || (Int128)frame.SourceTicks<(Int128)epochHostUs*10)return;
        try
        {
            if(lastSource is long last && frame.SourceTicks<=last)throw new RecordingException("Reversed or duplicate source image time");
            lastSource=frame.SourceTicks;
            if(lossStart is not null)
            {
                CloseLoss(frame.SourceTicks);
                if(zero is not null)throw new RecordingException("Video discontinuity unsupported after capture loss");
            }
            if(Failure is not null)throw new RecordingException(Failure);
            FlushPending(frame.SourceTicks);
            pending=frame;HasImage=true;
        }
        catch(Exception error) { Failure??=error.Message;throw; }
    }

    internal void Drop(long sourceTicks,string reason)
    {
        if(stopped || (Int128)sourceTicks<(Int128)epochHostUs*10)return;
        try
        {
            FlushPending(sourceTicks);
            CloseLoss(sourceTicks);
            lossStart=sourceTicks;lossReason=reason;
        }
        catch(Exception error) { Failure??=error.Message;throw; }
    }

    private void FlushPending(long endTicks)
    {
        if(pending is not WindowsVideoFrame frame)return;
        pending=null; // A failed native submission is never retried with the same historical image.
        try
        {
            var duration=checked(endTicks-frame.SourceTicks);
            if(duration<10)throw new RecordingException("Source image has no positive microsecond duration");
            var origin=zero??frame.SourceTicks;
            if(!submit(new(frame.Bgra,frame.SourceTicks,checked(frame.SourceTicks-origin),duration)))
                throw new RecordingException("Bounded encoder admission is full");
            zero??=frame.SourceTicks;
        }
        catch(Exception error)
        {
            lossStart??=frame.SourceTicks;lossReason=error.Message;Failure??=error.Message;throw;
        }
    }

    private void CloseLoss(long endTicks)
    {
        if(lossStart is not long start)return;
        lossStart=null;
        if(endTicks>start)reportGap(start,endTicks,GapKind.Backpressure,lossReason);
    }

    internal void Stop(long sourceTicks)
    {
        if(stopped)return;
        stopped=true;
        try { if(Failure is null)FlushPending(sourceTicks); }
        catch(Exception error) { Failure??=error.Message;throw; }
        finally { pending=null;CloseLoss(sourceTicks); }
    }
}
