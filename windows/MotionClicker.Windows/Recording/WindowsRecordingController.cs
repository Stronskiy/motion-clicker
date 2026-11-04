using System.Collections.Concurrent;
using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

public sealed partial class WindowsRecordingController : IAsyncDisposable
{
    private sealed class Stream(RecordingTarget target,WindowsRecordingDisplay display)
    {
        internal readonly RecordingTarget Target=target;
        internal readonly WindowsRecordingDisplay Display=display;
        internal IWindowsEncoderWorker Encoder=null!;
        internal IWindowsRecordingSource? Source;
        internal WindowsStreamTimeline? Timeline;
        internal readonly WindowsImageMailbox Mailbox=new();
        internal string? SourceFailure;
        internal bool Terminal;
        internal long AcknowledgedCount;
        internal int StopSignaled;
    }
    internal const long DeadlineUs=10_000_000;
    private readonly IWindowsRecordingPlatform platform;
    private readonly Func<Func<IWindowsVideoEncoder>,Action<string>,Action,IWindowsEncoderWorker> encoderWorker;
    private readonly WindowsPointerAdapter pointerAdapter;
    private readonly BlockingCollection<Action> commands=new(16);
    private readonly BlockingCollection<WindowsPointerSample> pointers=new(1024);
    private readonly AutoResetEvent wake=new(false);
    private readonly TaskCompletionSource disposed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WindowsRecordingCompletion?> stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource safetyStop=new();
    private readonly object commandGate=new();
    private sealed record StopBoundary(long HostUs,long WallTicks,bool ClockValid);
    private StopBoundary? stopBoundary;
    private Stream[] streams=[];
    private RecordingStore? store;
    private StoreRecovery? recovery;
    private RecordingSession? session;
    private IWindowsPointerSource? pointer;
    private TaskCompletionSource<WindowsRecordingPreparation>? preparation;
    private TaskCompletionSource? starting;
    private CancellationTokenRegistration prepareCancellation,startCancellation;
    private string? failure;
    private long epoch=-1,beginUs,stopUs,stopWallTicks;
    private long accepted,dropped;
    private int startRequested,stopRequested,pointerStopSignaled,disposeRequested,ingress,producersDrained,quarantined,publishing;
    private bool prepared,timelinesStopped,encodersStopping,exiting;
    private WindowsRecordingCompletion? completion;

    internal WindowsRecordingController(WindowsRecordingOptions options,IWindowsRecordingPlatform platform,
        Func<Func<IWindowsVideoEncoder>,Action<string>,Action,IWindowsEncoderWorker>? encoderWorker=null)
    {
        ArgumentNullException.ThrowIfNull(options);ArgumentNullException.ThrowIfNull(options.IsControl);
        this.platform=platform;pointerAdapter=new(options);
        this.encoderWorker=encoderWorker??((create,fault,wake)=>new WindowsEncoderWorker(create,fault,wake));
        new Thread(()=>{while(!safetyStop.Token.WaitHandle.WaitOne(100))SafetyTick();})
            {IsBackground=true,Name="Motion Clicker recording watchdog"}.Start();
        new Thread(Owner){IsBackground=true,Name="Motion Clicker recording owner"}.Start();
    }
    private Task<T> Command<T>(Func<T> action)
    {
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(commandGate)if(Volatile.Read(ref disposeRequested)!=0||!commands.TryAdd(()=>
        {
            try{result.TrySetResult(action());}catch(Exception error){result.TrySetException(error);}
        }))result.TrySetException(new RecordingException("Recording owner unavailable or command mailbox full"));
        wake.Set();return result.Task;
    }
    public async Task<WindowsRecordingPreparation> PrepareAsync(RecordingTarget[] targets,string root,string takeId,CancellationToken cancellation=default)
    {
        var frozen=targets.ToArray();
        var task=await Command(()=>
        {
            if(preparation is not null||Volatile.Read(ref stopRequested)!=0)throw new RecordingException("Recorder is single-use");
            preparation=new(TaskCreationOptions.RunContinuationsAsynchronously);beginUs=platform.NowUs;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var displays=platform.Validate(frozen);
                if(displays.Length!=frozen.Length)throw new RecordingException("Incomplete frozen monitor mapping");
                store=new(root,takeId);Volatile.Write(ref recovery,store.Recovery);
                session=new(takeId,frozen,store,PointerTimingBasis.NativeHostObservation);
                streams=frozen.Select(t=>new Stream(t,displays.Single(d=>d.MonitorSlotId==t.Slot.Id))).ToArray();
                foreach(var stream in streams)
                {
                    var path=store.MediaFile(stream.Target.Slot.Id);
                    stream.Encoder=encoderWorker(()=>platform.Encoder(path,stream.Display.PixelWidth,stream.Display.PixelHeight),Interrupt,()=>wake.Set());
                }
                prepareCancellation=cancellation.Register(()=>Interrupt("Recording preparation canceled"));
            }
            catch(Exception error){preparation.TrySetException(error);Interrupt(error.Message);}
            return preparation.Task;
        });
        return await task;
    }
    public async Task StartAsync(CancellationToken cancellation=default)
    {
        var task=await Command(()=>
        {
            if(!prepared||starting is not null||Volatile.Read(ref stopRequested)!=0)throw new RecordingException("Recorder cannot start");
            starting=new(TaskCreationOptions.RunContinuationsAsynchronously);beginUs=platform.NowUs;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                foreach(var stream in streams)stream.Source=platform.Capture(stream.Target,f=>Image(stream,f),
                    (ticks,reason)=>Drop(stream,ticks,reason),reason=>{Interlocked.CompareExchange(ref stream.SourceFailure,reason,null);Interrupt(reason);});
                pointer=platform.Pointer(Pointer,Interrupt);
                if(Volatile.Read(ref stopRequested)!=0){RequestNativeStop();throw new RecordingException("Stopped during native setup");}
                Volatile.Write(ref ingress,1);Volatile.Write(ref startRequested,1);
                foreach(var stream in streams)stream.Source!.Start();pointer.Start();
                if(Volatile.Read(ref stopRequested)!=0){Volatile.Write(ref ingress,0);RequestNativeStop();}
                startCancellation=cancellation.Register(()=>Interrupt("Recording start canceled"));
            }
            catch(Exception error){starting.TrySetException(error);Interrupt(error.Message);}
            return starting.Task;
        });
        await task;
    }
    public Task<WindowsRecordingCompletion?> StopAsync(string? reason=null){RequestStop(reason);return stopped.Task;}
    public Task<WindowsRecordingProgress> SnapshotAsync()=>Command(()=>new WindowsRecordingProgress(
        session?.State??RecordingState.Preparing,accepted,Interlocked.Read(ref dropped),Volatile.Read(ref failure)));
    public Task<StoreRecovery?> RecoveryAsync()=>Task.FromResult(Volatile.Read(ref recovery));
    public Task<RecordingResult> ProjectAsync()=>Command(()=>
    {
        if(completion is null)throw new RecordingException("Recording has no published completion");return session!.Project();
    });
    private void Image(Stream stream,WindowsVideoFrame frame)
    {
        if(!AcceptVideo(frame.SourceTicks))return;
        if(!stream.Mailbox.Image(frame))Interlocked.Increment(ref dropped);
        wake.Set();
    }
    private void Drop(Stream stream,long ticks,string reason)
    {
        if(!AcceptVideo(ticks))return;
        stream.Mailbox.Drop(ticks,reason);Interlocked.Increment(ref dropped);wake.Set();
    }
    private void Pointer(WindowsPointerSample sample)
    {
        if(!AcceptIngress(sample.ObservationHostUs))return;
        if(!pointers.TryAdd(sample))Interrupt("Pointer mailbox overflow");wake.Set();
    }
    private void Interrupt(string reason)=>RequestStop(reason);
    private bool AcceptVideo(long sourceTicks)
    {
        if(!AcceptIngress(sourceTicks/10))return false;
        var boundary=Volatile.Read(ref stopBoundary);return boundary is null||(Int128)sourceTicks<=(Int128)boundary.HostUs*10;
    }
    private bool AcceptIngress(long hostUs)
    {
        if(Volatile.Read(ref producersDrained)!=0||stopped.Task.IsCompleted)return false;
        if(Volatile.Read(ref ingress)!=0)return true;
        var boundary=Volatile.Read(ref stopBoundary);return boundary is not null&&hostUs<=boundary.HostUs&&Volatile.Read(ref startRequested)!=0;
    }
    private void RequestStop(string? reason)
    {
        if(stopped.Task.IsCompleted)return;
        if(reason is not null)Interlocked.CompareExchange(ref failure,reason,null);
        if(Volatile.Read(ref stopBoundary) is not null){wake.Set();return;}
        long host=0;bool clockValid=true;
        try{host=platform.NowUs;}
        catch(Exception error){clockValid=false;Interlocked.CompareExchange(ref failure,"Stop QPC unavailable: "+error.Message,null);}
        if(Interlocked.CompareExchange(ref stopBoundary,new(host,Environment.TickCount64,clockValid),null) is null)
        {
            var boundary=stopBoundary!;stopUs=boundary.HostUs;stopWallTicks=boundary.WallTicks;
            Volatile.Write(ref ingress,0);Volatile.Write(ref stopRequested,1);
        }
        wake.Set();
    }
    private void RequestNativeStop()
    {
        // Signal only: never joins/disposes or waits for native work.
        foreach(var stream in streams)
        {
            if(stream.Source is null||Interlocked.Exchange(ref stream.StopSignaled,1)!=0)continue;
            try{stream.Source.RequestStop();}catch(Exception error){Interlocked.CompareExchange(ref failure,error.Message,null);}
        }
        if(pointer is not null&&Interlocked.Exchange(ref pointerStopSignaled,1)==0)
            try{pointer.RequestStop();}catch(Exception error){Interlocked.CompareExchange(ref failure,error.Message,null);}
    }
    private void SafetyTick()
    {
        try
        {
            if(Volatile.Read(ref stopRequested)!=0)
            {
                RequestNativeStop();
                if(!stopped.Task.IsCompleted&&Environment.TickCount64-Interlocked.Read(ref stopWallTicks)>=DeadlineUs/1000&&
                    Interlocked.CompareExchange(ref publishing,2,0)==0)
                    Quarantine("Native shutdown deadline exceeded while owner or native work was unavailable");
                return;
            }
            if(Volatile.Read(ref startRequested)!=0&&Volatile.Read(ref stopRequested)==0)
            {
                if(streams.Any(s=>s.Source?.Closed.IsCompleted==true)||pointer?.Closed.IsCompleted==true)
                {Interrupt("Required native source or hook thread exited");RequestNativeStop();return;}
                pointer?.Probe(platform.NowUs);
                if(starting is {Task.IsCompleted:false}&&platform.NowUs-Interlocked.Read(ref beginUs)>=DeadlineUs)
                    Interrupt("Recording initialization requires a genuine post-epoch image within 10 seconds");
            }
        }
        catch(Exception error){Interrupt(error.Message);}
    }
    private void Owner()
    {
        try
        {
            while(!exiting)
            {
                try
                {
                    Advance();Drain();
                    for(int i=0;i<16&&commands.TryTake(out var command);i++)command();
                    Advance();
                }
                catch(Exception error){Interrupt(error.Message);}
                if(Volatile.Read(ref disposeRequested)!=0&&stopped.Task.IsCompleted)exiting=true;
                if(!exiting)wake.WaitOne(10);
            }
        }
        finally
        {
            safetyStop.Cancel();prepareCancellation.Dispose();startCancellation.Dispose();
            try{store?.Dispose();}catch(Exception error){disposed.TrySetException(error);}
            finally{disposed.TrySetResult();}
        }
    }
    private void Advance()
    {
        if(preparation is {Task.IsCompleted:false}&&streams.Length>0)
        {
            if(streams.All(s=>s.Encoder is not null&&s.Encoder.Ready.IsCompletedSuccessfully))
            {
                prepared=true;preparation.TrySetResult(new(streams.Select(s=>s.Display).ToArray()));prepareCancellation.Dispose();
            }
            else if(streams.Any(s=>s.Encoder?.Ready.IsFaulted==true))Interrupt("Native encoder initialization failed");
            else if(platform.NowUs-beginUs>=DeadlineUs)Interrupt("Native encoder initialization deadline exceeded");
        }
        if(starting is not null&&epoch<0&&Volatile.Read(ref stopRequested)==0)
        {
            var failed=streams.Select(s=>s.Source!.Ready).Append(pointer!.Ready)
                .FirstOrDefault(task=>task.IsCompleted&&!task.IsCompletedSuccessfully);
            if(failed is not null)Interrupt(failed.IsCanceled?"Required native source readiness canceled":failed.Exception!.GetBaseException().Message);
            else if(streams.All(s=>s.Source!.Ready.IsCompletedSuccessfully)&&pointer!.Ready.IsCompletedSuccessfully)
            {
                epoch=platform.NowUs;
                foreach(var stream in streams)
                {
                    session!.SourceReady(stream.Target.Slot.Id,new(0,0,10_000_000));
                    stream.Timeline=new(epoch,stream.Encoder.Submit,(a,b,k,r)=>session.ReportGap(stream.Target.Slot.Id,a,b,k,r));
                }
                session!.EventTapReady();session.Begin(epoch);
            }
        }
        if(starting is {Task.IsCompleted:false}&&epoch>=0&&streams.All(s=>s.Timeline!.HasImage)&&Volatile.Read(ref stopRequested)==0)
        {starting.TrySetResult();startCancellation.Dispose();}
        if(Volatile.Read(ref stopRequested)!=0&&!stopped.Task.IsCompleted)FinishStop();
    }
    private void Drain()
    {
        DrainAcknowledgments();
        foreach(var stream in streams)
        {
            if(stream.Encoder is null)continue;
            if(epoch<0)continue;
            var batch=stream.Mailbox.Drain();var image=batch.Image;var loss=batch.LossTicks;
            var reason=batch.LossReason??"Capture admission loss";
            if(image is not null&&image.SourceTicks<loss)stream.Timeline!.Image(image);
            if(loss!=long.MaxValue)stream.Timeline!.Drop(loss,reason);
            if(image is not null&&image.SourceTicks>=loss)stream.Timeline!.Image(image);
        }
        if(epoch<0)return;
        for(int i=0;i<1024&&pointers.TryTake(out var sample);i++)
        {
            var captured=pointerAdapter.Convert(sample,epoch,platform.NowUs);
            if(captured is not null)session!.Receive(captured);
            if(session!.State==RecordingState.Finalizing)Interrupt("Pointer ownership or persistence interrupted recording");
        }
    }
    private void DrainAcknowledgments()
    {
        foreach(var stream in streams)
        {
            if(stream.Encoder is null)continue;
            while(stream.Encoder.TryAcknowledge(out var frame))
            {
                var metadata=frame!;
                session?.AcceptedFrame(stream.Target.Slot.Id,metadata.SourceTicks,metadata.EncodedTicks/10,metadata.DurationTicks/10);
                stream.AcknowledgedCount=checked(stream.AcknowledgedCount+1);accepted=checked(accepted+1);
            }
        }
    }
    private bool NativeClosed()=>streams.All(s=>s.Source is null||s.Source.Closed.IsCompleted)&&(pointer is null||pointer.Closed.IsCompleted);
    private void FinishStop()
    {
        if(Volatile.Read(ref quarantined)!=0)return;
        RequestNativeStop();
        if(!NativeClosed())
        {
            if(Environment.TickCount64-stopWallTicks>=DeadlineUs/1000)Quarantine("Native producer closure deadline exceeded");return;
        }
        if(streams.Any(s=>s.Source is not null&&!s.Source.Closed.IsCompletedSuccessfully)||
            (pointer is not null&&!pointer.Closed.IsCompletedSuccessfully))
        {Quarantine("Native producer closure failed");return;}
        if(Volatile.Read(ref stopBoundary) is {ClockValid:false}){Quarantine("QPC stop boundary unavailable");return;}
        if(!timelinesStopped)
        {
            timelinesStopped=true;
            Volatile.Write(ref producersDrained,1);
            try
            {
                try{Drain();}catch(Exception error){Interlocked.CompareExchange(ref failure,error.Message,null);}
                if(epoch>=0)
                {
                    var stop=Math.Max(stopUs,epoch);
                    foreach(var stream in streams)if(Volatile.Read(ref stream.SourceFailure) is string sourceFailure)
                        session!.SourceFailed(stream.Target.Slot.Id,stop,sourceFailure);
                    session!.Stop(stop,Volatile.Read(ref failure));
                    foreach(var stream in streams)
                    {
                        try{stream.Timeline!.Stop(checked(stop*10));}
                        catch(Exception error){session.SourceFailed(stream.Target.Slot.Id,stop,error.Message);Interlocked.CompareExchange(ref failure,error.Message,null);}
                    }
                }
                else session?.PreparationFailed(Volatile.Read(ref failure)??"Stopped before native readiness");
            }
            catch(Exception error){Interlocked.CompareExchange(ref failure,error.Message,null);}
        }
        if(!encodersStopping){encodersStopping=true;foreach(var stream in streams)stream.Encoder?.Finish();}
        Drain();
        if(streams.Any(s=>s.Encoder is not null&&!s.Encoder.Closed.IsCompleted))
        {
            if(Environment.TickCount64-stopWallTicks>=DeadlineUs/1000)Quarantine("Native encoder closure deadline exceeded");return;
        }
        try
        {
            // Closed is published after the worker's last acknowledgment. This
            // second drain exploits that ordering, closing the empty-drain race.
            DrainAcknowledgments();
            if(streams.Any(s=>s.Encoder is not null&&!s.Encoder.Closed.GetAwaiter().GetResult().ClosureConfirmed))
            {Quarantine("Native encoder closure failed");return;}
            if(streams.Any(s=>s.Encoder is not null&&s.Encoder.Closed.GetAwaiter().GetResult().FrameCount!=s.AcknowledgedCount))
            {Quarantine("Native encoder frame count does not match accepted acknowledgments");return;}
            if(epoch>=0)
            {
                foreach(var stream in streams)
                {
                    if(stream.Terminal)continue;var terminal=stream.Encoder.Closed.GetAwaiter().GetResult();
                    session!.WriterFinished(stream.Target.Slot.Id,terminal.Succeeded?WriterStatus.Succeeded:WriterStatus.Failed,
                        terminal.EncodedDurationTicks/10,terminal.Failure);stream.Terminal=true;
                }
                var manifest=session!.Result??throw new RecordingException("Missing final native manifest");
                // CAS excludes an independent quarantine BEFORE any store publication.
                if(Interlocked.CompareExchange(ref publishing,1,0)!=0||Volatile.Read(ref quarantined)!=0)return;
                var path=store!.Publish(manifest);Volatile.Write(ref recovery,store.Recovery);completion=new(manifest,path,store.Recovery);
            }
            preparation?.TrySetException(new RecordingException(Volatile.Read(ref failure)??"Recording stopped before preparation"));
            starting?.TrySetException(new RecordingException(Volatile.Read(ref failure)??"Recording stopped before initialization"));
            stopped.TrySetResult(completion);
        }
        catch(Exception error){if(store is not null)Volatile.Write(ref recovery,store.Recovery);Quarantine("Recording publication failed: "+error.Message);}
    }
    private void Quarantine(string reason)
    {
        if(Interlocked.Exchange(ref quarantined,1)!=0)return;
        Interlocked.CompareExchange(ref publishing,2,0);Interlocked.CompareExchange(ref failure,reason,null);
        Volatile.Write(ref ingress,0);Volatile.Write(ref producersDrained,1);
        foreach(var stream in streams)stream.Encoder?.Quarantine();
        var error=new RecordingException(reason+"; staging retained for recovery");
        preparation?.TrySetException(error);starting?.TrySetException(error);stopped.TrySetException(error);
    }
    public async ValueTask DisposeAsync()
    {
        RequestStop("Recorder disposed");lock(commandGate)Interlocked.Exchange(ref disposeRequested,1);wake.Set();
        try{await stopped.Task;}catch(RecordingException){/* Recoverable quarantine is never deleted. */}
        if(Volatile.Read(ref quarantined)!=0)
        {
            safetyStop.Cancel();
            // Give a responsive owner its ordinary closure turn, but never await a
            // stalled owner's journal/classifier/OS operation indefinitely.
            if(await Task.WhenAny(disposed.Task,Task.Delay(100))==disposed.Task)await disposed.Task;
            return;
        }
        await disposed.Task;
    }
}
