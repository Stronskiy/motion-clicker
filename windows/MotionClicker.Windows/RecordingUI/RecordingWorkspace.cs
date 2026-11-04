using MotionClicker.Core.Recording;
namespace MotionClicker.Windows.RecordingUI;

public enum WorkspacePhase { Idle, Preparing, Countdown, Starting, Recording, Finalizing, Saving, Saved, Partial, Failed, Opening }
public interface IRecordingTake : IAsyncDisposable {
    Task PrepareAsync(RecordingTarget[] targets,string destination,CancellationToken cancellation);
    Task StartAsync(CancellationToken cancellation);
    Task<bool> StopAsync(string? reason);
    Task SaveAsync(string destination,CancellationToken cancellation);
    Task<string?> RecoveryAsync();
    Task<string> ProgressAsync();
}
public interface IRecordingWorkspaceServices {
    Task CreateDestinationAsync(string destination,CancellationToken cancellation);
    IRecordingTake CreateTake();
    Task CountdownAsync(int seconds,CancellationToken cancellation);
}
/// <summary>Native-free UI orchestration boundary; launch and construction are inert.</summary>
public sealed class RecordingWorkspace(IRecordingWorkspaceServices services) {
    public WorkspacePhase Phase { get; private set; }=WorkspacePhase.Idle;
    public string? Destination { get; private set; }
    public string? Message { get; private set; }
    public bool Busy=>Phase is WorkspacePhase.Preparing or WorkspacePhase.Countdown or WorkspacePhase.Starting or WorkspacePhase.Recording or WorkspacePhase.Finalizing or WorkspacePhase.Saving or WorkspacePhase.Opening;
    public Task RecordAsync(RecordingTarget[] targets,string destination,int countdownSeconds)=>Task.FromException(new NotImplementedException("Recording UI orchestration not implemented"));
    public Task StopAsync(string? reason=null)=>Task.CompletedTask;
}
