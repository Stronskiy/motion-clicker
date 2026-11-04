using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
namespace MotionClicker.Windows.RecordingUI;

internal sealed class NativeRecordingServices(ControlRegions regions) : IRecordingWorkspaceServices {
    public RecordedBundleSnapshot? SavedSnapshot { get; private set; }
    public Task CreateDestinationAsync(string destination,CancellationToken cancellation)=>Task.Run(()=>{cancellation.ThrowIfCancellationRequested();RecordedBundleStore.Create(destination);SavedSnapshot=null;},cancellation);
    public IRecordingTake CreateTake()=>new NativeTake(this,regions);
    public Task CountdownAsync(int seconds,CancellationToken cancellation)=>Task.Delay(TimeSpan.FromSeconds(seconds),cancellation);
    internal static T WithScratch<T>(Func<string,T> action) {
        // A unique caller-owned external directory; the store itself owns its validation child.
        var root=Path.Combine(Path.GetTempPath(),"motionclicker-validation-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try{return action(root);}finally{Directory.Delete(root,true);}
    }
    private sealed class NativeTake(NativeRecordingServices owner,ControlRegions regions) : IRecordingTake {
        private readonly WindowsRecordingController controller=new(new(unchecked((nuint)0x4d434c49434bUL),sample=>regions.Contains(sample.NativePosition)));
        private RecordingResult? result;
        public async Task PrepareAsync(RecordingTarget[] targets,string destination,CancellationToken cancellation)=>_ = await controller.PrepareAsync(targets,destination,"take-"+Guid.NewGuid().ToString("N"),cancellation).ConfigureAwait(false);
        public Task StartAsync(CancellationToken cancellation)=>controller.StartAsync(cancellation);
        public async Task<RecordingTakeStatus?> StopAsync(string? reason) {
            var completion=await controller.StopAsync(reason).ConfigureAwait(false);
            return completion is null?null:new(completion.Manifest.State==RecordingState.Completed);
        }
        public async Task SaveAsync(string destination,CancellationToken cancellation) {
            result??=await controller.ProjectAsync().ConfigureAwait(false);
            owner.SavedSnapshot=await Task.Run(()=>WithScratch(scratch=>RecordedBundleStore.SaveInitial(destination,result,scratch)),cancellation).ConfigureAwait(false);
        }
        public async Task<string?> RecoveryAsync() {var recovery=await controller.RecoveryAsync().ConfigureAwait(false);return recovery is null?null:$"Staging: {recovery.StagingPath}\nMedia: {recovery.MediaPath}\nManifest: {recovery.ManifestPath}";}
        public async Task<string> ProgressAsync() {
            var progress=await controller.SnapshotAsync().ConfigureAwait(false);
            if(progress.State!=RecordingState.Recording)throw new RecordingException(progress.Reason??"Native recording ended; finalize the interrupted source");
            return $"{progress.AcceptedFrames} accepted frames · {progress.DroppedFrames} dropped · {progress.Reason}";
        }
        public ValueTask DisposeAsync()=>controller.DisposeAsync();
    }
}
