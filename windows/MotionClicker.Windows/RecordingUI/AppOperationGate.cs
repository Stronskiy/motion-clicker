namespace MotionClicker.Windows.RecordingUI;
public enum AppOperation { Execution, Recording, Opening, ChoosingDestination, IdentifyingDisplays }
/// <summary>One app-wide ownership gate. Paused execution retains its lease until explicit Stop.</summary>
public sealed class AppOperationGate {
    private Lease? current;
    public bool Busy=>Volatile.Read(ref current) is not null;
    public IDisposable? TryEnter(AppOperation operation) {
        var lease=new Lease(this,operation);return Interlocked.CompareExchange(ref current,lease,null) is null?lease:null;
    }
    private sealed class Lease(AppOperationGate owner,AppOperation operation) : IDisposable {
        public AppOperation Operation { get; }=operation;
        public void Dispose()=>Interlocked.CompareExchange(ref owner.current,null,this);
    }
}
