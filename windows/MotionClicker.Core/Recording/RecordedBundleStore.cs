using System.Text;
using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

public enum BundleOperation { WriteProject, FlushProject, PublishProject }
public enum RecordedBundleAvailability { RequiresMonitorMapping, Partial }
public sealed record RecordedBundleMedia(string MonitorSlotId,string File,long MediaTimeZeroUs,long EncodedDurationUs);
public sealed class RecordedBundleSnapshot
{
    private readonly string project,manifest;
    private readonly RecordedBundleMedia[] media;
    public MotionProject Project=>ProjectJson.Deserialize<MotionProject>(project);
    public RecordingManifest Manifest=>RecordingJson.Deserialize<RecordingManifest>(manifest);
    public string ManifestFile { get; }
    public string EventsFile { get; }
    public string GapsFile { get; }
    public RecordedBundleMedia[] Media=>[..media];
    public RecordedBundleAvailability Availability { get; }
    internal RecordedBundleSnapshot(MotionProject project,RecordingManifest manifest,string manifestFile,string eventsFile,string gapsFile,RecordedBundleMedia[] media,RecordedBundleAvailability availability)
    {
        this.project=ProjectJson.Serialize(project);this.manifest=RecordingJson.Serialize(manifest);this.media=[..media];
        ManifestFile=manifestFile;EventsFile=eventsFile;GapsFile=gapsFile;Availability=availability;
    }
}
public static class RecordedBundleStore
{
    private const string Owner="motionclicker-recorded-bundle-v1\n";
    private sealed record Document(int BundleVersion,string ManifestPath,MotionProject Project);
    public static void Create(string root)
    {
        var destination=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));Require(Path.GetExtension(destination)==".motionclicker","Expected .motionclicker directory");
        var parent=new RecordingFiles(Path.GetDirectoryName(destination)??throw new RecordingException("Missing bundle parent"));
        var path=Path.Combine(parent.Root,Path.GetFileName(destination));parent.Check(path);Require(!RecordingFiles.Exists(path),"Bundle already exists");
        Directory.CreateDirectory(path);
        using var handle=new FileStream(Path.Combine(path,".bundle-owner"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        handle.Write(Encoding.UTF8.GetBytes(Owner));handle.Flush(true);
    }
    public static RecordedBundleSnapshot SaveInitial(string root,RecordingResult result,string validationScratch,Action<BundleOperation>? fault=null)
    {
        var files=OpenRoot(root);var projectFile=Path.Combine(files.Root,"project.json");files.Check(projectFile);Require(!RecordingFiles.Exists(projectFile),"Initial project already exists");
        var snapshot=Inspect(files,result.Manifest.TakeId,validationScratch);
        var expected=new RecordingResult(snapshot.Manifest,snapshot.Project.Takes[0],snapshot.Project.Events);
        Require(RecordingJson.Serialize(result)==RecordingJson.Serialize(expected),"Recording result differs from published evidence");
        var bytes=Encoding.UTF8.GetBytes(RecordingJson.Serialize(new Document(1,$"takes/{result.Manifest.TakeId}/manifest.json",snapshot.Project)));
        var stage=Path.Combine(files.Root,".project-"+Guid.NewGuid());files.Check(stage);
        using(var handle=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            fault?.Invoke(BundleOperation.WriteProject);handle.Write(bytes);fault?.Invoke(BundleOperation.FlushProject);handle.Flush(true);
        }
        fault?.Invoke(BundleOperation.PublishProject);files.Check(stage);files.Check(projectFile);File.Move(stage,projectFile,false);
        return snapshot;
    }
    public static RecordedBundleSnapshot ColdOpen(string root,string validationScratch)
    {
        var files=OpenRoot(root);var document=RecordingJson.Deserialize<Document>(ReadFile(Path.Combine(files.Root,"project.json"),files));
        Require(document.BundleVersion==1&&document.Project.Takes.Length==1,"Unsupported recorded bundle");
        var takeId=document.Project.Takes[0].Id;RecordingFiles.Id(takeId);Require(document.ManifestPath==$"takes/{takeId}/manifest.json","Invalid source manifest reference");
        _=RunPlanCompiler.Compile(document.Project);var snapshot=Inspect(files,takeId,validationScratch);
        Require(ProjectJson.Serialize(document.Project)==ProjectJson.Serialize(snapshot.Project),"Project differs from initial source projection");return snapshot;
    }
    public static string[] RecoveryEntries(string root)
    {
        var files=OpenRoot(root);var result=new List<string>();
        foreach(var path in Directory.EnumerateFileSystemEntries(files.Root))
        {
            var name=Path.GetFileName(path);if(name.StartsWith(".capture-",StringComparison.Ordinal)||name.StartsWith(".project-",StringComparison.Ordinal)){files.Check(path);result.Add(path);}
        }
        var projectFile=Path.Combine(files.Root,"project.json");files.Check(projectFile);
        if(!RecordingFiles.Exists(projectFile))foreach(var name in new[]{"media","takes"})
        {
            var directory=Path.Combine(files.Root,name);files.Check(directory);
            if(RecordingFiles.Exists(directory))
            {
                Require(File.GetAttributes(directory).HasFlag(FileAttributes.Directory),"Invalid recovery directory");
                foreach(var entry in Directory.EnumerateFileSystemEntries(directory)){files.Check(entry);result.Add(entry);}
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    private static RecordingFiles OpenRoot(string root)
    {
        Require(Path.GetExtension(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))==".motionclicker","Expected .motionclicker directory");
        var files=new RecordingFiles(root);Require(ReadFile(Path.Combine(files.Root,".bundle-owner"),files)==Owner,"Unowned recorded bundle");return files;
    }
    private static string ReadFile(string path,RecordingFiles files)
    {
        files.Check(path);Require(!File.GetAttributes(path).HasFlag(FileAttributes.Directory),"Expected regular file");return File.ReadAllText(path,new UTF8Encoding(false,true));
    }
    private static RecordedBundleSnapshot Inspect(RecordingFiles files,string takeId,string validationScratch)
    {
        RecordingFiles.Id(takeId);var manifestFile=Path.Combine(files.Root,"takes",takeId,"manifest.json");var m=RecordingJson.Deserialize<RecordingManifest>(ReadFile(manifestFile,files));
        foreach(var target in m.Targets)RecordingFiles.Id(target.Id);
        files.CheckTree(Path.Combine(files.Root,"takes",takeId));files.CheckTree(Path.Combine(files.Root,"media",takeId));
        var eventsFile=Path.Combine(files.Root,"takes",takeId,"events.jsonl");var gapsFile=Path.Combine(files.Root,"takes",takeId,"gaps.jsonl");
        foreach(var path in new[]{eventsFile,gapsFile}){files.Check(path);Require(!File.GetAttributes(path).HasFlag(FileAttributes.Directory),"Expected journal file");}
        void Read(Action<RawPointerRecord> visit)=>RecordingJournalReader.Read(eventsFile,files.Check,visit);
        void ReadGaps(Action<JournalGapRecord> visit)=>RecordingJournalReader.Read(gapsFile,files.Check,visit);
        WithScratch(files.Root,validationScratch,scratch=>RecordingEvidenceValidation.Validate(m,takeId,m.RawEventCount,scratch,id=>Path.Combine(files.Root,"media",takeId,id+".mp4"),files.Check,Read,ReadGaps));
        var events=new List<RecordedAction>();var sourceIDs=new List<string>();Read(r=>{if(r.Action is not null)sourceIDs.Add(r.Action.Id);if(r.Action is not null&&!m.IncompleteGroupIds.Contains(r.AtomicGroupId,StringComparer.Ordinal))events.Add(r.Action);});
        var project=MakeProject(m,events.ToArray(),sourceIDs);
        var media=m.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded).Select(s=>new RecordedBundleMedia(s.MonitorSlotId,Path.Combine(files.Root,s.RelativePath.Replace('/',Path.DirectorySeparatorChar)),s.MediaTimeZeroUs??throw new RecordingException("Missing media offset"),s.EncodedDurationUs)).ToArray();
        return new(project,m,manifestFile,eventsFile,gapsFile,media,m.State==RecordingState.Completed?RecordedBundleAvailability.RequiresMonitorMapping:RecordedBundleAvailability.Partial);
    }
    private static void WithScratch(string bundle,string root,Action<string> body)
    {
        var files=new RecordingFiles(root);
        static bool Contains(string parent,string child){var relative=Path.GetRelativePath(parent,child);return relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&!Path.IsPathRooted(relative);}
        Require(!Contains(bundle,files.Root)&&!Contains(files.Root,bundle),"Validation scratch must be external to bundle");
        var scratch=Path.Combine(files.Root,".bundle-validation-"+Guid.NewGuid());files.Check(scratch);Require(!RecordingFiles.Exists(scratch),"Scratch collision");Directory.CreateDirectory(scratch);
        using(var owner=new FileStream(Path.Combine(scratch,".owner"),FileMode.CreateNew,FileAccess.Write,FileShare.None)){}
        try{body(scratch);}finally{Directory.Delete(scratch,true);}
    }
    private static MotionProject MakeProject(RecordingManifest m,RecordedAction[] events,List<string> sourceIDs)
    {
        var ids=new HashSet<string>(m.Targets.Select(t=>t.Id).Concat(sourceIDs).Append(m.TakeId),StringComparer.Ordinal);
        string Allocate(string stem){var value=stem;var suffix=0;while(!ids.Add(value)){suffix++;value=stem+"-"+suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);}return value;}
        var projectId=Allocate("bundle-project");var take=new Take(m.TakeId,m.DurationUs,m.Targets.Select(t=>t.Id).ToArray(),m.Streams.Where(s=>s.WriterStatus==WriterStatus.Succeeded).Select(s=>new TakeMedia(s.MonitorSlotId,s.RelativePath)).ToArray());
        var tracks=new Track[m.Targets.Length];var clips=new TimelineClip[m.Targets.Length];
        for(var i=0;i<m.Targets.Length;i++)
        {
            var slot=m.Targets[i];var index=i.ToString(System.Globalization.CultureInfo.InvariantCulture);var trackId=Allocate("bundle-track-"+index);var clipId=Allocate("bundle-clip-"+index);
            tracks[i]=new(trackId,slot.Name,slot.Id,i);clips[i]=new(clipId,trackId,m.TakeId,0,m.DurationUs,0,1,"bundle-link");
        }
        var project=new MotionProject(1,projectId,0,m.Targets,[take],tracks,clips,events);_=RunPlanCompiler.Compile(project);return project;
    }
}
