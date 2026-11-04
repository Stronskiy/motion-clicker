using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

static class PixelChecks
{
    internal static byte[] Pattern()
    {
        var pixels=new byte[64*64*4];
        for(int y=0;y<64;y++)for(int x=0;x<64;x++)
        {
            var color=y<32?(x<32?(R:255,G:0,B:0):(R:0,G:255,B:0)):(x<32?(R:0,G:0,B:255):(R:255,G:255,B:0));
            int index=(y*64+x)*4;pixels[index]=(byte)color.B;pixels[index+1]=(byte)color.G;pixels[index+2]=(byte)color.R;pixels[index+3]=255;
        }
        return pixels;
    }
    internal static void DecodeAndCheck(string path)
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var reader=MediaFactory.MFCreateSourceReaderFromURL(path,null);
            using var format=MediaFactory.MFCreateMediaType();
            format.Set(MediaTypeAttributeKeys.MajorType,MediaTypeGuids.Video);
            format.Set(MediaTypeAttributeKeys.Subtype,new Guid("3231564e-0000-0010-8000-00aa00389b71")); // NV12, always top-down.
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream,format);
            for(int attempt=0;attempt<8;attempt++)
            {
                using var sample=reader.ReadSample(SourceReaderIndex.FirstVideoStream,SourceReaderControlFlag.None,out _,out var flags,out _);
                if(sample is not null)
                {
                    using var buffer=sample.ConvertToContiguousBuffer();buffer.Lock(out var address,out _,out var length);
                    byte[] bytes=new byte[length];try{Marshal.Copy(address,bytes,0,length);}finally{buffer.Unlock();}
                    if(length!=64*64*3/2)throw new Exception($"Unexpected decoded NV12 layout {length}");
                    Check(bytes,8,8,255,0,0);Check(bytes,56,8,0,255,0);
                    Check(bytes,8,56,0,0,255);Check(bytes,56,56,255,255,0);
                    Console.WriteLine("PASS actual H264 decoded asymmetric top/bottom/corner orientation and RGB channels (tolerance65)");return;
                }
                if((flags&SourceReaderFlag.EndOfStream)!=0)break;
            }
            throw new Exception("No decoded pixel sample");
        }
        finally{MediaFactory.MFShutdown().CheckError();}
    }
    private static void Check(byte[] bytes,int x,int y,int expectedR,int expectedG,int expectedB)
    {
        double luma=bytes[y*64+x]-16,u=bytes[64*64+(y/2)*64+(x/2)*2]-128,v=bytes[64*64+(y/2)*64+(x/2)*2+1]-128;
        double r=Math.Clamp(1.164*luma+1.596*v,0,255),g=Math.Clamp(1.164*luma-.392*u-.813*v,0,255),b=Math.Clamp(1.164*luma+2.017*u,0,255);
        Console.WriteLine($"DECODE ({x},{y}) RGB=({r:F1},{g:F1},{b:F1}) expected=({expectedR},{expectedG},{expectedB})");
        if(Math.Abs(r-expectedR)>65||Math.Abs(g-expectedG)>65||Math.Abs(b-expectedB)>65)
            throw new Exception($"Decoded orientation/channel mismatch at ({x},{y})");
    }
}
