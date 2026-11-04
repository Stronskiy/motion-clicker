namespace MotionClicker.Windows.RecordingUI;
/// <summary>Owns the complete lifetime of transient display-identifier windows.</summary>
public sealed class IdentifyOperation(AppOperationGate operations) {
    public async Task RunAsync(Action show,Func<Task> closeAcknowledged,Func<CancellationToken,Task> visibleDuration,CancellationToken cancellation=default) {
        // Extracted pre-review admission check, which did not retain ownership while visible.
        if(operations.Busy)return;
        try{show();await visibleDuration(cancellation);}
        finally{await closeAcknowledged();}
    }
}
