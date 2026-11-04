using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

public sealed class WindowsRecordingController : IAsyncDisposable
{
    public WindowsRecordingController(WindowsRecordingOptions options) { }
    internal WindowsRecordingController(WindowsRecordingOptions options,IWindowsRecordingPlatform platform) { }
    public Task<WindowsRecordingPreparation> PrepareAsync(RecordingTarget[] targets,string root,string takeId,CancellationToken cancellation=default)=>throw new NotImplementedException();
    public Task StartAsync(CancellationToken cancellation=default)=>throw new NotImplementedException();
    public Task<WindowsRecordingCompletion?> StopAsync(string? reason=null)=>throw new NotImplementedException();
    public Task<WindowsRecordingProgress> SnapshotAsync()=>throw new NotImplementedException();
    public Task<StoreRecovery?> RecoveryAsync()=>throw new NotImplementedException();
    public Task<RecordingResult> ProjectAsync()=>throw new NotImplementedException();
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
