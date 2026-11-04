using MotionClicker.Core;
using MotionClicker.Core.Runtime;
using Xunit;
using MotionClicker.Windows;
namespace MotionClicker.Core.Tests;
public sealed class WorkspaceSafetyTests {
    [Fact] public void FailedRunMustPublishUnresolvedOwnedButtonUntilReleaseAcknowledged() {
        var backend=new FailingBackend();var runner=new SafeRunner(new Clock(),new Monitors(),backend);
        runner.Start(new([new(0,"slot",new(1,1),ActionKind.ButtonDown,MouseButton.Left),new(1000,"slot",new(1,1),ActionKind.ButtonUp,MouseButton.Left)],[new("slot","Display",Geometry)]),new Dictionary<string,string>{{"slot","display"}},hotkeyReady:true);
        runner.Pump();runner.Stop();var snapshot=runner.Snapshot;
        Assert.Equal(RunnerState.Failed,snapshot.State);
        var ownership=typeof(RunnerSnapshot).GetProperty("OwnedButton");
        Assert.NotNull(ownership);Assert.Equal(MouseButton.Left,ownership.GetValue(snapshot));
        backend.FailRelease=false;runner.Stop();Assert.Null(ownership.GetValue(runner.Snapshot));Assert.Equal(2,backend.ReleaseAttempts);
    }
    [Fact] public async Task WorkerPublishesFailedOwnershipAndAcknowledgesOnlySuccessfulRetry() {
        var backend=new FailingBackend();using var worker=new RunWorker(new(new Clock(),new Monitors(),backend));
        await worker.StartAsync(new([new(0,"slot",new(1,1),ActionKind.ButtonDown,MouseButton.Left),new(1000,"slot",new(1,1),ActionKind.ButtonUp,MouseButton.Left)],[new("slot","Display",Geometry)]),new Dictionary<string,string>{{"slot","display"}},0,()=>{});
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(worker.Snapshot.Dispatched==0)await Task.Delay(1,deadline.Token);
        await worker.StopAsync();Assert.Equal(MouseButton.Left,worker.Snapshot.OwnedButton);Assert.Equal(RunnerState.Failed,worker.Snapshot.State);
        backend.FailRelease=false;await worker.StopAsync();Assert.Null(worker.Snapshot.OwnedButton);Assert.Equal(2,backend.ReleaseAttempts);
    }
    [Fact] public async Task UnexpectedWorkerReadinessFailureStillReleasesOwnedInput() {
        var backend=new FailingBackend { FailRelease=false };using var worker=new RunWorker(new(new Clock(),new Monitors(),backend));
        await worker.StartAsync(new([new(0,"slot",new(1,1),ActionKind.ButtonDown,MouseButton.Left),new(1000,"slot",new(1,1),ActionKind.ButtonUp,MouseButton.Left)],[new("slot","Display",Geometry)]),new Dictionary<string,string>{{"slot","display"}},0,()=>{});
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(worker.Snapshot.Dispatched==0)await Task.Delay(1,deadline.Token);
        backend.ThrowReadiness=true;while(worker.Snapshot.State==RunnerState.Running)await Task.Delay(1,deadline.Token);
        Assert.Null(worker.Snapshot.OwnedButton);Assert.Contains("Worker failed",worker.Snapshot.Reason);Assert.Equal(1,backend.ReleaseAttempts);
        await worker.StopAsync();Assert.Equal(1,backend.ReleaseAttempts);
    }
    private static readonly DisplayGeometry Geometry=new(new(0,0),new(64,64),1,0);
    private sealed class Clock : IMonotonicClock { public long NowUs=>0; }
    private sealed class Monitors : IMonitorProvider { public IReadOnlyList<RuntimeDisplay> Displays()=>[new("display","Display",Geometry,new(new(0,0),new(64,64)))]; }
    private sealed class FailingBackend : IInputBackend {
        public volatile bool ThrowReadiness;public bool Ready=>ThrowReadiness?throw new IOException("Readiness failed"):true;public bool PhysicalButtonsDown=>false;public ulong Interference=>0;
        public bool FailRelease=true;public int ReleaseAttempts;
        public Point Position()=>new(1,1);public void Send(RuntimeStep step,Point point) { }
        public void Release(MouseButton button,Point point) { ReleaseAttempts++;if(FailRelease)throw new InvalidOperationException("owned release unavailable"); }
    }
}
