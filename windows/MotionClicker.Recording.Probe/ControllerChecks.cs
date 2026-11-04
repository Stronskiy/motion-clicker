using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;

// Actual serial owner + bounded workers + native MF + publication, generated pixels only.
static class ControllerChecks
{
    private sealed class Source : IWindowsPointerSource
    {
        private readonly TaskCompletionSource closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready=>Task.CompletedTask;
        public Task Closed=>closed.Task;
        public void Start() { }
        public void RequestStop()=>closed.TrySetResult();
        public void Probe(long nowUs) { }
    }
    private sealed class Platform : IWindowsRecordingPlatform
    {
        internal long Now=1_000_000;
        internal Action<WindowsVideoFrame> Image=null!;
        public long NowUs=>Interlocked.Read(ref Now);
        public WindowsRecordingDisplay[] Validate(RecordingTarget[] targets)=>[new("screen","generated-only",64,64)];
        public IWindowsRecordingSource Capture(RecordingTarget target,Action<WindowsVideoFrame> image,Action<long,string> drop,Action<string> fault){Image=image;return new Source();}
        public IWindowsPointerSource Pointer(Action<WindowsPointerSample> sample,Action<string> fault)=>new Source();
        public IWindowsVideoEncoder Encoder(string path,int width,int height)=>new NativeVideoWriter(path,width,height);
    }
    internal static async Task Run(string directory,byte[] pixels)
    {
        var root=Path.Combine(directory,"controller");Directory.CreateDirectory(root);var platform=new Platform();
        var geometry=new DisplayGeometry(new(0,0),new(64,64),1,0);
        var target=new RecordingTarget(new("screen","Generated",geometry),new("generated-only","Generated",geometry,new(new(0,0),new(64,64))));
        await using var recorder=new WindowsRecordingController(new(99,_=>false),platform);
        await recorder.PrepareAsync([target],root,"synthetic");var start=recorder.StartAsync();
        await Until(recorder,p=>p.State==RecordingState.Recording);
        platform.Image(new(pixels,11_000_000));await start;
        platform.Image(new(pixels,12_000_000));await Until(recorder,p=>p.AcceptedFrames==1);
        platform.Image(new(pixels,13_000_000));await Until(recorder,p=>p.AcceptedFrames==2);
        Interlocked.Exchange(ref platform.Now,1_400_000);
        var completion=(await recorder.StopAsync())!;
        if(completion.Manifest.State!=RecordingState.Completed||completion.Manifest.Streams[0].FrameCount!=3||completion.Manifest.Streams[0].MediaTimeZeroUs!=100_000)
            throw new Exception("Controller did not publish truthful late-offset metadata");
        if(await recorder.StopAsync()!=completion)throw new Exception("Controller stop not idempotent");
        var path=Path.Combine(completion.Recovery.MediaPath,"screen.mp4");
        using(var reopened=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            if(reopened.Length==0)throw new Exception("Controller published an empty file");
        Probe.Read(path,[0,1_000_000,2_000_000],[1_000_000,1_000_000,1_000_000]);
        if(!File.Exists(completion.ManifestPath)||(await recorder.ProjectAsync()).Manifest.RawEventCount!=0)
            throw new Exception("Controller publication/projection failed");
        Console.WriteLine("PASS generated controller -> owned encoder worker -> MF closure -> published manifest/readback (no native producers)");
    }
    private static async Task Until(WindowsRecordingController controller,Func<WindowsRecordingProgress,bool> predicate)
    {
        var end=Environment.TickCount64+10_000;
        while(!predicate(await controller.SnapshotAsync())){if(Environment.TickCount64>end)throw new TimeoutException("Controller synthetic progress");await Task.Delay(10);}
    }
}
