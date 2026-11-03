using MotionClicker.Core;
using MotionClicker.Core.Runtime;

namespace MotionClicker.Windows;

/// <summary>Drawing only. Native bounds share a desktop space even at mixed DPI.</summary>
internal sealed class MonitorMapLayout {
    public IReadOnlyDictionary<string,Rect> Frames { get; }
    public MonitorMapLayout(IReadOnlyList<RuntimeDisplay> displays,double width,double height) {
        var valid=displays.Where(d=>new[]{d.NativeBounds.Origin.X,d.NativeBounds.Origin.Y,d.NativeBounds.Size.Width,d.NativeBounds.Size.Height}.All(double.IsFinite)&&d.NativeBounds.Size.Width>0&&d.NativeBounds.Size.Height>0).ToArray();
        if(!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0||valid.Length==0) { Frames=new Dictionary<string,Rect>(); return; }
        // Normalize before edge addition/subtraction so finite imports cannot overflow.
        var magnitude=valid.SelectMany(d=>new[]{Math.Abs(d.NativeBounds.Origin.X),Math.Abs(d.NativeBounds.Origin.Y),d.NativeBounds.Size.Width,d.NativeBounds.Size.Height}).Max();
        var normalized=valid.Select(d=>(d.Id,Bounds:new Rect(new(d.NativeBounds.Origin.X/magnitude,d.NativeBounds.Origin.Y/magnitude),new(d.NativeBounds.Size.Width/magnitude,d.NativeBounds.Size.Height/magnitude)))).ToArray();
        var minX=normalized.Min(d=>d.Bounds.Origin.X); var minY=normalized.Min(d=>d.Bounds.Origin.Y);
        var spanX=normalized.Max(d=>d.Bounds.Origin.X+d.Bounds.Size.Width)-minX;
        var spanY=normalized.Max(d=>d.Bounds.Origin.Y+d.Bounds.Size.Height)-minY;
        var scale=Math.Min(width/spanX,height/spanY);
        if(spanX<=0||spanY<=0||!double.IsFinite(scale)) { Frames=new Dictionary<string,Rect>(); return; }
        var dx=(width-spanX*scale)/2; var dy=(height-spanY*scale)/2;
        Frames=normalized.ToDictionary(d=>d.Id,d=>new Rect(new(dx+(d.Bounds.Origin.X-minX)*scale,dy+(d.Bounds.Origin.Y-minY)*scale),new(d.Bounds.Size.Width*scale,d.Bounds.Size.Height*scale)));
    }
    public Point? Project(Point point,RuntimeDisplay display) {
        var size=display.Geometry.Size;
        if(!Frames.TryGetValue(display.Id,out var frame)||!double.IsFinite(point.X)||!double.IsFinite(point.Y)||!double.IsFinite(size.Width)||!double.IsFinite(size.Height)||size.Width<=0||size.Height<=0||point.X<0||point.Y<0||point.X>=size.Width||point.Y>=size.Height) return null;
        return new(frame.Origin.X+point.X/size.Width*frame.Size.Width,frame.Origin.Y+point.Y/size.Height*frame.Size.Height);
    }
}
