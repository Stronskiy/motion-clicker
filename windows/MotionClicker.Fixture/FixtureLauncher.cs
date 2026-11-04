using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MotionClicker.Fixture;

internal sealed class FixtureLauncher : Window {
    private readonly ComboBox displays = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock destination = new() { Text = "Choose a local log folder. Each canvas creates a new file.", TextWrapping = TextWrapping.Wrap };
    private readonly IReadOnlyList<DisplayArea> areas;
    private readonly List<TargetWindow> canvases = [];
    private string? folder;
    public FixtureLauncher() {
        Title = "Motion Clicker · Generated target fixture";
        AppWindow.Resize(new(820, 500));
        areas = DisplayArea.FindAll();
        foreach(var area in areas) displays.Items.Add($"Display {area.DisplayId.Value} · {area.OuterBounds.Width} × {area.OuterBounds.Height} pixels");
        if(areas.Count > 0) displays.SelectedIndex = 0;
        var choose = new Button { Content = "Choose log folder…" };
        choose.Click += async (_, _) => {
            try {
                var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var selected = await picker.PickSingleFolderAsync();
                if(selected != null) { folder = selected.Path; destination.Text = folder; }
            } catch(Exception error) { destination.Text = $"Folder unavailable: {error.Message}"; }
        };
        var open = new Button { Content = "Open selected canvas" };
        open.Click += (_, _) => {
            if(folder == null || displays.SelectedIndex < 0) { destination.Text = "Choose a log folder and display first."; return; }
            try {
                var area = areas[displays.SelectedIndex];
                var canvas = new TargetWindow(area, Path.Combine(folder, $"fixture-{Guid.NewGuid()}.jsonl"));
                canvases.Add(canvas); canvas.Closed += (_, _) => canvases.Remove(canvas); canvas.Activate();
            } catch(Exception error) { destination.Text = $"Canvas unavailable: {error.Message}"; }
        };
        Content = new StackPanel {
            Padding = new(24), Spacing = 16,
            Children = {
                new TextBlock { Text = "Controlled recording target", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = "Separate process · generated content only. Cover selected display is deliberate. Verify all intended surfaces before recording. The fixture never starts the recorder.", TextWrapping = TextWrapping.Wrap },
                displays, choose, destination, open,
                new TextBlock { Text = "Left, right, middle, double-left, drag, second target = 7 down/up pairs. Then two opposite scroll gestures; callback counts vary. One physical pointer. No audio. Restore or close the canvas using its own controls.", TextWrapping = TextWrapping.Wrap }
            }
        };
    }
}
