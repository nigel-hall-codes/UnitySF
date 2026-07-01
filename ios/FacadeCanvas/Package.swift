// swift-tools-version:5.9
import PackageDescription

// The iPad facade-canvas authoring mode (#282), a mode inside the authoring client (#276).
// SwiftUI + PencilKit; talks ONLY to the Home PC Server (#274/#281) — never to AI or Unity
// directly. Open in Xcode and embed in the app target (PencilKit/SwiftUI need the iOS SDK).
let package = Package(
    name: "FacadeCanvas",
    platforms: [.iOS(.v16)],
    products: [
        .library(name: "FacadeCanvas", targets: ["FacadeCanvas"])
    ],
    targets: [
        .target(name: "FacadeCanvas"),
        .testTarget(name: "FacadeCanvasTests", dependencies: ["FacadeCanvas"])
    ]
)
