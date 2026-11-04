using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Core.Runtime;
using MotionClicker.Windows.RecordingUI;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class RecordingWorkspaceTests {
    // A regression that starts native capture during preparation or skips owned destination creation fails this order.
    [Fact] public async Task DeliberateRecordOwnsDestinationThenPreparesCountsDownAndStarts() {
        var services=new FakeServices();var model=new RecordingWorkspace(services);
        Assert.Empty(services.Calls);Assert.Equal(WorkspacePhase.Idle,model.Phase);
        await model.RecordAsync([Target],"new.motionclicker",3);
        Assert.Equal(new[]{"create:new.motionclicker","prepare","countdown:3","start"},services.Calls);
        Assert.Equal(WorkspacePhase.Recording,model.Phase);Assert.True(model.Busy);
        await model.StopAsync();Assert.Equal(WorkspacePhase.Saved,model.Phase);Assert.False(model.Busy);
        Assert.Equal(new[]{"create:new.motionclicker","prepare","countdown:3","start","stop:complete","save","dispose"},services.Calls);
    }
    [Fact] public async Task CountdownCancellationNeverStartsNativeSourcesAndDuplicateStopClosesOnce() {
        var services=new FakeServices { Countdown=new(TaskCreationOptions.RunContinuationsAsynchronously) };var model=new RecordingWorkspace(services);
        var beginning=model.RecordAsync([Target],"cancel.motionclicker",3);
        await Until(()=>model.Phase==WorkspacePhase.Countdown);
        var first=model.StopAsync("Canceled");var second=model.StopAsync("Canceled");await Task.WhenAll(beginning,first,second);
        Assert.DoesNotContain("start",services.Calls);Assert.Equal(1,services.Calls.Count(c=>c.StartsWith("stop:")));Assert.DoesNotContain("save",services.Calls);Assert.False(model.Busy);
    }
    [Fact] public async Task CloseDuringPreparingWaitsForWorkerAcknowledgmentAndKeepsGate() {
        var services=new FakeServices { Preparation=new(TaskCreationOptions.RunContinuationsAsynchronously) };var gate=new AppOperationGate();var model=new RecordingWorkspace(services,gate);
        var beginning=model.RecordAsync([Target],"close.motionclicker",0);await Until(()=>services.Calls.Contains("prepare"));
        var closing=model.StopAsync("Application closing");Assert.False(closing.IsCompleted);Assert.True(gate.Busy);Assert.DoesNotContain("dispose",services.Calls);
        services.Preparation.SetResult();await Task.WhenAll(beginning,closing);Assert.False(gate.Busy);Assert.DoesNotContain("start",services.Calls);
    }
    [Fact] public async Task SelectionDestinationAndExecutionGateBlockBeforeCreatingCapture() {
        var services=new FakeServices();var gate=new AppOperationGate();var model=new RecordingWorkspace(services,gate);
        await Assert.ThrowsAnyAsync<Exception>(()=>model.RecordAsync([],"empty.motionclicker",0));Assert.Empty(services.Calls);
        using(var paused=gate.TryEnter(AppOperation.Execution)) { await Assert.ThrowsAnyAsync<Exception>(()=>model.RecordAsync([Target],"blocked.motionclicker",0));Assert.Empty(services.Calls); }
        services.DestinationFailure=true;await model.RecordAsync([Target],"unowned.motionclicker",0);Assert.Equal(WorkspacePhase.Failed,model.Phase);Assert.DoesNotContain("prepare",services.Calls);
    }
    [Fact] public async Task SaveFailureRetainsRecoveryAndCannotAnnounceSaved() {
        var services=new FakeServices { SaveFailure=true };var gate=new AppOperationGate();var model=new RecordingWorkspace(services,gate);
        await model.RecordAsync([Target],"disk.motionclicker",0);await model.StopAsync();
        Assert.Equal(WorkspacePhase.Failed,model.Phase);Assert.Contains("disk",model.Message);Assert.Equal("recoverable staging",model.Recovery);Assert.False(gate.Busy);
    }
    [Fact] public async Task SourceStartFailureIsInterruptedAndPartialResultCannotBecomeSavedComplete() {
        var services=new FakeServices { StartFailure=true,Completed=false };var model=new RecordingWorkspace(services);
        await model.RecordAsync([Target],"source.motionclicker",0);Assert.Equal(WorkspacePhase.Partial,model.Phase);Assert.Contains("source",model.Message);Assert.Contains("save",services.Calls);
    }
    private static async Task Until(Func<bool> condition) { using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(1,deadline.Token); }
    internal static RecordingTarget Target { get; }=new(new("slot","Display",new(new(0,0),new(64,64),1,0)),new("native","Display",new(new(0,0),new(64,64),1,0),new(new(0,0),new(64,64))));
    private sealed class FakeServices : IRecordingWorkspaceServices {
        public List<string> Calls { get; }=[];
        public TaskCompletionSource? Preparation,Countdown;
        public bool DestinationFailure,StartFailure,SaveFailure;public bool Completed=true;
        public Task CreateDestinationAsync(string destination,CancellationToken cancellation){Calls.Add("create:"+destination);if(DestinationFailure)throw new IOException("destination not owned");return Task.CompletedTask;}
        public IRecordingTake CreateTake()=>new FakeTake(this);
        public async Task CountdownAsync(int seconds,CancellationToken cancellation){Calls.Add("countdown:"+seconds);if(Countdown is not null)await Countdown.Task.WaitAsync(cancellation);}
    }
    private sealed class FakeTake(FakeServices services) : IRecordingTake {
        private List<string> calls=>services.Calls;
        public async Task PrepareAsync(RecordingTarget[] targets,string destination,CancellationToken cancellation){Assert.Single(targets);calls.Add("prepare");if(services.Preparation is not null)await services.Preparation.Task;}
        public Task StartAsync(CancellationToken cancellation){calls.Add("start");if(services.StartFailure)throw new IOException("source failure");return Task.CompletedTask;}
        public Task<RecordingTakeStatus?> StopAsync(string? reason){calls.Add("stop:"+(reason??"complete"));return Task.FromResult<RecordingTakeStatus?>(calls.Contains("start")?new(services.Completed):null);}
        public Task SaveAsync(string destination,CancellationToken cancellation){calls.Add("save");if(services.SaveFailure)throw new IOException("disk full");return Task.CompletedTask;}
        public Task<string?> RecoveryAsync()=>Task.FromResult<string?>("recoverable staging");
        public Task<string> ProgressAsync()=>Task.FromResult("native progress");
        public ValueTask DisposeAsync(){calls.Add("dispose");return ValueTask.CompletedTask;}
    }
}
