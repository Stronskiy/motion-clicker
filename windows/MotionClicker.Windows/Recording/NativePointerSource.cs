using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MotionClicker.Core;
using MotionClicker.Core.Recording;

namespace MotionClicker.Windows.Recording;

internal sealed class NativePointerSource : IWindowsPointerSource
{
    private const uint ProbeMessage=0x8001;
    private readonly Action<WindowsPointerSample> sample;
    private readonly Action<string> fault;
    private readonly long frequency;
    private readonly nuint ownInjectedTag;
    private readonly WindowsHookWatchdog watchdog=new(1);
    private readonly NativeRecordingInterop.HookProc callback;
    private readonly NativeRecordingInterop.WindowProc windowCallback;
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously),closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint threadId;
    private nint hook;
    private int started,stop;
    public Task Ready=>ready.Task;
    public Task Closed=>closed.Task;
    internal NativePointerSource(long frequency,nuint ownInjectedTag,Action<WindowsPointerSample> sample,Action<string> fault)
    {
        this.frequency=frequency;this.ownInjectedTag=ownInjectedTag;this.sample=sample;this.fault=fault;
        callback=Observe;windowCallback=WindowMessage;
    }
    public void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0)throw new RecordingException("Pointer source already started");
        new Thread(Run){IsBackground=true,Name="Motion Clicker passive pointer hook"}.Start();
    }
    public void RequestStop()
    {
        Interlocked.Exchange(ref stop,1);
        var id=Volatile.Read(ref threadId);
        if(id!=0)NativeRecordingInterop.PostThreadMessage(id,0x12,0,0);
        else if(Volatile.Read(ref started)==0){ready.TrySetException(new RecordingException("Pointer source stopped before start"));closed.TrySetResult();}
    }
    public void Probe(long nowUs)
    {
        if(Volatile.Read(ref stop)!=0||!ready.Task.IsCompletedSuccessfully)return;
        var previous=watchdog.Check(1,nowUs);
        if(previous is not null){fault(previous);return;}
        watchdog.Probe(1,nowUs);
        if(!NativeRecordingInterop.PostThreadMessage(Volatile.Read(ref threadId),ProbeMessage,(nuint)nowUs,0))Fail("Pointer hook probe posting failed");
        var desktop=NativeRecordingInterop.OpenInputDesktop(0,false,1);
        if(desktop==0){Fail("Interactive pointer desktop unavailable");return;}
        try
        {
            var name=new StringBuilder(256);
            if(!NativeRecordingInterop.GetUserObjectInformation(desktop,2,name,512,out _)||name.ToString()!="Default")Fail("Interactive pointer desktop changed");
        }
        finally{NativeRecordingInterop.CloseDesktop(desktop);}
    }
    private void Fail(string reason){watchdog.Lost(1,reason);fault(reason);}
    private nint Observe(int code,nuint message,nint pointer)
    {
        bool clockValid=NativeRecordingInterop.QueryPerformanceCounter(out var counter); // FIRST executable callback operation.
        try
        {
            long observed=0;
            if(clockValid){observed=WindowsRecordingClock.CounterToHostUs(counter,frequency);watchdog.CallbackEntered(1,observed);}
            if(!clockValid)Fail("Pointer QPC observation failed");
            else if(code>=0)
            {
                var packet=Marshal.PtrToStructure<NativeRecordingInterop.MousePacket>(pointer);
                var kind=ActionKind.Move;MouseButton? button=null;double? dx=null,dy=null;bool supported=true;
                if(packet.Extra==ownInjectedTag)supported=false;
                else switch(message)
                {
                    case 0x200:break;
                    case 0x201:kind=ActionKind.ButtonDown;button=MouseButton.Left;break;
                    case 0x202:kind=ActionKind.ButtonUp;button=MouseButton.Left;break;
                    case 0x204:kind=ActionKind.ButtonDown;button=MouseButton.Right;break;
                    case 0x205:kind=ActionKind.ButtonUp;button=MouseButton.Right;break;
                    case 0x207:kind=ActionKind.ButtonDown;button=MouseButton.Middle;break;
                    case 0x208:kind=ActionKind.ButtonUp;button=MouseButton.Middle;break;
                    case 0x20A:kind=ActionKind.Scroll;dx=0;dy=-(short)(packet.Data>>16)/120d;break;
                    case 0x20E:kind=ActionKind.Scroll;dx=(short)(packet.Data>>16)/120d;dy=0;break;
                    case 0x20B:case 0x20C:Fail("Unsupported pointer button observed");supported=false;break;
                    default:supported=false;break;
                }
                if(supported)sample(new(observed,packet.Time,new(packet.Point.X,packet.Point.Y),kind,button,dx,dy,packet.Extra));
            }
        }
        catch(Exception error){Fail("Pointer callback failed: "+error.Message);}
        try{return NativeRecordingInterop.CallNextHookEx(hook,code,message,pointer);}
        finally
        {
            try{watchdog.CallbackExited(1,NativeRecordingInterop.Now(frequency));}
            catch(Exception error){Fail("Pointer callback clock failed: "+error.Message);}
        }
    }
    private nint WindowMessage(nint window,uint message,nuint wp,nint lp)
    {
        if(message==0x218&&(wp==4||wp==7||wp==18))Fail("System sleep or resume interrupted recording");
        if(message==0x2B1)Fail("Windows session changed");
        if(message==0x7E||message==0x2E0)Fail("Display topology or DPI changed");
        return NativeRecordingInterop.DefWindowProc(window,message,wp,lp);
    }
    private void Run()
    {
        var name="MotionClickerRecording"+Guid.NewGuid().ToString("N");var module=NativeRecordingInterop.GetModuleHandle(null);
        nint window=0;bool registered=false,notifications=false;Exception? failure=null;
        try
        {
            Volatile.Write(ref threadId,NativeRecordingInterop.GetCurrentThreadId());NativeRecordingInterop.PeekMessage(out _,0,0,0,0);
            if(Volatile.Read(ref stop)!=0)throw new RecordingException("Pointer source canceled before installation");
            var cls=new NativeRecordingInterop.WindowClass{Procedure=windowCallback,Instance=module,Name=name};
            if(NativeRecordingInterop.RegisterClass(ref cls)==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Pointer safety window class failed");registered=true;
            window=NativeRecordingInterop.CreateWindowEx(0,name,"",0,0,0,0,0,0,0,module,0);
            if(window==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Pointer safety window failed");
            if(!NativeRecordingInterop.WTSRegisterSessionNotification(window,0))throw new Win32Exception(Marshal.GetLastWin32Error(),"Session notification registration failed");notifications=true;
            hook=NativeRecordingInterop.SetWindowsHookEx(14,callback,module,0);
            if(hook==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Passive pointer hook installation failed");
            ready.TrySetResult();
            while(Volatile.Read(ref stop)==0)
            {
                var result=NativeRecordingInterop.GetMessage(out var message,0,0,0);
                if(result<=0){if(Volatile.Read(ref stop)==0)throw new RecordingException("Pointer hook message pump exited");break;}
                if(message.Id==ProbeMessage)watchdog.Acknowledge(1,(long)message.WParam);
                else{NativeRecordingInterop.TranslateMessage(ref message);NativeRecordingInterop.DispatchMessage(ref message);}
            }
        }
        catch(Exception error){failure=error;ready.TrySetException(error);Fail(error.Message);}
        finally
        {
            if(hook!=0&&!NativeRecordingInterop.UnhookWindowsHookEx(hook))
            {
                failure??=new RecordingException("Passive pointer hook uninstallation failed");Fail(failure.Message);
                _=GCHandle.Alloc(this); // Unknown closure: keep callback delegates rooted; never publish this take.
            }
            hook=0;
            if(notifications&&!NativeRecordingInterop.WTSUnRegisterSessionNotification(window))failure??=new RecordingException("Session notification closure failed");
            if(window!=0&&!NativeRecordingInterop.DestroyWindow(window))failure??=new RecordingException("Pointer safety window closure failed");
            if(registered&&!NativeRecordingInterop.UnregisterClass(name,module))failure??=new RecordingException("Pointer safety class closure failed");
            if(failure is null)closed.TrySetResult();else closed.TrySetException(failure);
            // Thread roots both delegates until every callback/unhook has returned.
            GC.KeepAlive(callback);GC.KeepAlive(windowCallback);
        }
    }
}
