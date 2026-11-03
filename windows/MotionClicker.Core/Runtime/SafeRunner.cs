namespace MotionClicker.Core.Runtime;

public sealed record RuntimeDisplay(string Id, string Name, DisplayGeometry Geometry, Rect NativeBounds);
public interface IMonitorProvider { IReadOnlyList<RuntimeDisplay> Displays(); }
public interface IMonotonicClock { long NowUs { get; } }
public interface IInputBackend {
    bool Ready { get; }
    ulong Interference { get; }
    Point Position();
    void Send(RuntimeStep step, Point point);
    void Release(MouseButton button, Point point);
}
public sealed record RuntimeStep(long TimeUs, string Slot, Point? Point, ActionKind Kind,
    MouseButton? Button = null, int ClickCount = 1, double DeltaX = 0, double DeltaY = 0);
public sealed class RunProgram {
    public IReadOnlyList<RuntimeStep> Steps { get; }
    public IReadOnlyList<MonitorSlot> Slots { get; }
    public long? PeriodUs { get; }
    public long? Count { get; }
    public long? DurationUs { get; }
    public RunProgram(IEnumerable<RuntimeStep> steps, IEnumerable<MonitorSlot> slots, long? periodUs = null, long? count = 1, long? durationUs = null) {
        Steps=Array.AsReadOnly(steps.ToArray()); Slots=Array.AsReadOnly(slots.ToArray()); PeriodUs=periodUs; Count=count; DurationUs=durationUs;
    }
}
public enum RunnerState { Idle, Armed, Running, Paused, Stopped, Failed }
public sealed record RunnerSnapshot(RunnerState State, string Reason, long Dispatched, long MaxLagUs);
/// <summary>One serialized gate covers input dispatch, cancellation and conservative hold ownership.</summary>
public sealed class SafeRunner(IMonotonicClock clock, IMonitorProvider monitors, IInputBackend backend) {
    private readonly object gate=new();
    private RunnerState state=RunnerState.Idle;
    private string reason="Ready";
    private long dispatched, maxLag, epoch, cycle;
    private int index;
    private RunProgram? program;
    private RuntimeDisplay[] topology=[];
    private Dictionary<string,string> mapping=[];
    private ulong initialInterference;
    private (MouseButton Button, Point Point)? owned;
    public RunnerSnapshot Snapshot { get { lock(gate) return new(state,reason,dispatched,maxLag); } }
    public void Start(RunProgram value,IReadOnlyDictionary<string,string> map,long delayUs=0,bool hotkeyReady=false) {
        lock(gate) {
            if(owned!=null || state is RunnerState.Running or RunnerState.Armed) throw new InvalidOperationException("Stop and resolve owned release before restart");
            if(!hotkeyReady || !backend.Ready) throw new InvalidOperationException("Emergency hotkey and input capability must be available");
            Validate(value);
            var displays=monitors.Displays().ToArray();
            if(value.Steps.Count==0 || delayUs<0 || clock.NowUs>long.MaxValue-delayUs) throw new InvalidOperationException("Empty plan or invalid delay");
            foreach(var slot in value.Slots) {
                if(!map.TryGetValue(slot.Id,out var id) || displays.FirstOrDefault(d=>d.Id==id) is not { } target ||
                    slot.Geometry.Size!=target.Geometry.Size || slot.Geometry.ScaleFactor!=target.Geometry.ScaleFactor || slot.Geometry.RotationDegrees!=target.Geometry.RotationDegrees)
                    throw new InvalidOperationException("Map every monitor explicitly to matching size, scale and rotation");
            }
            if(map.Values.Distinct(StringComparer.Ordinal).Count()!=map.Count) throw new InvalidOperationException("Monitor slots require distinct runtime displays");
            program=value; mapping=new(map,StringComparer.Ordinal); topology=displays;
            epoch=checked(clock.NowUs+delayUs); cycle=0; index=0; dispatched=0; maxLag=0;
            initialInterference=backend.Interference; state=delayUs>0?RunnerState.Armed:RunnerState.Running;
            reason="Emergency hotkey active; physical input stops execution";
        }
    }
    public void Pump() {
        lock(gate) {
            if(state is not (RunnerState.Armed or RunnerState.Running) || program is not { } p) return;
            if(!backend.Ready) { Finish("Input permission or session lost",failed:true); return; }
            if(backend.Interference!=initialInterference) { Finish("Physical input detected",failed:true); return; }
            if(!monitors.Displays().SequenceEqual(topology)) { Finish("Display topology changed; remap before restart",failed:true); return; }
            var now=clock.NowUs;
            if(now<epoch) return;
            if(p.DurationUs is { } duration && now-epoch>=duration) { Finish("Duration complete"); return; }
            long deadline;
            try { deadline=checked(epoch+checked((p.PeriodUs??0)*cycle)+p.Steps[index].TimeUs); }
            catch(OverflowException) { Finish("Schedule overflow",failed:true); return; }
            if(now<deadline) return;
            var lag=now-deadline; maxLag=Math.Max(maxLag,lag);
            if(lag>100000) { Finish("Scheduling lag exceeded 100 ms; no catch-up",failed:true); return; }
            state=RunnerState.Running;
            var step=p.Steps[index];
            try {
                Point point;
                if(step.Point is { } local) {
                    if(!mapping.TryGetValue(step.Slot,out var id)) throw new InvalidOperationException("Unresolved monitor");
                    point=NativePoint(local,topology.First(d=>d.Id==id));
                } else {
                    point=backend.Position();
                    if(!topology.Any(d=>mapping.Values.Contains(d.Id,StringComparer.Ordinal)&&Contains(point,d.NativeBounds)))
                        throw new InvalidOperationException("Pointer is outside selected displays");
                }
                if(step.Kind==ActionKind.ButtonDown) {
                    if(owned!=null || step.Button is not { } button) throw new InvalidOperationException("Invalid hold");
                    // Backend may partially dispatch before throwing: acquire conservatively first.
                    owned=(button,point);
                }
                backend.Send(step,point);
                if(step.Kind==ActionKind.ButtonUp) owned=null;
                else if(owned is { } hold) owned=(hold.Button,point);
                dispatched++; index++;
                if(index==p.Steps.Count) {
                    index=0;
                    if(p.PeriodUs==null || (p.Count is { } count && cycle>=count-1)) { Finish("Completed"); return; }
                    cycle=checked(cycle+1);
                }
            } catch(Exception error) { Finish($"Input dispatch failed: {error.Message}",failed:true); }
        }
    }
    public void Stop(string reason="Stopped",bool paused=false) { lock(gate) Finish(reason,paused:paused); }
    private void Finish(string message,bool failed=false,bool paused=false) {
        state=failed?RunnerState.Failed:(paused?RunnerState.Paused:RunnerState.Stopped); reason=message;
        if(owned is { } hold) {
            try { backend.Release(hold.Button,hold.Point); owned=null; }
            catch(Exception error) { state=RunnerState.Failed; reason=$"Owned release failed; retry Stop before restart: {error.Message}"; }
        }
        program=null;
    }
    public static bool Contains(Point p,Rect r)=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&p.X>=r.Origin.X&&p.Y>=r.Origin.Y&&p.X<r.Origin.X+r.Size.Width&&p.Y<r.Origin.Y+r.Size.Height;
    /// <summary>Adapter boundary: native physical pixels on Windows, CG global points on macOS.</summary>
    public static Point NativePoint(Point local,RuntimeDisplay display) {
        if(!Contains(local,new(new(0,0),display.Geometry.Size))) throw new InvalidOperationException("Point outside monitor");
        var p=new Point(display.NativeBounds.Origin.X+local.X/display.Geometry.Size.Width*display.NativeBounds.Size.Width,
            display.NativeBounds.Origin.Y+local.Y/display.Geometry.Size.Height*display.NativeBounds.Size.Height);
        if(!Contains(p,display.NativeBounds)) throw new InvalidOperationException("Native point outside display");
        return p;
    }
    private static void Validate(RunProgram p) {
        if(p.Steps.Count==0 || p.Count<=0 || p.DurationUs<=0 || (p.PeriodUs!=null&&(p.PeriodUs<20000||p.Steps[^1].TimeUs>=p.PeriodUs)))
            throw new InvalidOperationException("Invalid count, duration or repeat interval (minimum 20 ms)");
        long last=-1; MouseButton? held=null;
        foreach(var step in p.Steps) {
            if(step.TimeUs<last || (step.Point!=null&&!p.Slots.Any(s=>s.Id==step.Slot))) throw new InvalidOperationException("Invalid order or slot");
            last=step.TimeUs;
            switch(step.Kind) {
            case ActionKind.ButtonDown:
                if(held!=null||step.Button==null) throw new InvalidOperationException("Overlapping hold"); held=step.Button; break;
            case ActionKind.ButtonUp:
                if(held==null||held!=step.Button) throw new InvalidOperationException("Unpaired release"); held=null; break;
            case ActionKind.Scroll:
                if(held!=null) throw new InvalidOperationException("Scroll during hold"); break;
            }
        }
        if(held!=null) throw new InvalidOperationException("Plan ends held");
    }
}
/// <summary>Pure preview has no input backend dependency and caps infinite templates.</summary>
public static class PreviewRunner {
    public static IReadOnlyList<RuntimeStep> Events(RunProgram p,int limit=200) {
        if(limit<=0||p.Steps.Count==0) return [];
        var result=new List<RuntimeStep>(); long cycle=0;
        while(result.Count<limit && (p.Count==null||cycle<p.Count)) {
            foreach(var step in p.Steps) {
                long time;
                try { time=checked(checked((p.PeriodUs??0)*cycle)+step.TimeUs); }
                catch(OverflowException) { return result.AsReadOnly(); }
                if(p.DurationUs is {} duration && time>=duration) return result.AsReadOnly();
                result.Add(step with { TimeUs=time });
                if(result.Count==limit) return result.AsReadOnly();
            }
            if(p.PeriodUs==null||cycle==long.MaxValue) break; cycle++;
        }
        return result.AsReadOnly();
    }
}
public static class ProgramCompiler {
    public static RunProgram Project(MotionProject project)=>new(RunPlanCompiler.Compile(project).Select(a=>new RuntimeStep(a.TimeUs,a.MonitorSlotId,new(a.X,a.Y),a.Kind,a.Button,DeltaX:a.DeltaX??0,DeltaY:a.DeltaY??0)),project.MonitorSlots);
    public static RunProgram Quick(IEnumerable<MonitorSlot> slots,IReadOnlyList<(string Slot,Point? Point,long PauseUs)> targets,MouseButton button,bool doubleClick,long? count,long? durationUs) {
        if(targets.Count==0) throw new InvalidOperationException("Choose points");
        List<RuntimeStep> events=[]; long offset=0;
        foreach(var target in targets) {
            if(target.PauseUs<20000) throw new InvalidOperationException("Each point interval must be at least 20 ms");
            for(int click=1;click<=(doubleClick?2:1);click++) {
                var time=checked(offset+(click-1)*5000);
                events.Add(new(time,target.Slot,target.Point,ActionKind.ButtonDown,button,click));
                events.Add(new(checked(time+1000),target.Slot,target.Point,ActionKind.ButtonUp,button,click));
            }
            offset=checked(offset+target.PauseUs);
        }
        return new(events,slots,offset,count,durationUs);
    }
}
