namespace MotionClicker.Windows;

internal readonly record struct WindowPixelSize(int ClientWidth,int ClientHeight,int OuterWidth,int OuterHeight);
internal static class WindowSizing {
    internal const double MinimumWidth=980, MinimumHeight=720, DefaultWidth=1180, DefaultHeight=800;
    internal static bool TryCalculate(double logicalWidth,double logicalHeight,double scale,int chromeWidth,int chromeHeight,out WindowPixelSize size) {
        size=default;
        return false;
    }
}
