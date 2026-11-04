using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

/// <summary>Caller-confined to one serial owner; marshal native callback queues here.</summary>
public sealed class RecordingSession
{
    private sealed record Terminal(WriterStatus Status,long Duration,string? Reason);
    private sealed class StreamState
    {
        public ClockAnchor? Clock;
        public long? Zero,LastTicks;
        public long Count,End,GapCount;
        public RecordingGap[] Gaps=[];
        public Terminal? Terminal;
        public string? SourceFailure;
        public StreamState Copy()=>new(){Clock=Clock,Zero=Zero,LastTicks=LastTicks,Count=Count,End=End,GapCount=GapCount,Gaps=Gaps,Terminal=Terminal,SourceFailure=SourceFailure};
    }
    public RecordingState State { get; private set; } = RecordingState.Preparing;
    private RecordingManifest? result;
    public RecordingManifest? Result => result is null?null:Clone(result);
    private readonly string takeId;
    private readonly RecordingTarget[] targets;
    private readonly IEventJournal journal;
    private readonly PointerTimingBasis? pointerTimingBasis;
    private readonly PointerTimingValidation pointerTimingValidation=new();
    private readonly Dictionary<string,StreamState> streams;
    private bool tapReady,journalFailed;
    private long epoch,duration=1,count;
    private long? lastHost;
    private string? heldGroup,reason;
    private MouseButton? heldButton;
    private readonly HashSet<string> incomplete=new(StringComparer.Ordinal);
    private readonly List<string> warnings=[];
    private RecordingState terminalState=RecordingState.Completed;
    private static RecordingManifest Clone(RecordingManifest m)=>RecordingJson.Deserialize<RecordingManifest>(RecordingJson.Serialize(m));

    public RecordingSession(string takeId,RecordingTarget[] targets,IEventJournal journal,PointerTimingBasis? pointerTimingBasis=null)
    {
        Require(pointerTimingBasis is null || Enum.IsDefined(pointerTimingBasis.Value),"Invalid timing basis");
        this.pointerTimingBasis=pointerTimingBasis;
        Require(targets.Length>0,"No recording targets");
        RunPlanCompiler.Compile(new(1,takeId,0,targets.Select(t=>t.Slot).ToArray(),[],[],[],[]));
        var ids=new HashSet<string>(StringComparer.Ordinal);
        for(var i=0;i<targets.Length;i++)
        {
            var t=targets[i];Require(ids.Add(t.Display.Id),"Duplicate runtime display");
            Require(t.Slot.Geometry==t.Display.Geometry,"Snapshot geometry mismatch");
            var b=t.Display.NativeBounds;Require(Valid(b),"Invalid native bounds");
            foreach(var previous in targets.Take(i))
            {
                var a=previous.Display.NativeBounds;
                Require(!(a.Origin.X<b.Origin.X+b.Size.Width && b.Origin.X<a.Origin.X+a.Size.Width && a.Origin.Y<b.Origin.Y+b.Size.Height && b.Origin.Y<a.Origin.Y+a.Size.Height),"Overlapping native targets");
            }
        }
        this.takeId=takeId;this.targets=[..targets];this.journal=journal;
        streams=targets.ToDictionary(t=>t.Slot.Id,_=>new StreamState(),StringComparer.Ordinal);
    }
    private static bool Valid(Rect r)=>double.IsFinite(r.Origin.X)&&double.IsFinite(r.Origin.Y)&&double.IsFinite(r.Size.Width)&&double.IsFinite(r.Size.Height)&&r.Size.Width>0&&r.Size.Height>0&&double.IsFinite(r.Origin.X+r.Size.Width)&&double.IsFinite(r.Origin.Y+r.Size.Height);
    private static bool Contains(Point p,Rect r)=>p.X>=r.Origin.X&&p.Y>=r.Origin.Y&&p.X<r.Origin.X+r.Size.Width&&p.Y<r.Origin.Y+r.Size.Height;
    private StreamState Stream(string id)=>streams.TryGetValue(id,out var s)?s.Copy():throw new RecordingException("Unknown monitor source");
    private MediaClockMapper Mapper(StreamState s)
    {
        var c=s.Clock??throw new RecordingException("Source not ready");return new(c.SourceAnchorTicks,c.HostAnchorUs,c.TicksPerSecond,epoch);
    }
    public void SourceReady(string monitorSlotId,ClockAnchor clock)
    {
        Require(State==RecordingState.Preparing,"Source readiness after preparation");var s=Stream(monitorSlotId);
        Require(s.Clock is null,"Duplicate source readiness");_ =new MediaClockMapper(clock.SourceAnchorTicks,clock.HostAnchorUs,clock.TicksPerSecond,clock.HostAnchorUs);
        s.Clock=clock;streams[monitorSlotId]=s;
    }
    public void EventTapReady() { Require(State==RecordingState.Preparing,"Tap readiness after preparation");tapReady=true; }
    public void Begin(long epochHostUs)
    {
        Require(State==RecordingState.Preparing&&tapReady&&streams.Values.All(s=>s.Clock is not null),"Required sources are not ready");
        foreach(var s in streams.Values){var c=s.Clock!;_=new MediaClockMapper(c.SourceAnchorTicks,c.HostAnchorUs,c.TicksPerSecond,epochHostUs);}
        epoch=epochHostUs;State=RecordingState.Recording;
    }
    public void PreparationFailed(string reason) { if(State==RecordingState.Preparing){this.reason=reason;State=RecordingState.Failed;} }
    private void Interrupt(string message,bool failed=false)
    {
        if(failed)terminalState=RecordingState.Failed;else if(terminalState==RecordingState.Completed)terminalState=RecordingState.Interrupted;
        reason??=message;if(heldGroup is not null)incomplete.Add(heldGroup);if(State==RecordingState.Recording)State=RecordingState.Finalizing;
    }
    private void CheckedRecording(Action body) { try{body();}catch(Exception e){Interrupt(e.Message,true);throw;} }
    public void Receive(CapturedPointerEvent e)
    {
        if(State!=RecordingState.Recording||e.IsOwnInjected||e.IsControl)return;
        CheckedRecording(()=>{
            var time=checked(e.HostTimeUs-epoch);if(time<0)return;
            pointerTimingValidation.Accept(e,pointerTimingBasis);
            Require(lastHost is null||e.HostTimeUs>=lastHost,"Reversed pointer timestamp");
            Require(double.IsFinite(e.NativePosition.X)&&double.IsFinite(e.NativePosition.Y),"Invalid pointer coordinate");lastHost=e.HostTimeUs;
            var target=targets.FirstOrDefault(t=>Contains(e.NativePosition,t.Display.NativeBounds));
            if(target is null)
            {
                if(heldGroup is not null){Append(e,time,null,heldGroup);Interrupt("Held group left selected displays");}return;
            }
            var b=target.Display.NativeBounds;var size=target.Slot.Geometry.Size;
            var x=(e.NativePosition.X-b.Origin.X)/b.Size.Width*size.Width;var y=(e.NativePosition.Y-b.Origin.Y)/b.Size.Height*size.Height;
            Require(double.IsFinite(x)&&double.IsFinite(y)&&x>=0&&y>=0&&x<size.Width&&y<size.Height,"Mapped point outside target");
            var group=heldGroup;
            switch(e.Kind)
            {
                case ActionKind.ButtonDown:
                    Require(heldGroup is null&&e.Button is not null&&Enum.IsDefined(e.Button.Value)&&e.DeltaX is null&&e.DeltaY is null,"Invalid down or simultaneous hold");
                    group="g-"+Guid.NewGuid().ToString();break;
                case ActionKind.ButtonUp:Require(heldGroup is not null&&e.Button==heldButton&&e.DeltaX is null&&e.DeltaY is null,"Unmatched up");break;
                case ActionKind.Move:Require(e.Button is null&&e.DeltaX is null&&e.DeltaY is null,"Invalid move");break;
                case ActionKind.Scroll:Require(heldGroup is null&&e.Button is null&&e.DeltaX is double dx&&double.IsFinite(dx)&&e.DeltaY is double dy&&double.IsFinite(dy),"Invalid scroll or interleaved hold");break;
                default:throw new RecordingException("Unknown action kind");
            }
            Require(count<=int.MaxValue,"Event order exhausted");
            var action=new RecordedAction("e-"+Guid.NewGuid(),takeId,target.Slot.Id,time,(int)count,e.Kind,x,y,e.Button,group,e.DeltaX,e.DeltaY);
            Append(e,time,action,group);
            // Only accepted journal history may establish or release pointer ownership.
            if(e.Kind==ActionKind.ButtonDown){heldGroup=group;heldButton=e.Button;}
            else if(e.Kind==ActionKind.ButtonUp){heldGroup=null;heldButton=null;}
        });
    }
    private void Append(CapturedPointerEvent e,long time,RecordedAction? action,string? group)
    {
        Require(count<=int.MaxValue,"Event order exhausted");var nextDuration=checked(time+1);
        try{journal.Append(new((int)count,e,time,action,group));}catch{journalFailed=true;throw;}
        count++;duration=Math.Max(duration,nextDuration);
    }
    private void AppendGap(RecordingGap gap,string monitorSlotId,StreamState stream)
    {
        var next=checked(stream.GapCount+1);
        try{journal.AppendGap(new(monitorSlotId,gap));}catch{journalFailed=true;throw;}
        stream.GapCount=next;
        // Immutable bounded preview: ordinary frame/idle copies share it without allocation.
        if(stream.Gaps.Length<64)stream.Gaps=[..stream.Gaps,gap];
    }
    public void AcceptedFrame(string monitorSlotId,long sourceTicks,long encodedTimeUs,long durationUs)
    {
        if(State is not (RecordingState.Recording or RecordingState.Finalizing))return;
        CheckedRecording(()=>{
            var s=Stream(monitorSlotId);Require(s.Terminal is null,"Frame after writer terminal");
            var time=Mapper(s).Map(sourceTicks);if(time<0)return;
            Require(s.LastTicks is null||sourceTicks>s.LastTicks,"Reversed or duplicate frame timestamp");
            Require(encodedTimeUs>=s.End&&durationUs>0,"Invalid encoded frame range");
            var zero=s.Zero??time;
            if(s.Zero is null)Require(encodedTimeUs==0,"First encoded frame must start at zero");
            var expected=checked(zero+encodedTimeUs);var error=checked(time-expected);Require(error is >=-1 and <=1,"Encoder rebased source timing");
            var end=checked(encodedTimeUs+durationUs);var nextDuration=checked(zero+end);var nextCount=checked(s.Count+1);
            if(s.Zero is null&&time>0)AppendGap(new(0,time,GapKind.Leading),monitorSlotId,s);
            s.Zero=zero;s.End=end;s.Count=nextCount;s.LastTicks=sourceTicks;duration=Math.Max(duration,nextDuration);streams[monitorSlotId]=s;
        });
    }
    public void Idle(string monitorSlotId,long sourceTicks)
    {
        if(State!=RecordingState.Recording)return;CheckedRecording(()=>{_=Mapper(Stream(monitorSlotId)).Map(sourceTicks);});
    }
    public void ReportGap(string monitorSlotId,long startTicks,long endTicks,GapKind kind,string? reason=null)
    {
        if(State is not (RecordingState.Recording or RecordingState.Finalizing))return;
        CheckedRecording(()=>{
            var s=Stream(monitorSlotId);Require(s.Terminal is null&&kind is GapKind.Dropped or GapKind.Backpressure,"Invalid gap report");
            var m=Mapper(s);var start=m.Map(startTicks);var end=m.Map(endTicks);Require(end>start,"Invalid gap range");if(end<=0)return;
            AppendGap(new(Math.Max(0,start),end,kind,reason),monitorSlotId,s);streams[monitorSlotId]=s;duration=Math.Max(duration,end);
        });
    }
    public void SourceFailed(string monitorSlotId,long hostTimeUs,string reason)
    {
        if(State==RecordingState.Preparing){_=Stream(monitorSlotId);PreparationFailed(reason);return;}
        if(State is not (RecordingState.Recording or RecordingState.Finalizing))return;
        CheckedRecording(()=>{
            var s=Stream(monitorSlotId);var time=Math.Max(0,checked(hostTimeUs-epoch));AppendGap(new(time,time,GapKind.SourceFailure,reason),monitorSlotId,s);s.SourceFailure=reason;streams[monitorSlotId]=s;
            duration=Math.Max(duration,time);Interrupt(reason);
        });
    }
    public void Stop(long hostTimeUs,string? reason=null)
    {
        if(State==RecordingState.Preparing){PreparationFailed(reason??"Stopped before all sources ready");return;}
        if(State!=RecordingState.Recording)return;
        CheckedRecording(()=>{
            var end=checked(hostTimeUs-epoch);Require(end>=0&&(lastHost is null||hostTimeUs>=lastHost),"Stop timestamp precedes input");duration=Math.Max(duration,end);
            if(reason is not null)Interrupt(reason);if(heldGroup is not null)Interrupt("Stopped during held group");State=RecordingState.Finalizing;
        });
    }
    public void WriterFinished(string monitorSlotId,WriterStatus status,long encodedDurationUs,string? reason=null)
    {
        var s=Stream(monitorSlotId);var terminal=new Terminal(status,encodedDurationUs,reason);
        if(s.Terminal is not null){Require(s.Terminal==terminal,"Conflicting writer terminal");return;}
        Require(State==RecordingState.Finalizing,"Writer terminal before stop");Require(encodedDurationUs>=0&&Enum.IsDefined(status),"Invalid writer result");
        s.Terminal=terminal;streams[monitorSlotId]=s;
        if(status==WriterStatus.Failed||s.Count==0||encodedDurationUs<s.End)Interrupt(reason??"Empty or incomplete writer");
        if(streams.Values.All(s=>s.Terminal is not null))FinalizeRecording();
    }
    private void FinalizeRecording()
    {
        var media=new List<StreamManifest>();
        foreach(var target in targets)
        {
            var id=target.Slot.Id;var s=streams[id];var t=s.Terminal!;
            var successful=t.Status==WriterStatus.Succeeded&&s.Count>0&&t.Duration>=s.End&&s.Zero is not null;
            var failure=t.Reason;
            if(s.Zero is long zero){try{duration=Math.Max(duration,checked(zero+t.Duration));}catch(OverflowException){successful=false;failure="Writer duration overflow";Interrupt(failure,true);}}
            if(!successful){try{AppendGap(new(duration,duration,GapKind.EncoderFailure,failure??"Empty or incomplete writer"),id,s);}catch(Exception e){journalFailed=true;Interrupt(e.Message,true);}}
            if(s.GapCount>0)warnings.Add($"Stream {id} contains gaps or failures");
            media.Add(new(id,$"media/{takeId}/{id}.mp4",s.Clock!,s.Zero,t.Duration,s.Count,s.Gaps,successful?WriterStatus.Succeeded:WriterStatus.Failed,s.GapCount,$"takes/{takeId}/gaps.jsonl",successful?s.SourceFailure:failure??"Empty or incomplete writer"));
        }
        try{journal.Flush();}catch(Exception e){journalFailed=true;Interrupt(e.Message,true);}
        State=terminalState;result=new(1,takeId,State,epoch,duration,targets.Select(t=>t.Slot).ToArray(),media.ToArray(),count,incomplete.Order(StringComparer.Ordinal).ToArray(),warnings.ToArray(),reason,pointerTimingBasis);
    }
    public RecordingResult Project()
    {
        var manifest=Result??throw new RecordingException("Recording has no final manifest");var events=new List<RecordedAction>();
        if(!journalFailed)journal.Read(r=>{if(r.Action is not null&&(r.AtomicGroupId is null||!incomplete.Contains(r.AtomicGroupId)))events.Add(r.Action);});
        var take=new Take(takeId,manifest.DurationUs,targets.Select(t=>t.Slot.Id).ToArray(),manifest.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded).Select(s=>new TakeMedia(s.MonitorSlotId,s.RelativePath)).ToArray());
        return new(manifest,take,events.ToArray());
    }
}
