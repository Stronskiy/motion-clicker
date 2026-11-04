using MotionClicker.Core.Recording;
namespace MotionClicker.Windows.RecordingUI;

public enum WorkspacePhase { Idle, Preparing, Countdown, Starting, Recording, Finalizing, Saving, Saved, Partial, Failed, Opening, CleanupBlocked }
public sealed record RecordingTakeStatus(bool Completed);
public interface IRecordingTake : IAsyncDisposable {
    Task PrepareAsync(RecordingTarget[] targets,string destination,CancellationToken cancellation);
    Task StartAsync(CancellationToken cancellation);
    Task<RecordingTakeStatus?> StopAsync(string? reason);
    Task SaveAsync(string destination,CancellationToken cancellation);
    Task<string?> RecoveryAsync();
    Task<string> ProgressAsync();
}
public interface IRecordingWorkspaceServices {
    Task CreateDestinationAsync(string destination,CancellationToken cancellation);
    IRecordingTake CreateTake();
    Task CountdownAsync(int seconds,CancellationToken cancellation);
}
public sealed record RecordingWorkspaceSnapshot(WorkspacePhase Phase,string? Destination,string? Message,string? Recovery);
/// <summary>Native-free UI orchestration. All native closure precedes disposal and gate release.</summary>
public sealed class RecordingWorkspace(IRecordingWorkspaceServices services,AppOperationGate? operationGate=null) {
    private readonly object sync=new();
    private readonly AppOperationGate operations=operationGate??new();
    private RecordingWorkspaceSnapshot snapshot=new(WorkspacePhase.Idle,null,null,null);
    private IDisposable? lease;
    private CancellationTokenSource? cancellation;
    private IRecordingTake? take;
    private Task? beginning,stopping,retrying;
    private bool saveRetryEligible,retryCompleted;
    private int polling;
    public RecordingWorkspaceSnapshot Snapshot=>Volatile.Read(ref snapshot);
    public WorkspacePhase Phase=>Snapshot.Phase;
    public string? Destination=>Snapshot.Destination;
    public string? Message=>Snapshot.Message;
    public string? Recovery=>Snapshot.Recovery;
    public bool CanRetrySave=>Volatile.Read(ref saveRetryEligible)&&Phase==WorkspacePhase.Failed;
    public bool Busy=>Phase is WorkspacePhase.Preparing or WorkspacePhase.Countdown or WorkspacePhase.Starting or WorkspacePhase.Recording or WorkspacePhase.Finalizing or WorkspacePhase.Saving or WorkspacePhase.Opening or WorkspacePhase.CleanupBlocked;
    private void Set(WorkspacePhase phase,string? message=null,string? recovery=null)=>Volatile.Write(ref snapshot,new(phase,Destination,message,recovery??Recovery));
    public Task RecordAsync(RecordingTarget[] targets,string destination,int countdownSeconds) {
        lock(sync) {
            if(targets.Length==0||targets.Select(t=>t.Slot.Id).Distinct(StringComparer.Ordinal).Count()!=targets.Length||string.IsNullOrWhiteSpace(destination)||Path.GetExtension(destination)!=".motionclicker"||countdownSeconds<0||countdownSeconds>60)
                throw new RecordingException("Choose displays, a new .motionclicker destination and a countdown from0 to60seconds");
            if(Busy||lease is not null)throw new RecordingException("Stop the current operation first");
            lease=operations.TryEnter(AppOperation.Recording)??throw new RecordingException("Stop execution or inspection first");
            cancellation=new();take=null;stopping=null;retrying=null;saveRetryEligible=false;retryCompleted=false;snapshot=new(WorkspacePhase.Preparing,destination,null,null);
            beginning=BeginAsync(targets.ToArray(),destination,countdownSeconds,cancellation.Token);
            return ObserveBeginAsync(beginning);
        }
    }
    private async Task BeginAsync(RecordingTarget[] targets,string destination,int countdown,CancellationToken token) {
        await Task.Yield();token.ThrowIfCancellationRequested();
        await services.CreateDestinationAsync(destination,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        take=services.CreateTake();await take.PrepareAsync(targets,destination,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        Set(WorkspacePhase.Countdown,$"Starting in {countdown}s · Cancel stops before capture");
        await services.CountdownAsync(countdown,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        Set(WorkspacePhase.Starting,"Waiting for every selected display's first real image…");
        await take.StartAsync(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();Set(WorkspacePhase.Recording,"Recording · Stop finalizes original video and actions");
    }
    private async Task ObserveBeginAsync(Task begin) {
        try { await begin.ConfigureAwait(false); }
        catch(Exception error) { await StopAsync(error is OperationCanceledException?"Canceled":error.Message).ConfigureAwait(false); }
    }
    public Task StopAsync(string? reason=null) {
        lock(sync) {
            if(retrying is not null&&!retrying.IsCompleted)return retrying;
            if(stopping is not null)return stopping;
            if(beginning is null)return Task.CompletedTask;
            cancellation?.Cancel();stopping=FinishAsync(beginning,reason);return stopping;
        }
    }
    private async Task FinishAsync(Task begin,string? reason) {
        await Task.Yield();
        string? failure=null;var safeToRelease=false;
        try {
            try { await begin.ConfigureAwait(false); } catch(Exception error) { if(error is not OperationCanceledException)failure=error.Message;reason??=failure??"Canceled"; }
            Set(WorkspacePhase.Finalizing,failure??reason??"Finalizing native video…");
            RecordingTakeStatus? result=null;
            if(take is not null) {
                try { result=await take.StopAsync(reason).ConfigureAwait(false); }
                catch(Exception error) { Set(WorkspacePhase.CleanupBlocked,"Native closure is not acknowledged. Retry Stop; keep this window open. "+error.Message,await RecoveryOfTake());return; }
            }
            safeToRelease=true;
            if(result is null)Set(failure is null?WorkspacePhase.Idle:WorkspacePhase.Failed,failure??reason,await RecoveryOfTake());
            else {
                retryCompleted=result.Completed&&reason is null;
                Set(WorkspacePhase.Saving,"Native capture stopped · committing project.json…");
                try { await take!.SaveAsync(Destination!,CancellationToken.None).ConfigureAwait(false);Set(result.Completed&&reason is null?WorkspacePhase.Saved:WorkspacePhase.Partial,failure??reason??"Saved · open to inspect original source video"); }
                catch(Exception error) { saveRetryEligible=true;Set(WorkspacePhase.Failed,"Save failed: "+error.Message,await RecoveryOfTake()); }
            }
        } finally {
            if(safeToRelease) {
                if(take is not null)try{await take.DisposeAsync().ConfigureAwait(false);}catch(Exception error){Set(WorkspacePhase.Failed,"Recorder disposal failed: "+error.Message,await RecoveryOfTake());}
                lock(sync){lease?.Dispose();lease=null;cancellation?.Dispose();cancellation=null;}
            }
        }
    }
    private async Task<string?> RecoveryOfTake(){try{return take is null?null:await take.RecoveryAsync().ConfigureAwait(false);}catch(Exception error){return "Recovery lookup failed: "+error.Message;}}
    public async Task<string?> PollProgressAsync() {
        if(Phase!=WorkspacePhase.Recording||take is null||Interlocked.Exchange(ref polling,1)!=0)return null;
        var current=take;
        try {
            var progress=await current.ProgressAsync().ConfigureAwait(false);
            if(ReferenceEquals(current,take)&&Phase==WorkspacePhase.Recording)return progress;return null;
        } catch(Exception error) { if(ReferenceEquals(current,take)&&Phase==WorkspacePhase.Recording)await StopAsync("Source progress failed: "+error.Message).ConfigureAwait(false);return null; }
        finally{Volatile.Write(ref polling,0);}
    }
    public Task RetrySaveAsync() {
        lock(sync) {
            if(!CanRetrySave||take is null||lease is not null)throw new RecordingException("No stopped initial save is available to retry");
            lease=operations.TryEnter(AppOperation.Recording)??throw new RecordingException("Finish the current app operation first");
            Set(WorkspacePhase.Saving,"Retrying initial project commit · source bytes stay unchanged…");
            return retrying=RetrySaveCoreAsync();
        }
    }
    private async Task RetrySaveCoreAsync() {
        await Task.Yield();
        try {await take!.SaveAsync(Destination!,CancellationToken.None).ConfigureAwait(false);saveRetryEligible=false;Set(retryCompleted?WorkspacePhase.Saved:WorkspacePhase.Partial,"Saved · open the original source to inspect");}
        catch(Exception error){Set(WorkspacePhase.Failed,"Save failed: "+error.Message,await RecoveryOfTake());}
        finally {lock(sync){lease?.Dispose();lease=null;}}
    }
    public async Task RetryStopAsync() {
        lock(sync){if(Phase!=WorkspacePhase.CleanupBlocked)return;stopping=null;}
        await StopAsync("Retry native closure").ConfigureAwait(false);
    }
}
