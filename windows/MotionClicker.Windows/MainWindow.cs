using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MotionClicker.Core;
using MotionClicker.Core.Runtime;
using global::Windows.Storage.Pickers;
using global::Windows.Graphics;
using Point=MotionClicker.Core.Point;
using RuntimeDisplay=MotionClicker.Core.Runtime.RuntimeDisplay;

namespace MotionClicker.Windows;

internal sealed class MainWindow : Window {
    private readonly WinMonitors monitors=new();
    private readonly WinInput backend=new();
    private readonly EmergencyHotkey emergency;
    private RunWorker? worker;
    private IReadOnlyList<RuntimeDisplay> displays=[];
    private readonly NavigationView navigation=new() { PaneDisplayMode=NavigationViewPaneDisplayMode.LeftCompact,IsBackButtonVisible=NavigationViewBackButtonVisible.Collapsed,IsSettingsVisible=false,OpenPaneLength=180 };
    private readonly StackPanel content=new() { Spacing=20,Padding=new Thickness(28),MaxWidth=1050,HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly StackPanel controls=new() { Spacing=16 };
    private readonly StackPanel displayChoices=new() { Spacing=8 };
    private readonly List<CheckBox> selected=[];
    private readonly List<PointRow> points=[];
    private readonly StackPanel pointList=new() { Spacing=8 };
    private readonly ComboBox button=new() { ItemsSource=new[]{"Left","Right","Middle"},SelectedIndex=0,Header="Mouse button",MinWidth=130 };
    private readonly CheckBox doubleClick=new() { Content="Double-click" };
    private readonly CheckBox currentPosition=new() { Content="Current pointer position" };
    private readonly NumberBox interval=Number("Current-position interval (ms)",100);
    private readonly NumberBox delay=Number("Start delay (seconds)",3);
    private readonly ComboBox limit=new() { ItemsSource=new[]{"Count","Duration","Infinite"},SelectedIndex=0,Header="Run limit",MinWidth=130 };
    private readonly NumberBox count=Number("Passes through points",10);
    private readonly NumberBox duration=Number("Duration (seconds)",10);
    private readonly ComboBox hotkey=new() { ItemsSource=new[]{"Ctrl + Alt + Win + F8","Ctrl + Alt + Win + F9"},SelectedIndex=0,Header="Emergency stop",MinWidth=230 };
    private readonly TextBlock status=new() { Text="Idle — no input is sent until you press Start",TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock metrics=new() { Text="Dispatched attempts: 0",Opacity=0.65 };
    private readonly ListView preview=new() { Height=150,SelectionMode=ListViewSelectionMode.None };
    private readonly Dictionary<string,ComboBox> mapping=[];
    private MotionProject? project;
    private string projectName="No project loaded";
    private bool projectMode;
    private readonly Button start=new() { Content="Start" },pause=new() { Content="Pause",IsEnabled=false },stop=new() { Content="Stop" };
    private readonly DispatcherTimer ticker=new() { Interval=TimeSpan.FromMilliseconds(100) };
    private readonly List<Window> identifiers=[];
    private bool Active=>worker?.Runner.Snapshot.State is RunnerState.Running or RunnerState.Armed;
    private sealed class PointRow {
        public Guid Id { get; }=Guid.NewGuid();
        public ComboBox Display { get; }=new() { Width=230,Header="Display" };
        public NumberBox X { get; }=Number("X",100);
        public NumberBox Y { get; }=Number("Y",100);
        public NumberBox Pause { get; }=Number("Pause (ms)",100);
    }
    public MainWindow() {
        Title="Motion Clicker";
        AppWindow.Resize(new SizeInt32(1100,880));
        try { SystemBackdrop=new MicaBackdrop(); } catch { /* Default theme background below remains usable. */ }
        Content=navigation;
        navigation.MenuItems.Add(new NavigationViewItem { Content="Quick Click",Icon=new SymbolIcon(Symbol.TouchPointer),Tag="quick" });
        navigation.MenuItems.Add(new NavigationViewItem { Content="Project",Icon=new SymbolIcon(Symbol.Document),Tag="project" });
        navigation.SelectionChanged+=(_,args)=> { projectMode=(args.SelectedItemContainer?.Tag as string)=="project"; RenderControls(); };
        var scroll=new ScrollViewer { Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        navigation.Content=scroll;
        var toolbar=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };
        toolbar.Children.Add(Action("Open Project…",async()=>await LoadProject()));
        toolbar.Children.Add(Action("Preview",DryRun)); toolbar.Children.Add(start); toolbar.Children.Add(pause); toolbar.Children.Add(stop);
        start.Click+=(_,_)=>Start(); pause.Click+=(_,_)=>Stop("Paused — Start restarts from the beginning",true); stop.Click+=(_,_)=>Stop();
        content.Children.Add(toolbar);
        content.Children.Add(new TextBlock { Text="Motion Clicker",Style=(Style)Application.Current.Resources["TitleTextBlockStyle"] });
        content.Children.Add(controls);
        content.Children.Add(new TextBlock { Text="Event preview · no input",Style=(Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        content.Children.Add(preview); content.Children.Add(status); content.Children.Add(metrics);
        content.Children.Add(new TextBlock { Text="Pause releases owned holds; Start restarts from the beginning. Physical input stops execution. One serialized pointer; no physical simultaneity. Recording, video and editing are unavailable in this preview.",TextWrapping=TextWrapping.Wrap,Opacity=0.65 });
        emergency=new(WinRT.Interop.WindowNative.GetWindowHandle(this),()=>Stop("Emergency, sleep, session or display change — restart required"));
        RefreshDisplays();
        navigation.SelectedItem=navigation.MenuItems[0];
        ticker.Tick+=(_,_)=>Poll(); ticker.Start();
        Closed+=(_,_)=> { Stop("Application closing"); ticker.Stop(); emergency.Dispose(); backend.Dispose(); };
    }
    private static NumberBox Number(string header,double value)=>new() { Header=header,Value=value,Width=145,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,SmallChange=1 };
    private static Button Action(string label,Action action) { var button=new Button { Content=label }; button.Click+=(_,_)=>action(); return button; }
    private static Button Action(string label,Func<Task> action) { var button=new Button { Content=label }; button.Click+=async(_,_)=>await action(); return button; }
    private static StackPanel Row(params UIElement[] elements) { var row=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 }; foreach(var e in elements) row.Children.Add(e); return row; }
    private void RefreshDisplays() {
        if(Active) return;
        displays=monitors.Displays();
        displayChoices.Children.Clear(); selected.Clear();
        foreach(var d in displays) { var check=new CheckBox { Content=d.Name,Tag=d.Id,IsChecked=selected.Count==0 }; selected.Add(check); displayChoices.Children.Add(check); }
        if(points.Count==0&&displays.Count>0) AddPoint();
        foreach(var point in points) { var old=(point.Display.SelectedItem as RuntimeDisplay)?.Id; point.Display.ItemsSource=displays; point.Display.DisplayMemberPath="Name"; point.Display.SelectedItem=displays.FirstOrDefault(d=>d.Id==old); }
    }
    private void AddPoint() {
        var row=new PointRow(); row.Display.ItemsSource=displays; row.Display.DisplayMemberPath="Name"; row.Display.SelectedItem=displays.FirstOrDefault(d=>selected.Any(s=>(string)s.Tag==d.Id&&s.IsChecked==true)); points.Add(row); RenderPoints();
    }
    private void RenderPoints() {
        pointList.Children.Clear();
        foreach(var p in points) {
            // Detach controls from old containers before reordering native UIElements.
            if(p.Display.Parent is Panel old) old.Children.Clear();
            var row=Row(p.Display,p.X,p.Y,p.Pause);
            row.Children.Add(Action("↑",()=>Move(p,-1))); row.Children.Add(Action("↓",()=>Move(p,1))); row.Children.Add(Action("−",()=> { points.Remove(p); RenderPoints(); }));
            pointList.Children.Add(new ScrollViewer { Content=row,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled });
        }
    }
    private void Move(PointRow p,int delta) { var i=points.IndexOf(p); if(i+delta<0||i+delta>=points.Count) return; (points[i],points[i+delta])=(points[i+delta],points[i]); RenderPoints(); }
    private void RenderControls() {
        // All reused controls must be detached first (WinUI elements have a single parent).
        foreach(var e in new FrameworkElement[]{displayChoices,pointList,button,doubleClick,currentPosition,interval,limit,count,duration,delay,hotkey}) if(e.Parent is Panel parent) parent.Children.Clear();
        controls.Children.Clear();
        if(projectMode) {
            controls.Children.Add(new TextBlock { Text=projectName,Style=(Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            mapping.Clear();
            if(project!=null) foreach(var slot in project.MonitorSlots) {
                var combo=new ComboBox { Header=$"{slot.Name} · {slot.Geometry.Size.Width:0} × {slot.Geometry.Size.Height:0}",PlaceholderText="Choose runtime display explicitly…",ItemsSource=displays,DisplayMemberPath="Name",MinWidth=380 };
                mapping.Add(slot.Id,combo); controls.Children.Add(combo);
            }
            controls.Children.Add(new TextBlock { Text="Every slot needs a distinct display with matching size, scale and rotation. Imports are never assigned to the primary display automatically.",TextWrapping=TextWrapping.Wrap });
        } else {
            controls.Children.Add(new TextBlock { Text="Quick Click",Style=(Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            controls.Children.Add(displayChoices); controls.Children.Add(Row(Action("Identify",Identify),Action("Refresh Displays",RefreshDisplays)));
            controls.Children.Add(Row(button,doubleClick,currentPosition));
            controls.Children.Add(interval);
            controls.Children.Add(Row(new TextBlock { Text="Ordered points · local logical coordinates",VerticalAlignment=VerticalAlignment.Center },Action("Add Point",AddPoint)));
            controls.Children.Add(pointList); RenderPoints();
            controls.Children.Add(Row(limit,count,duration));
            controls.Children.Add(new TextBlock { Text="Minimum interval / point pause: 20 ms (provisional). Count means full passes through the ordered list. Infinite mode is explicit and runs until Stop.",TextWrapping=TextWrapping.Wrap,Opacity=0.65 });
        }
        controls.Children.Add(Row(delay,hotkey));
    }
    private async Task LoadProject() {
        try {
            if(Active) return;
            var picker=new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file=await picker.PickSingleFileAsync(); if(file==null) return;
            if(new FileInfo(file.Path).Length>16777216) throw new InvalidOperationException("Project JSON exceeds 16 MB");
            var loaded=ProjectJson.Deserialize<MotionProject>(await File.ReadAllTextAsync(file.Path)); var plan=ProgramCompiler.Project(loaded);
            Stop("Project loaded"); project=loaded; projectName=file.Name; projectMode=true; navigation.SelectedItem=navigation.MenuItems[1]; RenderControls();
            ShowPreview(plan); status.Text="Map every imported monitor explicitly before Start. Domain events only; video is unavailable.";
        } catch(Exception error) { await Error($"Project cannot be loaded: {error.Message}"); }
    }
    private static long Microseconds(NumberBox box,double maxSeconds=604800) {
        if(!double.IsFinite(box.Value)||box.Value<0||box.Value>maxSeconds) throw new InvalidOperationException($"Invalid {box.Header}");
        return checked((long)(box.Value*1000000));
    }
    private RunProgram Program() {
        if(projectMode) return project!=null?ProgramCompiler.Project(project):throw new InvalidOperationException("Load a project first");
        var chosen=displays.Where(d=>selected.Any(s=>(string)s.Tag==d.Id&&s.IsChecked==true)).ToArray();
        if(chosen.Length==0) throw new InvalidOperationException("Select at least one display");
        var slots=chosen.Select(d=>new MonitorSlot(d.Id,d.Name,d.Geometry)).ToArray();
        var targets=new List<(string,Point?,long)>();
        if(currentPosition.IsChecked==true) {
            if(!double.IsFinite(interval.Value)||interval.Value<20||interval.Value>86400000) throw new InvalidOperationException("Interval must be 20 ms…one day");
            targets.Add((chosen[0].Id,null,(long)(interval.Value*1000)));
        } else foreach(var row in points) {
            if(row.Display.SelectedItem is not RuntimeDisplay display||!chosen.Any(d=>d.Id==display.Id)||!double.IsFinite(row.Pause.Value)||row.Pause.Value<20||row.Pause.Value>86400000)
                throw new InvalidOperationException("Choose a selected display and pause of at least 20 ms for every point");
            var point=new Point(row.X.Value,row.Y.Value);
            // Validate before arm/preview; no clamp, primary fallback or screen-edge spill.
            SafeRunner.NativePoint(point,display);
            targets.Add((display.Id,point,(long)(row.Pause.Value*1000)));
        }
        long? repetitions=null,durationUs=null;
        if(limit.SelectedIndex==0) {
            if(!double.IsFinite(count.Value)||count.Value<1||count.Value>int.MaxValue||Math.Truncate(count.Value)!=count.Value) throw new InvalidOperationException("Pass count must be a positive integer");
            repetitions=(long)count.Value;
        }
        if(limit.SelectedIndex==1) { durationUs=Microseconds(duration); if(durationUs==0) throw new InvalidOperationException("Duration must be positive"); }
        return ProgramCompiler.Quick(slots,targets,(MouseButton)button.SelectedIndex,doubleClick.IsChecked==true,repetitions,durationUs);
    }
    private void ShowPreview(RunProgram program)=>preview.ItemsSource=PreviewRunner.Events(program).Select(e=>$"{e.TimeUs/1000.0:0.0} ms · {e.Kind} · {e.Slot} · {(e.Point==null?"current pointer":$"({e.Point.X:0.##}, {e.Point.Y:0.##})")}").ToArray();
    private async void DryRun() { try { ShowPreview(Program()); status.Text="Dry run — first 200 events at most; zero input."; } catch(Exception e) { await Error(e.Message); } }
    private async void Start() {
        try {
            if(Active) return;
            var plan=Program(); var startDelay=Microseconds(delay,86400);
            emergency.Register(hotkey.SelectedIndex==0?0x77u:0x78u); backend.BeginMonitoring();
            var map=projectMode?mapping.ToDictionary(p=>p.Key,p=>(p.Value.SelectedItem as RuntimeDisplay)?.Id??""):plan.Slots.ToDictionary(s=>s.Id,s=>s.Id);
            var runner=worker?.Runner??new SafeRunner(new MonotonicClock(),monitors,backend); runner.Start(plan,map,startDelay,true);
            worker=new(runner); worker.Launch(); Poll();
        } catch(Exception e) { emergency.Unregister(); await Error($"Start blocked: {e.Message}"); }
    }
    private void Stop(string reason="Stopped",bool paused=false) { worker?.Stop(reason,paused); Poll(); }
    private void Poll() {
        var snapshot=worker?.Runner.Snapshot;
        if(snapshot!=null) { status.Text=snapshot.Reason; metrics.Text=$"{snapshot.State} · dispatched attempts: {snapshot.Dispatched} · maximum scheduler lag: {snapshot.MaxLagUs/1000.0:0.1} ms"; }
        controls.IsEnabled=!Active; start.IsEnabled=!Active; pause.IsEnabled=Active;
        if(!Active) emergency.Unregister();
    }
    private async Task Error(string text) { status.Text=text; if(Content is FrameworkElement root) await new ContentDialog { Title="Motion Clicker",Content=text,CloseButtonText="OK",XamlRoot=root.XamlRoot }.ShowAsync(); }
    private async void Identify() {
        foreach(var old in identifiers) old.Close(); identifiers.Clear();
        foreach(var d in displays) {
            var window=new Window { Title="Identify Display",Content=new Border { Padding=new Thickness(24),Background=(Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],Child=new TextBlock { Text=d.Name,TextWrapping=TextWrapping.Wrap,FontSize=28 } } };
            window.AppWindow.MoveAndResize(new RectInt32((int)d.NativeBounds.Origin.X+40,(int)d.NativeBounds.Origin.Y+40,500,150)); window.Activate(); identifiers.Add(window);
        }
        await Task.Delay(2000); foreach(var window in identifiers) window.Close(); identifiers.Clear();
    }
}
