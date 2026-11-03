using System.Text;
using System.Text.RegularExpressions;
using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

/// <summary>Capture-only writer. No project writer, overwrite, or automatic recovery deletion.</summary>
public sealed class RecordingStore : IEventJournal, IDisposable
{
    private readonly string root,staging,media,metadata,takeId;
    private readonly Action<StoreOperation> fault;
    private FileStream? handle;
    private bool poisoned,closed,mediaPublished,manifestPublished;
    private readonly HashSet<string> reserved=new(StringComparer.OrdinalIgnoreCase);
    private long rawCount;
    private string? frozenManifest;
    public StoreRecovery Recovery=>new(staging,media,Path.Combine(metadata,"manifest.json"),mediaPublished,manifestPublished);
    public RecordingStore(string root,string takeId,Action<StoreOperation>? fault=null)
    {
        Id(takeId);var initial=Path.GetFullPath(root);var attrs=File.GetAttributes(initial);
        Require(attrs.HasFlag(FileAttributes.Directory)&&!attrs.HasFlag(FileAttributes.ReparsePoint),"Root must be an ordinary directory");
        this.root=CanonicalDirectory(initial);this.takeId=takeId;this.fault=fault??(_=>{});
        staging=Path.Combine(this.root,".capture-"+Guid.NewGuid());media=Path.Combine(this.root,"media",takeId);metadata=Path.Combine(this.root,"takes",takeId);
        Check(media);Check(metadata);Require(!Exists(media)&&!Exists(metadata),"Take already exists");
        EnsureDirectory(Path.Combine(this.root,"media"));EnsureDirectory(Path.Combine(this.root,"takes"));
        Require(!Exists(staging),"Staging collision");Directory.CreateDirectory(staging);
        // Exclusive ownership marker prevents two constructors claiming the same directory.
        using(var owner=new FileStream(Path.Combine(staging,".owner"),FileMode.CreateNew,FileAccess.Write,FileShare.None))owner.Flush(true);
        Directory.CreateDirectory(Path.Combine(staging,"media"));Directory.CreateDirectory(Path.Combine(staging,"metadata"));
        handle=new(JournalPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
    }
    private static string CanonicalDirectory(string path)
    {
        var info=new DirectoryInfo(path);if(info.Parent is null)return info.FullName;
        var parent=CanonicalDirectory(info.Parent.FullName);var candidate=Path.Combine(parent,info.Name);
        var current=new DirectoryInfo(candidate);
        return current.Attributes.HasFlag(FileAttributes.ReparsePoint)?current.ResolveLinkTarget(true)?.FullName??throw new RecordingException("Unresolved directory link"):candidate;
    }
    private static void Id(string id)
    {
        var stem=id.Split('.')[0].ToUpperInvariant();
        Require(!id.EndsWith('.') && !Regex.IsMatch(stem,@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",RegexOptions.CultureInvariant),"Nonportable filesystem ID");
        Require(Regex.IsMatch(id,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z",RegexOptions.CultureInvariant),"Invalid capture ID");
    }
    private static bool Exists(string path)
    {
        try{_=File.GetAttributes(path);return true;}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
    }
    private void Check(string path)
    {
        var relative=Path.GetRelativePath(root,Path.GetFullPath(path));
        Require(relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&!Path.IsPathRooted(relative),"Path outside capture root");
        var cursor=root;var attrs=File.GetAttributes(cursor);Require(attrs.HasFlag(FileAttributes.Directory)&&!attrs.HasFlag(FileAttributes.ReparsePoint),"Root redirected");
        if(relative==".")return;
        foreach(var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if(Directory.Exists(cursor))foreach(var child in Directory.EnumerateFileSystemEntries(cursor))
            {
                var name=Path.GetFileName(child);if(string.Equals(name,part,StringComparison.OrdinalIgnoreCase))Require(name==part,"Case-folded path collision");
            }
            cursor=Path.Combine(cursor,part);if(Exists(cursor))Require(!File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint),"Reparse point in capture path");
        }
    }
    private void CheckTree(string path)
    {
        Check(path);
        if(File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            foreach(var child in Directory.EnumerateFileSystemEntries(path))CheckTree(child);
    }
    private void EnsureDirectory(string path)
    {
        Check(path);if(!Exists(path))Directory.CreateDirectory(path);Require(File.GetAttributes(path).HasFlag(FileAttributes.Directory),"Expected directory");
    }
    private string JournalPath=>Path.Combine(manifestPublished?metadata:Path.Combine(staging,"metadata"),"events.jsonl");
    public string MediaFile(string monitorSlotId)
    {
        Id(monitorSlotId);Require(!closed&&!mediaPublished&&frozenManifest is null&&!poisoned,"Capture store no longer writable");
        Require(!reserved.Contains(monitorSlotId),"Duplicate media reservation");var path=Path.Combine(staging,"media",monitorSlotId+".mp4");
        Check(path);Require(!Exists(path),"Media path collision");reserved.Add(monitorSlotId);return path;
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
    public void AppendGap(JournalGapRecord record)=>throw new RecordingException("Gap journal not implemented");
    public void ReadGaps(Action<JournalGapRecord> visit) { }
    public void Flush()
    {
        Require(!poisoned,"Capture journal failed");try{Check(JournalPath);fault(StoreOperation.Flush);handle?.Flush(true);}catch{poisoned=true;throw;}
    }
    public void Read(Action<RawPointerRecord> visit)
    {
        Check(JournalPath);handle?.Flush(true);
        using var input=new FileStream(JournalPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var reader=new StreamReader(input,new UTF8Encoding(false,true));var buffer=new char[65536];var line=new StringBuilder();int length;
        while((length=reader.Read(buffer,0,buffer.Length))>0)for(var i=0;i<length;i++)
        {
            if(buffer[i]=='\n')
            {
                Require(line.Length>0,"Invalid journal line");visit(RecordingJson.Deserialize<RawPointerRecord>(line.ToString()));line.Clear();
            }
            else{line.Append(buffer[i]);Require(line.Length<=1048576,"Oversized journal record");}
        }
        Require(line.Length==0,"Truncated event journal");
    }
    private void Validate(RecordingManifest m)
    {
        Require(m.ManifestVersion==1&&m.TakeId==takeId&&m.State is RecordingState.Completed or RecordingState.Interrupted or RecordingState.Failed&&m.DurationUs>0&&m.RawEventCount==rawCount,"Invalid terminal manifest");
        Require(m.Streams.Length==m.Targets.Length&&m.Streams.Length>0,"Missing manifest streams");
        var seen=new HashSet<string>(StringComparer.Ordinal);var incomplete=new HashSet<string>(m.IncompleteGroupIds,StringComparer.Ordinal);
        foreach(var id in incomplete)Id(id);
        foreach(var s in m.Streams)
        {
            Require(seen.Add(s.MonitorSlotId)&&m.Targets.Any(t=>t.Id==s.MonitorSlotId),"Invalid stream target");
            Require(s.RelativePath==$"media/{takeId}/{s.MonitorSlotId}.mp4","Unsafe stream media path");
            _=new MediaClockMapper(s.Clock.SourceAnchorTicks,s.Clock.HostAnchorUs,s.Clock.TicksPerSecond,m.EpochHostUs);
            Require(s.FrameCount>=0&&s.EncodedDurationUs>=0,"Invalid media counters");
            if(s.MediaTimeZeroUs is long zero)Require(zero>=0&&(s.WriterStatus==WriterStatus.Failed||checked(zero+s.EncodedDurationUs)<=m.DurationUs),"Media beyond take");
            foreach(var gap in s.Gaps)Require(gap.StartUs>=0&&gap.EndUs>=gap.StartUs&&gap.EndUs<=m.DurationUs&&Enum.IsDefined(gap.Kind),"Invalid gap range");
            Require(Enum.IsDefined(s.WriterStatus),"Invalid writer status");
            if(s.WriterStatus==WriterStatus.Succeeded)
            {
                Require(s.FrameCount>0&&s.EncodedDurationUs>0&&s.MediaTimeZeroUs is not null,"Empty successful media");
                var file=Path.Combine(mediaPublished?media:Path.Combine(staging,"media"),s.MonitorSlotId+".mp4");Check(file);
                Require(File.Exists(file)&&!File.GetAttributes(file).HasFlag(FileAttributes.Directory)&&new FileInfo(file).Length>0,"Missing or empty successful media");
            }
            else Require(m.State!=RecordingState.Completed,"Failed writer labeled completed");
        }
        _=RecordingJournalValidation.Validate(m,staging,Read);
    }
    public string Publish(RecordingManifest manifest)
    {
        var text=RecordingJson.Serialize(manifest);if(frozenManifest is not null)Require(frozenManifest==text,"Conflicting publication retry");
        if(manifestPublished)return Path.Combine(metadata,"manifest.json");Require(!closed&&!poisoned,"Capture store failed or closed");
        Check(staging);Check(media);Check(metadata);Require(!Exists(metadata)&&(mediaPublished||!Exists(media)),"Publish collision");
        CheckTree(mediaPublished?media:Path.Combine(staging,"media"));CheckTree(Path.Combine(staging,"metadata"));
        Validate(manifest);Flush();var staged=Path.Combine(staging,"metadata","manifest.json");
        if(frozenManifest is null)
        {
            Check(staged);
            try{using var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(Encoding.UTF8.GetBytes(text));output.Flush(true);}
            catch{poisoned=true;throw;}
            frozenManifest=text;
        }
        handle?.Dispose();handle=null;
        if(!mediaPublished){fault(StoreOperation.MoveMedia);Check(media);CheckTree(Path.Combine(staging,"media"));Directory.Move(Path.Combine(staging,"media"),media);mediaPublished=true;}
        fault(StoreOperation.PublishManifest);Check(metadata);CheckTree(Path.Combine(staging,"metadata"));Directory.Move(Path.Combine(staging,"metadata"),metadata);manifestPublished=true;
        return Path.Combine(metadata,"manifest.json");
    }
    public void Dispose(){handle?.Dispose();handle=null;closed=true;}
}
