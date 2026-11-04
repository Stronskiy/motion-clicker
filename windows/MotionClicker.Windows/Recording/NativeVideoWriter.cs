using System.Runtime.InteropServices;
using MotionClicker.Core.Recording;
using Vortice.MediaFoundation;

namespace MotionClicker.Windows.Recording;

// All methods and COM lifetime belong to a single dedicated encoder thread.
// Each submitted buffer is immutable and independently owned by its MF sample.
internal sealed class NativeVideoWriter : IWindowsVideoEncoder
{
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    private readonly int thread=Environment.CurrentManagedThreadId;
    private readonly int byteCount;
    private IMFByteStream? output;
    private IMFSinkWriter? writer;
    private int stream;
    private bool comStarted,mfStarted;
    private long? firstSource,lastSource;
    private long count,end;
    private string? failure;
    private NativeWriterResult? terminal;

    internal NativeVideoWriter(string path,int width,int height)
    {
        if(width<=0 || height<=0 || width%2!=0 || height%2!=0 || (long)width*height>33_177_600)
            throw new RecordingException("Unsupported H264 dimensions (positive even dimensions, at most 8K pixels)");
        byteCount=checked(width*height*4);
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0,0));comStarted=true;
            MediaFactory.MFStartup().CheckError();mfStarted=true;
            output=MediaFactory.MFCreateFile(FileAccessMode.MfAccessModeWrite,FileOpenMode.MfOpenModeFailIfExist,FileFlags.None,path);
            using var attributes=MediaFactory.MFCreateAttributes(1);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms,0u);
            // With a byte stream the URL only selects MP4 by extension, never reopens/replaces a file.
            writer=MediaFactory.MFCreateSinkWriterFromURL(path,output,attributes);
            using var encoded=MediaFactory.MFCreateMediaType();
            ConfigureType(encoded,new Guid("34363248-0000-0010-8000-00aa00389b71"),width,height);
            encoded.Set(MediaTypeAttributeKeys.AvgBitrate,(uint)Math.Clamp((long)width*height*6,1_000_000,40_000_000));
            encoded.Set(MediaTypeAttributeKeys.Mpeg2Profile,66u); // Baseline avoids the observed one-frame origin shift.
            stream=writer.AddStream(encoded);
            using var input=MediaFactory.MFCreateMediaType();
            ConfigureType(input,VideoFormatGuids.Rgb32,width,height);
            // Owned D3D/SoftwareBitmap BGRA rows are contiguous and top-down.
            // RGB system-memory defaults may be bottom-up unless explicitly set.
            uint stride=checked((uint)width*4);
            input.Set(MediaTypeAttributeKeys.DefaultStride,stride);
            if(input.GetUInt32(MediaTypeAttributeKeys.DefaultStride)!=stride)
                throw new RecordingException("Native RGB input refused positive top-down stride");
            writer.SetInputMediaType(stream,input,null);
            DisableFrameRateConversion(writer,stream);
            writer.BeginWriting();
        }
        catch(Exception initialization)
        {
            try{CloseNative();}
            catch(Exception closure){throw new NativeEncoderClosureException("Encoder initialization failed: "+initialization.Message+"; closure unknown: "+closure.Message);}
            throw;
        }
    }

    private static void ConfigureType(IMFMediaType type,Guid subtype,int width,int height)
    {
        type.Set(MediaTypeAttributeKeys.MajorType,MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype,subtype);
        type.Set(MediaTypeAttributeKeys.InterlaceMode,2u);
        type.Set(MediaTypeAttributeKeys.FrameSize,MediaFactory.PackSize((uint)width,(uint)height));
        type.Set(MediaTypeAttributeKeys.FrameRate,MediaFactory.PackRatio(30,1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio,MediaFactory.PackRatio(1,1));
    }

    private static void DisableFrameRateConversion(IMFSinkWriter writer,int stream)
    {
        using var extended=writer.QueryInterface<IMFSinkWriterEx>();
        for(int index=0;index<8;index++)
        {
            extended.GetTransformForStream(stream,index,out var category,out var transform);
            using(transform)
            {
                if(category!=TransformCategoryGuids.VideoProcessor)continue;
                using var attributes=transform.Attributes;
                attributes.Set(MediaAttributeKeys.XvpDisableFrcGuid,1u);
                if(attributes.GetUInt32(MediaAttributeKeys.XvpDisableFrcGuid)!=1)
                    throw new RecordingException("Native video processor refused to disable frame-rate conversion");
                return;
            }
        }
        throw new RecordingException("No supported video processor with disabled frame-rate conversion");
    }

    public void Append(byte[] bgra,long sourceTicks,long durationTicks)
    {
        CheckThread();
        if(terminal is not null)throw new RecordingException("Frame after encoder finalization");
        if(failure is not null)throw new RecordingException(failure);
        try
        {
            if(bgra.Length!=byteCount || sourceTicks<0 || durationTicks<=0 || (lastSource is long last && sourceTicks<=last))
                throw new RecordingException("Invalid image size or source timestamp/duration");
            long pts=checked(sourceTicks-(firstSource??sourceTicks));
            long nextEnd=checked(pts+durationTicks);
            if(firstSource is not null && (pts<end || pts-end>10))
                throw new RecordingException("Video discontinuity unsupported: native MP4 stretches the preceding image across gaps");
            using var buffer=MediaFactory.MFCreateMemoryBuffer(byteCount);
            buffer.Lock(out var data,out _,out _);
            try { Marshal.Copy(bgra,0,data,byteCount); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength=byteCount;
            using var sample=MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime=pts;sample.SampleDuration=durationTicks;
            writer!.WriteSample(stream,sample);
            // Only the successful native submission establishes origin/count/range.
            firstSource??=sourceTicks;lastSource=sourceTicks;end=nextEnd;count=checked(count+1);
        }
        catch(Exception error) { failure??=error.Message;throw; }
    }

    public NativeWriterResult Finish()
    {
        CheckThread();
        if(terminal is not null)return terminal;
        try { writer?.Finalize(); }
        catch(Exception error) { failure??=error.Message; }
        bool closureConfirmed=true;
        try { CloseNative(); }
        catch(Exception error) { failure??=error.Message;closureConfirmed=false; }
        if(count==0)failure??="No captured images";
        terminal=new(failure is null,count,end,failure,closureConfirmed);
        return terminal;
    }

    private void CheckThread()
    {
        if(Environment.CurrentManagedThreadId!=thread)throw new RecordingException("Encoder accessed outside its owned thread");
    }
    private void CloseNative()
    {
        Exception? first=null;
        void Attempt(Action action) { try { action(); } catch(Exception error) { first??=error; } }
        Attempt(()=>writer?.Dispose());writer=null;
        // Finalize + sink release closes its byte stream. Releasing our remaining COM
        // reference follows the MF sink-writer lifetime contract; calling Close again
        // on the finalized native file stream returned E_INVALIDARG on Windows26100.
        if(output is not null) { Attempt(()=>output.Dispose());output=null; }
        if(mfStarted) { Attempt(()=>MediaFactory.MFShutdown().CheckError());mfStarted=false; }
        if(comStarted) { Attempt(CoUninitialize);comStarted=false; }
        if(first is not null)throw new RecordingException("Encoder resource closure failed: "+first.Message);
    }
    public void Dispose() { if(terminal is null)Finish(); }
}
