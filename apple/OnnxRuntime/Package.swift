// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "OnnxRuntime",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "OnnxRuntimeC", targets: ["OnnxRuntimeC"]),
    ],
    targets: [
        .binaryTarget(
            name: "onnxruntime",
            path: "onnxruntime.xcframework"
        ),
        .target(
            name: "OnnxRuntimeC",
            dependencies: ["onnxruntime"],
            publicHeadersPath: "include",
            linkerSettings: [
                .linkedLibrary("c++"),
                .linkedFramework("Accelerate"),
                .linkedFramework("Foundation"),
            ]
        ),
    ]
)
