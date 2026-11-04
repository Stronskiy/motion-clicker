using MotionClicker.Core.Runtime;
namespace MotionClicker.Windows;
/// <summary>All runner/backend work stays off UI. The UI consumes only a published immutable snapshot.</summary>
internal sealed class RunWorker(SafeRunner runner) : IDisposable {
    private readonly SemaphoreSlim commands=new(1,1);
    private readonly object sync=new();
    private RunnerSnapshot snapshot=runner.Snapshot;
    private CancellationTokenSource? cancellation;
    private Task? loop,stopping;
    public RunnerSnapshot Snapshot=>Volatile.Read(ref snapshot);
    private void Publish()=>Volatile.Write(ref snapshot,runner.Snapshot);
    public async Task StartAsync(RunProgram program,IReadOnlyDictionary<string,string> mapping,long delayUs,Action prepare) {
        await commands.WaitAsync().ConfigureAwait(false);
        try {
            if(Snapshot.OwnedButton is not null||Snapshot.State is RunnerState.Running or RunnerState.Armed)throw new InvalidOperationException("Stop and acknowledge owned release before restarting");
            if(loop is not null)await loop.ConfigureAwait(false);
            lock(sync){cancellation?.Dispose();cancellation=new();}
            var token=cancellation.Token;
            await Task.Run(()=>{prepare();token.ThrowIfCancellationRequested();runner.Start(program,mapping,delayUs,true);Publish();}).ConfigureAwait(false);
            loop=Task.Run(()=> {
                try {
                    while(!token.IsCancellationRequested&&runner.Snapshot.State is RunnerState.Armed or RunnerState.Running) {
                        runner.Pump();Publish();if(token.WaitHandle.WaitOne(1))break;
                    }
                } catch(Exception error){runner.Stop("Worker failed: "+error.Message);}
                finally{Publish();}
            });
        } finally {Publish();commands.Release();}
    }
    public Task StopAsync(string reason="Stopped",bool paused=false) {
        lock(sync) {cancellation?.Cancel();return stopping??=StopCoreAsync(reason,paused);}
    }
    private async Task StopCoreAsync(string reason,bool paused) {
        await Task.Yield();await commands.WaitAsync().ConfigureAwait(false);
        try {
            if(loop is not null)await loop.ConfigureAwait(false);
            await Task.Run(()=>{runner.Stop(reason,paused);Publish();}).ConfigureAwait(false);
        } finally {Publish();commands.Release();lock(sync){stopping=null;}}
    }
    public void Dispose()=>_ = StopAsync("Worker disposed");
}
