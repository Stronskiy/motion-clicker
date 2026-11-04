namespace MotionClicker.Fixture;

public sealed record FixtureGeometry(double OriginX, double OriginY, double Width, double Height, double Scale) {
    public bool Contains(double x, double y) => double.IsFinite(x) && double.IsFinite(y)
        && x >= 0 && y >= 0 && x < Width && y < Height;
    public (double X, double Y) NativePoint(double x, double y) => (OriginX + x * Scale, OriginY + y * Scale);
}
