using System.Collections.Concurrent;

namespace MotionClicker.Windows.Recording;

internal sealed record WindowsEncodedFrame(long SourceTicks,long EncodedTicks,long DurationTicks);
internal interface IWindowsEncoderWorker
{
    Task Ready { get; }
    Task<NativeWriterResult> Closed { get; }
    bool Submit(WindowsTimedFrame frame);
    bool TryAcknowledge(out WindowsEncodedFrame? frame);
    void Finish();
    void Quarantine();
}

// Native writer creation, append, finalization and disposal all run on this thread.
internal sealed class WindowsEncoderWorker : IWindowsEncoderWorker
{
    private readonly BlockingCollection<WindowsTimedFrame> input=new(3);
    private readonly BlockingCollection<WindowsEncodedFrame> acknowledged=new(3);
    private readonly CancellationTokenSource quarantine=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NativeWriterResult> closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready=>ready.Task;
    public Task<NativeWriterResult> Closed=>closed.Task;
    internal WindowsEncoderWorker(Func<IWindowsVideoEncoder> create,Action<string> fault,Action wake)
    {
        new Thread(()=>
        {
            IWindowsVideoEncoder? writer=null;string? failure=null;NativeWriterResult? result=null;bool closureConfirmed=true;
            try
            {
                writer=create();ready.TrySetResult();
                foreach(var frame in input.GetConsumingEnumerable())
                {
                    writer.Append(frame.Bgra,frame.SourceTicks,frame.DurationTicks);
                    acknowledged.Add(new(frame.SourceTicks,frame.EncodedTicks,frame.DurationTicks),quarantine.Token);wake();
                }
            }
            catch(Exception error){failure=error.Message;if(error is NativeEncoderClosureException)closureConfirmed=false;ready.TrySetException(error);fault(error.Message);}
            finally
            {
                try{if(writer is not null)result=writer.Finish();}
                catch(Exception error){failure??=error.Message;closureConfirmed=false;fault(error.Message);}
                try{writer?.Dispose();}catch(Exception error){failure??=error.Message;closureConfirmed=false;fault(error.Message);}
                result??=new(false,0,0,failure??"Encoder initialization failed");
                if(failure is not null)result=result with {Succeeded=false,Failure=failure};
                result=result with {ClosureConfirmed=result.ClosureConfirmed&&closureConfirmed};
                closed.TrySetResult(result);wake();
            }
        }){IsBackground=true,Name="Motion Clicker recording encoder"}.Start();
    }
    public bool Submit(WindowsTimedFrame frame)=>!input.IsAddingCompleted&&input.TryAdd(frame);
    public bool TryAcknowledge(out WindowsEncodedFrame? frame)=>acknowledged.TryTake(out frame);
    public void Finish()=>input.CompleteAdding();
    // Cancels managed acknowledgment waits only. Never disposes under a live COM call.
    public void Quarantine(){quarantine.Cancel();input.CompleteAdding();}
}
