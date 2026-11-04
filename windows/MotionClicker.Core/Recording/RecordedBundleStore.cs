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
    public static void Create(string root)=>throw new RecordingException("Recorded bundle creation not implemented");
    public static RecordedBundleSnapshot SaveInitial(string root,RecordingResult result,string validationScratch,Action<BundleOperation>? fault=null)=>throw new RecordingException("Recorded bundle save not implemented");
    public static RecordedBundleSnapshot ColdOpen(string root,string validationScratch)=>throw new RecordingException("Recorded bundle reopen not implemented");
    public static string[] RecoveryEntries(string root)=>throw new RecordingException("Recorded bundle recovery not implemented");
}
