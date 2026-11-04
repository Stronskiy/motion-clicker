using System.Runtime.InteropServices;
using System.Text;

namespace MotionClicker.Windows.Recording;

internal static class NativeRecordingInterop
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct MonitorInfo
    {
        public uint Size;public Rect Monitor,Work;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;
        public ushort SpecVersion,DriverVersion,Size,DriverExtra;
        public uint Fields;public int X,Y;public uint Orientation,FixedOutput;
        public short Color,Duplex,YResolution,TTOption,Collate;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Form;
        public ushort LogPixels;public uint BitsPerPel,Width,Height,Flags,Frequency,ICMMethod,ICMIntent,MediaType,DitherType,Reserved1,Reserved2,PanningWidth,PanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)]internal struct MousePacket {public Point Point;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]internal struct Message {public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public Point Point;public uint Private;}
    internal delegate bool MonitorEnum(nint monitor,nint dc,ref Rect rect,nint data);
    internal delegate nint HookProc(int code,nuint message,nint packet);
    internal delegate nint WindowProc(nint window,uint message,nuint wp,nint lp);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct WindowClass
    {
        public uint Style;public WindowProc Procedure;public int ClassExtra,WindowExtra;
        public nint Instance,Icon,Cursor,Background;public string? Menu;public string Name;
    }
    [DllImport("kernel32.dll")]internal static extern bool QueryPerformanceCounter(out long counter);
    [DllImport("kernel32.dll")]internal static extern bool QueryPerformanceFrequency(out long frequency);
    [DllImport("kernel32.dll")]internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]internal static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorEnum callback,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool EnumDisplaySettings(string device,int mode,ref DisplayMode value);
    [DllImport("shcore.dll")]internal static extern int GetScaleFactorForMonitor(nint monitor,out int scale);
    [DllImport("user32.dll",SetLastError=true)]internal static extern nint SetWindowsHookEx(int kind,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll",SetLastError=true)]internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]internal static extern nint CallNextHookEx(nint hook,int code,nuint message,nint packet);
    [DllImport("user32.dll")]internal static extern int GetMessage(out Message message,nint window,uint first,uint last);
    [DllImport("user32.dll")]internal static extern bool PeekMessage(out Message message,nint window,uint first,uint last,uint remove);
    [DllImport("user32.dll")]internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll",SetLastError=true)]internal static extern bool PostThreadMessage(uint thread,uint message,nuint wp,nint lp);
    [DllImport("user32.dll")]internal static extern nint DefWindowProc(nint window,uint message,nuint wp,nint lp);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool UnregisterClass(string name,nint instance);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern nint CreateWindowEx(uint extended,string name,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]internal static extern bool DestroyWindow(nint window);
    [DllImport("wtsapi32.dll",SetLastError=true)]internal static extern bool WTSRegisterSessionNotification(nint window,uint flags);
    [DllImport("wtsapi32.dll")]internal static extern bool WTSUnRegisterSessionNotification(nint window);
    [DllImport("user32.dll")]internal static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")]internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool GetUserObjectInformation(nint handle,int index,StringBuilder value,uint length,out uint needed);
    [DllImport("combase.dll")]internal static extern int RoInitialize(uint mode);
    [DllImport("combase.dll")]internal static extern void RoUninitialize();
    [DllImport("combase.dll",CharSet=CharSet.Unicode)]internal static extern int WindowsCreateString(string value,int length,out nint result);
    [DllImport("combase.dll")]internal static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")]internal static extern int RoGetActivationFactory(nint name,ref Guid iid,out nint factory);
    [DllImport("d3d11.dll",ExactSpelling=true)]internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device,out nint result);

    internal static long Frequency()
    {
        if(!QueryPerformanceFrequency(out var frequency)||frequency<=0)throw new InvalidOperationException("QPC frequency unavailable");
        return frequency;
    }
    internal static long Now(long frequency)
    {
        if(!QueryPerformanceCounter(out var counter))throw new InvalidOperationException("QPC observation failed");
        return WindowsRecordingClock.CounterToHostUs(counter,frequency);
    }
}
