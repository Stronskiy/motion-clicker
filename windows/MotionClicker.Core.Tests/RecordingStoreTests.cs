using MotionClicker.Core.Recording;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class RecordingStoreTests : IDisposable
{
    private readonly List<string> roots=[];
    private string Root()
    {
        var p=Path.Combine(Path.GetTempPath(),"motionclicker-synthetic-"+Guid.NewGuid());Directory.CreateDirectory(p);roots.Add(p);
        File.WriteAllText(Path.Combine(p,"project.json"),"previous-save");return p;
    }
    public void Dispose() { foreach(var p in roots)Directory.Delete(p,true); }
    private static RawPointerRecord Record()=>new(0,new(1,new(1,1),ActionKind.Move),1,new("event","take","left",1,0,ActionKind.Move,1,1));
    private static RecordingManifest Manifest()=>new(1,"take",RecordingState.Completed,0,1000,[RecordingSessionTests.Targets()[0].Slot],
        [new("left","media/take/left.mp4",new(0,0,1000000),0,1000,1,[],WriterStatus.Succeeded,0,"takes/take/gaps.jsonl")],1,[],[]);
    [Fact] public void ActualDisposableFilesystemPublishesManifestLastAndStreamsJournal()
    {
        var r=Root();using var store=new RecordingStore(r,"take");store.Append(Record());File.WriteAllText(store.MediaFile("left"),"synthetic-media");
        Assert.False(File.Exists(Path.Combine(r,"takes","take","manifest.json")));
        var path=store.Publish(Manifest());Assert.Equal(path,store.Publish(Manifest()));
        Assert.Equal(RecordingJson.Serialize(Manifest()),RecordingJson.Serialize(RecordingJson.Deserialize<RecordingManifest>(File.ReadAllText(path))));
        var raw=new List<RawPointerRecord>();store.Read(raw.Add);Assert.Equal(new[]{Record()},raw);
        Assert.Equal("previous-save",File.ReadAllText(Path.Combine(r,"project.json")));
        Assert.True(store.Recovery.MediaPublished);Assert.True(store.Recovery.ManifestPublished);
        Assert.ThrowsAny<Exception>(()=>new RecordingStore(r,"take"));
    }
    [Theory][InlineData(StoreOperation.Append)][InlineData(StoreOperation.Flush)][InlineData(StoreOperation.MoveMedia)][InlineData(StoreOperation.PublishManifest)]
    public void FaultsPreservePreviousSaveAndExposeStagingOrOrphan(StoreOperation operation)
    {
        var r=Root();using var store=new RecordingStore(r,"take",op=>{if(op==operation)throw new IOException("disk full");});
        File.WriteAllText(store.MediaFile("left"),"synthetic-media");
        if(operation==StoreOperation.Append)Assert.ThrowsAny<Exception>(()=>store.Append(Record()));else store.Append(Record());
        Assert.ThrowsAny<Exception>(()=>store.Publish(Manifest()));Assert.Equal("previous-save",File.ReadAllText(Path.Combine(r,"project.json")));
        Assert.False(store.Recovery.ManifestPublished);Assert.Equal(operation==StoreOperation.PublishManifest,store.Recovery.MediaPublished);
        Assert.True(Directory.Exists(store.Recovery.StagingPath));
    }
    [Fact] public void RejectsTraversalSymlinkAndCaseCollisions()
    {
        var r=Root();foreach(var id in new[]{"../escape","bad/name",".","..","","CON","aux","com1","LPT9","tail."})Assert.ThrowsAny<Exception>(()=>new RecordingStore(r,id));
        var outside=Root();Directory.CreateSymbolicLink(Path.Combine(r,"media"),outside);Assert.ThrowsAny<Exception>(()=>new RecordingStore(r,"take"));
        var q=Root();Directory.CreateDirectory(Path.Combine(q,"MEDIA"));Assert.ThrowsAny<Exception>(()=>new RecordingStore(q,"take"));
    }
    [Fact] public void RejectsRedirectedStagingAndFailedMediaCannotPublishAsSuccessful()
    {
        var r=Root();using var store=new RecordingStore(r,"take");store.Append(Record());store.MediaFile("left");Assert.ThrowsAny<Exception>(()=>store.Publish(Manifest()));
        var outside=Root();var media=Path.Combine(store.Recovery.StagingPath,"media");Directory.Delete(media,true);Directory.CreateSymbolicLink(media,outside);
        Assert.ThrowsAny<Exception>(()=>store.MediaFile("right"));
    }

    [Fact] public void RejectsUnexpectedSymlinkInMediaTreeBeforeAnyPublication()
    {
        var r=Root();var outside=Root();using var store=new RecordingStore(r,"take");store.Append(Record());var file=store.MediaFile("left");
        Assert.False(File.Exists(file));File.WriteAllText(file,"synthetic-media");Assert.ThrowsAny<Exception>(()=>store.MediaFile("left"));
        Directory.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(file)!,"hidden"),outside);
        Assert.ThrowsAny<Exception>(()=>store.Publish(Manifest()));Assert.False(store.Recovery.MediaPublished);
    }
    [Fact] public void PublicationRetryAfterMetadataFailureAndConcurrentTakeCollision()
    {
        var r=Root();var fail=true;using var store=new RecordingStore(r,"take",op=>{if(op==StoreOperation.PublishManifest&&fail)throw new IOException("disk full");});
        store.Append(Record());File.WriteAllText(store.MediaFile("left"),"synthetic-media");Assert.ThrowsAny<Exception>(()=>store.Publish(Manifest()));fail=false;
        Assert.True(File.Exists(store.Publish(Manifest())));
        var q=Root();using var first=new RecordingStore(q,"take");using var second=new RecordingStore(q,"take");
        foreach(var s in new[]{first,second}){s.Append(Record());File.WriteAllText(s.MediaFile("left"),"synthetic-media");}
        var path=first.Publish(Manifest());var before=File.ReadAllBytes(path);Assert.ThrowsAny<Exception>(()=>second.Publish(Manifest()));Assert.Equal(before,File.ReadAllBytes(path));
    }

    [Fact] public void SessionToPublishedInterruptedTakeKeepsRawHoldOnly()
    {
        var r=Root();using var store=new RecordingStore(r,"take");var s=new RecordingSession("take",RecordingSessionTests.Targets(),store);
        foreach(var id in new[]{"left","right"}){s.SourceReady(id,new(0,1000000,1000000));File.WriteAllText(store.MediaFile(id),"synthetic-media");}
        s.EventTapReady();s.Begin(1000000);RecordingSessionTests.Frames(s);s.Receive(new(1000001,new(1,1),ActionKind.ButtonDown,MouseButton.Left));
        RecordingSessionTests.Finish(s);var p=s.Project();var path=store.Publish(p.Manifest);Assert.Empty(p.Events);Assert.Equal(RecordingState.Interrupted,p.Manifest.State);
        Assert.Equal(RecordingJson.Serialize(p.Manifest),RecordingJson.Serialize(RecordingJson.Deserialize<RecordingManifest>(File.ReadAllText(path))));
        var records=new List<RawPointerRecord>();store.Read(records.Add);Assert.Single(records);Assert.Equal(ActionKind.ButtonDown,records[0].Event.Kind);
        Assert.Equal(p.Manifest.IncompleteGroupIds[0],records[0].AtomicGroupId);
    }

    [Fact] public void LargeTakePublishesBothJournalsWithoutRequestingEventProjection()
    {
        var r=Root();using var store=new RecordingStore(r,"take");var s=new RecordingSession("take",RecordingSessionTests.Targets(),store);
        foreach(var id in new[]{"left","right"}){s.SourceReady(id,new(0,1000000,1000000));File.WriteAllText(store.MediaFile(id),"synthetic-media");}
        s.EventTapReady();s.Begin(1000000);RecordingSessionTests.Frames(s);
        for(var i=0;i<10000;i++)s.Receive(new(1000000+i,new(1,1),ActionKind.Move));
        for(var i=0;i<5000;i++)s.ReportGap("left",i*2000L+1000,i*2000L+1100,GapKind.Dropped);
        RecordingSessionTests.Finish(s);var m=s.Result!;store.Publish(m);var events=0;var gaps=0;store.Read(_=>events++);store.ReadGaps(_=>gaps++);
        Assert.Equal(10000,events);Assert.Equal(5001,gaps);Assert.Equal(64,m.Streams[0].Gaps.Length);Assert.Equal("previous-save",File.ReadAllText(Path.Combine(r,"project.json")));
    }
    [Fact] public void GapAppendFailureFailsCaptureAndCannotPublish()
    {
        var r=Root();var fail=false;using var store=new RecordingStore(r,"take",op=>{if(fail&&op==StoreOperation.Append)throw new IOException("disk full");});
        var s=new RecordingSession("take",RecordingSessionTests.Targets(),store);foreach(var id in new[]{"left","right"})s.SourceReady(id,new(0,1000000,1000000));
        s.EventTapReady();s.Begin(1000000);RecordingSessionTests.Frames(s);fail=true;
        Assert.ThrowsAny<Exception>(()=>s.ReportGap("left",1000,2000,GapKind.Dropped));RecordingSessionTests.Finish(s);Assert.Equal(RecordingState.Failed,s.State);
        Assert.ThrowsAny<Exception>(()=>store.Publish(s.Result!));Assert.False(store.Recovery.ManifestPublished);
    }
}
