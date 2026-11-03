import XCTest
import MotionClickerCore
import MotionClickerRuntime
@testable import MotionClickerApp

final class MonitorMapLayoutTests: XCTestCase {
    func display(_ id: String, _ x: Double, _ y: Double, _ w: Double, _ h: Double, scale: Double = 1) -> RuntimeDisplay {
        RuntimeDisplay(id:id,name:id,geometry:DisplayGeometry(origin:Point(x:x/scale,y:y/scale),size:Size(width:w/scale,height:h/scale),scaleFactor:scale,rotationDegrees:0),nativeBounds:Rect(origin:Point(x:x,y:y),size:Size(width:w,height:h)))
    }
    func testMixedDPIAdjacencyAndLogicalPointProjection() throws {
        let a=display("a",0,0,1920,1080), b=display("b",1920,0,3840,2160,scale:2)
        let map=MonitorMapLayout(displays:[a,b],width:600,height:300)
        let left=try XCTUnwrap(map.frames["a"]), right=try XCTUnwrap(map.frames["b"])
        XCTAssertEqual(left.maxX,right.minX,accuracy:0.00001)
        XCTAssertEqual(right.width,left.width*2,accuracy:0.00001)
        let p=try XCTUnwrap(map.point(Point(x:960,y:540),on:b))
        XCTAssertEqual(p.x,right.midX,accuracy:0.00001); XCTAssertEqual(p.y,right.midY,accuracy:0.00001)
    }
    func testNegativeOriginsFitWithoutReordering() throws {
        let map=MonitorMapLayout(displays:[display("a",-1600,-900,1600,900),display("b",0,0,1920,1080)],width:500,height:260)
        let a=try XCTUnwrap(map.frames["a"]), b=try XCTUnwrap(map.frames["b"])
        XCTAssertLessThan(a.minX,b.minX); XCTAssertLessThan(a.minY,b.minY)
        XCTAssertGreaterThanOrEqual(a.minX,0); XCTAssertLessThanOrEqual(b.maxX,500)
    }
    func testExtremeFiniteBoundsAndInvalidPointsNeverProduceNonfiniteDrawing() throws {
        let d=display("huge",-1e308,-1e308,1e308,1e308)
        let map=MonitorMapLayout(displays:[d,display("right",0,0,1e308,1e308)],width:500,height:260)
        let frame=try XCTUnwrap(map.frames["huge"])
        XCTAssertTrue(frame.minX.isFinite && frame.width.isFinite)
        XCTAssertNil(map.point(Point(x:.infinity,y:0),on:d))
        XCTAssertNil(map.point(Point(x:-1,y:0),on:d))
        XCTAssertNil(map.point(Point(x:1e308,y:0),on:d))
        XCTAssertNotNil(map.point(Point(x:5e307,y:5e307),on:d))
        XCTAssertTrue(MonitorMapLayout(displays:[],width:500,height:260).frames.isEmpty)
        XCTAssertTrue(MonitorMapLayout(displays:[d],width:.infinity,height:260).frames.isEmpty)
    }
}
