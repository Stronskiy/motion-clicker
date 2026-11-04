namespace MotionClicker.Windows.Recording;

internal sealed record NativeWriterResult(bool Succeeded,long FrameCount,long EncodedDurationTicks,string? Failure);
internal sealed class NativeVideoWriter : IDisposable
{
    internal NativeVideoWriter(string path,int width,int height) { }
    internal void Append(byte[] bgra,long sourceTicks,long durationTicks)=>throw new NotImplementedException();
    internal NativeWriterResult Finish()=>throw new NotImplementedException();
    public void Dispose() { }
}
