using System.Security.Cryptography;
using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
using MotionClicker.Windows.RecordingUI;
static class BundlePlaybackChecks {
    internal static async Task Run(string directory) {
        var root=Path.Combine(directory,"generated.motionclicker");var scratch=Path.Combine(directory,"external");Directory.CreateDirectory(scratch);RecordedBundleStore.Create(root);
        RecordingTarget Target(string id,int x) {var geometry=new DisplayGeometry(new(x,0),new(64,64),1,0);return new(new(id,id,geometry),new(id,id,geometry,new(new(x,0),new(64,64))));}
        using(var store=new RecordingStore(root,"take")) {
            var session=new RecordingSession("take",[Target("left",0),Target("right",64)],store);
            foreach(var id in new[]{"left","right"})session.SourceReady(id,new(50_000_000,1_000_000,10_000_000));session.EventTapReady();session.Begin(1_000_000);
            await Task.Run(()=>WriterChecks.Encode(store.MediaFile("left"),PixelChecks.Pattern(),[50_000_000],[80_000_000]));
            await Task.Run(()=>WriterChecks.Encode(store.MediaFile("right"),PixelChecks.Pattern(),[100_000_000],[30_000_000]));
            session.AcceptedFrame("left",50_000_000,0,8_000_000);session.AcceptedFrame("right",100_000_000,0,3_000_000);
            for(int i=0;i<65;i++){var start=100_000L+i*40_000;session.ReportGap("left",50_000_000+start*10,50_000_000+(start+1_000)*10,GapKind.Dropped);}
            session.Receive(new(1_000_010,new(2,2),ActionKind.Move));session.Stop(9_000_000);session.WriterFinished("left",WriterStatus.Succeeded,8_000_000);session.WriterFinished("right",WriterStatus.Succeeded,3_000_000);
            var result=session.Project();store.Publish(result.Manifest);_=RecordedBundleStore.SaveInitial(root,result,scratch);
        }
        static string[] Hashes(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>Path.GetRelativePath(root,p)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        var before=Hashes(root);var opened=RecordedBundleStore.ColdOpen(root,scratch);
        foreach(var media in opened.Media)await NativeVideoInspector.InspectAsync(media.File,1,media.EncodedDurationUs,64,64);
        var lookup=new CompleteGapLookup(opened.GapsFile,opened.Manifest.DurationUs,opened.Manifest.Streams);
        if(opened.Manifest.Streams[0].Gaps.Length!=64||opened.Manifest.Streams[0].GapCount!=65)throw new Exception("Fixture did not exceed manifest preview");
        if((await lookup.WindowAsync("left",2_660_100)).Kind!=GapKind.Dropped)throw new Exception("Journal-only65th gap not masked");
        var clear=await lookup.WindowAsync("left",2_659_000);
        if(!SourcePlayback.TryRange(opened.Media[0],clear,2_659_000,out var pts,out var end)||pts.Ticks!=26_590_000||end.Ticks!=26_600_000)throw new Exception("Wrong65th gap native endpoint");
        var right=opened.Media[1];if(right.MediaTimeZeroUs!=5_000_000||SourcePlayback.TryRange(right,await lookup.WindowAsync("right",2_659_000),2_659_000,out _,out _))throw new Exception("Late monitor source offset was rebased");
        if(!SourcePlayback.TryRange(right,await lookup.WindowAsync("right",5_100_000),5_100_000,out var latePTS,out _)||latePTS.Ticks!=1_000_000)throw new Exception("Late file/source mapping wrong");
        if(!before.SequenceEqual(Hashes(root)))throw new Exception("ColdOpen/native inspection/gap lookup rewrote saved bundle");
        Console.WriteLine("PASS actual generated H264 -> initialSave -> freshColdOpen -> native coded/decoded inspection -> complete65thgap/nativeendpoint, late5s monitor mapping and immutable entire saved bundle");
    }
}
