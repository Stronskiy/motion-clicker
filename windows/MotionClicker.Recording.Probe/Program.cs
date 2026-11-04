// Synthetic native capability probe. Never creates a capture item or installs a hook.
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

await Probe.Run();

static class Probe
{
    [DllImport("d3d11.dll", ExactSpelling=true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
    [DllImport("d3d11.dll", ExactSpelling=true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint surface, out nint result);

    public static async Task Run()
    {
        Console.WriteLine($"OS={Environment.OSVersion}; CLR={Environment.Version}; Vortice={typeof(D3D11).Assembly.FullName}; CsWinRT={typeof(MarshalInterface<>).Assembly.FullName}");
        using var device = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var devicePointer));
        using var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer);
        Marshal.Release(devicePointer);
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,64,64,mipLevels:1,bindFlags:BindFlags.RenderTarget | BindFlags.ShaderResource));
        using var view = device.CreateRenderTargetView(texture);
        using var context = device.ImmediateContext;
        context.ClearRenderTargetView(view,new Color4(1,0,0,1));
        using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer,out var surfacePointer));
        using var surface = MarshalInterface<IDirect3DSurface>.FromAbi(surfacePointer);
        Marshal.Release(surfacePointer);
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
        var pixels = new Windows.Storage.Streams.Buffer(64*64*4);
        bitmap.CopyToBuffer(pixels);
        using(var reader=DataReader.FromBuffer(pixels))
        {
            var bytes=new byte[pixels.Length]; reader.ReadBytes(bytes);
            if(bytes[2]!=255 || bytes[3]!=255) throw new Exception("Generated GPU surface copy lost red pixels");
        }
        Console.WriteLine("PASS generated WARP device/surface -> completed owned BGRA buffer (64x64)");
        var directory=Path.Combine(Path.GetTempPath(),"motion-clicker-synthetic-"+Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var cases = new (string Name,long[] Pts,long[] Durations)[] {
                ("regular",Enumerable.Range(0,30).Select(i=>(long)i*10_000_000/30).ToArray(),Enumerable.Range(0,30).Select(i=>(long)(i+1)*10_000_000/30-(long)i*10_000_000/30).ToArray()),
                ("single",[0],[333333]),
                ("static-tail",[0],[20_000_000]),
                ("vfr",[0,1_000_000,9_000_000],[1_000_000,8_000_000,1_000_000]),
                ("late",[50_000_000,50_333_333],[333333,333333])
            };
            List<string> failures=[];
            foreach(var test in cases)
            {
                var path=Path.Combine(directory,$"{test.Name}.mp4");
                try {
                    Console.WriteLine($"CASE {test.Name}; inputPTS100ns=[{string.Join(',',test.Pts)}]; inputDuration100ns=[{string.Join(',',test.Durations)}]");
                    byte[] bytes=new byte[pixels.Length];
                    using(var pixelReader=DataReader.FromBuffer(pixels))pixelReader.ReadBytes(bytes);
                    await Task.Run(()=>WriterChecks.Encode(path,bytes,test.Pts,test.Durations)).WaitAsync(TimeSpan.FromSeconds(45));
                    Read(path,test.Pts,test.Durations);
                } catch(Exception error) { failures.Add(test.Name+": "+error);Console.WriteLine($"FAIL {test.Name}: {error.Message}"); }
            }
            try {
                byte[] bytes=new byte[pixels.Length];
                using(var pixelReader=DataReader.FromBuffer(pixels))pixelReader.ReadBytes(bytes);
                await Task.Run(()=>WriterChecks.Failures(directory,bytes)).WaitAsync(TimeSpan.FromSeconds(45));
            } catch(Exception error) { failures.Add("writer failures: "+error); }
            if(failures.Count>0)throw new Exception(string.Join("\n",failures));
        }
        finally { Directory.Delete(directory,true); }
    }

    private static async Task Encode(string path,IBuffer pixels,long[] timestamps,long[] durations)
    {
        var properties=VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8,64,64);
        properties.FrameRate.Numerator=30; properties.FrameRate.Denominator=1;
        var source=new MediaStreamSource(new VideoStreamDescriptor(properties)) { BufferTime=TimeSpan.Zero };
        source.Starting+=(_,e)=>e.Request.SetActualStartPosition(TimeSpan.Zero);
        var index=0;
        source.SampleRequested+=(_,e)=>
        {
            if(index==timestamps.Length) { e.Request.Sample=null;return; }
            var pts=timestamps[index]-timestamps[0];
            var end=pts+durations[index];
            var sample=MediaStreamSample.CreateFromBuffer(pixels,TimeSpan.FromTicks(pts));
            sample.Duration=TimeSpan.FromTicks(end-pts);
            index++;
            e.Request.Sample=sample;
        };
        var profile=MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Audio=null;
        profile.Video.Width=64;profile.Video.Height=64;profile.Video.Bitrate=1_000_000;
        profile.Video.ProfileId=H264ProfileIds.Baseline;
        profile.Video.FrameRate.Numerator=30;profile.Video.FrameRate.Denominator=1;
        var folder=await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file=await folder.CreateFileAsync(Path.GetFileName(path),CreationCollisionOption.FailIfExists);
        using var output=await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var transcoder=new MediaTranscoder { HardwareAccelerationEnabled=false };
        var prepared=await transcoder.PrepareMediaStreamSourceTranscodeAsync(source,output,profile);
        if(!prepared.CanTranscode) throw new Exception($"Required H264 unavailable: {prepared.FailureReason}");
        await prepared.TranscodeAsync();
        Console.WriteLine($"ENCODE generated inputs={index}; bytes={output.Size}");
    }

    internal static void Read(string path,long[] timestamps,long[] durations)
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var reader=MediaFactory.MFCreateSourceReaderFromURL(path,null);
            using var native=reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream,0);
            var subtype=native.GetGUID(MediaTypeAttributeKeys.Subtype);
            if(subtype!=new Guid("34363248-0000-0010-8000-00aa00389b71"))throw new Exception("Not native H264");
            Console.WriteLine($"READ subtype={subtype}");
            bool audioAbsent=false;
            try { using var audio=reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream,0); }
            catch(SharpGen.Runtime.SharpGenException ex) when(ex.ResultCode.Code==unchecked((int)0xC00D36B3)) { audioAbsent=true; }
            if(!audioAbsent)throw new Exception("Unexpected audio stream");
            var expected=timestamps.Length;
            var stamps=new List<long>();
            var actualDurations=new List<long>();
            long end=0;
            while(true)
            {
                using var sample=reader.ReadSample(SourceReaderIndex.FirstVideoStream,SourceReaderControlFlag.None,out _,out var flags,out var pts);
                if(sample!=null) { stamps.Add(pts);actualDurations.Add(sample.SampleDuration);end=Math.Max(end,pts+sample.SampleDuration); }
                if((flags&SourceReaderFlag.EndOfStream)!=0) break;
                if(stamps.Count>expected+10)throw new Exception("Unexpected extra coded frames");
            }
            stamps.Sort();
            Console.WriteLine($"READ count={stamps.Count}; PTS100ns=[{string.Join(',',stamps)}]; durations100ns=[{string.Join(',',actualDurations)}]; end100ns={end}; no audio");
            if(stamps.Count!=expected || stamps[0]!=0) throw new Exception("Coded frame count/origin differs from source");
            for(int i=0;i<expected;i++) if(Math.Abs(stamps[i]-(timestamps[i]-timestamps[0]))>10)throw new Exception("Coded PTS changed");
            for(int i=0;i<expected;i++) if(Math.Abs(actualDurations[i]-durations[i])>10)throw new Exception("Coded sample duration changed");
            if(Math.Abs(end-(timestamps[^1]-timestamps[0]+durations[^1]))>10)throw new Exception("Coded duration changed");
            Console.WriteLine($"PASS native H264 readback {expected} frames, PTS, coded duration, no audio");
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }
}
