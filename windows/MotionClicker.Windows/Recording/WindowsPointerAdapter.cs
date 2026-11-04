using MotionClicker.Core;
using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

public sealed record WindowsRecordingOptions(nuint OwnInjectedTag,Func<WindowsPointerSample,bool> IsControl);
public sealed record WindowsPointerSample(long ObservationHostUs,uint PacketTimeMs,Point NativePosition,
    ActionKind Kind,MouseButton? Button,double? DeltaX,double? DeltaY,nuint SourceTag);

internal sealed class WindowsPointerAdapter(WindowsRecordingOptions options)
{
    internal CapturedPointerEvent? Convert(WindowsPointerSample sample,long epochHostUs,long ownerHostUs)
    {
        _=options;
        throw new NotImplementedException();
    }
}
