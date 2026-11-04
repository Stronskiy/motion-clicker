using MotionClicker.Core.Recording;
using Xunit;
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
    private static string[] TreeBytes(string root)=>Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>Path.GetRelativePath(root,p)+":"+Convert.ToHexString(File.ReadAllBytes(p))).ToArray();
}
