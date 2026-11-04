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
    internal static RecordingTarget Target { get; }=new(new("slot","Display",new(new(0,0),new(64,64),1,0)),new("native","Display",new(new(0,0),new(64,64),1,0),new(new(0,0),new(64,64))));
    private sealed class FakeServices : IRecordingWorkspaceServices {
        public List<string> Calls { get; }=[];
        public Task CreateDestinationAsync(string destination,CancellationToken cancellation){Calls.Add("create:"+destination);return Task.CompletedTask;}
        public IRecordingTake CreateTake()=>new FakeTake(Calls);
        public Task CountdownAsync(int seconds,CancellationToken cancellation){Calls.Add("countdown:"+seconds);return Task.CompletedTask;}
    }
    private sealed class FakeTake(List<string> calls) : IRecordingTake {
        public Task PrepareAsync(RecordingTarget[] targets,string destination,CancellationToken cancellation){Assert.Single(targets);calls.Add("prepare");return Task.CompletedTask;}
        public Task StartAsync(CancellationToken cancellation){calls.Add("start");return Task.CompletedTask;}
        public Task<bool> StopAsync(string? reason){calls.Add("stop:"+(reason??"complete"));return Task.FromResult(true);}
        public Task SaveAsync(string destination,CancellationToken cancellation){calls.Add("save");return Task.CompletedTask;}
        public Task<string?> RecoveryAsync()=>Task.FromResult<string?>(null);
        public Task<string> ProgressAsync()=>Task.FromResult("native progress");
        public ValueTask DisposeAsync(){calls.Add("dispose");return ValueTask.CompletedTask;}
    }
}
