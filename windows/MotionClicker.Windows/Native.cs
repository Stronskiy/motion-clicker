using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MotionClicker.Core;
using MotionClicker.Core.Runtime;

namespace MotionClicker.Windows;

internal static class Native {
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct MONITORINFO {
        public uint Size; public RECT Monitor,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
        public ushort SpecVersion,DriverVersion,Size,DriverExtra;
        public uint Fields; public int X,Y; public uint Orientation,FixedOutput;
        public short Color,Duplex,YResolution,TTOption,Collate;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Form;
        public ushort LogPixels;
        public uint BitsPerPel,Width,Height,Flags,Frequency,ICMMethod,ICMIntent,MediaType,DitherType,Reserved1,Reserved2,PanningWidth,PanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int X,Y; public uint Data,Flags,Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit,Size=40)] internal struct INPUT { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct MSLLHOOKSTRUCT { public POINT Pt; public uint Data,Flags,Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KBDLLHOOKSTRUCT { public uint Vk,Scan,Flags,Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MSG { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public POINT Point; public uint Private; }
    internal delegate bool MonitorEnum(nint h,nint dc,ref RECT rect,nint data);
    internal delegate nint HookProc(int code,nuint wParam,nint lParam);
    internal delegate nint SubclassProc(nint hwnd,uint message,nuint wp,nint lp,nuint id,nuint data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorEnum callback,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor,ref MONITORINFO info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device,int mode,ref DEVMODE value);
    [DllImport("shcore.dll")] internal static extern int GetScaleFactorForMonitor(nint monitor,out int scale);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd,out RECT rect);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] internal static extern uint SendInput(uint count,INPUT[] inputs,int size);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd,int id);
    [DllImport("comctl32.dll",SetLastError=true)] internal static extern bool SetWindowSubclass(nint hwnd,SubclassProc proc,nuint id,nuint data);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(nint hwnd,SubclassProc proc,nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint hwnd,uint message,nuint wp,nint lp);
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint SetWindowsHookEx(int id,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook,int code,nuint wp,nint lp);
    [DllImport("user32.dll")] internal static extern int GetMessage(out MSG msg,nint hwnd,uint first,uint last);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out MSG msg,nint hwnd,uint first,uint last,uint remove);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread,uint msg,nuint wp,nint lp);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetUserObjectInformation(nint handle,int index,System.Text.StringBuilder value,uint length,out uint needed);
    [DllImport("wtsapi32.dll")] internal static extern bool WTSRegisterSessionNotification(nint hwnd,uint flags);
    [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
internal sealed class WinMonitors : IMonitorProvider {
    public IReadOnlyList<RuntimeDisplay> Displays() {
        List<RuntimeDisplay> result=[];
        bool valid=true;
        Native.MonitorEnum callback=(nint handle,nint dc,ref Native.RECT rect,nint data)=> {
            var info=new Native.MONITORINFO { Size=(uint)Marshal.SizeOf<Native.MONITORINFO>(),Device="" };
            if(!Native.GetMonitorInfo(handle,ref info)||Native.GetScaleFactorForMonitor(handle,out var percent)!=0||percent<=0) { valid=false; return false; }
            var mode=new Native.DEVMODE { Size=(ushort)Marshal.SizeOf<Native.DEVMODE>(),Device="",Form="" };
            if(!Native.EnumDisplaySettings(info.Device,-1,ref mode)) { valid=false; return false; }
            var b=info.Monitor; double scale=percent/100.0;
            // Per-monitor origin is evidence only. Mapping always translates local logical units into this physical rectangle.
            var geometry=new DisplayGeometry(new(b.Left/scale,b.Top/scale),new((b.Right-b.Left)/scale,(b.Bottom-b.Top)/scale),scale,(int)mode.Orientation*90);
            result.Add(new(info.Device,$"{info.Device} · {geometry.Size.Width:0} × {geometry.Size.Height:0} · {percent}%",geometry,new(new(b.Left,b.Top),new(b.Right-b.Left,b.Bottom-b.Top))));
            return true;
        };
        if(!Native.EnumDisplayMonitors(0,0,callback,0)||!valid) return [];
        return result.OrderBy(d=>d.Id,StringComparer.Ordinal).ToArray();
    }
}
internal sealed class MonotonicClock : IMonotonicClock { public long NowUs => (long)(Stopwatch.GetTimestamp()*(1000000.0/Stopwatch.Frequency)); }
internal sealed class WinInput : IInputBackend,IDisposable {
    private static readonly nuint Tag=unchecked((nuint)0x4d434c49434bUL); // Native app is explicitly x64.
    private long interference;
    private nint mouseHook,keyboardHook;
    private uint hookThread;
    private readonly Native.HookProc mouseProc,keyboardProc;
    private Thread? thread;
    private volatile bool healthy;
    public WinInput() {
        mouseProc=(code,wp,lp)=> {
            if(code>=0) { var data=Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lp); if(data.Extra!=Tag) Interlocked.Increment(ref interference); }
            return Native.CallNextHookEx(mouseHook,code,wp,lp);
        };
        keyboardProc=(code,wp,lp)=> { if(code>=0) Interlocked.Increment(ref interference); return Native.CallNextHookEx(keyboardHook,code,wp,lp); };
    }
    public bool PhysicalButtonsDown => new[]{1,2,4}.Any(key=>(Native.GetAsyncKeyState(key)&0x8000)!=0);
    public ulong Interference => unchecked((ulong)Interlocked.Read(ref interference));
    public bool Ready {
        get {
            if(!healthy) return false;
            var desktop=Native.OpenInputDesktop(0,false,1);
            if(desktop==0) return false;
            try { var name=new System.Text.StringBuilder(256); return Native.GetUserObjectInformation(desktop,2,name,512,out _)&&name.ToString()=="Default"; }
            finally { Native.CloseDesktop(desktop); }
        }
    }
    public void BeginMonitoring() {
        if(healthy) return;
        if(thread!=null) throw new InvalidOperationException("Input monitor failed; restart the app before running");
        using var started=new ManualResetEventSlim();
        thread=new Thread(()=> {
            hookThread=Native.GetCurrentThreadId(); Native.PeekMessage(out _,0,0,0,0);
            mouseHook=Native.SetWindowsHookEx(14,mouseProc,Native.GetModuleHandle(null),0);
            keyboardHook=Native.SetWindowsHookEx(13,keyboardProc,Native.GetModuleHandle(null),0);
            healthy=mouseHook!=0&&keyboardHook!=0; started.Set();
            if(healthy) while(Native.GetMessage(out _,0,0,0)>0) { }
            healthy=false;
            if(mouseHook!=0) Native.UnhookWindowsHookEx(mouseHook);
            if(keyboardHook!=0) Native.UnhookWindowsHookEx(keyboardHook);
        }) { IsBackground=true,Name="Motion Clicker input safety monitor" };
        thread.Start();
        if(!started.Wait(TimeSpan.FromSeconds(3))||!Ready) throw new InvalidOperationException("Cannot monitor physical input or access the interactive desktop");
    }
    public Point Position() { if(!Native.GetCursorPos(out var p)) throw new Win32Exception(); return new(p.X,p.Y); }
    public void Send(RuntimeStep step,Point point) {
        int left=Native.GetSystemMetrics(76),top=Native.GetSystemMetrics(77),width=Native.GetSystemMetrics(78),height=Native.GetSystemMetrics(79);
        if(width<=1||height<=1) throw new InvalidOperationException("Invalid virtual desktop");
        var move=new Native.INPUT { Type=0,Mouse=new() { X=(int)Math.Round((point.X-left)*65535/(width-1)),Y=(int)Math.Round((point.Y-top)*65535/(height-1)),Flags=0x8000|0x4000|0x0001,Extra=Tag } };
        List<Native.INPUT> inputs=[move];
        if(step.Kind is ActionKind.ButtonDown or ActionKind.ButtonUp) {
            uint flag=step.Button switch { MouseButton.Left=>step.Kind==ActionKind.ButtonDown?2u:4u,MouseButton.Right=>step.Kind==ActionKind.ButtonDown?8u:16u,_=>step.Kind==ActionKind.ButtonDown?32u:64u };
            inputs.Add(new() { Type=0,Mouse=new() { Flags=flag,Extra=Tag } });
        } else if(step.Kind==ActionKind.Scroll) {
            // Canonical logical scroll units map to wheel deltas; quantization is explicit at the adapter boundary.
            uint Wheel(double value)=>(uint)unchecked((int)Math.Clamp(Math.Round(value*120),int.MinValue,int.MaxValue));
            if(step.DeltaY!=0) inputs.Add(new() { Type=0,Mouse=new() { Flags=0x0800,Data=Wheel(-step.DeltaY),Extra=Tag } });
            if(step.DeltaX!=0) inputs.Add(new() { Type=0,Mouse=new() { Flags=0x1000,Data=Wheel(step.DeltaX),Extra=Tag } });
        }
        if(Native.SendInput((uint)inputs.Count,inputs.ToArray(),Marshal.SizeOf<Native.INPUT>())!=inputs.Count)
            throw new Win32Exception(Marshal.GetLastWin32Error(),"SendInput failed or partially inserted events; elevated/secure targets are unsupported");
    }
    public void Release(MouseButton button,Point point) {
        // Emergency cleanup sends ONLY our owned up, without moving the physical user's pointer.
        uint flag=button switch { MouseButton.Left=>4u,MouseButton.Right=>16u,_=>64u };
        var input=new Native.INPUT { Type=0,Mouse=new() { Flags=flag,Extra=Tag } };
        if(Native.SendInput(1,[input],Marshal.SizeOf<Native.INPUT>())!=1) throw new Win32Exception(Marshal.GetLastWin32Error(),"Owned release failed");
    }
    public void Dispose() { healthy=false; if(hookThread!=0) Native.PostThreadMessage(hookThread,0x12,0,0); }
}
internal sealed class EmergencyHotkey : IDisposable {
    private readonly nint hwnd;
    private readonly Native.SubclassProc proc;
    private readonly Action stop;
    private bool registered;
    public EmergencyHotkey(nint hwnd,Action stop) {
        this.hwnd=hwnd; this.stop=stop;
        proc=(window,message,wp,lp,id,data)=> {
            if(message==0x312&&wp==1) stop();
            if(message==0x218 && (wp==4||wp==7||wp==18)) stop(); // suspend/resume boundary
            if(message==0x2B1 || message==0x7E || message==0x2E0) stop(); // session/display/DPI change
            return Native.DefSubclassProc(window,message,wp,lp);
        };
        if(!Native.SetWindowSubclass(hwnd,proc,1,0)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Safety window message registration failed");
        if(!Native.WTSRegisterSessionNotification(hwnd,0)) { Native.RemoveWindowSubclass(hwnd,proc,1); throw new Win32Exception(Marshal.GetLastWin32Error(),"Session notification registration failed"); }
    }
    public void Register(uint key) {
        Unregister();
        if(!Native.RegisterHotKey(hwnd,1,0x4000|1|2|8,key)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Emergency hotkey conflict. Choose another key.");
        registered=true;
    }
    public void Unregister() { if(registered) Native.UnregisterHotKey(hwnd,1); registered=false; }
    public void Dispose() { Unregister(); Native.WTSUnRegisterSessionNotification(hwnd); Native.RemoveWindowSubclass(hwnd,proc,1); }
}
