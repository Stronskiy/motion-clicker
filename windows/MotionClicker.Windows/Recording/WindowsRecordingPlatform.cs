using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

internal sealed record NativeWriterResult(bool Succeeded,long FrameCount,long EncodedDurationTicks,string? Failure,bool ClosureConfirmed=true);
internal sealed class NativeEncoderClosureException(string message) : Exception(message);

public sealed record WindowsRecordingDisplay(string MonitorSlotId,string DisplayId,int PixelWidth,int PixelHeight);
public sealed record WindowsRecordingPreparation(WindowsRecordingDisplay[] Displays);
public sealed record WindowsRecordingProgress(RecordingState State,long AcceptedFrames,long DroppedFrames,string? Reason);
public sealed record WindowsRecordingCompletion(RecordingManifest Manifest,string ManifestPath,StoreRecovery Recovery);

internal interface IWindowsRecordingSource
{
    Task Ready { get; }
    Task Closed { get; }
    void Start();
    void RequestStop();
}
internal interface IWindowsPointerSource : IWindowsRecordingSource
{
    void Probe(long nowUs);
}
internal interface IWindowsCaptureSource : IWindowsRecordingSource
{
    void BeginEpoch(long epochHostUs);
}
internal interface IWindowsVideoEncoder : IDisposable
{
    void Append(byte[] bgra,long sourceTicks,long durationTicks);
    NativeWriterResult Finish();
}
internal interface IWindowsRecordingPlatform
{
    long NowUs { get; }
    WindowsRecordingDisplay[] Validate(RecordingTarget[] targets);
    IWindowsCaptureSource Capture(RecordingTarget target,Action<WindowsVideoFrame> image,Action<long,string> drop,Action<string> fault);
    IWindowsPointerSource Pointer(Action<WindowsPointerSample> sample,Action<string> fault);
    IWindowsVideoEncoder Encoder(string path,int width,int height);
}
