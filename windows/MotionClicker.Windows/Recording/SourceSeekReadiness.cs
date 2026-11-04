namespace MotionClicker.Windows.Recording;

/// <summary>Native-session seam for source seek acknowledgment; no UI or input dependency.</summary>
public interface ISourceSeekSession {
    TimeSpan Position { get; set; }
    event Action? SeekCompleted;
    event Action<Exception>? Failed;
}
public static class SourceSeekReadiness {
    public static Task<TimeSpan> WaitAsync(ISourceSeekSession session,TimeSpan target,TimeSpan intervalStart,TimeSpan intervalEnd,Func<bool> isCurrent,CancellationToken cancellation=default,TimeSpan? timeout=null) {
        // Extracted pre-review policy: setting Position was incorrectly treated as readiness.
        session.Position=target;return Task.FromResult(session.Position);
    }
}
