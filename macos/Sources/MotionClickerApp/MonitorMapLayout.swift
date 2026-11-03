import CoreGraphics
import MotionClickerCore
import MotionClickerRuntime

/// A drawing-only transform. Native bounds share one desktop coordinate space,
/// unlike independently DPI-divided monitor origins on Windows.
struct MonitorMapLayout {
    let frames: [String: CGRect]
    init(displays: [RuntimeDisplay], width: Double, height: Double) {
        let valid=displays.filter { d in
            let b=d.nativeBounds
            return [b.origin.x,b.origin.y,b.size.width,b.size.height].allSatisfy(\.isFinite) && b.size.width > 0 && b.size.height > 0
        }
        guard width.isFinite, height.isFinite, width > 0, height > 0, !valid.isEmpty else { frames=[:]; return }
        // Normalize BEFORE adding edges or subtracting origins: even finite imports
        // can otherwise overflow at x + width or maxX - minX.
        let magnitude=valid.flatMap { [abs($0.nativeBounds.origin.x),abs($0.nativeBounds.origin.y),$0.nativeBounds.size.width,$0.nativeBounds.size.height] }.max()!
        let normalized=valid.map { d in
            (d.id,CGRect(x:d.nativeBounds.origin.x/magnitude,y:d.nativeBounds.origin.y/magnitude,width:d.nativeBounds.size.width/magnitude,height:d.nativeBounds.size.height/magnitude))
        }
        let minX=normalized.map { $0.1.minX }.min()!, minY=normalized.map { $0.1.minY }.min()!
        let spanX=normalized.map { $0.1.maxX }.max()!-minX, spanY=normalized.map { $0.1.maxY }.max()!-minY
        guard spanX > 0, spanY > 0 else { frames=[:]; return }
        let scale=min(width/spanX,height/spanY)
        guard scale.isFinite else { frames=[:]; return }
        let dx=(width-spanX*scale)/2, dy=(height-spanY*scale)/2
        frames=Dictionary(uniqueKeysWithValues:normalized.map { id,b in
            (id,CGRect(x:dx+(b.minX-minX)*scale,y:dy+(b.minY-minY)*scale,width:b.width*scale,height:b.height*scale))
        })
    }
    func point(_ point: Point, on display: RuntimeDisplay) -> CGPoint? {
        let size=display.geometry.size
        guard let frame=frames[display.id], point.x.isFinite,point.y.isFinite,
              size.width.isFinite,size.height.isFinite,size.width > 0,size.height > 0,
              point.x >= 0,point.y >= 0,point.x < size.width,point.y < size.height else { return nil }
        return CGPoint(x:frame.minX+(point.x/size.width)*frame.width,y:frame.minY+(point.y/size.height)*frame.height)
    }
}
