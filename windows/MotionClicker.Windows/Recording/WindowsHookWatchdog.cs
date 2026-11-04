namespace MotionClicker.Windows.Recording;

// Independent watchdog state; no dependency on owner, UI or physical mouse traffic.
internal sealed class WindowsHookWatchdog(long generation)
{
    internal const long ProbeIntervalUs=100_000,PumpDeadlineUs=250_000,CallbackBudgetUs=100_000;
    private long pending=-1,callback=-1;
    private string? failure;
    internal void Probe(long currentGeneration,long nowUs)
    {
        if(currentGeneration==generation)Interlocked.CompareExchange(ref pending,nowUs,-1);
    }
    internal void Acknowledge(long currentGeneration,long probeUs)
    {
        if(currentGeneration!=generation)return;
        var first=Interlocked.Read(ref pending);
        if(first>=0&&probeUs>=first)Interlocked.CompareExchange(ref pending,-1,first);
    }
    internal void CallbackEntered(long currentGeneration,long nowUs)
    {
        if(currentGeneration==generation)Interlocked.Exchange(ref callback,nowUs);
    }
    internal void CallbackExited(long currentGeneration,long nowUs)
    {
        if(currentGeneration!=generation)return;
        var entered=Interlocked.Exchange(ref callback,-1);
        if(entered>=0&&nowUs-entered>=CallbackBudgetUs)Lost(currentGeneration,"Pointer hook callback budget exceeded");
    }
    internal void Lost(long currentGeneration,string reason)
    {
        if(currentGeneration==generation)Interlocked.CompareExchange(ref failure,reason,null);
    }
    internal string? Check(long currentGeneration,long nowUs)
    {
        if(currentGeneration!=generation)return null;
        var entered=Interlocked.Read(ref callback);
        var first=Interlocked.Read(ref pending);
        if(entered>=0&&nowUs-entered>=CallbackBudgetUs)Lost(currentGeneration,"Pointer hook callback budget exceeded");
        if(first>=0&&nowUs-first>=PumpDeadlineUs)Lost(currentGeneration,"Pointer hook pump deadline exceeded");
        return Volatile.Read(ref failure);
    }
    internal long Generation=>generation;
}
