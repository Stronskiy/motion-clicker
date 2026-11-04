using System.Runtime.InteropServices;
using MotionClicker.Core.Recording;
using Vortice.MediaFoundation;
namespace MotionClicker.Windows.Recording;
public sealed record NativeVideoEvidence(long CodedFrames,long DecodedFrames,long OriginTicks,long DurationTicks);
/// <summary>Native codec evidence, distinct from portable bundle path validation. One owned MTA worker, read-only files.</summary>
public static class NativeVideoInspector {
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    public static Task<NativeVideoEvidence> InspectAsync(string path,long frameCount,long durationUs,int pixelWidth,int pixelHeight,CancellationToken cancellation=default)=>Task.Run(()=>Inspect(path,frameCount,durationUs,pixelWidth,pixelHeight,cancellation),cancellation);
    private static NativeVideoEvidence Inspect(string path,long count,long duration,int width,int height,CancellationToken token) {
        if(count<=0||duration<=0||duration>long.MaxValue/10||width<=0||height<=0||width%2!=0||height%2!=0||(long)width*height>33_177_600)throw new RecordingException("Unsupported native media metadata limits");
        if((File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new RecordingException("Media must be an ordinary file");
        using var immutable=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        Marshal.ThrowExceptionForHR(CoInitializeEx(0,0));var started=false;
        try {
            MediaFactory.MFStartup().CheckError();started=true;
            var coded=Read(path,false,count,width,height,token);var decoded=Read(path,true,count,width,height,token);
            // Writer ticks are rounded up to stored microseconds; MP4 rational quantization may add one further tick.
            const long tolerance=20;var expected=checked(duration*10);
            if(coded.Count!=count||decoded.Count!=count||coded.Origin!=0||decoded.Origin!=0||Math.Abs(coded.End-expected)>tolerance||Math.Abs(decoded.End-expected)>tolerance||Math.Abs(coded.End-decoded.End)>tolerance)
                throw new RecordingException("Native coded/decoded frame count, origin or duration differs from saved evidence");
            return new(coded.Count,decoded.Count,coded.Origin,coded.End);
        } finally {if(started)MediaFactory.MFShutdown().CheckError();CoUninitialize();}
    }
    private static (long Count,long Origin,long End) Read(string path,bool decode,long expected,int width,int height,CancellationToken token) {
        using var reader=MediaFactory.MFCreateSourceReaderFromURL(path,null);
        using var native=reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream,0);
        if(native.GetGUID(MediaTypeAttributeKeys.Subtype)!=new Guid("34363248-0000-0010-8000-00aa00389b71"))throw new RecordingException("Only native H.264 video is supported");
        var size=native.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        if((size>>32)!=(uint)width||(uint)size!=(uint)height)throw new RecordingException("Native video dimensions differ from saved display");
        bool noAudio=false;
        try {using var audio=reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream,0);}
        catch(SharpGen.Runtime.SharpGenException error)when(error.ResultCode.Code==unchecked((int)0xC00D36B3)){noAudio=true;}
        if(!noAudio)throw new RecordingException("Recorded video unexpectedly contains audio");
        if(decode) {
            using var output=MediaFactory.MFCreateMediaType();output.Set(MediaTypeAttributeKeys.MajorType,MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype,new Guid("3231564e-0000-0010-8000-00aa00389b71"));reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream,output);
        }
        long count=0,origin=long.MaxValue,end=0;
        while(true) {
            token.ThrowIfCancellationRequested();
            using var sample=reader.ReadSample(SourceReaderIndex.FirstVideoStream,SourceReaderControlFlag.None,out _,out var flags,out var pts);
            if(sample is not null) {
                if(pts<0||sample.SampleDuration<=0||++count>expected)throw new RecordingException("Unexpected native video sample count or timing");
                origin=Math.Min(origin,pts);end=Math.Max(end,checked(pts+sample.SampleDuration));
                if(decode){using var buffer=sample.ConvertToContiguousBuffer();if(buffer.CurrentLength<checked((long)width*height*3/2))throw new RecordingException("Decoded video pixels are incomplete");}
            }
            if((flags&SourceReaderFlag.EndOfStream)!=0)break;
        }
        return(count,origin,end);
    }
}
