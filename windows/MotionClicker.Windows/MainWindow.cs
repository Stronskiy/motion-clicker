using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
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
    private readonly NavigationView navigation=new() { PaneDisplayMode=NavigationViewPaneDisplayMode.Left,IsBackButtonVisible=NavigationViewBackButtonVisible.Collapsed,IsSettingsVisible=false,OpenPaneLength=184,IsPaneOpen=true };
    private readonly Grid workspace=new();
    private readonly StackPanel content=new() { Spacing=20,Padding=new Thickness(24) };
    private readonly StackPanel controls=new() { Spacing=16 };
    private readonly ContentControl controlsHost=new() { HorizontalContentAlignment=HorizontalAlignment.Stretch };
    private readonly StackPanel inspector=new() { Spacing=16,Padding=new Thickness(20) };
    private readonly Expander inspectorHost=new() { Header="Inspector",IsExpanded=true,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch };
    private readonly StackPanel displayChoices=new() { Spacing=8 };
    private readonly List<CheckBox> selected=[];
    private readonly List<PointRow> points=[];
    private readonly ListView pointList=new() { Height=128,SelectionMode=ListViewSelectionMode.Single };
    private readonly StackPanel pointEditor=new() { Spacing=10 };
    private readonly MonitorMap monitorMap=new();
    private PointRow? focusedPoint;
    private readonly ComboBox button=new() { ItemsSource=new[]{"Left","Right","Middle"},SelectedIndex=0,Header="Mouse button",HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly CheckBox doubleClick=new() { Content="Double-click" };
    private readonly CheckBox currentPosition=new() { Content="Current pointer position" };
    private readonly NumberBox interval=Number("Interval (ms)",100);
    private readonly NumberBox delay=Number("Start delay (s)",3);
    private readonly ComboBox limit=new() { ItemsSource=new[]{"Count","Duration","Infinite"},SelectedIndex=0,Header="Run limit",HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly NumberBox count=Number("Passes",10);
    private readonly NumberBox duration=Number("Duration (s)",10);
    private readonly TextBlock infiniteHint=new() { Text="Runs until stopped. Keep the emergency shortcut available.",FontSize=12,TextWrapping=TextWrapping.Wrap,Opacity=0.7 };
    private readonly TextBlock currentHint=new() { Text="Position the pointer during the start delay. Moving it during a run stops execution.",FontSize=12,TextWrapping=TextWrapping.Wrap,Opacity=0.7 };
    private readonly ComboBox hotkey=new() { ItemsSource=new[]{"Ctrl + Alt + Win + F8","Ctrl + Alt + Win + F9"},SelectedIndex=0,Header="Emergency stop",HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly TextBlock status=new() { Text="Idle — no input is sent until you press Run",TextWrapping=TextWrapping.Wrap,FontSize=12 };
    private readonly TextBlock metrics=new() { Text="Dispatched attempts: 0",Opacity=0.7,TextWrapping=TextWrapping.Wrap,FontSize=12 };
    private readonly TextBlock stopShortcut=new() { FontSize=12,Opacity=0.7 };
    private readonly TextBlock heading=new() { FontSize=26,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly ListView preview=new() { Height=128,SelectionMode=ListViewSelectionMode.None };
    private readonly TextBlock previewEmpty=new() { Text="Preview the event order without sending input.",Opacity=0.7,TextWrapping=TextWrapping.Wrap };
    private readonly Dictionary<string,ComboBox> mapping=[];
    private MotionProject? project;
    private string projectName="No project loaded";
    private bool projectMode;
    private readonly Button start=new() { Content="Run" },pause=new() { Content="Pause",IsEnabled=false },stop=new() { Content="Stop",IsEnabled=false };
    private Button previewButton=null!;
    private Button openButton=null!;
    private readonly DispatcherTimer ticker=new() { Interval=TimeSpan.FromMilliseconds(100) };
    private readonly List<Window> identifiers=[];
    private bool Active=>worker?.Runner.Snapshot.State is RunnerState.Running or RunnerState.Armed;
    private sealed class PointRow {
        public Guid Id { get; }=Guid.NewGuid();
        public ComboBox Display { get; }=new() { Header="Display",HorizontalAlignment=HorizontalAlignment.Stretch };
        public NumberBox X { get; }=Number("X (pt)",100);
        public NumberBox Y { get; }=Number("Y (pt)",100);
        public NumberBox Pause { get; }=Number("Pause (ms)",100);
        public TextBlock Summary { get; }=new() { FontSize=13,TextWrapping=TextWrapping.Wrap };
    }
    public MainWindow() {
        Title="Motion Clicker"; AppWindow.Resize(new SizeInt32(1180,800));
        if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) { presenter.PreferredMinimumWidth=980; presenter.PreferredMinimumHeight=720; }
        try { SystemBackdrop=new MicaBackdrop(); } catch { /* Native fallback. */ }
        Content=navigation;
        navigation.MenuItems.Add(new NavigationViewItem { Content="Quick Click",Icon=new SymbolIcon(Symbol.TouchPointer),Tag="quick" });
        navigation.MenuItems.Add(new NavigationViewItem { Content="Project",Icon=new SymbolIcon(Symbol.Document),Tag="project" });
        navigation.SelectionChanged+=(_,args)=> { projectMode=(args.SelectedItemContainer?.Tag as string)=="project"; RenderControls(); };
        var shell=new Grid(); shell.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) }); shell.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        navigation.Content=shell;
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) }); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(290) });
        workspace.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); workspace.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        workspace.Children.Add(content); inspectorHost.Content=inspector; workspace.Children.Add(inspectorHost); Grid.SetColumn(inspectorHost,1);
        controlsHost.Content=workspace;
        shell.Children.Add(new ScrollViewer { Content=controlsHost,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        shell.SizeChanged+=(_,_)=> {
            var narrow=shell.ActualWidth<900;
            workspace.ColumnDefinitions[1].Width=new GridLength(narrow?0:290);
            Grid.SetColumn(inspectorHost,narrow?0:1); Grid.SetRow(inspectorHost,narrow?1:0);
            if(inspectorHost.Tag is not bool old||old!=narrow) { inspectorHost.IsExpanded=!narrow; inspectorHost.Tag=narrow; }
        };
        content.Children.Add(heading); content.Children.Add(controls);
        var previewContents=new StackPanel { Spacing=8 }; previewContents.Children.Add(previewEmpty); previewContents.Children.Add(preview);
        content.Children.Add(new Expander { Header="Event preview · no input",IsExpanded=true,Content=previewContents,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch });
        var transport=new StackPanel { Spacing=10,Padding=new Thickness(24,14,24,14) }; Grid.SetRow(transport,1); shell.Children.Add(transport);
        var actions=new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var summary=new StackPanel { Spacing=4 }; summary.Children.Add(new TextBlock { Text="Inspect or run",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold }); summary.Children.Add(stopShortcut); actions.Children.Add(summary);
        previewButton=Action("Preview",DryRun); openButton=Action("Open Project…",async()=>await LoadProject());
        var buttons=Row(previewButton,start,pause,stop); Grid.SetColumn(buttons,1); actions.Children.Add(buttons); transport.Children.Add(actions);
        var details=new StackPanel { Spacing=5 }; details.Children.Add(status); details.Children.Add(metrics); details.Children.Add(new TextBlock { Text="Pause releases a hold. Run restarts from the beginning.",FontSize=12,Opacity=0.7 });
        transport.Children.Add(new Expander { Header="Run details",Content=details,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch });
        start.Style=(Style)Application.Current.Resources["AccentButtonStyle"];
        start.Click+=(_,_)=>Start(); pause.Click+=(_,_)=>Stop("Paused — Run restarts from the beginning",true); stop.Click+=(_,_)=>Stop();
        pointList.SelectionChanged+=(_,_)=> { if(pointList.SelectedItem is ListViewItem item&&item.Tag is PointRow p) { focusedPoint=p; RenderPointEditor(); UpdateMap(); } };
        currentPosition.Checked+=(_,_)=>UpdateRelevantControls(); currentPosition.Unchecked+=(_,_)=>UpdateRelevantControls();
        limit.SelectionChanged+=(_,_)=>UpdateRelevantControls(); hotkey.SelectionChanged+=(_,_)=>UpdateShortcut();
        emergency=new(WinRT.Interop.WindowNative.GetWindowHandle(this),()=>Stop("Emergency, sleep, session or display change — restart required"));
        RefreshDisplays(); navigation.SelectedItem=navigation.MenuItems[0];
        ticker.Tick+=(_,_)=>Poll(); ticker.Start();
        Closed+=(_,_)=> { Stop("Application closing"); ticker.Stop(); emergency.Dispose(); backend.Dispose(); };
        ApplyVisualQAArguments(); UpdateShortcut();
    }
    private static NumberBox Number(string header,double value)=>new() { Header=header,Value=value,MinWidth=70,HorizontalAlignment=HorizontalAlignment.Stretch,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,SmallChange=1 };
    private static Button Action(string label,Action action) { var b=new Button { Content=label }; b.Click+=(_,_)=>action(); return b; }
    private static Button Action(string label,Func<Task> action) { var b=new Button { Content=label }; b.Click+=async(_,_)=>await action(); return b; }
    private static StackPanel Row(params UIElement[] elements) { var row=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 }; foreach(var e in elements) row.Children.Add(e); return row; }
    private static TextBlock Section(string text)=>new() { Text=text,FontSize=13,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold };
    private string DisplayName(RuntimeDisplay d)=>$"Display {displays.ToList().FindIndex(x=>x.Id==d.Id)+1}";
    private void RefreshDisplays() {
        if(Active) return;
        var previous=selected.Where(s=>s.IsChecked==true).Select(s=>(string)s.Tag).ToHashSet();
        displays=monitors.Displays().Select((d,i)=>d with { Name=$"Display {i+1} · {d.Geometry.Size.Width:0.##} × {d.Geometry.Size.Height:0.##} pt" }).ToArray(); displayChoices.Children.Clear(); selected.Clear();
        foreach(var d in displays) {
            var check=new CheckBox { Content=$"{DisplayName(d)} · {d.Geometry.Size.Width:0.##} × {d.Geometry.Size.Height:0.##} pt",Tag=d.Id,IsChecked=previous.Count==0?selected.Count==0:previous.Contains(d.Id) };
            ToolTipService.SetToolTip(check,d.Name); check.Checked+=(_,_)=>UpdateMap(); check.Unchecked+=(_,_)=>UpdateMap(); selected.Add(check); displayChoices.Children.Add(check);
        }
        if(points.Count==0&&displays.Count>0) AddPoint();
        foreach(var p in points) { var old=(p.Display.SelectedItem as RuntimeDisplay)?.Id; SetPointDisplays(p); p.Display.SelectedItem=displays.FirstOrDefault(d=>d.Id==old); }
        UpdateMap(); UpdatePointSummaries();
    }
    private void SetPointDisplays(PointRow p) {
        p.Display.ItemsSource=displays;
        // Native templates keep runtime objects in selection while showing stable friendly labels.
        p.Display.DisplayMemberPath="Name";
    }
    private void AddPoint() {
        var row=new PointRow(); SetPointDisplays(row); row.Display.SelectedItem=displays.FirstOrDefault(d=>selected.Any(s=>(string)s.Tag==d.Id&&s.IsChecked==true));
        row.Display.SelectionChanged+=(_,_)=> { UpdatePointSummaries(); UpdateMap(); };
        foreach(var number in new[]{row.X,row.Y,row.Pause}) number.ValueChanged+=(_,_)=> { UpdatePointSummaries(); UpdateMap(); };
        points.Add(row); focusedPoint=row; RenderPoints();
    }
    private void RenderPoints() {
        foreach(var oldItem in pointList.Items.OfType<ListViewItem>()) oldItem.Content=null;
        pointList.Items.Clear();
        foreach(var p in points) { var item=new ListViewItem { Content=p.Summary,Tag=p,HorizontalContentAlignment=HorizontalAlignment.Stretch }; pointList.Items.Add(item); if(p==focusedPoint) pointList.SelectedItem=item; }
        UpdatePointSummaries(); RenderPointEditor(); UpdateMap();
    }
    private void UpdatePointSummaries() {
        for(var i=0;i<points.Count;i++) { var p=points[i]; p.Summary.Text=$"{i+1}    {(p.Display.SelectedItem is RuntimeDisplay d?DisplayName(d):"Missing display")}    {p.X.Value:0.##}, {p.Y.Value:0.##} pt    ·    {p.Pause.Value:0.##} ms"; }
    }
    private void RenderPointEditor() {
        pointEditor.Children.Clear(); if(focusedPoint is not PointRow p||!points.Contains(p)) return;
        foreach(var e in new FrameworkElement[]{p.Display,p.X,p.Y,p.Pause}) if(e.Parent is Panel old) old.Children.Clear();
        var up=Action("Move up",()=>Move(p,-1)); up.IsEnabled=points.IndexOf(p)>0;
        var down=Action("Move down",()=>Move(p,1)); down.IsEnabled=points.IndexOf(p)<points.Count-1;
        pointEditor.Children.Add(p.Display);
        var fields=new Grid { ColumnSpacing=10 }; for(var i=0;i<3;i++) fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.Children.Add(p.X); fields.Children.Add(p.Y); fields.Children.Add(p.Pause); Grid.SetColumn(p.Y,1); Grid.SetColumn(p.Pause,2); pointEditor.Children.Add(fields);
        pointEditor.Children.Add(Row(up,down,Action("Remove",()=> { points.Remove(p); focusedPoint=points.FirstOrDefault(); RenderPoints(); })));
    }
    private void Move(PointRow p,int delta) { var i=points.IndexOf(p); if(i+delta<0||i+delta>=points.Count) return; (points[i],points[i+delta])=(points[i+delta],points[i]); RenderPoints(); }
    private void UpdateMap() {
        var markers=currentPosition.IsChecked==true?new List<(Guid,string,Point)>():points.Where(p=>p.Display.SelectedItem is RuntimeDisplay).Select(p=>(p.Id,((RuntimeDisplay)p.Display.SelectedItem).Id,new Point(p.X.Value,p.Y.Value))).ToList();
        monitorMap.Update(displays,selected.Where(s=>s.IsChecked==true).Select(s=>(string)s.Tag).ToHashSet(),markers,focusedPoint?.Id,id=> { var item=pointList.Items.Cast<ListViewItem>().FirstOrDefault(i=>(i.Tag as PointRow)?.Id==id); pointList.SelectedItem=item; });
    }
    private void RenderControls() {
        foreach(var e in new FrameworkElement[]{displayChoices,pointList,pointEditor,monitorMap,button,doubleClick,currentPosition,interval,limit,count,duration,delay,hotkey,openButton}) if(e.Parent is Panel parent) parent.Children.Clear();
        controls.Children.Clear(); inspector.Children.Clear(); heading.Text=projectMode?"Project":"Quick Click";
        if(projectMode) {
            controls.Children.Add(new TextBlock { Text=projectName,TextWrapping=TextWrapping.Wrap,Opacity=0.7 });
            controls.Children.Add(openButton);
            var oldMapping=mapping.ToDictionary(p=>p.Key,p=>(p.Value.SelectedItem as RuntimeDisplay)?.Id); mapping.Clear();
            if(project!=null) foreach(var slot in project.MonitorSlots) {
                var combo=new ComboBox { Header=$"{slot.Name} · {slot.Geometry.Size.Width:0.##} × {slot.Geometry.Size.Height:0.##} pt",PlaceholderText="Choose runtime display…",ItemsSource=displays,DisplayMemberPath="Name",HorizontalAlignment=HorizontalAlignment.Stretch };
                if(oldMapping.TryGetValue(slot.Id,out var id)) combo.SelectedItem=displays.FirstOrDefault(d=>d.Id==id);
                mapping.Add(slot.Id,combo); controls.Children.Add(combo);
            }
            controls.Children.Add(new TextBlock { Text=project==null?"Open a domain project JSON to inspect its event plan. Video playback and editing are not available.":"Map each slot explicitly. Size, scale and rotation must match.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,24) });
        } else {
            controls.Children.Add(Row(Section("Coordinate map · no screen capture"),Action("Identify",Identify),Action("Refresh",RefreshDisplays)));
            controls.Children.Add(monitorMap); controls.Children.Add(displayChoices);
            controls.Children.Add(Row(Section("Ordered points"),Action("Add Point",AddPoint)));
            controls.Children.Add(pointList); controls.Children.Add(pointEditor); RenderPoints();
            inspector.Children.Add(Section("Click")); inspector.Children.Add(button); inspector.Children.Add(doubleClick); inspector.Children.Add(currentPosition); inspector.Children.Add(interval); inspector.Children.Add(currentHint);
            inspector.Children.Add(Section("Repeat")); inspector.Children.Add(limit); inspector.Children.Add(count); inspector.Children.Add(duration); inspector.Children.Add(infiniteHint);
        }
        inspector.Children.Add(Section("Start & stop")); inspector.Children.Add(delay); inspector.Children.Add(hotkey);
        UpdateRelevantControls();
    }
    private void UpdateRelevantControls() {
        currentHint.Visibility=interval.Visibility=currentPosition.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        pointList.Visibility=pointEditor.Visibility=currentPosition.IsChecked==true?Visibility.Collapsed:Visibility.Visible;
        count.Visibility=limit.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        duration.Visibility=limit.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
        infiniteHint.Visibility=limit.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
        UpdateMap();
    }
    private void UpdateShortcut()=>stopShortcut.Text=$"Emergency stop: {hotkey.SelectedItem}";
    private void ApplyVisualQAArguments() {
        var args=Environment.GetCommandLineArgs();
        string? Value(string key) { var i=Array.IndexOf(args,key); return i>=0&&i+1<args.Length?args[i+1]:null; }
        if(Value("--appearance") is string appearance&&appearance is "light" or "dark") navigation.RequestedTheme=appearance=="light"?ElementTheme.Light:ElementTheme.Dark;
        if(double.TryParse(Value("--window-width"),out var w)&&double.TryParse(Value("--window-height"),out var h)&&double.IsFinite(w)&&double.IsFinite(h)) AppWindow.Resize(new SizeInt32((int)Math.Clamp(w,980,2400),(int)Math.Clamp(h,720,1600)));
        if(args.Contains("--project-empty")) navigation.SelectedItem=navigation.MenuItems[1];
        else if(args.Contains("--preview")) DryRun();
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
    private void ShowPreview(RunProgram program) {
        var events=PreviewRunner.Events(program);
        preview.ItemsSource=events.Select(e=>$"{e.TimeUs/1000.0:0.0} ms · {e.Kind} · {(displays.FirstOrDefault(d=>d.Id==e.Slot) is RuntimeDisplay d?DisplayName(d):project?.MonitorSlots.FirstOrDefault(s=>s.Id==e.Slot)?.Name??"Unmapped display")} · {(e.Point==null?"current pointer":$"({e.Point.X:0.##}, {e.Point.Y:0.##})")}").ToArray();
        previewEmpty.Visibility=Visibility.Collapsed;
    }
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
        controlsHost.IsEnabled=!Active; start.IsEnabled=!Active; pause.IsEnabled=Active; stop.IsEnabled=Active; previewButton.IsEnabled=!Active; openButton.IsEnabled=!Active;
        foreach(var item in navigation.MenuItems.OfType<NavigationViewItem>()) item.IsEnabled=!Active;
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
