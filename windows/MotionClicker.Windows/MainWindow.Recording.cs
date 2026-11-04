using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Core.Runtime;
using MotionClicker.Windows.RecordingUI;
using global::Windows.Storage.Pickers;
namespace MotionClicker.Windows;

internal sealed partial class MainWindow {
    private readonly AppOperationGate operations=new();
    private readonly ControlRegions controlRegions=new();
    private NativeRecordingServices recordingServices=null!;
    private RecordingWorkspace recording=null!;
    private readonly StackPanel recordingChoices=new() { Spacing=8 };
    private readonly List<CheckBox> captureDisplays=[];
    private readonly TextBox bundleName=new() { Header="New recording name",Text="Recording",PlaceholderText="Recording name",HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly TextBlock destinationText=new() { Text="Choose a parent folder. A new owned .motionclicker folder will be created before capture.",TextWrapping=TextWrapping.Wrap,FontSize=12 };
    private readonly NumberBox recordingDelay=Number("Recording countdown (s)",3);
    private readonly TextBlock recordingStatus=new() { TextWrapping=TextWrapping.Wrap };
    private readonly Button chooseDestination=new() { Content="Choose destination folder…" },recordStart=new() { Content="Record selected displays" },openSaved=new() { Content="Open saved recording…" },retrySave=new() { Content="Retry initial save",Visibility=Visibility.Collapsed };
    private string? destinationParent;
    private bool recordMode,runStarting,closingRequested,closeApproved,finishingClose,pollingRecording;
    private IDisposable? executionLease;
    private BundleInspection? bundleInspection;
    private SavedVideoPanel? savedVideo;
    private CancellationTokenSource? openingCancellation;
    private Task? openingTask;
    private void InitializeRecording() {
        recordingServices=new(controlRegions);recording=new(recordingServices,operations);
        navigation.MenuItems.Add(new NavigationViewItem { Content="Record",Icon=new SymbolIcon(Symbol.Video),Tag="record" });
        chooseDestination.Click+=async(_,_)=>await ChooseDestinationAsync();
        recordStart.Style=(Style)Application.Current.Resources["AccentButtonStyle"];
        recordStart.Click+=async(_,_)=>await RecordAsync();
        openSaved.Click+=async(_,_)=>await OpenSavedAsync();
        retrySave.Click+=async(_,_)=>{await recording.RetrySaveAsync();Poll();};
        AppWindow.Closing+=(_,args)=> {
            if(closeApproved)return;
            args.Cancel=true;closingRequested=true;
            DispatcherQueue.TryEnqueue(async()=>await CompleteCloseAsync());
        };
        Activated+=(_,_)=>UpdateControlRegions();
    }
    private void UpdateControlRegions() {
        var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
        if(Native.IsWindowVisible(hwnd)&&!Native.IsIconic(hwnd)&&Native.GetWindowRect(hwnd,out var r))
            controlRegions.Update([new(new(r.Left,r.Top),new(r.Right-r.Left,r.Bottom-r.Top))]);
        else controlRegions.Update([]);
    }
    private void RefreshRecordingDisplays() {
        var previous=captureDisplays.Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToHashSet();
        captureDisplays.Clear();recordingChoices.Children.Clear();
        foreach(var display in displays) {
            var check=new CheckBox { Content=$"{display.Name} · {display.NativeBounds.Size.Width:0} × {display.NativeBounds.Size.Height:0} pixels · {display.Geometry.ScaleFactor*100:0}%",Tag=display.Id,IsChecked=previous.Contains(display.Id) };
            captureDisplays.Add(check);recordingChoices.Children.Add(check);
        }
    }
    private void RenderRecordingControls() {
        foreach(var element in new FrameworkElement[]{recordingChoices,bundleName,destinationText,recordingDelay,chooseDestination,recordStart,recordingStatus,retrySave,openSaved})if(element.Parent is Panel parent)parent.Children.Clear();
        heading.Text="Record";
        controls.Children.Add(new TextBlock { Text="Record original display video and pointer actions. Capture begins only after Record and the countdown. Stop finalizes and saves this take.",TextWrapping=TextWrapping.Wrap });
        controls.Children.Add(Section("Displays to capture"));controls.Children.Add(recordingChoices);
        controls.Children.Add(bundleName);controls.Children.Add(chooseDestination);controls.Children.Add(destinationText);controls.Children.Add(recordStart);controls.Children.Add(recordingStatus);controls.Children.Add(retrySave);controls.Children.Add(openSaved);
        inspector.Children.Add(Section("Recording"));inspector.Children.Add(recordingDelay);inspector.Children.Add(hotkey);
        inspector.Children.Add(new TextBlock { Text="Windows 11 x64 · H.264 video · no audio\nPreparation validates displays and storage without capturing pixels. Windows capture/input support and current desktop access are checked at deliberate start. Secure/elevated targets are unsupported.\n\nEmergency stop remains available while capture is owned. Source video stays immutable.",TextWrapping=TextWrapping.Wrap,FontSize=12 });
    }
    private async Task<string?> PickFolderAsync() {
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        return(await picker.PickSingleFolderAsync())?.Path;
    }
    private async Task ChooseDestinationAsync() {
        using var lease=operations.TryEnter(AppOperation.ChoosingDestination);if(lease is null)return;
        Poll();
        try {
            var folder=await PickFolderAsync();if(folder is null||closingRequested)return;
            var free=await Task.Run(()=>new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace);
            destinationParent=folder;destinationText.Text=$"{folder}\n{free/1_073_741_824d:0.00} GB available · creates a new .motionclicker folder; existing data is never replaced.";
        } catch(Exception error){status.Text="Destination unavailable: "+error.Message;}
        finally{lease.Dispose();Poll();if(closingRequested)_=CompleteCloseAsync();}
    }
    private async Task RecordAsync() {
        try {
            if(Active)return;
            if(destinationParent is null)throw new RecordingException("Choose a destination folder before recording");
            var name=bundleName.Text.Trim();
            if(name.Length==0||name.Length>100||name is "." or ".."||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.EndsWith('.')||name.EndsWith(' '))throw new RecordingException("Enter a valid new recording name");
            var root=Path.Combine(destinationParent,name+".motionclicker");
            var targets=displays.Where(d=>captureDisplays.Any(c=>(string)c.Tag==d.Id&&c.IsChecked==true)).Select((d,i)=>new RecordingTarget(new("display-"+(i+1),DisplayName(d),d.Geometry),d)).ToArray();
            if(!double.IsFinite(recordingDelay.Value)||recordingDelay.Value<0||recordingDelay.Value>60||recordingDelay.Value!=Math.Truncate(recordingDelay.Value))throw new RecordingException("Countdown must be an integer from0 to60seconds");
            savedVideo?.StopPlayback();UpdateControlRegions();emergency.Register(hotkey.SelectedIndex==0?0x77u:0x78u);
            var task=recording.RecordAsync(targets,root,(int)recordingDelay.Value);Poll();await task;
        } catch(Exception error){await Error("Record blocked: "+error.Message);}
        finally{Poll();}
    }
    private async Task OpenSavedAsync() {
        using var lease=operations.TryEnter(AppOperation.Opening);if(lease is null)return;
        Poll();savedVideo?.StopPlayback();
        try {
            var path=await PickFolderAsync();if(path is null||closingRequested)return;
            await InspectBundleAsync(path);
        } catch(Exception error){status.Text="Saved recording cannot be opened: "+error.Message;recordingStatus.Text=status.Text;}
        finally {lease.Dispose();Poll();if(closingRequested)_=CompleteCloseAsync();}
    }
    private async Task InspectBundleAsync(string path) {
        openingCancellation?.Dispose();openingCancellation=new();
        var token=openingCancellation.Token;
        status.Text="Opening saved source · validating journals and native video…";
        var task=BundleInspection.OpenAsync(path,token);openingTask=task;
        var opened=await task;
        if(token.IsCancellationRequested||closingRequested)return;
        savedVideo?.Dispose();bundleInspection=opened;project=opened.Project;projectName=Path.GetFileName(path);mapping.Clear();savedVideo=new(opened);
        projectMode=true;recordMode=false;navigation.SelectedItem=navigation.MenuItems[1];RenderControls();
        status.Text=opened.CanRun?"Source validated. Map every display explicitly before Run.":"Inspection only · partial or unsupported source cannot run.";
    }
    private async Task CompleteCloseAsync() {
        if(finishingClose||closeApproved)return;finishingClose=true;
        try {
            status.Text="Closing · waiting for owned work to stop safely…";
            openingCancellation?.Cancel();
            await recording.StopAsync("Application closing");
            if(worker is not null)await worker.StopAsync("Application closing");
            if(openingTask is not null)try{await openingTask;}catch{ }
            Poll();
            if(recording.Busy||worker?.Snapshot.OwnedButton is not null){status.Text="Close blocked until owned cleanup is acknowledged. Retry Stop or use the emergency shortcut.";return;}
            if(operations.Busy){status.Text="Finish the owned folder/file picker before closing.";return;}
            savedVideo?.Dispose();closeApproved=true;DispatcherQueue.TryEnqueue(Close);
        } finally {finishingClose=false;}
    }
    private async Task PollRecordingAsync() {
        if(pollingRecording)return;pollingRecording=true;
        try {var progress=await recording.PollProgressAsync();if(progress is not null&&!closeApproved)metrics.Text=progress;}
        finally {pollingRecording=false;}
    }
}
