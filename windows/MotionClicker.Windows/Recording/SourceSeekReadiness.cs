namespace MotionClicker.Windows.Recording;

/// <summary>Native-session seam for source seek acknowledgment; no UI or input dependency.</summary>
public interface ISourceSeekSession {
    TimeSpan Position { get; set; }
    event Action? SeekCompleted;
    event Action<Exception>? Failed;
}
public static class SourceSeekReadiness {
    /// <summary>Only an acknowledged, current-generation position in the approved interval grants readiness.</summary>
    public static async Task<TimeSpan> WaitAsync(ISourceSeekSession session,TimeSpan target,TimeSpan intervalStart,TimeSpan intervalEnd,Func<bool> isCurrent,CancellationToken cancellation=default,TimeSpan? timeout=null) {
        var deadline=timeout??TimeSpan.FromSeconds(20);
        if(intervalStart<TimeSpan.Zero||intervalEnd<=intervalStart||target<intervalStart||target>=intervalEnd||deadline<=TimeSpan.Zero)
            throw new InvalidOperationException("Seek target must belong to a bounded approved source interval");
        cancellation.ThrowIfCancellationRequested();
        if(!isCurrent())throw new OperationCanceledException("Stale source seek",cancellation);
        var completed=new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure=null;
        void OnCompleted() {
            if(!isCurrent()){completed.TrySetCanceled(cancellation);return;}
            try{completed.TrySetResult(session.Position);}catch(Exception error){completed.TrySetException(error);}
        }
        void OnFailed(Exception error){Interlocked.CompareExchange(ref failure,error,null);completed.TrySetException(error);}
        session.SeekCompleted+=OnCompleted;session.Failed+=OnFailed;
        try {
            cancellation.ThrowIfCancellationRequested();
            if(!isCurrent())throw new OperationCanceledException("Stale source seek",cancellation);
            TimeSpan actual;
            // Setting an unchanged native position need not emit SeekCompleted. Do not issue that seek.
            if(session.Position==target)actual=target;
            else {
                session.Position=target;
                actual=await completed.Task.WaitAsync(deadline,cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            if(!isCurrent())throw new OperationCanceledException("Stale source seek",cancellation);
            if(Volatile.Read(ref failure) is Exception error)throw error;
            if(actual<intervalStart||actual>=intervalEnd)throw new InvalidOperationException("Native seek completed outside the approved clear source interval");
            return actual;
        } finally {session.SeekCompleted-=OnCompleted;session.Failed-=OnFailed;}
    }
}
