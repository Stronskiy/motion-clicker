namespace MotionClicker.Core;

/// <summary>Canonical logical units. Rotation, physical pixel and OS conversion belong to future adapters.</summary>
public static class CoordinateMapper
{
    public static Point DesktopPoint(Point local, Point displayOrigin)
    {
        var result = new Point(local.X + displayOrigin.X, local.Y + displayOrigin.Y);
        if (!Finite(local) || !Finite(displayOrigin) || !Finite(result))
            throw new ProjectError(ErrorCode.InvalidCoordinate, "desktopPoint", "Coordinates must remain finite.");
        return result;
    }
    public static Rect AspectFitRect(Size sourceSize, Rect viewport)
    {
        if (!Positive(sourceSize) || !Valid(viewport))
            throw new ProjectError(ErrorCode.InvalidGeometry, "preview", "Source/viewport must have finite positive bounds.");
        var widthScale = viewport.Size.Width / sourceSize.Width;
        var heightScale = viewport.Size.Height / sourceSize.Height;
        var scale = Math.Min(widthScale, heightScale);
        // Preserve the limiting dimension exactly, matching the shared contract.
        var size = widthScale <= heightScale
            ? new Size(viewport.Size.Width, sourceSize.Height * scale)
            : new Size(sourceSize.Width * scale, viewport.Size.Height);
        var result = new Rect(new(viewport.Origin.X + (viewport.Size.Width - size.Width) / 2,
            viewport.Origin.Y + (viewport.Size.Height - size.Height) / 2), size);
        if (!double.IsFinite(scale) || scale <= 0 || !Valid(result))
            throw new ProjectError(ErrorCode.InvalidGeometry, "preview", "Aspect fit would overflow or collapse.");
        return result;
    }
    public static Point PreviewPoint(Point local, Size sourceSize, Rect viewport)
    {
        var fit = AspectFitRect(sourceSize, viewport);
        if (!Contains(local, sourceSize)) throw new ProjectError(ErrorCode.InvalidCoordinate, "local", "Point outside source half-open bounds.");
        var result = new Point(fit.Origin.X + (local.X / sourceSize.Width) * fit.Size.Width,
            fit.Origin.Y + (local.Y / sourceSize.Height) * fit.Size.Height);
        if (!Finite(result)) throw new ProjectError(ErrorCode.InvalidCoordinate, "preview", "Coordinate would overflow.");
        return result;
    }
    public static Point LocalPoint(Point preview, Size sourceSize, Rect viewport)
    {
        var fit = AspectFitRect(sourceSize, viewport);
        var offset = new Point(preview.X - fit.Origin.X, preview.Y - fit.Origin.Y);
        if (!Contains(offset, fit.Size)) throw new ProjectError(ErrorCode.InvalidCoordinate, "preview", "Point outside image or in letterbox bar.");
        var result = new Point((offset.X / fit.Size.Width) * sourceSize.Width, (offset.Y / fit.Size.Height) * sourceSize.Height);
        if (!Contains(result, sourceSize)) throw new ProjectError(ErrorCode.InvalidCoordinate, "local", "Mapped point outside source bounds.");
        return result;
    }
    internal static bool Finite(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    internal static bool Positive(Size s) => double.IsFinite(s.Width) && double.IsFinite(s.Height) && s.Width > 0 && s.Height > 0;
    internal static bool Valid(Rect r) => Finite(r.Origin) && Positive(r.Size) && double.IsFinite(r.Origin.X + r.Size.Width) && double.IsFinite(r.Origin.Y + r.Size.Height);
    internal static bool Contains(Point p, Size s) => Finite(p) && p.X >= 0 && p.Y >= 0 && p.X < s.Width && p.Y < s.Height;
}
