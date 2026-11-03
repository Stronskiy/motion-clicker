// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "MotionClicker",
    platforms: [.macOS(.v26)],
    products: [.library(name: "MotionClickerCore", targets: ["MotionClickerCore"])],
    targets: [
        .target(name: "MotionClickerCore"),
        .testTarget(name: "MotionClickerCoreTests", dependencies: ["MotionClickerCore"])
    ]
)
