using MotionClicker.Core;
using MotionClicker.Core.Runtime;
namespace MotionClicker.Windows.RecordingUI;
/// <summary>Hook-readable immutable regions in physical virtual-desktop pixels. No UI callback.</summary>
public sealed class ControlRegions {
    private Rect[] snapshot=[];
    public void Update(IEnumerable<Rect> rectangles)=>Volatile.Write(ref snapshot,rectangles.ToArray());
    public bool Contains(Point point) {
        foreach(var rectangle in Volatile.Read(ref snapshot))if(SafeRunner.Contains(point,rectangle))return true;return false;
    }
}
