using MotionClicker.Core;
using MotionClicker.Core.Runtime;
using Xunit;

namespace MotionClicker.Core.Tests;

public sealed class RunnerTests {
    private sealed class Clock : IMonotonicClock { public long NowUs { get; set; } }
    private sealed class Monitors : IMonitorProvider {
        public List<RuntimeDisplay> Values = [new("display","Synthetic", new(new(-100,0),new(100,100),2,0),new(new(-200,0),new(200,200)))];
        public IReadOnlyList<RuntimeDisplay> Displays()=>Values.ToArray();
    }
    private sealed class Backend : IInputBackend {
        public bool PhysicalButtonsDown { get; set; }
        public bool Ready { get; set; }=true;
        public ulong Interference { get; set; }
        public List<string> Calls { get; }=[];
        public List<Point> Points { get; }=[];
        public bool FailSend { get; set; }
        public bool FailRelease { get; set; }
        public Point Position()=>new(-150,50);
        public void Send(RuntimeStep s,Point p) { Calls.Add(s.Kind.ToString()); Points.Add(p); if(FailSend) throw new InvalidOperationException("send failure"); }
        public void Release(MouseButton b,Point p) { Calls.Add("release"); if(FailRelease) throw new InvalidOperationException("release failure"); }
    }
    private sealed class Fixture {
        public Clock Clock { get; }=new(); public Monitors Monitors { get; }=new(); public Backend Backend { get; }=new();
        public SafeRunner Runner { get; } public RunProgram Program { get; }
        public Dictionary<string,string> Map { get; }=new(){["slot"]="display"};
        public Fixture() {
            Runner=new(Clock,Monitors,Backend);
            Program=new([new(100,"slot",new(25,25),ActionKind.ButtonDown,MouseButton.Left),new(1000,"slot",new(25,25),ActionKind.ButtonUp,MouseButton.Left)], [new("slot","Synthetic",Monitors.Values[0].Geometry)]);
        }
        public void Start()=>Runner.Start(Program,Map,hotkeyReady:true);
        public void Pump(long time) { Clock.NowUs=time; Runner.Pump(); }
    }
    [Fact] public void StopBeforeDeadlineIsIdempotent() {
        var f=new Fixture(); f.Start(); f.Runner.Stop(); f.Runner.Stop(); f.Pump(2000);
        Assert.Equal(RunnerState.Stopped,f.Runner.Snapshot.State); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void StopDuringHoldReleasesOwnedOnly() {
        var f=new Fixture(); f.Start(); f.Pump(100); f.Runner.Stop(); f.Runner.Stop(); f.Pump(2000);
        Assert.Equal(["ButtonDown","release"],f.Backend.Calls); Assert.Equal([new Point(-150,50)],f.Backend.Points);
    }
    [Fact] public void TopologyChangeStopsBeforeDispatch() {
        var f=new Fixture(); f.Start(); f.Monitors.Values.Clear(); f.Pump(100);
        Assert.Equal(RunnerState.Failed,f.Runner.Snapshot.State); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void PhysicalInterferenceReleases() {
        var f=new Fixture(); f.Start(); f.Pump(100); f.Backend.Interference++; f.Pump(200);
        Assert.Equal(RunnerState.Failed,f.Runner.Snapshot.State); Assert.Equal(["ButtonDown","release"],f.Backend.Calls);
    }
    [Fact] public void SleepNeverCatchesUp() {
        var f=new Fixture(); f.Start(); f.Pump(10000000); f.Runner.Pump();
        Assert.Equal(RunnerState.Failed,f.Runner.Snapshot.State); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void PermissionLossReleases() {
        var f=new Fixture(); f.Start(); f.Pump(100); f.Backend.Ready=false; f.Pump(150);
        Assert.Equal(RunnerState.Failed,f.Runner.Snapshot.State); Assert.Equal(["ButtonDown","release"],f.Backend.Calls);
    }
    [Fact] public void BackendFailureConservativelyReleases() {
        var f=new Fixture(); f.Start(); f.Backend.FailSend=true; f.Pump(100);
        Assert.Equal(RunnerState.Failed,f.Runner.Snapshot.State); Assert.Equal(["ButtonDown","release"],f.Backend.Calls);
    }
    [Fact] public void ReleaseFailureBlocksRestart() {
        var f=new Fixture(); f.Start(); f.Pump(100); f.Backend.FailRelease=true; f.Runner.Stop();
        Assert.Equal(RunnerState.Failed,f.Runner.Snapshot.State); Assert.Contains("release",f.Runner.Snapshot.Reason);
        Assert.Throws<InvalidOperationException>(f.Start);
    }
    [Fact] public void HotkeyAndMappingRequired() {
        var f=new Fixture(); Assert.Throws<InvalidOperationException>(()=>f.Runner.Start(f.Program,f.Map));
        Assert.Throws<InvalidOperationException>(()=>f.Runner.Start(f.Program,new Dictionary<string,string>(),hotkeyReady:true)); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void PauseReleasesWithoutQueuedUp() {
        var f=new Fixture(); f.Start(); f.Pump(100); f.Runner.Stop("Paused; restart from beginning",paused:true); f.Pump(1000);
        Assert.Equal(RunnerState.Paused,f.Runner.Snapshot.State); Assert.Equal(["ButtonDown","release"],f.Backend.Calls);
    }
    [Fact] public void PreviewHasNoBackendDependency() {
        var f=new Fixture(); Assert.Equal(2,PreviewRunner.Events(f.Program).Count); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void RepeatsAreBoundedAndInfinitePreviewCapped() {
        var f=new Fixture(); var p=new RunProgram(f.Program.Steps,f.Program.Slots,20000,2);
        f.Runner.Start(p,f.Map,hotkeyReady:true); foreach(var t in new long[]{100,1000,20100,21000,21001}) f.Pump(t);
        Assert.Equal(["ButtonDown","ButtonUp","ButtonDown","ButtonUp"],f.Backend.Calls); Assert.Equal(RunnerState.Stopped,f.Runner.Snapshot.State);
        Assert.Equal(3,PreviewRunner.Events(new(f.Program.Steps,f.Program.Slots,20000,null),3).Count);
    }
    [Fact] public void PreheldPhysicalButtonBlocksWithoutRelease() {
        var f=new Fixture(); f.Backend.PhysicalButtonsDown=true;
        Assert.Throws<InvalidOperationException>(f.Start); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void InvalidLaterPointBlocksAllInput() {
        var f=new Fixture(); var bad=new RunProgram(f.Program.Steps.Append(new RuntimeStep(2000,"slot",new(100,0),ActionKind.Move)),f.Program.Slots);
        Assert.Throws<InvalidOperationException>(()=>f.Runner.Start(bad,f.Map,hotkeyReady:true)); Assert.Empty(f.Backend.Calls);
    }
    [Fact] public void ArmDelayAllowsPositioningThenInterferenceStops() {
        var f=new Fixture(); f.Runner.Start(f.Program,f.Map,1000,true); f.Backend.Interference=2; f.Pump(500); f.Pump(1100);
        Assert.Equal(["ButtonDown"],f.Backend.Calls); f.Backend.Interference=3; f.Pump(1150); Assert.Equal(["ButtonDown","release"],f.Backend.Calls);
    }
    [Fact] public void DurationCutoffReleasesAndNeverContinues() {
        var f=new Fixture(); f.Runner.Start(new(f.Program.Steps,f.Program.Slots,20000,null,500),f.Map,hotkeyReady:true); f.Pump(100); f.Pump(500); f.Pump(20000);
        Assert.Equal(["ButtonDown","release"],f.Backend.Calls); Assert.Equal(RunnerState.Stopped,f.Runner.Snapshot.State);
    }
    [Fact] public void DoubleClickHasExplicitNativeCountsAndFinitePreview() {
        var f=new Fixture(); var p=ProgramCompiler.Quick(f.Program.Slots,new[]{("slot",(Point?)new Point(25,25),20000L)},MouseButton.Middle,true,2,null);
        Assert.Equal(new[]{1,1,2,2},p.Steps.Select(s=>s.ClickCount)); Assert.Equal(8,PreviewRunner.Events(p).Count);
    }

}
