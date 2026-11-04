namespace MotionClicker.Fixture;

public sealed record FixtureGeometry(double OriginX, double OriginY, double Width, double Height, double Scale) {
    public bool Contains(double x, double y) => true;
    public (double X, double Y) NativePoint(double x, double y) => (x, y);
}
