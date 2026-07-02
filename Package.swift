// swift-tools-version:6.2
import PackageDescription

// Root manifest so external consumers (e.g. the UnitySFApp repo, design #326 D3) can add
// this monorepo as a remote SPM dependency and resolve the FacadeCanvas product — SPM only
// resolves a remote git dependency's Package.swift from the repository root, so
// ios/FacadeCanvas/Package.swift (kept as-is for standalone local development/testing)
// can't be reached directly from a cross-repo `.package(url:)` dependency. This manifest
// points at the same sources via explicit target paths; it does not move or duplicate
// anything under ios/FacadeCanvas/.
//
// Targets share names with ios/FacadeCanvas/Package.swift on purpose (same product). Don't
// add both this root package and ios/FacadeCanvas as dependencies of the same build graph —
// SPM will reject the duplicate target name. Consumers pick one: this repo root for a remote
// dependency, or ios/FacadeCanvas directly for local standalone development.
let package = Package(
    name: "UnitySF",
    platforms: [.iOS(.v26)],
    products: [
        .library(name: "FacadeCanvas", targets: ["FacadeCanvas"])
    ],
    targets: [
        .target(name: "FacadeCanvas", path: "ios/FacadeCanvas/Sources/FacadeCanvas"),
        .testTarget(name: "FacadeCanvasTests", dependencies: ["FacadeCanvas"],
                    path: "ios/FacadeCanvas/Tests/FacadeCanvasTests"),
    ]
)
