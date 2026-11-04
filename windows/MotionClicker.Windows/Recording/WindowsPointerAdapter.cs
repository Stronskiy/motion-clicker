using MotionClicker.Core;
using MotionClicker.Core.Recording;
using System.Globalization;

namespace MotionClicker.Windows.Recording;

public sealed record WindowsRecordingOptions(nuint OwnInjectedTag,Func<WindowsPointerSample,bool> IsControl);
public sealed record WindowsPointerSample(long ObservationHostUs,uint PacketTimeMs,Point NativePosition,
    ActionKind Kind,MouseButton? Button,double? DeltaX,double? DeltaY,nuint SourceTag);

internal sealed class WindowsPointerAdapter(WindowsRecordingOptions options)
{
    private MouseButton? held;
    private bool controlHeld;
    internal CapturedPointerEvent? Convert(WindowsPointerSample sample,long epochHostUs,long ownerHostUs)
    {
        if(sample.ObservationHostUs<epochHostUs || sample.SourceTag==options.OwnInjectedTag)return null;
        if(sample.ObservationHostUs<0 || ownerHostUs<sample.ObservationHostUs)
            throw new RecordingException("Invalid pointer observation/owner time");
        bool control=held is not null?controlHeld:options.IsControl(sample);
        switch(sample.Kind)
        {
            case ActionKind.ButtonDown:
                if(held is not null || sample.Button is null)throw new RecordingException("Repeated pointer ownership");
                held=sample.Button;controlHeld=control;break;
            case ActionKind.ButtonUp:
                if(held is null || held!=sample.Button)throw new RecordingException("Unmatched pointer release");
                held=null;controlHeld=false;break;
        }
        if(control)return null;
        var timing=new PointerTiming(PointerTimingBasis.NativeHostObservation,PointerHostClock.QueryPerformanceCounter,
            PointerPacketClock.WindowsLowLevelMouseTime,PointerPacketUnits.Milliseconds,
            sample.PacketTimeMs.ToString(CultureInfo.InvariantCulture),checked(ownerHostUs-sample.ObservationHostUs));
        return new(sample.ObservationHostUs,sample.NativePosition,sample.Kind,sample.Button,sample.DeltaX,sample.DeltaY,Timing:timing);
    }
}
