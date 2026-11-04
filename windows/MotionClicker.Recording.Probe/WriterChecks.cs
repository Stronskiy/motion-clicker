using MotionClicker.Windows.Recording;

static class WriterChecks
{
    internal static void Encode(string path,byte[] pixels,long[] timestamps,long[] durations)
    {
        using var writer=new NativeVideoWriter(path,64,64);
        for(int i=0;i<timestamps.Length;i++)writer.Append(pixels,timestamps[i],durations[i]);
        var result=writer.Finish();
        if(!result.Succeeded || result.FrameCount!=timestamps.Length || result.EncodedDurationTicks!=timestamps[^1]-timestamps[0]+durations[^1])
            throw new Exception($"Native writer metadata did not match accepted work: {result}");
        if(writer.Finish()!=result)throw new Exception("Writer finalization is not idempotent");
        using var exclusive=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        if(exclusive.Length==0)throw new Exception("Finalized encoder has no media");
    }

    internal static void Failures(string directory,byte[] pixels)
    {
        using(var zero=new NativeVideoWriter(Path.Combine(directory,"zero.mp4"),64,64))
        {
            var result=zero.Finish();
            if(result.Succeeded || result.FrameCount!=0)throw new Exception("Zero images labeled successful");
        }
        var existing=Path.Combine(directory,"prior.mp4");
        byte[] saved=[1,2,3,4,5];File.WriteAllBytes(existing,saved);
        bool rejected=false;
        try { using var overwrite=new NativeVideoWriter(existing,64,64); }
        catch { rejected=true; }
        if(!rejected || !File.ReadAllBytes(existing).SequenceEqual(saved))throw new Exception("Prior file not preserved");
        var sparsePath=Path.Combine(directory,"sparse-prefix.mp4");
        using(var sparse=new NativeVideoWriter(sparsePath,64,64))
        {
            sparse.Append(pixels,50_000_000,1_000_000);
            rejected=false;
            try { sparse.Append(pixels,90_000_000,1_000_000); }
            catch { rejected=true; }
            var result=sparse.Finish();
            if(!rejected || result.Succeeded || result.FrameCount!=1 || result.EncodedDurationTicks!=1_000_000)
                throw new Exception("Unsupported sparse continuation did not retain failed valid prefix");
        }
        Probe.Read(sparsePath,[0],[1_000_000]);
        Console.WriteLine("PASS writer zero-frame, idempotent finalization, nooverwrite and sparse-prefix failure/readback");
    }
}
