using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

internal static class RecordingEvidenceValidation
{
    internal static void Validate(RecordingManifest m,string takeId,long rawCount,string scratchRoot,Func<string,string> mediaFile,Action<string> check,Action<Action<RawPointerRecord>> read,Action<Action<JournalGapRecord>> readGaps)
    {
        Require(m.ManifestVersion==1&&m.TakeId==takeId&&m.State is RecordingState.Completed or RecordingState.Interrupted or RecordingState.Failed&&m.DurationUs>0&&m.RawEventCount==rawCount,"Invalid terminal manifest");
        Require(m.Streams.Length==m.Targets.Length&&m.Streams.Length>0,"Missing manifest streams");
        var seen=new HashSet<string>(StringComparer.Ordinal);Require(m.IncompleteGroupIds.Length<=1,"Too many incomplete pointer groups");
        foreach(var s in m.Streams)
        {
            Require(seen.Add(s.MonitorSlotId)&&m.Targets.Any(t=>t.Id==s.MonitorSlotId),"Invalid stream target");
            Require(s.RelativePath==$"media/{takeId}/{s.MonitorSlotId}.mp4","Unsafe stream media path");
            _=new MediaClockMapper(s.Clock.SourceAnchorTicks,s.Clock.HostAnchorUs,s.Clock.TicksPerSecond,m.EpochHostUs);
            Require(s.FrameCount>=0&&s.EncodedDurationUs>=0,"Invalid media counters");
            if(s.MediaTimeZeroUs is long zero)Require(zero>=0&&(s.WriterStatus==WriterStatus.Failed||checked(zero+s.EncodedDurationUs)<=m.DurationUs),"Media beyond take");
            Require(s.GapCount>=0&&s.Gaps.Length==Math.Min(s.GapCount,64)&&s.GapJournalPath==$"takes/{takeId}/gaps.jsonl","Invalid gap preview metadata");
            foreach(var gap in s.Gaps)Require(gap.StartUs>=0&&gap.EndUs>=gap.StartUs&&gap.EndUs<=m.DurationUs&&Enum.IsDefined(gap.Kind),"Invalid gap range");
            Require(Enum.IsDefined(s.WriterStatus),"Invalid writer status");
            if(s.WriterStatus==WriterStatus.Succeeded)
            {
                Require(s.FrameCount>0&&s.EncodedDurationUs>0&&s.MediaTimeZeroUs is not null,"Empty successful media");
                var file=mediaFile(s.MonitorSlotId);check(file);
                Require(File.Exists(file)&&!File.GetAttributes(file).HasFlag(FileAttributes.Directory)&&new FileInfo(file).Length>0,"Missing or empty successful media");
            }
            else Require(m.State!=RecordingState.Completed,"Failed writer labeled completed");
        }
        var counts=m.Streams.ToDictionary(s=>s.MonitorSlotId,_=>0L,StringComparer.Ordinal);var streamMap=m.Streams.ToDictionary(s=>s.MonitorSlotId,StringComparer.Ordinal);
        readGaps(r=>{
            Require(streamMap.ContainsKey(r.MonitorSlotId),"Unknown gap source");var stream=streamMap[r.MonitorSlotId];var count=counts[r.MonitorSlotId];var gap=r.Gap;
            Require(gap.StartUs>=0&&gap.EndUs>=gap.StartUs&&gap.EndUs<=m.DurationUs,"Invalid journal gap range");
            if(count<64)Require(count<stream.Gaps.Length&&stream.Gaps[(int)count]==gap,"Gap preview mismatch");counts[r.MonitorSlotId]=checked(count+1);
        });
        foreach(var stream in m.Streams)Require(counts[stream.MonitorSlotId]==stream.GapCount,"Gap journal count mismatch");
        _=RecordingJournalValidation.Validate(m,scratchRoot,read);
    }
}
