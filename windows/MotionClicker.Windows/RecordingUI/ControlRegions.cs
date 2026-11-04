using MotionClicker.Core;
namespace MotionClicker.Windows.RecordingUI;
/// <summary>Hook-readable immutable regions in physical virtual-desktop pixels.</summary>
public sealed class ControlRegions {
    public void Update(IEnumerable<Rect> rectangles) { }
    public bool Contains(Point point)=>false;
}
