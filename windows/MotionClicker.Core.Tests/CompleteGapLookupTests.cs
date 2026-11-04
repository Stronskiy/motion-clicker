using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Windows.RecordingUI;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class CompleteGapLookupTests : IDisposable {
    private readonly string directory=Path.Combine(Path.GetTempPath(),"motion-gap-ui-"+Guid.NewGuid());
    public CompleteGapLookupTests()=>Directory.CreateDirectory(directory);
    public void Dispose()=>Directory.Delete(directory,true);
    [Fact] public async Task JournalOnly65thGapMonitorSwitchLeadingTrailingAndBytesRemainExact() {
        var path=Path.Combine(directory,"gaps.jsonl");var gaps=Enumerable.Range(0,65).Select(i=>new RecordingGap(100+i*20,110+i*20,GapKind.Dropped)).ToArray();
        var other=new RecordingGap(1500,1510,GapKind.SourceFailure);
        File.WriteAllText(path,string.Concat(gaps.Select(g=>RecordingJson.Serialize(new JournalGapRecord("left",g))+"\n"))+RecordingJson.Serialize(new JournalGapRecord("right",other))+"\n");
        var streams=new[]{Stream("left",gaps),Stream("right",[other])};var before=File.ReadAllBytes(path);var lookup=new CompleteGapLookup(path,2000,streams);
        Assert.Equal(GapKind.Dropped,(await lookup.WindowAsync("left",1385)).Kind);
        var clear=await lookup.WindowAsync("left",1379);Assert.Null(clear.Kind);Assert.Equal(1380,clear.EndUs);
        Assert.Equal(100,(await lookup.WindowAsync("left",0)).EndUs);
        Assert.Null((await lookup.WindowAsync("right",1385)).Kind);Assert.Equal(GapKind.SourceFailure,(await lookup.WindowAsync("right",1501)).Kind);
        Assert.False(SourcePlayback.TryRange(new("left","unused",50,1400),new("left",0,2000,null),0,out _,out _));
        Assert.False(SourcePlayback.TryRange(new("left","unused",50,1400),new("left",0,2000,null),1450,out _,out _));
        Assert.True(SourcePlayback.TryRange(new("left","unused",50,1400),clear,1379,out var pts,out var end));Assert.Equal(13290,pts.Ticks);Assert.Equal(13300,end.Ticks);
        Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Fact] public async Task TruncatedMissingAndInvalidCompleteHistoryFailClosed() {
        var path=Path.Combine(directory,"gaps.jsonl");var gap=new RecordingGap(10,20,GapKind.Dropped);var streams=new[]{Stream("left",[gap])};
        foreach(var bytes in new[]{"",RecordingJson.Serialize(new JournalGapRecord("left",gap)),RecordingJson.Serialize(new JournalGapRecord("foreign",gap))+"\n"}) {
            File.WriteAllText(path,bytes);await Assert.ThrowsAnyAsync<Exception>(()=>new CompleteGapLookup(path,100,streams).WindowAsync("left",5));
        }
        File.Delete(path);await Assert.ThrowsAnyAsync<Exception>(()=>new CompleteGapLookup(path,100,streams).WindowAsync("left",5));
        Assert.False(SourcePlayback.TryRange(new("left","unused",long.MaxValue-1,1),new("left",0,long.MaxValue,null),long.MaxValue-1,out _,out _));
    }
    private static StreamManifest Stream(string id,RecordingGap[] gaps)=>new(id,$"media/take/{id}.mp4",new(0,0,1_000_000),0,2000,1,gaps.Take(64).ToArray(),WriterStatus.Succeeded,gaps.Length,"takes/take/gaps.jsonl");
}
