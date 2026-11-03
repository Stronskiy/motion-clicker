import SwiftUI
import MotionClickerCore
import MotionClickerRuntime

struct MonitorMapView: View {
    let displays: [RuntimeDisplay]
    let selected: Set<String>
    let points: [ClickPoint]
    let focusedPoint: UUID?
    let currentPosition: Bool
    let selectPoint: (UUID) -> Void
    var body: some View {
        GeometryReader { proxy in
            let inset=24.0
            let layout=MonitorMapLayout(displays:displays,width:max(0,proxy.size.width-inset*2),height:max(0,proxy.size.height-inset*2))
            ZStack(alignment:.topLeading) {
                Color(nsColor:.controlBackgroundColor)
                ForEach(displays,id:\.id) { display in
                    if let frame=layout.frames[display.id] {
                        RoundedRectangle(cornerRadius:12)
                            .fill(selected.contains(display.id) ? Color.accentColor.opacity(0.08) : Color.secondary.opacity(0.04))
                            .overlay { RoundedRectangle(cornerRadius:12).strokeBorder(selected.contains(display.id) ? Color.accentColor : Color.secondary.opacity(0.4),lineWidth:selected.contains(display.id) ? 2 : 1) }
                            .overlay(alignment:.bottomLeading) {
                                VStack(alignment:.leading,spacing:3) {
                                    Text(displayTitle(display,displays:displays)).font(.callout.weight(.medium))
                                    Text("\(DisplayFormatting.number(display.geometry.size.width)) × \(DisplayFormatting.number(display.geometry.size.height)) pt").font(.caption).foregroundStyle(.secondary)
                                }.padding(12).lineLimit(1).frame(maxWidth:.infinity,alignment:.leading)
                            }
                            .frame(width:frame.width,height:frame.height)
                            .position(x:frame.midX+inset,y:frame.midY+inset)
                            .accessibilityLabel("\(display.name), \(selected.contains(display.id) ? "selected" : "not selected")")
                    }
                }
                if !currentPosition {
                    ForEach(points) { point in
                        if let display=displays.first(where:{$0.id==point.displayID}),let location=layout.point(Point(x:point.x,y:point.y),on:display) {
                            Button { selectPoint(point.id) } label: {
                                Text("\((points.firstIndex(where:{$0.id==point.id}) ?? 0)+1)")
                                    .font(.caption.weight(.semibold).monospacedDigit()).frame(width:26,height:26)
                                    .foregroundStyle(focusedPoint == point.id ? Color(nsColor:.controlBackgroundColor) : Color.accentColor)
                                    .background(focusedPoint == point.id ? Color.accentColor : Color(nsColor:.controlBackgroundColor),in:Circle())
                                    .overlay { Circle().strokeBorder(Color.accentColor,lineWidth:1.5) }
                            }.buttonStyle(.plain).position(x:location.x+inset,y:location.y+inset)
                                .accessibilityLabel("Select point \((points.firstIndex(where:{$0.id==point.id}) ?? 0)+1)")
                        }
                    }
                }
                if displays.isEmpty { ContentUnavailableView("No displays available",systemImage:"display",description:Text("Refresh to find connected displays.")).frame(width:proxy.size.width,height:proxy.size.height) }
            }
        }.frame(height:240).clipShape(RoundedRectangle(cornerRadius:12))
    }
}

func displayTitle(_ display: RuntimeDisplay, displays: [RuntimeDisplay]) -> String {
    "Display \((displays.firstIndex(where:{$0.id==display.id}) ?? 0)+1)"
}
