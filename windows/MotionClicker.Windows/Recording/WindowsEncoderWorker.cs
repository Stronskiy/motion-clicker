using System.Collections.Concurrent;

namespace MotionClicker.Windows.Recording;

// Native writer creation, append, finalization and disposal all run on this thread.
internal sealed class WindowsEncoderWorker
{
    private readonly BlockingCollection<WindowsTimedFrame> input=new(3),acknowledged=new(3);
    private readonly CancellationTokenSource quarantine=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NativeWriterResult> closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready=>ready.Task;
    internal Task<NativeWriterResult> Closed=>closed.Task;
    internal WindowsEncoderWorker(Func<IWindowsVideoEncoder> create,Action<string> fault,Action wake)
    {
        new Thread(()=>
        {
            IWindowsVideoEncoder? writer=null;string? failure=null;NativeWriterResult? result=null;
            try
            {
                writer=create();ready.TrySetResult();
                foreach(var frame in input.GetConsumingEnumerable())
                {
                    writer.Append(frame.Bgra,frame.SourceTicks,frame.DurationTicks);
                    acknowledged.Add(frame,quarantine.Token);wake();
                }
            }
            catch(Exception error){failure=error.Message;ready.TrySetException(error);fault(error.Message);}
            finally
            {
                try{if(writer is not null)result=writer.Finish();}
                catch(Exception error){failure??=error.Message;fault(error.Message);}
                try{writer?.Dispose();}catch(Exception error){failure??=error.Message;fault(error.Message);}
                result??=new(false,0,0,failure??"Encoder initialization failed");
                if(failure is not null)result=result with {Succeeded=false,Failure=failure};
                closed.TrySetResult(result);wake();
            }
        }){IsBackground=true,Name="Motion Clicker recording encoder"}.Start();
    }
    internal bool Submit(WindowsTimedFrame frame)=>!input.IsAddingCompleted&&input.TryAdd(frame);
    internal bool TryAcknowledge(out WindowsTimedFrame? frame)=>acknowledged.TryTake(out frame);
    internal void Finish()=>input.CompleteAdding();
    // Cancels managed acknowledgment waits only. Never disposes under a live COM call.
    internal void Quarantine(){quarantine.Cancel();input.CompleteAdding();}
}
