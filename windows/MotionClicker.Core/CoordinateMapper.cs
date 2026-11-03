namespace MotionClicker.Core;

public static class CoordinateMapper
{
    public static Point DesktopPoint(Point local, Point displayOrigin) => local;
    public static Rect AspectFitRect(Size sourceSize, Rect viewport) => viewport;
    public static Point PreviewPoint(Point local, Size sourceSize, Rect viewport) => local;
    public static Point LocalPoint(Point preview, Size sourceSize, Rect viewport) => preview;
}
