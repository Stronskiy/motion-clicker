namespace MotionClicker.Windows.RecordingUI;
/// <summary>Owns the complete lifetime of transient display-identifier windows.</summary>
public sealed class IdentifyOperation(AppOperationGate operations) {
    public async Task RunAsync(Action show,Func<Task> closeAcknowledged,Func<CancellationToken,Task> visibleDuration,CancellationToken cancellation=default) {
        var lease=operations.TryEnter(AppOperation.IdentifyingDisplays)??throw new InvalidOperationException("Finish the current app operation before identifying displays");
        try{cancellation.ThrowIfCancellationRequested();show();await visibleDuration(cancellation);}
        finally {
            // Unknown native closure must retain exclusion rather than permit capture over a live control window.
            await closeAcknowledged();lease.Dispose();
        }
    }
}
