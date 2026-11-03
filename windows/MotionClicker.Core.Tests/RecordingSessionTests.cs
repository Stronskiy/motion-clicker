using MotionClicker.Core.Recording;
using MotionClicker.Core.Runtime;
using Xunit;
namespace MotionClicker.Core.Tests;
internal sealed class MemoryJournal : IEventJournal
{
    public readonly List<RawPointerRecord> Records = [];
    public bool Fail;
    public void Append(RawPointerRecord record) { if(Fail) throw new IOException("disk full"); Records.Add(record); }
    public void Flush() { if(Fail) throw new IOException("disk full"); }
    public void Read(Action<RawPointerRecord> visit) { foreach(var r in Records) visit(r); }
}
public sealed class RecordingSessionTests
{
    internal static RecordingTarget[] Targets() => new[]{(-400d,"left"),(0d,"right")}.Select(pair=>{
        var g=new DisplayGeometry(new(pair.Item1/2,0),new(200,100),2,0);
        return new RecordingTarget(new(pair.Item2,pair.Item2,g),new("native-"+pair.Item2,pair.Item2,g,new(new(pair.Item1,0),new(400,200))));
    }).ToArray();
    internal static RecordingSession Ready(MemoryJournal j,long epoch=1000000)
    {
        var s=new RecordingSession("take",Targets(),j);
        foreach(var id in new[]{"left","right"}) s.SourceReady(id,new(0,1000000,1000000));
        s.EventTapReady();s.Begin(epoch);return s;
    }
    internal static void Frames(RecordingSession s) { s.AcceptedFrame("left",0,0,1000);s.AcceptedFrame("right",250000,0,1000); }
    internal static void Finish(RecordingSession s)
    {
        s.Stop(2000000);s.WriterFinished("left",WriterStatus.Succeeded,1000);
        Assert.Equal(RecordingState.Finalizing,s.State);s.WriterFinished("right",WriterStatus.Succeeded,1000);
    }
    private sealed record Case(string Name,CapturedPointerEvent[] Events,RecordingState State,int RawCount,int ProjectedCount,int IncompleteCount,bool SourceFailure);
    [Fact] public void SharedDragInterruptionAndSourceLossFixtures()
    {
        var cases=RecordingJson.Deserialize<Case[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","sessions.json")));
        foreach(var c in cases)
        {
            var j=new MemoryJournal();var s=Ready(j);Frames(s);
            foreach(var e in c.Events)s.Receive(e);
            if(c.SourceFailure)s.SourceFailed("right",1000030,"source disconnected");
            Finish(s);var p=s.Project();
            Assert.Equal(c.State,s.State);Assert.Equal(c.RawCount,j.Records.Count);Assert.Equal(c.ProjectedCount,p.Events.Length);
            Assert.Equal(c.IncompleteCount,p.Manifest.IncompleteGroupIds.Length);
            Assert.Equal(new long?[]{0,250000},p.Manifest.Streams.Select(x=>x.MediaTimeZeroUs));
            Assert.Equal(GapKind.Leading,p.Manifest.Streams[1].Gaps[0].Kind);
            if(p.Events.Length>0)
            {
                Assert.Equal(new[]{0,1,2},p.Events.Select(x=>x.Order));Assert.Equal(new long[]{10,10,20},p.Events.Select(x=>x.TimeUs));
                Assert.Equal(new double[]{150,50,100},p.Events.Select(x=>x.X));Assert.Single(p.Events.Select(x=>x.AtomicGroupId).Distinct());
                Assert.True(p.Take.DurationUs>p.Events[^1].TimeUs);
            }
            var before=RecordingJson.Serialize(p);s.Stop(long.MaxValue);s.WriterFinished("right",WriterStatus.Succeeded,1000);
            Assert.Equal(before,RecordingJson.Serialize(s.Project()));
        }
    }
    [Fact] public void ReadinessAndPreparingFailure()
    {
        var j=new MemoryJournal();var s=new RecordingSession("take",Targets(),j);
        s.Receive(new(1,new(1,1),ActionKind.Move));Assert.ThrowsAny<Exception>(()=>s.Begin(0));Assert.Equal(RecordingState.Preparing,s.State);
        s.EventTapReady();s.SourceReady("left",new(0,0,1));Assert.ThrowsAny<Exception>(()=>s.Begin(0));
        s.PreparationFailed("event tap unavailable");Assert.Equal(RecordingState.Failed,s.State);Assert.Null(s.Result);Assert.Empty(j.Records);
    }
    [Fact] public void IgnoresOwnControlPreEpochAndIdleDoesNotInventLoss()
    {
        var j=new MemoryJournal();var s=Ready(j);
        s.Receive(new(long.MaxValue,new(1,1),ActionKind.Move,IsOwnInjected:true));
        s.Receive(new(long.MaxValue,new(1,1),ActionKind.Move,IsControl:true));s.Receive(new(999999,new(1,1),ActionKind.Move));
        s.AcceptedFrame("left",-1,0,1000);Frames(s);s.Idle("left",500000);Finish(s);
        Assert.Empty(j.Records);Assert.Empty(s.Project().Manifest.Streams[0].Gaps);Assert.Equal(1,s.Project().Manifest.Streams[0].FrameCount);
    }
    [Fact] public void DropZeroFrameAndEncoderFailureAreTruthful()
    {
        var s=Ready(new());s.AcceptedFrame("left",0,0,1000);s.ReportGap("left",1000,2000,GapKind.Backpressure);s.Stop(1003000);
        s.WriterFinished("left",WriterStatus.Succeeded,1000);s.WriterFinished("right",WriterStatus.Failed,0,"encoder");
        var p=s.Project();Assert.Equal(RecordingState.Interrupted,p.Manifest.State);Assert.Single(p.Take.Media);
        Assert.NotEmpty(p.Manifest.Warnings);Assert.Equal(WriterStatus.Failed,p.Manifest.Streams[1].WriterStatus);
        var z=Ready(new());z.Stop(1000000);foreach(var id in new[]{"left","right"})z.WriterFinished(id,WriterStatus.Succeeded,0);
        Assert.Equal(RecordingState.Interrupted,z.State);Assert.Empty(z.Project().Take.Media);
    }
    [Fact] public void ReversalOverflowAndJournalFailureCannotProjectUnsafeEvents()
    {
        for(var mode=0;mode<3;mode++)
        {
            var j=new MemoryJournal();var s=Ready(j,mode==1?-1:1000000);Frames(s);
            s.Receive(new(1000010,new(1,1),ActionKind.ButtonDown,MouseButton.Left));if(mode==2)j.Fail=true;
            Assert.ThrowsAny<Exception>(()=>s.Receive(new(mode==0?1000009:mode==1?long.MaxValue:1000020,new(1,1),ActionKind.Move)));
            j.Fail=false;Finish(s);Assert.Equal(RecordingState.Failed,s.State);Assert.Empty(s.Project().Events);
        }
    }

    [Fact] public void FramesRejectReversalAndStopWaitsForLateWriterDrain()
    {
        var s=Ready(new());Frames(s);Assert.ThrowsAny<Exception>(()=>s.AcceptedFrame("left",0,1000,1000));Finish(s);Assert.Equal(RecordingState.Failed,s.State);
        var drain=Ready(new());Frames(drain);drain.Stop(1500000);drain.AcceptedFrame("left",700000,700000,1000);drain.WriterFinished("left",WriterStatus.Succeeded,701000);
        Assert.Null(drain.Result);drain.WriterFinished("right",WriterStatus.Succeeded,1000);Assert.Equal(701000,drain.Project().Take.DurationUs);
        Assert.ThrowsAny<Exception>(()=>drain.WriterFinished("right",WriterStatus.Failed,1000));
    }
    [Fact] public void KnownDropsCompleteWithWarningsAndScrollRemainsLogical()
    {
        var s=Ready(new());Frames(s);s.ReportGap("left",1000,2000,GapKind.Dropped);s.Receive(new(1000010,new(100,50),ActionKind.Scroll,DeltaX:-2,DeltaY:3));Finish(s);
        var p=s.Project();Assert.Equal(RecordingState.Completed,p.Manifest.State);Assert.NotEmpty(p.Manifest.Warnings);Assert.Equal(-2,p.Events[0].DeltaX);Assert.Equal(25,p.Events[0].Y);
    }
    [Fact] public void UnknownRecordingEnumsAndLargeWireIntegerRemainStrict()
    {
        var e=new CapturedPointerEvent(9007199254740993,new(1,1),ActionKind.Move);
        Assert.Equal(e,RecordingJson.Deserialize<CapturedPointerEvent>(RecordingJson.Serialize(e)));
        foreach(var invalid in new[]{"0","\"Completed\"","\"unknown\""})Assert.Throws<System.Text.Json.JsonException>(()=>System.Text.Json.JsonSerializer.Deserialize<RecordingState>(invalid,RecordingJson.Options));
    }
    [Fact] public void FrozenTargetsAndInvalidMappings()
    {
        var targets=Targets();var s=new RecordingSession("take",targets,new MemoryJournal());targets[0]=targets[1];s.SourceReady("left",new(0,0,1));
        Assert.ThrowsAny<Exception>(()=>new RecordingSession("take",[Targets()[0],Targets()[0]],new MemoryJournal()));
        Assert.ThrowsAny<Exception>(()=>new RecordingSession("left",Targets(),new MemoryJournal()));
        var ready=Ready(new());Frames(ready);Finish(ready);var old=ready.Result!;old.Streams[0]=old.Streams[1];old.Targets[0]=old.Targets[1];
        Assert.Equal("left",ready.Result!.Streams[0].MonitorSlotId);Assert.Equal("left",ready.Project().Manifest.Targets[0].Id);
    }

    [Fact] public void OverflowingWriterCannotAppearAsSuccessfulMedia()
    {
        var s=Ready(new());Frames(s);s.Stop(2000000);s.WriterFinished("left",WriterStatus.Succeeded,1000);s.WriterFinished("right",WriterStatus.Succeeded,long.MaxValue);
        var p=s.Project();Assert.Equal(RecordingState.Failed,p.Manifest.State);Assert.Equal(WriterStatus.Failed,p.Manifest.Streams[1].WriterStatus);Assert.Single(p.Take.Media);
    }

    [Fact] public void RequiredCaptureFlagsCannotDefaultFromWire()
    {
        const string json="""{"hostTimeUs":0,"nativePosition":{"x":1,"y":1},"kind":"move"}""";
        Assert.Throws<System.Text.Json.JsonException>(()=>RecordingJson.Deserialize<CapturedPointerEvent>(json));
    }
}
