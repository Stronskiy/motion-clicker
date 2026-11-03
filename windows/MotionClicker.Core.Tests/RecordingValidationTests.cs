using MotionClicker.Core.Recording;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class RecordingValidationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"recording-validation-"+Guid.NewGuid());
    public RecordingValidationTests(){Directory.CreateDirectory(root);}
    public void Dispose(){Directory.Delete(root,true);}
    private static RecordingManifest Manifest(int count)=>new(1,"take",RecordingState.Interrupted,0,count+1,[RecordingSessionTests.Targets()[0].Slot],[],count,[],[]);
    private static RawPointerRecord Raw(int i,string? id=null)=>new(i,new(i,new(1,1),ActionKind.Move),i,new(id??$"event-{i}","take","left",i,i,ActionKind.Move,1,1));
    [Fact] public void LargeJournalUsesBoundedValidationBuffersAndFindsDistantDuplicate()
    {
        const int total=30000;
        var stats=RecordingJournalValidation.Validate(Manifest(total),root,visit=>{for(var i=0;i<total;i++)visit(Raw(i));});
        Assert.Equal(total,stats.Records);Assert.True(stats.MaximumBufferedIDs<=4096,$"Retained {stats.MaximumBufferedIDs} IDs");
        Assert.ThrowsAny<Exception>(()=>RecordingJournalValidation.Validate(Manifest(total),root,visit=>{for(var i=0;i<total;i++)visit(Raw(i,i==total-1?"event-0":null));}));
    }
    [Fact] public void StreamingValidatorReleasesConsumedActionsBeforeReadingWholeJournal()
    {
        var weak=new List<WeakReference<RecordedAction>>();
        _=RecordingJournalValidation.Validate(Manifest(30000),root,visit=>{
            for(var i=0;i<30000;i++)
            {
                var record=Raw(i);if(i<1000)weak.Add(new(record.Action!));visit(record);
                if(i==10000){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert.True(weak.Count(w=>w.TryGetTarget(out _))<=8,"Validator retained historical action objects");}
            }
        });
    }
}
