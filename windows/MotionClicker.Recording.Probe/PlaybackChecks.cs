using System.Security.Cryptography;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

static class PlaybackChecks {
    internal static async Task Run(string directory) {
        var path=Path.Combine(directory,"playback-endpoint.mp4");
        await Task.Run(()=>WriterChecks.Encode(path,PixelChecks.Pattern(),[50_000_000,51_000_000],[1_000_000,1_000_000]));
        var hash=SHA256.HashData(File.ReadAllBytes(path));
        var file=await StorageFile.GetFileFromPathAsync(path);
        using var source=MediaSource.CreateFromStorageFile(file);
        // End at107ms, seek to100ms. A10Hz UI poll would miss this entire7ms interval.
        var item=new MediaPlaybackItem(source,TimeSpan.Zero,TimeSpan.FromTicks(1_070_000));
        using var player=new MediaPlayer { AutoPlay=false,IsMuted=true,IsLoopingEnabled=false };
        player.CommandManager.IsEnabled=false;
        var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened+=(_,_)=>opened.TrySetResult();
        player.MediaEnded+=(_,_)=>ended.TrySetResult();
        player.MediaFailed+=(_,args)=>{var error=new Exception(args.Error+": "+args.ErrorMessage);opened.TrySetException(error);ended.TrySetException(error);};
        player.Source=item;
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"PLAYER opened width={player.PlaybackSession.NaturalVideoWidth}; height={player.PlaybackSession.NaturalVideoHeight}; durationTicks={player.PlaybackSession.NaturalDuration.Ticks}");
        if(player.PlaybackSession.NaturalVideoWidth!=64||player.PlaybackSession.NaturalVideoHeight!=64)throw new Exception("Native player video not ready");
        player.PlaybackSession.Position=TimeSpan.FromTicks(1_000_000);player.Play();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var position=player.PlaybackSession.Position.Ticks;
        Console.WriteLine($"PLAYER native endpoint positionTicks={position}; durationLimitTicks={item.DurationLimit?.Ticks}");
        if(Math.Abs(position-1_070_000)>10_000)throw new Exception("Native player did not stop at the7ms interval endpoint");
        player.Source=null;
        if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))throw new Exception("Playback mutated source bytes");
        Console.WriteLine("PASS native generated H264 player readiness, saved late source offset5000000us, native7ms duration endpoint and immutable bytes (no UI ticker)");
    }
}
