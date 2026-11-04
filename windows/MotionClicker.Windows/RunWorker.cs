using MotionClicker.Core.Runtime;
namespace MotionClicker.Windows;
internal sealed class RunWorker(SafeRunner runner) : IDisposable {
    private readonly CancellationTokenSource cancellation=new();
    public SafeRunner Runner=>runner;
    public RunnerSnapshot Snapshot=>runner.Snapshot;
    public Task StartAsync(RunProgram program,IReadOnlyDictionary<string,string> mapping,long delayUs,Action prepare)=>Task.FromException(new NotImplementedException("Worker start boundary not implemented"));
    public Task StopAsync(string reason="Stopped",bool paused=false)=>Task.FromException(new NotImplementedException("Worker stop boundary not implemented"));
    public void Launch()=>Task.Run(()=> {
        while(!cancellation.IsCancellationRequested && runner.Snapshot.State is RunnerState.Armed or RunnerState.Running) {
            runner.Pump();
            if(cancellation.Token.WaitHandle.WaitOne(1)) break; // cancellable monotonic scheduling off UI thread
        }
    });
    public void Stop(string reason="Stopped",bool paused=false) { runner.Stop(reason,paused); cancellation.Cancel(); }
    public void Dispose() { Stop(); }
}
