namespace MotionClicker.Core.Recording;
public sealed class RecordingStore : IEventJournal, IDisposable
{
    public StoreRecovery Recovery => new("","","",false,false);
    public RecordingStore(string root,string takeId,Action<StoreOperation>? fault=null) { }
    public string MediaFile(string monitorSlotId) => throw new RecordingException("Not implemented");
    public void Append(RawPointerRecord record) { }
    public void Flush() { }
    public void Read(Action<RawPointerRecord> visit) { }
    public string Publish(RecordingManifest manifest) => throw new RecordingException("Not implemented");
    public void Dispose() { }
}
