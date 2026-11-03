// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "MotionClicker",
    platforms: [.macOS(.v26)],
    products: [.library(name: "MotionClickerCore", targets: ["MotionClickerCore"]),
               .executable(name: "MotionClicker", targets: ["MotionClickerApp"])],
    targets: [
        .target(name: "MotionClickerCore"),
        .target(name: "MotionClickerRuntime", dependencies: ["MotionClickerCore"]),
        .executableTarget(name: "MotionClickerApp", dependencies: ["MotionClickerCore", "MotionClickerRuntime"]),
        .testTarget(name: "MotionClickerAppTests", dependencies: ["MotionClickerApp"]),
        .testTarget(name: "MotionClickerRuntimeTests", dependencies: ["MotionClickerRuntime", "MotionClickerCore"]),
        .testTarget(name: "MotionClickerCoreTests", dependencies: ["MotionClickerCore"])
    ]
)
