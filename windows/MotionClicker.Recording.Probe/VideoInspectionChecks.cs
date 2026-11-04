using System.Security.Cryptography;
using MotionClicker.Windows.Recording;
static class VideoInspectionChecks {
    internal static async Task Run(string directory) {
        var path=Path.Combine(directory,"inspected-late.mp4");
        await Task.Run(()=>WriterChecks.Encode(path,PixelChecks.Pattern(),[50_000_000,51_000_000],[1_000_000,1_000_000]));
        var before=SHA256.HashData(File.ReadAllBytes(path));
        var evidence=await NativeVideoInspector.InspectAsync(path,2,200_000,64,64);
        if(evidence.CodedFrames!=2||evidence.DecodedFrames!=2||evidence.OriginTicks!=0||evidence.DurationTicks!=2_000_000)throw new Exception("Coded/decoded count/origin/duration mismatch");
        foreach(var invalid in new[]{(Count:3L,Duration:200_000L,Width:64),(Count:2L,Duration:500_000L,Width:64),(Count:2L,Duration:200_000L,Width:128)}) {
            bool rejected=false;try{await NativeVideoInspector.InspectAsync(path,invalid.Count,invalid.Duration,invalid.Width,64);}catch{rejected=true;}if(!rejected)throw new Exception("Manifest/native metadata mismatch accepted");
        }
        var bad=Path.Combine(directory,"not-video.mp4");File.WriteAllText(bad,"portable nonempty placeholder is not codec evidence");
        bool failed=false;try{await NativeVideoInspector.InspectAsync(bad,1,100_000,64,64);}catch{failed=true;}if(!failed)throw new Exception("Nonempty fake video accepted");
        if(!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))throw new Exception("Inspector rewrote source media");
        Console.WriteLine("PASS production native inspector coded+decoded2frames/origin0/duration200000us/noaudio, rejects count/duration/dimensions/fake media, immutable bytes; saved source offset5000000us remains external");
    }
}
