using System.Runtime.CompilerServices;
using static MotionClicker.Core.Recording.RecordingCheck;
[assembly: InternalsVisibleTo("MotionClicker.Core.Tests")]
namespace MotionClicker.Core.Recording;
internal sealed record RecordingValidationStatistics(long Records,int MaximumBufferedIDs);
internal static class RecordingJournalValidation
{
    internal static RecordingValidationStatistics Validate(RecordingManifest m,string scratchRoot,Action<Action<RawPointerRecord>> read)
    {
        var events=new List<RecordedAction>();long actual=0;var incomplete=new HashSet<string>(m.IncompleteGroupIds,StringComparer.Ordinal);
        read(r=>{
            Require(r.Sequence==actual&&r.TimeUs>=0&&r.TimeUs<m.DurationUs,"Invalid raw journal ordering/range");actual++;
            if(r.Action is RecordedAction a)
            {
                Require(a.TimeUs==r.TimeUs&&a.Order==r.Sequence&&a.AtomicGroupId==r.AtomicGroupId,"Raw/projection mismatch");
                if(a.AtomicGroupId is null||!incomplete.Contains(a.AtomicGroupId))events.Add(a);
            }
        });
        Require(actual==m.RawEventCount,"Journal count mismatch");
        var take=new Take(m.TakeId,m.DurationUs,m.Targets.Select(t=>t.Id).ToArray(),m.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded).Select(s=>new TakeMedia(s.MonitorSlotId,s.RelativePath)).ToArray());
        RunPlanCompiler.Compile(new(1,"validation-"+Guid.NewGuid(),0,m.Targets,[take],[],[],events.ToArray()));
        Require(m.State!=RecordingState.Completed||incomplete.Count==0,"Incomplete group labeled completed");
        return new(actual,events.Count);
    }
}
