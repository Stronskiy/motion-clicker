using Windows.Media.Playback;
namespace MotionClicker.Windows.Recording;
/// <summary>Bridges native completion/failure events; construction does not seek or play.</summary>
public sealed class NativePlaybackSeekSession : ISourceSeekSession,IDisposable {
    private readonly MediaPlayer player;
    private readonly MediaPlaybackSession session;
    private int disposed;
    public NativePlaybackSeekSession(MediaPlayer player) {
        this.player=player;session=player.PlaybackSession;
        session.SeekCompleted+=OnSeekCompleted;player.MediaFailed+=OnFailed;
    }
    public TimeSpan Position {get=>session.Position;set=>session.Position=value;}
    public event Action? SeekCompleted;
    public event Action<Exception>? Failed;
    private void OnSeekCompleted(MediaPlaybackSession sender,object args)=>SeekCompleted?.Invoke();
    private void OnFailed(MediaPlayer sender,MediaPlayerFailedEventArgs args)=>Failed?.Invoke(new InvalidOperationException(args.Error+": "+args.ErrorMessage));
    public void Dispose() {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try{session.SeekCompleted-=OnSeekCompleted;player.MediaFailed-=OnFailed;}catch(ObjectDisposedException){ /* Native player already closed after cancellation. */ }
    }
}
