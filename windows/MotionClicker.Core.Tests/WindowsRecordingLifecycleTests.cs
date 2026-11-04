using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
using Xunit;

namespace MotionClicker.Core.Tests;

public sealed class WindowsRecordingLifecycleTests
{
    private sealed class Source : IWindowsPointerSource
    {
        internal readonly TaskCompletionSource ReadySignal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ClosedSignal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool AutoReady=true,AutoClose=true;
        internal int Starts,Stops;
        public Task Ready=>ReadySignal.Task;
        public Task Closed=>ClosedSignal.Task;
        public void Start(){Starts++;if(AutoReady)ReadySignal.TrySetResult();}
        public void RequestStop(){Stops++;if(AutoClose)ClosedSignal.TrySetResult();}
        public void Probe(long nowUs) { }
    }
    private sealed class Platform : IWindowsRecordingPlatform
    {
        internal long Now=1_000_000;
        internal bool ClockFails;
        public long NowUs=>ClockFails?throw new IOException("QPC unavailable"):Interlocked.Read(ref Now);
        internal readonly Source Video=new(),Hook=new();
        internal Action<WindowsVideoFrame>? Image;
        internal Action<WindowsPointerSample>? Sample;
        internal Action<string>? Fault;
        internal readonly Dictionary<string,Source> Sources=[];
        internal readonly Dictionary<string,Action<WindowsVideoFrame>> Images=[];
        internal bool EncoderFails,EncoderClosureUnknown;
        internal int Encoders,Finishes;
        public WindowsRecordingDisplay[] Validate(RecordingTarget[] targets)=>targets.Select(t=>new WindowsRecordingDisplay(t.Slot.Id,t.Display.Id,400,200)).ToArray();
        public IWindowsRecordingSource Capture(RecordingTarget target,Action<WindowsVideoFrame> image,Action<long,string> drop,Action<string> fault)
        {
            Image??=image;Fault=fault;Images.Add(target.Slot.Id,image);
            var source=Sources.Count==0?Video:new Source();Sources.Add(target.Slot.Id,source);return source;
        }
        public IWindowsPointerSource Pointer(Action<WindowsPointerSample> sample,Action<string> fault){Sample=sample;return Hook;}
        public IWindowsVideoEncoder Encoder(string path,int width,int height){Interlocked.Increment(ref Encoders);return new EncoderFake(this,path);}
        private sealed class EncoderFake(Platform platform,string path) : IWindowsVideoEncoder
        {
            private long count,end,zero;
            public void Append(byte[] bgra,long sourceTicks,long durationTicks){if(platform.EncoderFails)throw new IOException("encoder/disk failure");if(count==0)zero=sourceTicks;count++;end=sourceTicks-zero+durationTicks;File.WriteAllBytes(path,[1,2,3]);}
            public NativeWriterResult Finish(){Interlocked.Increment(ref platform.Finishes);return new(count>0,count,end,count>0?null:"No captured images",!platform.EncoderClosureUnknown);}
            public void Dispose() { }
        }
        internal void Frame(long hostUs)=>Image!(new(new byte[400*200*4],hostUs*10));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"mc-lifecycle-"+Guid.NewGuid());
        internal readonly Platform Platform=new();
        internal readonly WindowsRecordingController Controller;
        internal Fixture(Func<WindowsPointerSample,bool>? control=null){Directory.CreateDirectory(Root);Controller=new(new(99,control??(_=>false)),Platform);}
        internal Task<WindowsRecordingPreparation> Prepare()=>Controller.PrepareAsync([RecordingSessionTests.Targets()[0]],Root,"take");
        public async ValueTask DisposeAsync(){await Controller.DisposeAsync();Directory.Delete(Root,true);}
    }
    [Fact] public async Task PreparationIsInertAndStopBeforeEpochClosesEveryWriter()
    {
        await using var f=new Fixture();await f.Prepare();
        Assert.Equal(0,f.Platform.Video.Starts);Assert.Equal(0,f.Platform.Hook.Starts);
        Assert.Null(await f.Controller.StopAsync());Assert.Equal(1,f.Platform.Finishes);
    }
    [Fact] public async Task BarrierWaitsForEverySourceAndRealPostEpochImage()
    {
        await using var f=new Fixture();f.Platform.Hook.AutoReady=false;await f.Prepare();
        var start=f.Controller.StartAsync();await Task.Delay(30);Assert.False(start.IsCompleted);
        f.Platform.Hook.ReadySignal.TrySetResult();await WaitForState(f.Controller,RecordingState.Recording);
        f.Platform.Frame(999_999);await Task.Delay(20);Assert.False(start.IsCompleted);
        f.Platform.Frame(1_050_000);await start.WaitAsync(TimeSpan.FromSeconds(3));
        Interlocked.Exchange(ref f.Platform.Now,1_100_000);var done=await f.Controller.StopAsync();
        Assert.Equal(50_000,done!.Manifest.Streams[0].MediaTimeZeroUs);Assert.Equal(1,done.Manifest.Streams[0].FrameCount);
        Assert.Equal(RecordingState.Completed,done.Manifest.State);
    }
    [Fact] public async Task StopIsIdempotentAndRejectsStaleCallbacks()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        Interlocked.Exchange(ref f.Platform.Now,1_100_000);
        var first=await f.Controller.StopAsync();f.Platform.Frame(1_200_000);
        f.Platform.Sample!(new(1_200_000,3,new(-1,1),ActionKind.Move,null,null,null,0));
        Assert.Same(first,await f.Controller.StopAsync());Assert.Equal(1,f.Platform.Finishes);
        Assert.Equal(0,(await f.Controller.ProjectAsync()).Manifest.RawEventCount);
    }
    [Fact] public async Task SourceLossFinalizesAllWritersAndPreservesIncompleteHold()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        Interlocked.Exchange(ref f.Platform.Now,1_000_030);
        f.Platform.Sample!(new(1_000_020,77,new(-10,10),ActionKind.ButtonDown,MouseButton.Left,null,null,0));
        await f.Controller.SnapshotAsync();Interlocked.Exchange(ref f.Platform.Now,1_100_000);f.Platform.Fault!("session locked");
        var done=await f.Controller.StopAsync();Assert.Equal(RecordingState.Interrupted,done!.Manifest.State);
        Assert.Single(done.Manifest.IncompleteGroupIds);Assert.Empty((await f.Controller.ProjectAsync()).Events);
        Assert.Equal(1,f.Platform.Finishes);
    }
    [Fact] public async Task MissingPostEpochImageTimesOutWithoutInventingFrame()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(999_999);
        Interlocked.Exchange(ref f.Platform.Now,12_000_000);
        await Assert.ThrowsAsync<RecordingException>(()=>start.WaitAsync(TimeSpan.FromSeconds(3)));
        var done=await f.Controller.StopAsync();Assert.Equal(0,done!.Manifest.Streams[0].FrameCount);
        Assert.Equal(RecordingState.Interrupted,done.Manifest.State);
    }
    [Fact] public async Task StopDrainsAlreadyObservedImageBeforeNativeClosure()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        f.Platform.Video.AutoClose=false;Interlocked.Exchange(ref f.Platform.Now,1_100_000);
        var stop=f.Controller.StopAsync();f.Platform.Frame(1_050_000);await f.Controller.SnapshotAsync();
        Assert.False(stop.IsCompleted);f.Platform.Video.ClosedSignal.TrySetResult();
        Assert.Equal(2,(await stop)!.Manifest.Streams[0].FrameCount);
    }
    [Fact] public async Task UnknownClosureQuarantinesStagingInsteadOfPublishing()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        f.Platform.Video.AutoClose=false;Interlocked.Exchange(ref f.Platform.Now,1_100_000);
        var stop=f.Controller.StopAsync();f.Platform.Video.ClosedSignal.TrySetException(new IOException("unhook/closure unknown"));
        await Assert.ThrowsAsync<RecordingException>(()=>stop);
        var recovery=(await f.Controller.RecoveryAsync())!;
        Assert.False(recovery.MediaPublished);Assert.False(recovery.ManifestPublished);Assert.True(Directory.Exists(recovery.StagingPath));
    }
    [Fact] public async Task PointerOverflowRequestsNativeStopWhileOwnerIsBlocked()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using var f=new Fixture(_=>{entered.Set();release.Wait(TimeSpan.FromSeconds(5));return false;});
        await f.Prepare();var start=f.Controller.StartAsync();await WaitForState(f.Controller,RecordingState.Recording);
        f.Platform.Frame(1_000_010);await start;Interlocked.Exchange(ref f.Platform.Now,1_000_100);
        var packet=new WindowsPointerSample(1_000_020,3,new(-10,10),ActionKind.Move,null,null,null,0);
        f.Platform.Sample!(packet);Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            for(int i=0;i<1025;i++)f.Platform.Sample(packet);
            Assert.True(SpinWait.SpinUntil(()=>Volatile.Read(ref f.Platform.Video.Stops)>0,TimeSpan.FromSeconds(2)));
        }
        finally{release.Set();}
        var done=await f.Controller.StopAsync();Assert.Equal(RecordingState.Interrupted,done!.Manifest.State);
        Assert.Contains("Pointer mailbox overflow",done.Manifest.Reason);
    }
    [Fact] public async Task UnexpectedNativeThreadExitInterruptsWholeTake()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        Interlocked.Exchange(ref f.Platform.Now,1_100_000);f.Platform.Hook.ClosedSignal.TrySetResult();
        Assert.True(SpinWait.SpinUntil(()=>Volatile.Read(ref f.Platform.Video.Stops)>0,TimeSpan.FromSeconds(2)));
        Assert.Equal(RecordingState.Interrupted,(await f.Controller.StopAsync())!.Manifest.State);
    }
    [Fact] public async Task SourceFailureFinalizesEverySelectedWriter()
    {
        await using var f=new Fixture();await f.Controller.PrepareAsync(RecordingSessionTests.Targets(),f.Root,"take");
        var start=f.Controller.StartAsync();await WaitForState(f.Controller,RecordingState.Recording);
        f.Platform.Frame(1_000_010);f.Platform.Images["right"](new(new byte[400*200*4],10_000_200));await start;
        Interlocked.Exchange(ref f.Platform.Now,1_100_000);f.Platform.Fault!("display topology changed");
        var done=await f.Controller.StopAsync();Assert.Equal(2,done!.Manifest.Streams.Length);Assert.Equal(2,f.Platform.Finishes);
        Assert.All(f.Platform.Sources.Values,s=>Assert.Equal(1,s.Stops));Assert.Equal(1,f.Platform.Hook.Stops);
        Assert.Equal(RecordingState.Interrupted,done.Manifest.State);
    }
    [Fact] public async Task EncoderFailureDuringStopCannotBecomeComplete()
    {
        await using var f=new Fixture();f.Platform.EncoderFails=true;await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        Interlocked.Exchange(ref f.Platform.Now,1_100_000);var done=await f.Controller.StopAsync();
        Assert.Equal(RecordingState.Interrupted,done!.Manifest.State);Assert.Equal(0,done.Manifest.Streams[0].FrameCount);
        Assert.Equal(WriterStatus.Failed,done.Manifest.Streams[0].WriterStatus);Assert.Equal(1,f.Platform.Finishes);
    }
    [Fact] public async Task HookInstallFailureHasNoEpochAndLeavesRecoverableStaging()
    {
        await using var f=new Fixture();f.Platform.Hook.AutoReady=false;f.Platform.Hook.ReadySignal.TrySetException(new IOException("hook installation failed"));
        await f.Prepare();await Assert.ThrowsAsync<RecordingException>(()=>f.Controller.StartAsync());
        Assert.Null(await f.Controller.StopAsync());Assert.Equal(1,f.Platform.Finishes);
        Assert.False((await f.Controller.RecoveryAsync())!.ManifestPublished);
    }
    [Fact] public async Task PreparationCancellationNeverStartsSourcesOrOverwritesPriorSave()
    {
        await using var f=new Fixture();var old=Path.Combine(f.Root,"prior.json");File.WriteAllText(old,"prior save");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Controller.PrepareAsync([RecordingSessionTests.Targets()[0]],f.Root,"take",cancel.Token));
        Assert.Null(await f.Controller.StopAsync());Assert.Equal("prior save",File.ReadAllText(old));
        Assert.Equal(0,f.Platform.Video.Starts);Assert.Equal(0,f.Platform.Hook.Starts);
    }
    [Fact] public async Task UnknownEncoderClosureCannotPublishEvenAfterSuccessfulAppend()
    {
        await using var f=new Fixture();f.Platform.EncoderClosureUnknown=true;await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        Interlocked.Exchange(ref f.Platform.Now,1_100_000);
        await Assert.ThrowsAsync<RecordingException>(()=>f.Controller.StopAsync());
        var recovery=(await f.Controller.RecoveryAsync())!;
        Assert.False(recovery.MediaPublished);Assert.False(recovery.ManifestPublished);Assert.True(Directory.Exists(recovery.StagingPath));
    }
    [Fact] public async Task NativeProducerClosureHasBoundedWaitAndDoesNotDisposeLiveWork()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        f.Platform.Video.AutoClose=false;Interlocked.Exchange(ref f.Platform.Now,1_100_000);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<RecordingException>(()=>f.Controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(13)));
        Assert.InRange(elapsed.Elapsed.TotalSeconds,9,13);Assert.False(f.Platform.Video.Closed.IsCompleted);
        Assert.False((await f.Controller.RecoveryAsync())!.ManifestPublished);
        f.Platform.Video.ClosedSignal.TrySetResult();
    }
    [Fact] public async Task ShutdownDeadlineDoesNotDependOnOwnerProgress()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using var f=new Fixture(_=>{entered.Set();release.Wait(TimeSpan.FromSeconds(20));return false;});
        await f.Prepare();var start=f.Controller.StartAsync();await WaitForState(f.Controller,RecordingState.Recording);
        f.Platform.Frame(1_000_010);await start;Interlocked.Exchange(ref f.Platform.Now,1_000_100);
        f.Platform.Sample!(new(1_000_020,3,new(-10,10),ActionKind.Move,null,null,null,0));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Assert.ThrowsAsync<RecordingException>(()=>f.Controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(13)));
            var recovery=(await f.Controller.RecoveryAsync().WaitAsync(TimeSpan.FromSeconds(1)))!;
            Assert.False(recovery.ManifestPublished);Assert.True(Directory.Exists(recovery.StagingPath));
        }
        finally{release.Set();}
    }
    [Fact] public async Task StopClockFailureStillStopsNativeSourcesAndPreservesStaging()
    {
        await using var f=new Fixture();await f.Prepare();var start=f.Controller.StartAsync();
        await WaitForState(f.Controller,RecordingState.Recording);f.Platform.Frame(1_000_010);await start;
        f.Platform.ClockFails=true;
        try
        {
            await Assert.ThrowsAsync<RecordingException>(()=>f.Controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1,f.Platform.Video.Stops);Assert.Equal(1,f.Platform.Hook.Stops);
            Assert.False((await f.Controller.RecoveryAsync())!.ManifestPublished);
        }
        finally{f.Platform.ClockFails=false;}
    }
    private static async Task WaitForState(WindowsRecordingController controller,RecordingState state)
    {
        var timeout=DateTime.UtcNow.AddSeconds(3);
        while((await controller.SnapshotAsync()).State!=state){if(DateTime.UtcNow>timeout)throw new TimeoutException();await Task.Delay(5);}
    }
}
