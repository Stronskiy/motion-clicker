using System.Text;
using System.Text.RegularExpressions;
using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

/// <summary>Capture-only writer. No project writer, overwrite, or automatic recovery deletion.</summary>
public sealed class RecordingStore : IEventJournal, IDisposable
{
    private readonly RecordingFiles files;
    private readonly string root,staging,media,metadata,takeId;
    private readonly Action<StoreOperation> fault;
    private FileStream? handle,gapHandle;
    private bool poisoned,closed,mediaPublished,manifestPublished;
    private readonly HashSet<string> reserved=new(StringComparer.OrdinalIgnoreCase);
    private long rawCount;
    private string? frozenManifest;
    public StoreRecovery Recovery=>new(staging,media,Path.Combine(metadata,"manifest.json"),mediaPublished,manifestPublished);
    public RecordingStore(string root,string takeId,Action<StoreOperation>? fault=null)
    {
        RecordingFiles.Id(takeId);files=new(root);this.root=files.Root;this.takeId=takeId;this.fault=fault??(_=>{});
        staging=Path.Combine(this.root,".capture-"+Guid.NewGuid());media=Path.Combine(this.root,"media",takeId);metadata=Path.Combine(this.root,"takes",takeId);
        Check(media);Check(metadata);Require(!RecordingFiles.Exists(media)&&!RecordingFiles.Exists(metadata),"Take already exists");
        EnsureDirectory(Path.Combine(this.root,"media"));EnsureDirectory(Path.Combine(this.root,"takes"));
        Require(!RecordingFiles.Exists(staging),"Staging collision");Directory.CreateDirectory(staging);
        // Exclusive ownership marker prevents two constructors claiming the same directory.
        using(var owner=new FileStream(Path.Combine(staging,".owner"),FileMode.CreateNew,FileAccess.Write,FileShare.None))owner.Flush(true);
        Directory.CreateDirectory(Path.Combine(staging,"media"));Directory.CreateDirectory(Path.Combine(staging,"metadata"));
        handle=new(JournalPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        try{gapHandle=new(GapPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);}catch{handle.Dispose();throw;}
    }
    private void Check(string path)=>files.Check(path);
    private void CheckTree(string path)=>files.CheckTree(path);
    private void EnsureDirectory(string path)=>files.EnsureDirectory(path);
    private string JournalPath=>Path.Combine(manifestPublished?metadata:Path.Combine(staging,"metadata"),"events.jsonl");
    private string GapPath=>Path.Combine(manifestPublished?metadata:Path.Combine(staging,"metadata"),"gaps.jsonl");
    public string MediaFile(string monitorSlotId)
    {
        RecordingFiles.Id(monitorSlotId);Require(!closed&&!mediaPublished&&frozenManifest is null&&!poisoned,"Capture store no longer writable");
        Require(!reserved.Contains(monitorSlotId),"Duplicate media reservation");var path=Path.Combine(staging,"media",monitorSlotId+".mp4");
        Check(path);Require(!RecordingFiles.Exists(path),"Media path collision");reserved.Add(monitorSlotId);return path;
    }
    public void Append(RawPointerRecord record)
    {
        Require(!closed&&!poisoned&&frozenManifest is null,"Capture journal no longer writable");
        try
        {
            Check(JournalPath);Require(record.Sequence==rawCount,"Journal sequence mismatch");fault(StoreOperation.Append);
            var output=handle??throw new RecordingException("Journal closed");var bytes=Encoding.UTF8.GetBytes(RecordingJson.Serialize(record)+"\n");
            output.Write(bytes);rawCount=checked(rawCount+1);
        }
        catch{poisoned=true;throw;}
    }
    public void AppendGap(JournalGapRecord record)
    {
        Require(!closed&&!poisoned&&frozenManifest is null,"Capture journal no longer writable");
        try{Check(GapPath);fault(StoreOperation.Append);var output=gapHandle??throw new RecordingException("Gap journal closed");output.Write(Encoding.UTF8.GetBytes(RecordingJson.Serialize(record)+"\n"));}
        catch{poisoned=true;throw;}
    }
    public void ReadGaps(Action<JournalGapRecord> visit){gapHandle?.Flush(true);RecordingJournalReader.Read(GapPath,Check,visit);}
    public void Flush()
    {
        Require(!poisoned,"Capture journal failed");try{Check(JournalPath);Check(GapPath);fault(StoreOperation.Flush);handle?.Flush(true);gapHandle?.Flush(true);}catch{poisoned=true;throw;}
    }
    public void Read(Action<RawPointerRecord> visit)
    {
        handle?.Flush(true);RecordingJournalReader.Read(JournalPath,Check,visit);
    }
    private void Validate(RecordingManifest m)=>RecordingEvidenceValidation.Validate(m,takeId,rawCount,staging,
        id=>Path.Combine(mediaPublished?media:Path.Combine(staging,"media"),id+".mp4"),Check,Read,ReadGaps);
    public string Publish(RecordingManifest manifest)
    {
        var text=RecordingJson.Serialize(manifest);if(frozenManifest is not null)Require(frozenManifest==text,"Conflicting publication retry");
        if(manifestPublished)return Path.Combine(metadata,"manifest.json");Require(!closed&&!poisoned,"Capture store failed or closed");
        Check(staging);Check(media);Check(metadata);Require(!RecordingFiles.Exists(metadata)&&(mediaPublished||!RecordingFiles.Exists(media)),"Publish collision");
        CheckTree(mediaPublished?media:Path.Combine(staging,"media"));CheckTree(Path.Combine(staging,"metadata"));
        Validate(manifest);Flush();var staged=Path.Combine(staging,"metadata","manifest.json");
        if(frozenManifest is null)
        {
            Check(staged);
            try{using var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(Encoding.UTF8.GetBytes(text));output.Flush(true);}
            catch{poisoned=true;throw;}
            frozenManifest=text;
        }
        handle?.Dispose();handle=null;gapHandle?.Dispose();gapHandle=null;
        if(!mediaPublished){fault(StoreOperation.MoveMedia);Check(media);CheckTree(Path.Combine(staging,"media"));Directory.Move(Path.Combine(staging,"media"),media);mediaPublished=true;}
        fault(StoreOperation.PublishManifest);Check(metadata);CheckTree(Path.Combine(staging,"metadata"));Directory.Move(Path.Combine(staging,"metadata"),metadata);manifestPublished=true;
        return Path.Combine(metadata,"manifest.json");
    }
    public void Dispose(){handle?.Dispose();handle=null;gapHandle?.Dispose();gapHandle=null;closed=true;}
}
