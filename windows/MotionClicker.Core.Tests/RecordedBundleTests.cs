using MotionClicker.Core.Recording;
using Xunit;
using System.Text.Json.Nodes;
namespace MotionClicker.Core.Tests;
public sealed class RecordedBundleTests : IDisposable
{
    private readonly List<string> roots=[];
    private sealed record Fixture(RecordingManifest Manifest,RawPointerRecord[] Raw,JournalGapRecord[] Gaps,MotionProject ExpectedProject);
    private static Fixture ReadFixture()=>RecordingJson.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","bundle.json")));
    private string Sandbox(){var r=Path.Combine(Path.GetTempPath(),"bundle-tests-"+Guid.NewGuid());Directory.CreateDirectory(r);roots.Add(r);return r;}
    public void Dispose(){foreach(var r in roots)Directory.Delete(r,true);}
    private static void Published(Fixture f,string root)
    {
        Directory.CreateDirectory(Path.Combine(root,"takes","take"));Directory.CreateDirectory(Path.Combine(root,"media","take"));
        File.WriteAllText(Path.Combine(root,"takes","take","manifest.json"),RecordingJson.Serialize(f.Manifest));
        File.WriteAllText(Path.Combine(root,"takes","take","events.jsonl"),string.Concat(f.Raw.Select(r=>RecordingJson.Serialize(r)+"\n")));
        File.WriteAllText(Path.Combine(root,"takes","take","gaps.jsonl"),string.Concat(f.Gaps.Select(r=>RecordingJson.Serialize(r)+"\n")));
        foreach(var s in f.Manifest.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded))File.WriteAllText(Path.Combine(root,s.RelativePath),$"synthetic-video-{s.MonitorSlotId}");
    }
    [Fact] public void InitialSaveAndReadOnlyColdOpenPreserveSharedProjectionOffsetsAndTiming()
    {
        var f=ReadFixture();var root=Path.Combine(Sandbox(),"recording.motionclicker");var scratch=Sandbox();RecordedBundleStore.Create(root);Published(f,root);
        var saved=RecordedBundleStore.SaveInitial(root,new(f.Manifest,f.ExpectedProject.Takes[0],f.ExpectedProject.Events),scratch);
        Assert.Equal(ProjectJson.Serialize(f.ExpectedProject),ProjectJson.Serialize(saved.Project));var before=TreeBytes(root);
        var opened=RecordedBundleStore.ColdOpen(root,scratch);Assert.Equal(ProjectJson.Serialize(saved.Project),ProjectJson.Serialize(opened.Project));
        Assert.Equal(RecordingJson.Serialize(saved.Manifest),RecordingJson.Serialize(opened.Manifest));Assert.Equal(saved.Media,opened.Media);
        Assert.Equal(new long[]{0,5},opened.Media.Select(s=>s.MediaTimeZeroUs));Assert.Equal(9007199254740993,opened.Manifest.EpochHostUs);
        Assert.Equal(PointerTimingBasis.NativeHostObservation,opened.Manifest.PointerTimingBasis);Assert.Equal(RecordedBundleAvailability.RequiresMonitorMapping,opened.Availability);
        _=RecordedBundleStore.ColdOpen(root,scratch);Assert.Equal(before,TreeBytes(root));Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        var mutated=opened.Project;mutated.Events[0]=mutated.Events[0] with { TimeUs=99 };Assert.Equal(10,opened.Project.Events[0].TimeUs);
    }
    private (Fixture F,string Root,string Scratch) Prepared()
    {
        var f=ReadFixture();var root=Path.Combine(Sandbox(),"recording.motionclicker");var scratch=Sandbox();RecordedBundleStore.Create(root);Published(f,root);return(f,root,scratch);
    }
    private static RecordedBundleSnapshot Save(Fixture f,string root,string scratch)=>RecordedBundleStore.SaveInitial(root,new(f.Manifest,f.ExpectedProject.Takes[0],f.ExpectedProject.Events),scratch);
    private static void Rewrite(string path,Action<JsonObject> mutation)
    {
        var json=JsonNode.Parse(File.ReadAllText(path))!.AsObject();mutation(json);File.WriteAllText(path,json.ToJsonString());
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void PartialHoldAndSourceFailureRemainInspectableNeverExecutable(bool hold)
    {
        var(f,root,scratch)=Prepared();var manifestPath=Path.Combine(root,"takes","take","manifest.json");
        Rewrite(manifestPath,j=>{j["state"]="interrupted";if(hold){j["incompleteGroupIds"]=new JsonArray("hold");j["rawEventCount"]=1;}
            else{j["streams"]![1]!["writerStatus"]="failed";j["streams"]![1]!["failureReason"]="synthetic source failure";}});
        if(hold)File.WriteAllText(Path.Combine(root,"takes","take","events.jsonl"),RecordingJson.Serialize(f.Raw[0])+"\n");
        var m=RecordingJson.Deserialize<RecordingManifest>(File.ReadAllText(manifestPath));
        var take=new Take("take",100,["left","right"],m.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded).Select(s=>new TakeMedia(s.MonitorSlotId,s.RelativePath)).ToArray());
        var snapshot=RecordedBundleStore.SaveInitial(root,new(m,take,hold?[]:f.ExpectedProject.Events),scratch);
        Assert.Equal(RecordedBundleAvailability.Partial,snapshot.Availability);Assert.Equal(hold?0:4,snapshot.Project.Events.Length);Assert.Equal(hold?2:1,snapshot.Media.Length);
        Assert.Equal(ProjectJson.Serialize(snapshot.Project),ProjectJson.Serialize(RecordedBundleStore.ColdOpen(root,scratch).Project));
    }
    [Theory][InlineData(BundleOperation.WriteProject)][InlineData(BundleOperation.FlushProject)][InlineData(BundleOperation.PublishProject)]
    public void PublicationFaultsPreserveEveryPriorByteAndAllowRetry(BundleOperation operation)
    {
        var(f,root,scratch)=Prepared();var before=TreeBytes(root);
        Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.SaveInitial(root,new(f.Manifest,f.ExpectedProject.Takes[0],f.ExpectedProject.Events),scratch,op=>{if(op==operation)throw new IOException("synthetic disk failure");}));
        var after=TreeBytes(root);foreach(var entry in before)Assert.Contains(entry,after);
        Assert.False(File.Exists(Path.Combine(root,"project.json")));Assert.NotEmpty(RecordedBundleStore.RecoveryEntries(root));
        _=Save(f,root,scratch);var committed=TreeBytes(root);Assert.ThrowsAny<Exception>(()=>Save(f,root,scratch));Assert.Equal(committed,TreeBytes(root));
    }
    [Fact] public void RealDestinationCollisionPreservesConcurrentSave()
    {
        var(f,root,scratch)=Prepared();
        Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.SaveInitial(root,new(f.Manifest,f.ExpectedProject.Takes[0],f.ExpectedProject.Events),scratch,op=>{if(op==BundleOperation.PublishProject)File.WriteAllText(Path.Combine(root,"project.json"),"prior concurrent save");}));
        Assert.Equal("prior concurrent save",File.ReadAllText(Path.Combine(root,"project.json")));
    }
    [Fact] public void ColdOpenRejectsCorruptSchemaReferencesRawSummaryGapTimingAndLegacyDomainJSON()
    {
        Action<string>[] mutations=[
            r=>Rewrite(Path.Combine(r,"project.json"),j=>j["bundleVersion"]=2),
            r=>Rewrite(Path.Combine(r,"project.json"),j=>j["manifestPath"]="takes/../escape/manifest.json"),
            r=>Rewrite(Path.Combine(r,"project.json"),j=>j["project"]!["schemaVersion"]=2),
            r=>Rewrite(Path.Combine(r,"project.json"),j=>j["project"]!["events"]![0]!["timeUs"]=11),
            r=>Rewrite(Path.Combine(r,"takes","take","manifest.json"),j=>j["rawEventCount"]=7),
            r=>Rewrite(Path.Combine(r,"takes","take","manifest.json"),j=>j["manifestVersion"]=2),
            r=>Rewrite(Path.Combine(r,"takes","take","manifest.json"),j=>j["streams"]![0]!["relativePath"]="media/../escape.mp4"),
            r=>File.WriteAllText(Path.Combine(r,"takes","take","events.jsonl"),"{}"),
            r=>File.WriteAllText(Path.Combine(r,"takes","take","gaps.jsonl"),""),
            r=>{var p=Path.Combine(r,"takes","take","events.jsonl");File.WriteAllText(p,File.ReadAllText(p).Replace("nativeHostObservation","unknownBasis",StringComparison.Ordinal));},
            r=>File.WriteAllText(Path.Combine(r,"project.json"),ProjectJson.Serialize(ReadFixture().ExpectedProject))
        ];
        foreach(var mutation in mutations)
        {
            var(f,root,scratch)=Prepared();_=Save(f,root,scratch);mutation(root);Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(root,scratch));Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
    }
    [Fact] public void MissingFilesUnsafeLinksCaseAliasesAndScratchConfinementReject()
    {
        foreach(var path in new[]{"project.json","takes/take/manifest.json","takes/take/events.jsonl","takes/take/gaps.jsonl","media/take/left.mp4"})
        {
            var(f,r,s)=Prepared();_=Save(f,r,s);File.Delete(Path.Combine(r,path));Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(r,s));
        }
        foreach(var redirect in new[]{"media/take/left.mp4","takes/take/events.jsonl","media/take/hidden"})
        {
            var(f,r,s)=Prepared();_=Save(f,r,s);var target=Path.Combine(r,redirect);File.Delete(target);Directory.CreateSymbolicLink(target,s);
            Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(r,s));
        }
        var(fixture,root,scratch)=Prepared();_=Save(fixture,root,scratch);
        foreach(var invalid in new[]{root,Path.Combine(root,"media"),Path.GetDirectoryName(root)!})Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(root,invalid));
        var link=Path.Combine(Sandbox(),"alias.motionclicker");Directory.CreateSymbolicLink(link,root);Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(link,scratch));
        Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.Create(root));
        Directory.Move(Path.Combine(root,"media"),Path.Combine(root,"temporary-media"));Directory.Move(Path.Combine(root,"temporary-media"),Path.Combine(root,"MEDIA"));
        Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(root,scratch));
    }
    [Fact] public void ResultMismatchAndRecoveryDiscoveryNeverAdoptOrDeleteSource()
    {
        var(f,root,scratch)=Prepared();var unknown=Path.Combine(root,".capture-constructor-orphan");Directory.CreateDirectory(unknown);var prior=TreeBytes(root);
        Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.SaveInitial(root,new(f.Manifest,f.ExpectedProject.Takes[0],[]),scratch));Assert.Equal(prior,TreeBytes(root));
        var entries=RecordedBundleStore.RecoveryEntries(root);Assert.Contains(unknown,entries);Assert.Contains(Path.Combine(root,"media","take"),entries);Assert.Contains(Path.Combine(root,"takes","take"),entries);
        Assert.ThrowsAny<Exception>(()=>RecordedBundleStore.ColdOpen(root,scratch));_=Save(f,root,scratch);Assert.Equal(new[]{unknown},RecordedBundleStore.RecoveryEntries(root));
    }
    private static string[] TreeBytes(string root)=>Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>Path.GetRelativePath(root,p)+":"+Convert.ToHexString(File.ReadAllBytes(p))).ToArray();
}
