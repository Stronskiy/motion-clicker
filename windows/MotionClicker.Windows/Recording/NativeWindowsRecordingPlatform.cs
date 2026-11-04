using System.ComponentModel;
using System.Runtime.InteropServices;
using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Core.Runtime;

namespace MotionClicker.Windows.Recording;

public sealed partial class WindowsRecordingController
{
    public WindowsRecordingController(WindowsRecordingOptions options):this(options,NativePlatform(options)) { }
    private static IWindowsRecordingPlatform NativePlatform(WindowsRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);return new NativeWindowsRecordingPlatform(options.OwnInjectedTag);
    }
}

internal sealed class NativeWindowsRecordingPlatform(nuint ownInjectedTag) : IWindowsRecordingPlatform
{
    private readonly long frequency=NativeRecordingInterop.Frequency();
    private readonly Dictionary<string,nint> frozenMonitors=new(StringComparer.Ordinal);
    public long NowUs=>NativeRecordingInterop.Now(frequency);
    public WindowsRecordingDisplay[] Validate(RecordingTarget[] targets)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)||!Environment.Is64BitProcess)
            throw new RecordingException("Recording requires Windows 11 x64");
        if(targets.Length==0)throw new RecordingException("No selected recording monitors");
        return targets.Select(t=>
        {
            var monitor=Resolve(t);var size=monitor.Display.NativeBounds.Size;
            if(!frozenMonitors.TryAdd(t.Display.Id,monitor.Handle))throw new RecordingException("Duplicate frozen native monitor");
            int width=checked((int)size.Width),height=checked((int)size.Height);
            if(width<=0||height<=0||width%2!=0||height%2!=0||(long)width*height>33_177_600)
                throw new RecordingException("Selected monitor has unsupported H264 dimensions");
            return new WindowsRecordingDisplay(t.Slot.Id,t.Display.Id,width,height);
        }).ToArray();
    }
    internal static (nint Handle,RuntimeDisplay Display) Resolve(RecordingTarget target)
    {
        nint found=0;RuntimeDisplay? selected=null;bool valid=true;
        NativeRecordingInterop.MonitorEnum callback=(nint handle,nint dc,ref NativeRecordingInterop.Rect rectangle,nint data)=>
        {
            var info=new NativeRecordingInterop.MonitorInfo{Size=(uint)Marshal.SizeOf<NativeRecordingInterop.MonitorInfo>(),Device=""};
            if(!NativeRecordingInterop.GetMonitorInfo(handle,ref info)){valid=false;return false;}
            if(info.Device!=target.Display.Id)return true;
            if(found!=0||NativeRecordingInterop.GetScaleFactorForMonitor(handle,out int percent)!=0||percent<=0){valid=false;return false;}
            var mode=new NativeRecordingInterop.DisplayMode{Size=(ushort)Marshal.SizeOf<NativeRecordingInterop.DisplayMode>(),Device="",Form=""};
            if(!NativeRecordingInterop.EnumDisplaySettings(info.Device,-1,ref mode)){valid=false;return false;}
            var b=info.Monitor;double scale=percent/100d;
            var geometry=new DisplayGeometry(new(b.Left/scale,b.Top/scale),new((b.Right-b.Left)/scale,(b.Bottom-b.Top)/scale),scale,(int)mode.Orientation*90);
            selected=new(info.Device,info.Device,geometry,new(new(b.Left,b.Top),new(b.Right-b.Left,b.Bottom-b.Top)));found=handle;return true;
        };
        if(!NativeRecordingInterop.EnumDisplayMonitors(0,0,callback,0)||!valid||found==0||selected is null)
            throw new Win32Exception("Exact selected MONITORINFO.Device/HMONITOR is unavailable");
        if(selected.NativeBounds!=target.Display.NativeBounds||selected.Geometry!=target.Display.Geometry||selected.Geometry!=target.Slot.Geometry)
            throw new RecordingException("Frozen monitor identity, bounds, scaling or rotation changed");
        return(found,selected);
    }
    public IWindowsRecordingSource Capture(RecordingTarget target,Action<WindowsVideoFrame> image,Action<long,string> drop,Action<string> fault)=>
        new NativeWgcSource(target,frozenMonitors[target.Display.Id],image,drop,fault);
    public IWindowsPointerSource Pointer(Action<WindowsPointerSample> sample,Action<string> fault)=>new NativePointerSource(frequency,ownInjectedTag,sample,fault);
    public IWindowsVideoEncoder Encoder(string path,int width,int height)=>new NativeVideoWriter(path,width,height);
}
