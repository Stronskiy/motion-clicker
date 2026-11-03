namespace MotionClicker.Windows;

internal readonly record struct WindowPixelSize(int ClientWidth,int ClientHeight,int OuterWidth,int OuterHeight);
internal static class WindowSizing {
    internal const double MinimumWidth=980, MinimumHeight=720, DefaultWidth=1180, DefaultHeight=800;
    internal static bool TryCalculate(double logicalWidth,double logicalHeight,double scale,int chromeWidth,int chromeHeight,out WindowPixelSize size) {
        size=default;
        if(!double.IsFinite(logicalWidth)||!double.IsFinite(logicalHeight)||!double.IsFinite(scale)
            ||logicalWidth<=0||logicalHeight<=0||scale<=0||chromeWidth<0||chromeHeight<0) return false;
        var width=Math.Ceiling(logicalWidth*scale);
        var height=Math.Ceiling(logicalHeight*scale);
        // Check before integer conversion and before adding the already-physical chrome.
        if(!double.IsFinite(width)||!double.IsFinite(height)||width<1||height<1
            ||width>int.MaxValue-chromeWidth||height>int.MaxValue-chromeHeight) return false;
        size=new((int)width,(int)height,(int)width+chromeWidth,(int)height+chromeHeight);
        return true;
    }
}
