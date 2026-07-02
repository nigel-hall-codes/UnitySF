// swift-tools-version:5.9
import PackageDescription

// Root manifest so external consumers (e.g. the UnitySFApp repo, design #326 D3) can add
// this monorepo as a remote SPM dependency and resolve the FacadeCanvas product — SPM only
// resolves a remote git dependency's Package.swift from the repository root, so
// ios/FacadeCanvas/Package.swift (kept as-is for standalone local development/testing)
// can't be reached directly from a cross-repo `.package(url:)` dependency. This manifest
// points at the same sources via explicit target paths; it does not move or duplicate
// anything under ios/FacadeCanvas/.
let package = Package(
    name: "UnitySF",
    platforms: [.iOS(.v16)],
    products: [
        .library(name: "FacadeCanvas", targets: ["FacadeCanvas"])
    ],
    targets: [
        .target(name: "FacadeCanvas", path: "ios/FacadeCanvas/Sources/FacadeCanvas"),
        .testTarget(name: "FacadeCanvasTests", dependencies: ["FacadeCanvas"],
                    path: "ios/FacadeCanvas/Tests/FacadeCanvasTests"),
    ]
)
