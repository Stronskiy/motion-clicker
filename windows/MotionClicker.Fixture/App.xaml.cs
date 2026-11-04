using Microsoft.UI.Xaml;

namespace MotionClicker.Fixture;

public partial class App : Application {
    private FixtureLauncher? window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args) {
        window = new FixtureLauncher(); window.Activate();
    }
}
