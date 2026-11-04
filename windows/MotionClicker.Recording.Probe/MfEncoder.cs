using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Windows.Storage.Streams;

// Capability experiment only. Generated pixels; no native capture/hook entry point.
static class MfEncoder
{
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    public static void Encode(string path,IBuffer pixels,long[] timestamps,long[] durations)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0,0));
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var output=MediaFactory.MFCreateFile(Vortice.MediaFoundation.FileAccessMode.MfAccessModeWrite,
                FileOpenMode.MfOpenModeFailIfExist,FileFlags.None,path);
            using var attributes=MediaFactory.MFCreateAttributes(2);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms,0u);
            // With a byte stream the URL supplies only the container extension; no second file is opened.
            using var writer=MediaFactory.MFCreateSinkWriterFromURL(path,output,attributes);
            using var encoded=MediaFactory.MFCreateMediaType();
            SetVideo(encoded,new Guid("34363248-0000-0010-8000-00aa00389b71"));
            encoded.Set(MediaTypeAttributeKeys.AvgBitrate,1_000_000u);
            encoded.Set(MediaTypeAttributeKeys.Mpeg2Profile,66u);
            int stream=writer.AddStream(encoded);
            using var input=MediaFactory.MFCreateMediaType();
            SetVideo(input,VideoFormatGuids.Rgb32);
            writer.SetInputMediaType(stream,input,null);
            writer.BeginWriting();
            byte[] bytes=new byte[pixels.Length];
            using(var reader=DataReader.FromBuffer(pixels))reader.ReadBytes(bytes);
            for(int i=0;i<timestamps.Length;i++)
            {
                using var buffer=MediaFactory.MFCreateMemoryBuffer(bytes.Length);
                buffer.Lock(out var pointer,out _,out _);
                try { Marshal.Copy(bytes,0,pointer,bytes.Length); }
                finally { buffer.Unlock(); }
                buffer.CurrentLength=bytes.Length;
                using var sample=MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime=timestamps[i]-timestamps[0];
                sample.SampleDuration=durations[i];
                writer.WriteSample(stream,sample);
            }
            writer.Finalize();
            Console.WriteLine($"MF ENCODE generated inputs={timestamps.Length}");
        }
        finally { MediaFactory.MFShutdown().CheckError();CoUninitialize(); }
    }

    private static void SetVideo(IMFMediaType type,Guid subtype)
    {
        type.Set(MediaTypeAttributeKeys.MajorType,MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype,subtype);
        type.Set(MediaTypeAttributeKeys.InterlaceMode,2u);
        type.Set(MediaTypeAttributeKeys.FrameSize,MediaFactory.PackSize(64,64));
        type.Set(MediaTypeAttributeKeys.FrameRate,MediaFactory.PackRatio(30,1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio,MediaFactory.PackRatio(1,1));
    }
}
