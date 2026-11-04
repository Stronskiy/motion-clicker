using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace MotionClicker.Fixture;

internal sealed class TargetWindow : Window {
    private readonly Grid canvas = new() { Background = new SolidColorBrush(Colors.WhiteSmoke), MinHeight = 300 };
    private readonly TextBlock reference = new() { FontSize = 22, Foreground = new SolidColorBrush(Colors.Black) };
    private readonly TextBlock outcome = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Black) };
    private readonly TextBlock status = new() { Text = "Ready for ordinary physical input", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Black) };
    private readonly Ellipse marker = new() { Width = 22, Height = 22, Fill = new SolidColorBrush(Colors.CornflowerBlue), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly FixtureJournal journal;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DisplayArea selected;
    private readonly Button cover = new() { Content = "Cover selected display" };
    private RectInt32? prior;
    private long marks;
    private int downs, ups, deliveries;
    private double scrollY;
    private bool failed;
    public TargetWindow(DisplayArea selected, string path) {
        this.selected = selected;
        journal = new FixtureJournal(path, Guid.NewGuid().ToString(), selected.DisplayId.Value.ToString(), Stopwatch.Frequency);
        Title = $"Generated canvas · Display {selected.DisplayId.Value}";
        AppWindow.Resize(new(1000, 760));
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var close = new Button { Content = "Close canvas and flush log" }; close.Click += (_, _) => Close();
        cover.Click += (_, _) => ToggleCover();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(12), Children = { cover, close } };
        root.Children.Add(controls); Grid.SetRow(canvas, 1); root.Children.Add(canvas);
        var body = new StackPanel { Margin = new(44, 100, 44, 70), Spacing = 22, IsHitTestVisible = false, Children = {
            new TextBlock { Text = $"Generated target · Display {selected.DisplayId.Value}", FontSize = 26, Foreground = new SolidColorBrush(Colors.Black) }, reference, outcome, status,
            new TextBlock { Text = "Click target A\n\nDrag wholly inside this canvas →\n\nTarget B / second-screen target", FontSize = 22, Foreground = new SolidColorBrush(Colors.Black) },
            new TextBlock { Text = "10 Hz UI reference requests are not capture or hardware timing.", FontSize = 14, Foreground = new SolidColorBrush(Colors.Black), TextWrapping = TextWrapping.Wrap }
        } };
        canvas.Children.Add(body);
        AddCorner("TOP LEFT", Colors.CornflowerBlue, HorizontalAlignment.Left, VerticalAlignment.Top);
        AddCorner("TOP RIGHT", Colors.DarkOrange, HorizontalAlignment.Right, VerticalAlignment.Top);
        AddCorner("BOTTOM LEFT", Colors.ForestGreen, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        AddCorner("BOTTOM RIGHT", Colors.MediumPurple, HorizontalAlignment.Right, VerticalAlignment.Bottom);
        canvas.Children.Add(marker);
        canvas.PointerPressed += OnPressed; canvas.PointerReleased += OnReleased;
        canvas.PointerMoved += (_, e) => Receive(e, e.GetCurrentPoint(canvas).IsInContact ? "drag" : "move");
        canvas.PointerWheelChanged += (_, e) => { Receive(e, "scroll"); e.Handled = true; };
        timer.Tick += (_, _) => Mark();
        canvas.Loaded += (_, _) => timer.Start();
        Closed += (_, _) => { timer.Stop(); try { journal.Dispose(); } catch(Exception error) { Fail(error); } };
        Content = root;
    }
    private void AddCorner(string text, Windows.UI.Color color, HorizontalAlignment horizontal, VerticalAlignment vertical) {
        canvas.Children.Add(new Border { Background = new SolidColorBrush(color), Padding = new(12, 6, 12, 6), Margin = new(16), HorizontalAlignment = horizontal, VerticalAlignment = vertical, IsHitTestVisible = false,
            Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Colors.White), FontSize = 14 } });
    }
    private void ToggleCover() {
        if(AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if(prior is RectInt32 frame) {
            presenter.SetBorderAndTitleBar(true, true); presenter.IsAlwaysOnTop = false;
            AppWindow.MoveAndResize(frame); prior = null; cover.Content = "Cover selected display";
        } else {
            prior = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true;
            AppWindow.MoveAndResize(selected.OuterBounds); cover.Content = "Restore window";
        }
    }
    private FixtureGeometry Geometry() {
        var rootPoint = canvas.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var scale = canvas.XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        if(!ClientToScreen(WindowNative.GetWindowHandle(this), ref origin)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(origin.X + rootPoint.X * scale, origin.Y + rootPoint.Y * scale, canvas.ActualWidth, canvas.ActualHeight, scale);
    }
    private void Mark() {
        if(failed || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        var stamp = Stopwatch.GetTimestamp();
        try {
            journal.Append(stamp, "marker", Geometry());
            reference.Text = $"Reference {++marks} · QPC ticks {stamp}";
        } catch(Exception error) { Fail(error); }
    }
    private static int? Button(PointerUpdateKind kind) => kind switch {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 0,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 1,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 2,
        _ => null
    };
    private void OnPressed(object sender, PointerRoutedEventArgs e) { Receive(e, "down", Button(e.GetCurrentPoint(canvas).Properties.PointerUpdateKind)); e.Handled = true; }
    private void OnReleased(object sender, PointerRoutedEventArgs e) { Receive(e, "up", Button(e.GetCurrentPoint(canvas).Properties.PointerUpdateKind)); e.Handled = true; }
    private void Receive(PointerRoutedEventArgs e, string kind, int? button = null) {
        if(failed || e.GetCurrentPoint(canvas).PointerDeviceType != PointerDeviceType.Mouse) return;
        var stamp = Stopwatch.GetTimestamp();
        var point = e.GetCurrentPoint(canvas);
        var properties = point.Properties;
        var dx = kind == "scroll" && properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : 0;
        var dy = kind == "scroll" && !properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : 0;
        try {
            journal.Append(stamp, kind, Geometry(), button, point.Position.X, point.Position.Y, dx, dy, point.Timestamp.ToString());
            if(kind == "down") downs++;
            if(kind == "up") ups++;
            scrollY += dy;
            outcome.Text = $"Down {downs}   Up {ups}   Scroll Y {scrollY}";
            status.Text = $"{kind} · button {button?.ToString() ?? "–"} · target event {++deliveries}";
            marker.Margin = new(Math.Max(0, point.Position.X - 11), Math.Max(0, point.Position.Y - 11), 0, 0);
        } catch(Exception error) { Fail(error); }
    }
    private void Fail(Exception error) { failed = true; timer.Stop(); status.Text = $"LOG FAILED — stop acceptance: {error.Message}"; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
}
