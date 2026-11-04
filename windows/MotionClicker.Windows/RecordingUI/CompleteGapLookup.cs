using System.Text;
using MotionClicker.Core.Recording;
namespace MotionClicker.Windows.RecordingUI;
public sealed record SourceGapWindow(string MonitorSlotId,long StartUs,long EndUs,GapKind? Kind) {
    public bool Contains(string monitor,long sourceUs)=>MonitorSlotId==monitor&&sourceUs>=StartUs&&sourceUs<EndUs;
}
/// <summary>Reads complete history off UI with64KiB chunks, one bounded record and one interval; no gap array/index allocation.</summary>
public sealed class CompleteGapLookup(string path,long durationUs,StreamManifest[] streams) {
    public Task<SourceGapWindow> WindowAsync(string monitor,long sourceUs,CancellationToken cancellation=default)=>Task.Run(()=>Read(monitor,sourceUs,cancellation),cancellation);
    private SourceGapWindow Read(string monitor,long sourceUs,CancellationToken token) {
        var expected=streams.ToDictionary(s=>s.MonitorSlotId,StringComparer.Ordinal);
        if(!expected.ContainsKey(monitor)||sourceUs<0||sourceUs>=durationUs)throw new RecordingException("Source time or display is outside the saved take");
        var counts=expected.Keys.ToDictionary(k=>k,_=>0L,StringComparer.Ordinal);long start=0,end=durationUs;RecordingGap? covering=null;
        var attributes=File.GetAttributes(path);
        if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new RecordingException("Gap history must be an ordinary read-only file");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
        using var reader=new StreamReader(input,new UTF8Encoding(false,true));var chunk=new char[65536];var line=new StringBuilder();int length;
        void Visit() {
            token.ThrowIfCancellationRequested();
            if(line.Length==0)throw new RecordingException("Empty gap journal record");
            var record=RecordingJson.Deserialize<JournalGapRecord>(line.ToString());line.Clear();
            if(!expected.TryGetValue(record.MonitorSlotId,out var stream))throw new RecordingException("Unknown monitor in complete gap history");
            var gap=record.Gap;var count=counts[record.MonitorSlotId];
            if(gap.StartUs<0||gap.EndUs<gap.StartUs||gap.EndUs>durationUs||count>=stream.GapCount)throw new RecordingException("Invalid complete gap history range or count");
            if(count<Math.Min(64,stream.GapCount)&&(count>=stream.Gaps.Length||stream.Gaps[(int)count]!=gap))throw new RecordingException("Gap preview differs from complete history");
            counts[record.MonitorSlotId]=checked(count+1);
            if(record.MonitorSlotId!=monitor||gap.StartUs==gap.EndUs)return;
            if(sourceUs>=gap.StartUs&&sourceUs<gap.EndUs)covering??=gap;
            else if(gap.EndUs<=sourceUs)start=Math.Max(start,gap.EndUs);
            else if(gap.StartUs>sourceUs)end=Math.Min(end,gap.StartUs);
        }
        while((length=reader.Read(chunk,0,chunk.Length))>0) {
            token.ThrowIfCancellationRequested();
            for(int i=0;i<length;i++) {
                if(chunk[i]=='\n')Visit();
                else {line.Append(chunk[i]);if(line.Length>1048576)throw new RecordingException("Oversized gap journal record");}
            }
        }
        if(line.Length!=0)throw new RecordingException("Truncated gap journal record");
        if(expected.Any(p=>counts[p.Key]!=p.Value.GapCount||p.Value.Gaps.Length!=Math.Min(64,p.Value.GapCount)))throw new RecordingException("Complete gap journal count mismatch");
        return covering is null?new(monitor,start,end,null):new(monitor,covering.StartUs,covering.EndUs,covering.Kind);
    }
}
public static class SourcePlayback {
    /// <summary>Only a validated clear source interval grants native playback. Conversion never overflows ticks.</summary>
    public static bool TryRange(RecordedBundleMedia media,SourceGapWindow? window,long sourceUs,out TimeSpan position,out TimeSpan end) {
        position=end=default;
        try {
            if(media.MediaTimeZeroUs<0||media.MediaTimeZeroUs>long.MaxValue/10||media.EncodedDurationUs<=0||window is null||window.Kind is not null||!window.Contains(media.MonitorSlotId,sourceUs))return false;
            var mediaEnd=checked(media.MediaTimeZeroUs+media.EncodedDurationUs);
            if(sourceUs<media.MediaTimeZeroUs||sourceUs>=mediaEnd)return false;
            var pts=sourceUs-media.MediaTimeZeroUs;var endpoint=Math.Min(window.EndUs,mediaEnd)-media.MediaTimeZeroUs;
            if(endpoint<=pts)return false;
            position=TimeSpan.FromTicks(checked(pts*10));end=TimeSpan.FromTicks(checked(endpoint*10));return true;
        } catch(OverflowException){return false;}
    }
}
