using System.Security.Cryptography;
using MotionClicker.Windows.Recording;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

// Generated files only. The production readiness boundary is shared with WinUI; no window, capture or input.
static class PlaybackSeekChecks {
    internal static async Task Run(string directory) {
        var path=Path.Combine(directory,"seek-acknowledgment.mp4");
        await Task.Run(()=>WriterChecks.Encode(path,PixelChecks.Pattern(),[0,5_000_000,10_000_000,15_000_000],[5_000_000,5_000_000,5_000_000,5_000_000]));
        await NativeVideoInspector.InspectAsync(path,4,2_000_000,64,64);
        var hash=SHA256.HashData(File.ReadAllBytes(path));var file=await StorageFile.GetFileFromPathAsync(path);
        using var source=MediaSource.CreateFromStorageFile(file);
        using var player=new MediaPlayer {AutoPlay=false,IsMuted=true,IsLoopingEnabled=false,IsVideoFrameServerEnabled=true};
        player.CommandManager.IsEnabled=false;
        var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened+=(_,_)=>opened.TrySetResult();player.MediaFailed+=(_,args)=>opened.TrySetException(new Exception(args.Error+": "+args.ErrorMessage));
        player.Source=new MediaPlaybackItem(source,TimeSpan.Zero,TimeSpan.FromSeconds(1.5));
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var session=new NativePlaybackSeekSession(player);int acknowledgments=0;
        session.SeekCompleted+=()=>Interlocked.Increment(ref acknowledgments);
        // Take time6s maps through the immutable5s offset into a clear file interval after declared loss.
        var target=TimeSpan.FromSeconds(1);var clearStart=TimeSpan.FromSeconds(.8);var clearEnd=TimeSpan.FromSeconds(1.5);
        var ready=SourceSeekReadiness.WaitAsync(session,target,clearStart,clearEnd,()=>true);
        bool prematurelyReady=ready.IsCompletedSuccessfully&&Volatile.Read(ref acknowledgments)==0;
        var actual=await ready;
        if(prematurelyReady||Volatile.Read(ref acknowledgments)==0||actual<clearStart||actual>=clearEnd)throw new Exception("Readiness preceded native SeekCompleted or landed outside the approved source interval");
        Console.WriteLine($"SEEK native acknowledgments={acknowledgments}; targetTicks={target.Ticks}; actualTicks={actual.Ticks}; intervalTicks=[{clearStart.Ticks},{clearEnd.Ticks}); sourceOffsetUs=5000000; readinessAfterAck=true");
        var count=Volatile.Read(ref acknowledgments);var same=await SourceSeekReadiness.WaitAsync(session,session.Position,clearStart,clearEnd,()=>true);
        if(Volatile.Read(ref acknowledgments)!=count||same<clearStart||same>=clearEnd)throw new Exception("Already-at-target no-op required another native seek");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool rejected=false;
        try{await SourceSeekReadiness.WaitAsync(session,TimeSpan.FromSeconds(1.2),clearStart,clearEnd,()=>true,canceled.Token);}catch(OperationCanceledException){rejected=true;}
        if(!rejected)throw new Exception("Canceled source seek granted readiness");
        player.Source=null;
        if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))throw new Exception("Native seek changed immutable source bytes");
        Console.WriteLine("PASS production native SeekCompleted readiness/order, post-gap clear interval/late5s source mapping, explicit no-op/cancellation and immutable H264 (headless, not GUI rendering)");
    }
}
