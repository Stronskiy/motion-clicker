// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "MotionClicker",
    platforms: [.macOS(.v26)],
    products: [.library(name: "MotionClickerCore", targets: ["MotionClickerCore"]),
               .library(name: "MotionClickerMacRecording", targets: ["MotionClickerMacRecording"]),
               .executable(name: "MotionClicker", targets: ["MotionClickerApp"])],
    targets: [
        .target(name: "MotionClickerCore"),
        .target(name: "MotionClickerRuntime", dependencies: ["MotionClickerCore"]),
        .target(name: "MotionClickerRecording", dependencies: ["MotionClickerCore", "MotionClickerRuntime"]),
        .target(name: "MotionClickerMacRecording", dependencies: ["MotionClickerCore", "MotionClickerRuntime", "MotionClickerRecording"]),
        .testTarget(name: "MotionClickerMacRecordingTests", dependencies: ["MotionClickerMacRecording"]),
        .testTarget(name: "MotionClickerRecordingTests", dependencies: ["MotionClickerRecording"]),
        .executableTarget(name: "MotionClickerApp", dependencies: ["MotionClickerCore", "MotionClickerRuntime", "MotionClickerRecording", "MotionClickerMacRecording"]),
        .testTarget(name: "MotionClickerAppTests", dependencies: ["MotionClickerApp"]),
        .testTarget(name: "MotionClickerRuntimeTests", dependencies: ["MotionClickerRuntime", "MotionClickerCore"]),
        .testTarget(name: "MotionClickerCoreTests", dependencies: ["MotionClickerCore"])
    ]
)
