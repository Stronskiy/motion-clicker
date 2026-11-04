using System.Runtime.InteropServices;
using MotionClicker.Core.Recording;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace MotionClicker.Windows.Recording;

internal sealed class NativeWgcSource(RecordingTarget target,nint frozenMonitor,Action<WindowsVideoFrame> image,Action<long,string> drop,Action<string> fault) : IWindowsCaptureSource
{
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously),closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim stopSignal=new(false);
    private readonly ManualResetEventSlim callbacksChanged=new(false);
    private int started,stop,callbacks,copying;
    public Task Ready=>ready.Task;
    public Task Closed=>closed.Task;
    public void BeginEpoch(long epochHostUs) { }
    public void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0)throw new RecordingException("Capture source already started");
        new Thread(Run){IsBackground=true,Name="Motion Clicker WGC "+target.Slot.Id}.Start();
    }
    public void RequestStop()
    {
        Interlocked.Exchange(ref stop,1);stopSignal.Set();
        if(Volatile.Read(ref started)==0){ready.TrySetException(new RecordingException("Capture stopped before start"));closed.TrySetResult();}
    }
    private void Arrived(Direct3D11CaptureFramePool pool,object sender)
    {
        if(Volatile.Read(ref stop)!=0)return;
        Interlocked.Increment(ref callbacks);
        long? observedSourceTicks=null;
        try
        {
            if(Volatile.Read(ref stop)!=0)return;
            using var frame=pool.TryGetNextFrame();
            if(frame is null)return;
            var sourceTicks=frame.SystemRelativeTime.Ticks; // compositor QPC 100ns, never arrival time.
            observedSourceTicks=sourceTicks;
            if(sourceTicks<0)throw new RecordingException("Negative WGC source timestamp");
            if(frame.ContentSize.Width!=target.Display.NativeBounds.Size.Width||frame.ContentSize.Height!=target.Display.NativeBounds.Size.Height)
                throw new RecordingException("WGC source pixel size changed");
            if(Interlocked.CompareExchange(ref copying,1,0)!=0){drop(sourceTicks,"WGC owned-copy admission full");return;}
            try
            {
                // This free-threaded callback owns frame/surface until the asynchronous
                // GPU readback has COMPLETED. No UI or recording-owner dispatcher is used.
                using var bitmap=SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface,BitmapAlphaMode.Ignore).AsTask().GetAwaiter().GetResult();
                if(bitmap.BitmapPixelFormat!=BitmapPixelFormat.Bgra8)throw new RecordingException("Unsupported WGC color format");
                if(bitmap.PixelWidth!=target.Display.NativeBounds.Size.Width||bitmap.PixelHeight!=target.Display.NativeBounds.Size.Height)
                    throw new RecordingException("WGC surface dimensions differ from frozen source");
                var count=checked(bitmap.PixelWidth*bitmap.PixelHeight*4);
                var buffer=new global::Windows.Storage.Streams.Buffer((uint)count);bitmap.CopyToBuffer(buffer);
                var owned=new byte[count];using(var reader=DataReader.FromBuffer(buffer))reader.ReadBytes(owned);
                image(new(owned,sourceTicks)); // Owner admits only actual pre-stop source time during closure.
            }
            finally{Volatile.Write(ref copying,0);}
        }
        catch(Exception error)
        {
            // A genuine frame that failed ownership/copy is actual loss. End the
            // previous valid image there, never stretch stale pixels to arrival/stop.
            var reason="WGC source failed: "+error.Message;
            if(observedSourceTicks is long ticks&&ticks>=0)drop(ticks,reason);
            fault(reason);
        }
        finally{Interlocked.Decrement(ref callbacks);callbacksChanged.Set();}
    }
    private void ItemClosed(GraphicsCaptureItem sender,object args)=>fault("Selected WGC monitor source closed or access was lost");
    private void Run()
    {
        bool initialized=false;ID3D11Device? device=null;IDirect3DDevice? winrtDevice=null;
        Direct3D11CaptureFramePool? pool=null;GraphicsCaptureSession? capture=null;GraphicsCaptureItem? item=null;
        Exception? failure=null;
        try
        {
            Marshal.ThrowExceptionForHR(NativeRecordingInterop.RoInitialize(1));initialized=true;
            if(Volatile.Read(ref stop)!=0)throw new RecordingException("Capture canceled before start");
            if(!GraphicsCaptureSession.IsSupported())throw new RecordingException("Windows Graphics Capture is unsupported or unavailable");
            var mapped=NativeWindowsRecordingPlatform.Resolve(target);
            if(mapped.Handle!=frozenMonitor)throw new RecordingException("Frozen HMONITOR changed before WGC start");
            item=ForMonitor(mapped.Handle);
            if(item.Size.Width!=target.Display.NativeBounds.Size.Width||item.Size.Height!=target.Display.NativeBounds.Size.Height)
                throw new RecordingException("WGC item dimensions differ from frozen monitor");
            device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport);
            using(var dxgi=device.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(NativeRecordingInterop.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer,out var abi));
                try{winrtDevice=MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{Marshal.Release(abi);}
            }
            // The pool has two OS buffers; only one completed owned copy is admitted.
            pool=Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
            pool.FrameArrived+=Arrived;item.Closed+=ItemClosed;
            capture=pool.CreateCaptureSession(item);capture.IsCursorCaptureEnabled=false;
            if(Volatile.Read(ref stop)!=0)throw new RecordingException("Capture canceled before native start");
            capture.StartCapture();ready.TrySetResult();
            stopSignal.Wait();
        }
        catch(Exception error){failure=error;ready.TrySetException(error);fault(error.Message);}
        finally
        {
            Volatile.Write(ref stop,1);
            if(pool is not null)pool.FrameArrived-=Arrived;
            if(item is not null)item.Closed-=ItemClosed;
            // The controller has a bounded wait and quarantines if readback hangs.
            // Resource disposal belongs here, AFTER callbacks release their surfaces.
            while(Volatile.Read(ref callbacks)!=0){callbacksChanged.Wait(100);callbacksChanged.Reset();}
            void Close(Action action){try{action();}catch(Exception error){failure??=error;}}
            Close(()=>capture?.Dispose());Close(()=>pool?.Dispose());Close(()=>winrtDevice?.Dispose());Close(()=>device?.Dispose());
            if(initialized)NativeRecordingInterop.RoUninitialize();
            if(failure is null)closed.TrySetResult();else closed.TrySetException(failure);
        }
    }
    private static unsafe GraphicsCaptureItem ForMonitor(nint monitor)
    {
        const string name="Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(NativeRecordingInterop.WindowsCreateString(name,name.Length,out var hstring));
        nint factory=0,abi=0;
        try
        {
            var iid=new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(NativeRecordingInterop.RoGetActivationFactory(hstring,ref iid,out factory));
            var itemId=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var table=*(nint**)factory;
            var create=(delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)table[4]; // IUnknown + CreateForWindow + CreateForMonitor.
            Marshal.ThrowExceptionForHR(create(factory,monitor,&itemId,&abi));
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
        }
        finally
        {
            if(abi!=0)Marshal.Release(abi);if(factory!=0)Marshal.Release(factory);
            NativeRecordingInterop.WindowsDeleteString(hstring);
        }
    }
}
