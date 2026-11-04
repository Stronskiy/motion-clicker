using MotionClicker.Core.Recording;
using System.Text.Json.Nodes;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class RecordingTests
{
    private sealed record Timing(long SourceAnchorTicks, long HostAnchorUs, long TicksPerSecond, long EpochHostUs, long SourceTicks, long ExpectedUs);
    [Fact] public void NativeAnchorsLargeIntegersAndFloorFromSharedFixtures()
    {
        var cases = RecordingJson.Deserialize<Timing[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","timing.json")));
        foreach(var c in cases) Assert.Equal(c.ExpectedUs,new MediaClockMapper(c.SourceAnchorTicks,c.HostAnchorUs,c.TicksPerSecond,c.EpochHostUs).Map(c.SourceTicks));
    }
    [Fact] public void NewAndLegacyPointerMetadataRoundTripExactlyFromSharedFixtures()
    {
        var cases=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","pointer-timing.json")))!.AsArray();
        foreach(var c in cases)
        {
            var expected=c!["event"]!;
            var actual=JsonNode.Parse(RecordingJson.Serialize(RecordingJson.Deserialize<CapturedPointerEvent>(expected.ToJsonString())));
            Assert.True(JsonNode.DeepEquals(actual,expected),c["name"]!.ToString());
        }
    }
    [Fact] public void MalformedPointerMetadataRejectedFromSharedFixtures()
    {
        var cases=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","pointer-timing-invalid.json")))!.AsArray();
        foreach(var c in cases) Assert.ThrowsAny<Exception>(()=>RecordingJson.Deserialize<CapturedPointerEvent>(c!.ToJsonString()));
    }
    [Fact] public void NewAndLegacyTakeJournalFixturesPreserveCanonicalTimesAndValidate()
    {
        var cases=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","recording","pointer-timing-takes.json")))!.AsArray();
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());Directory.CreateDirectory(root);
        try { foreach(var c in cases) {
            var m=RecordingJson.Deserialize<RecordingManifest>(c!["manifest"]!.ToJsonString());
            var records=RecordingJson.Deserialize<RawPointerRecord[]>(c["records"]!.ToJsonString());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(RecordingJson.Serialize(m)),c["manifest"]));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(RecordingJson.Serialize(records)),c["records"]));
            Assert.Equal(new long[]{10},records.Select(r=>r.TimeUs));Assert.Equal(new long?[]{10},records.Select(r=>r.Action?.TimeUs));
            Assert.Equal(1,RecordingJournalValidation.Validate(m,root,visit=>{foreach(var r in records)visit(r);}).Records);
            var wrong=m with { PointerTimingBasis=m.PointerTimingBasis is null ? PointerTimingBasis.NativeHostObservation:null };
            Assert.ThrowsAny<Exception>(()=>RecordingJournalValidation.Validate(wrong,root,visit=>{foreach(var r in records)visit(r);}));
        }} finally { Directory.Delete(root,true); }
    }
    [Fact] public void SessionRejectsMissingMetadataAndMixedObservationClocks()
    {
        var timing=new PointerTiming(PointerTimingBasis.NativeHostObservation,PointerHostClock.MachAbsolute,PointerPacketClock.CgEventTimestamp,PointerPacketUnits.Nanoseconds,"18446744073709551615",0);
        var windows=new PointerTiming(PointerTimingBasis.NativeHostObservation,PointerHostClock.QueryPerformanceCounter,PointerPacketClock.WindowsLowLevelMouseTime,PointerPacketUnits.Milliseconds,"0",0);
        var missing=RecordingSessionTests.Ready(new MemoryJournal(),basis:PointerTimingBasis.NativeHostObservation);
        Assert.ThrowsAny<Exception>(()=>missing.Receive(new(1000010,new(1,1),ActionKind.Move)));
        var mixed=RecordingSessionTests.Ready(new MemoryJournal(),basis:PointerTimingBasis.NativeHostObservation);
        mixed.Receive(new(1000010,new(1,1),ActionKind.Move,Timing:timing));
        Assert.ThrowsAny<Exception>(()=>mixed.Receive(new(1000020,new(1,1),ActionKind.Move,Timing:windows)));
    }
    [Fact] public void UnknownManifestTimingBasisRejected()
    {
        const string json="""{"manifestVersion":1,"takeId":"take","state":"completed","epochHostUs":0,"durationUs":1,"targets":[],"streams":[],"rawEventCount":0,"incompleteGroupIds":[],"warnings":[],"pointerTimingBasis":"futureUnknown"}""";
        Assert.ThrowsAny<Exception>(()=>RecordingJson.Deserialize<RecordingManifest>(json));
    }
    [Fact] public void InvalidScaleAndOverflowAreVisible()
    {
        foreach(long scale in new long[]{0,-1,2147483648}) Assert.ThrowsAny<Exception>(()=>new MediaClockMapper(0,0,scale,0));
        Assert.ThrowsAny<Exception>(()=>new MediaClockMapper(0,0,1,0).Map(long.MaxValue));
        Assert.ThrowsAny<Exception>(()=>new MediaClockMapper(long.MinValue,0,1000000,0).Map(long.MaxValue));
    }
}
