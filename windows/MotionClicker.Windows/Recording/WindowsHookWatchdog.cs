namespace MotionClicker.Windows.Recording;

// Independent watchdog state; no dependency on owner, UI or physical mouse traffic.
internal sealed class WindowsHookWatchdog(long generation)
{
    internal const long ProbeIntervalUs=100_000,PumpDeadlineUs=250_000,CallbackBudgetUs=100_000;
    internal void Probe(long currentGeneration,long nowUs) { }
    internal void Acknowledge(long currentGeneration,long probeUs) { }
    internal void CallbackEntered(long currentGeneration,long nowUs) { }
    internal void CallbackExited(long currentGeneration,long nowUs) { }
    internal void Lost(long currentGeneration,string reason) { }
    internal string? Check(long currentGeneration,long nowUs)=>null;
    internal long Generation=>generation;
}
