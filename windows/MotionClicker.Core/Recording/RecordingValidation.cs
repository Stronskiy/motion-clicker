using System.Runtime.CompilerServices;
using System.Text;
using static MotionClicker.Core.Recording.RecordingCheck;
[assembly: InternalsVisibleTo("MotionClicker.Core.Tests")]
namespace MotionClicker.Core.Recording;
internal sealed record RecordingValidationStatistics(long Records,int MaximumBufferedIDs);

/// <summary>Mandatory publication retains no historical actions. Only explicit Project() does.</summary>
internal static class RecordingJournalValidation
{
    internal static RecordingValidationStatistics Validate(RecordingManifest m,string scratchRoot,Action<Action<RawPointerRecord>> read)
    {
        var take=new Take(m.TakeId,m.DurationUs,m.Targets.Select(t=>t.Id).ToArray(),m.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded).Select(s=>new TakeMedia(s.MonitorSlotId,s.RelativePath)).ToArray());
        // This validates only the monitor/take snapshot, never recorded events.
        RunPlanCompiler.Compile(new(1,"validation-"+Guid.NewGuid(),0,m.Targets,[take],[],[],[]));
        Require(m.IncompleteGroupIds.Length<=1&&(m.State!=RecordingState.Completed||m.IncompleteGroupIds.Length==0),"Invalid incomplete group summary");
        var incomplete=m.IncompleteGroupIds.FirstOrDefault();if(incomplete is not null)Id(incomplete);
        var slots=m.Targets.ToDictionary(t=>t.Id,StringComparer.Ordinal);
        using var ids=new RecordingIdentityIndex(scratchRoot);ids.Add("entity:"+m.TakeId);foreach(var slot in m.Targets)ids.Add("entity:"+slot.Id);
        long actual=0,lastTime=-1;string? held=null;MouseButton? button=null;var crossed=false;
        read(r=>{
            Require(!crossed&&r.Sequence==actual&&r.TimeUs>=0&&r.TimeUs>=lastTime&&r.TimeUs<m.DurationUs,"Invalid raw journal ordering/range");
            Require(!r.Event.IsOwnInjected&&!r.Event.IsControl&&double.IsFinite(r.Event.NativePosition.X)&&double.IsFinite(r.Event.NativePosition.Y)&&checked(r.Event.HostTimeUs-m.EpochHostUs)==r.TimeUs,"Invalid raw capture timestamp/origin");
            lastTime=r.TimeUs;actual=checked(actual+1);
            if(r.Action is not RecordedAction a){Require(held is not null&&r.AtomicGroupId==held&&incomplete==held,"Unmapped event outside incomplete hold");crossed=true;return;}
            Id(a.Id);ids.Add("entity:"+a.Id);
            Require(a.TakeId==m.TakeId&&a.TimeUs==r.TimeUs&&a.Order==r.Sequence&&a.AtomicGroupId==r.AtomicGroupId&&a.Kind==r.Event.Kind&&a.Button==r.Event.Button&&a.DeltaX==r.Event.DeltaX&&a.DeltaY==r.Event.DeltaY,"Raw/projection mismatch");
            Require(slots.ContainsKey(a.MonitorSlotId),"Unknown action monitor");var slot=slots[a.MonitorSlotId];
            Require(double.IsFinite(a.X)&&double.IsFinite(a.Y)&&a.X>=0&&a.Y>=0&&a.X<slot.Geometry.Size.Width&&a.Y<slot.Geometry.Size.Height,"Invalid recorded coordinate");
            if(a.AtomicGroupId is string group)Id(group);
            switch(a.Kind)
            {
                case ActionKind.ButtonDown:
                    Require(held is null&&a.Button is not null&&Enum.IsDefined(a.Button.Value)&&a.AtomicGroupId is not null&&a.DeltaX is null&&a.DeltaY is null,"Invalid down or overlapping hold");
                    held=a.AtomicGroupId;button=a.Button;ids.Add("group:"+held);break;
                case ActionKind.ButtonUp:
                    Require(held is not null&&a.AtomicGroupId==held&&a.Button==button&&a.DeltaX is null&&a.DeltaY is null,"Unmatched up");held=null;button=null;break;
                case ActionKind.Move:Require(a.AtomicGroupId==held&&a.Button is null&&a.DeltaX is null&&a.DeltaY is null,"Invalid move ownership");break;
                case ActionKind.Scroll:Require(held is null&&a.AtomicGroupId is null&&a.Button is null&&a.DeltaX is double dx&&double.IsFinite(dx)&&a.DeltaY is double dy&&double.IsFinite(dy),"Invalid scroll ownership");break;
                default:throw new RecordingException("Unknown action kind");
            }
        });
        Require(actual==m.RawEventCount&&incomplete==held,"Journal count or incomplete ownership mismatch");ids.Finish();return new(actual,ids.MaximumBufferedIDs);
    }
    private static void Id(string id)
    {
        static bool Alnum(char c)=>c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        Require(id.Length is >0 and <=128&&Alnum(id[0])&&id.All(c=>Alnum(c)||c is '.' or '_' or '-'),"Invalid source ID");
    }
}

/// <summary>4096 keys per sorted run; two cursors per merge; integer counters instead of a run list.</summary>
internal sealed class RecordingIdentityIndex : IDisposable
{
    private readonly string directory;
    private readonly List<string> keys=new(4096);
    private long runs;
    public int MaximumBufferedIDs { get; private set; }
    public RecordingIdentityIndex(string root)
    {
        directory=Path.Combine(root,".validation-"+Guid.NewGuid());Require(!Directory.Exists(directory),"Validation scratch collision");Directory.CreateDirectory(directory);
        using var owner=new FileStream(Path.Combine(directory,".owner"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
    }
    public void Dispose(){try{Directory.Delete(directory,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public void Add(string key){keys.Add(key);MaximumBufferedIDs=Math.Max(MaximumBufferedIDs,keys.Count);if(keys.Count==4096)Spill();}
    private string RunPath(int pass,long run)=>Path.Combine(directory,$"p{pass}-r{run}");
    private static StreamWriter Output(string path)=>new(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None),new UTF8Encoding(false),65536);
    private void Spill()
    {
        if(keys.Count==0)return;keys.Sort(StringComparer.Ordinal);
        for(var i=1;i<keys.Count;i++)Require(keys[i]!=keys[i-1],"Duplicate source identity");
        using(var output=Output(RunPath(0,runs)))foreach(var key in keys)output.WriteLine(key);
        runs=checked(runs+1);keys.Clear();
    }
    public void Finish()
    {
        Spill();var pass=0;
        while(runs>1)
        {
            long next=0;
            for(long i=0;i<runs;i+=2)
            {
                var first=RunPath(pass,i);var output=RunPath(pass+1,next);
                if(i+1==runs)File.Move(first,output);
                else{var second=RunPath(pass,i+1);Merge(first,second,output);File.Delete(first);File.Delete(second);}
                next++;
            }
            runs=next;pass++;
        }
    }
    private static void Merge(string first,string second,string destination)
    {
        using var left=new StreamReader(first,new UTF8Encoding(false,true));using var right=new StreamReader(second,new UTF8Encoding(false,true));using var output=Output(destination);
        var a=Next(left);var b=Next(right);
        while(a is not null||b is not null)
        {
            if(a is not null&&b is not null)
            {
                var comparison=StringComparer.Ordinal.Compare(a,b);Require(comparison!=0,"Duplicate source identity");
                if(comparison<0){output.WriteLine(a);a=Next(left);}else{output.WriteLine(b);b=Next(right);}
            }
            else if(a is not null){output.WriteLine(a);a=Next(left);}else{output.WriteLine(b!);b=Next(right);}
        }
    }
    private static string? Next(StreamReader reader){var line=reader.ReadLine();Require(line is null||line.Length<=135,"Invalid identity spool line");return line;}
}
