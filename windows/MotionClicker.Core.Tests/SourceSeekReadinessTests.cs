using MotionClicker.Windows.Recording;
using Xunit;
namespace MotionClicker.Core.Tests;
public sealed class SourceSeekReadinessTests {
    private static readonly TimeSpan Target=TimeSpan.FromSeconds(1),Start=TimeSpan.FromSeconds(.8),End=TimeSpan.FromSeconds(1.5);
    [Fact] public async Task DelayedSeekCannotRevealOrPlayOrPublishAnUnacknowledgedPosition() {
        var native=new Session();bool revealed=false,playing=false;TimeSpan? cursor=null;
        var ready=RevealAsync();
        Assert.False(ready.IsCompleted);Assert.False(revealed);Assert.False(playing);Assert.Null(cursor);
        native.Complete(Target);await ready;Assert.True(revealed);Assert.True(playing);Assert.Equal(Target,cursor);Assert.Equal(0,native.Subscribers);
        async Task RevealAsync(){cursor=await SourceSeekReadiness.WaitAsync(native,Target,Start,End,()=>true);revealed=true;playing=true;}
    }
    [Fact] public async Task StaleSeekAcknowledgmentCannotGrantReadinessToAnotherGeneration() {
        var native=new Session();long generation=1;
        var ready=SourceSeekReadiness.WaitAsync(native,Target,Start,End,()=>Volatile.Read(ref generation)==1);
        generation=2;native.Complete(Target);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ready);Assert.Equal(0,native.Subscribers);
    }
    [Fact] public async Task NativeCompletionOutsideApprovedClearIntervalFailsClosed() {
        var native=new Session();var ready=SourceSeekReadiness.WaitAsync(native,Target,Start,End,()=>true);
        native.Complete(TimeSpan.Zero);await Assert.ThrowsAnyAsync<Exception>(()=>ready);Assert.Equal(0,native.Subscribers);
    }
    [Fact] public async Task AlreadyAtTargetIsExplicitNoOpWithoutASeekAssignmentOrEvent() {
        var native=new Session { Current=Target };var actual=await SourceSeekReadiness.WaitAsync(native,Target,Start,End,()=>true);
        Assert.Equal(Target,actual);Assert.Equal(0,native.Assignments);Assert.Equal(0,native.Subscribers);
    }
    [Fact] public async Task CancellationTimeoutAndNativeFailureLeaveNoReadyResultOrEventSubscriptions() {
        var canceled=new Session();using var cancellation=new CancellationTokenSource();
        var pending=SourceSeekReadiness.WaitAsync(canceled,Target,Start,End,()=>true,cancellation.Token);cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(0,canceled.Subscribers);canceled.Complete(Target);
        var timed=new Session();await Assert.ThrowsAsync<TimeoutException>(()=>SourceSeekReadiness.WaitAsync(timed,Target,Start,End,()=>true,timeout:TimeSpan.FromMilliseconds(50)));Assert.Equal(0,timed.Subscribers);
        var failed=new Session();var failing=SourceSeekReadiness.WaitAsync(failed,Target,Start,End,()=>true);failed.Fail();await Assert.ThrowsAsync<IOException>(()=>failing);Assert.Equal(0,failed.Subscribers);
    }
    private sealed class Session : ISourceSeekSession {
        public TimeSpan Current;public int Assignments;
        private Action? completed;private Action<Exception>? failed;
        public int Subscribers=>(completed?.GetInvocationList().Length??0)+(failed?.GetInvocationList().Length??0);
        // Native Position may report the requested value before its independent completion acknowledgment.
        public TimeSpan Position {get=>Current;set{Current=value;Assignments++;}}
        public event Action? SeekCompleted {add=>completed+=value;remove=>completed-=value;}
        public event Action<Exception>? Failed {add=>failed+=value;remove=>failed-=value;}
        public void Complete(TimeSpan actual){Current=actual;completed?.Invoke();}
        public void Fail()=>failed?.Invoke(new IOException("native seek failed"));
    }
}
