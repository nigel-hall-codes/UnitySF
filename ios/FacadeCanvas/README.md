# FacadeCanvas — iPad facade canvas authoring mode (#282)

A SwiftUI + PencilKit **mode inside the iPad authoring client** (design #276), realizing the
building-specific facade canvas (#278). Pick one building + one facade, paint freehand strokes and
place images / AI signs over the facade's unit square, and save to the **Home PC Server** — which
flattens strokes to a paint PNG and writes `facadeDecals[]` into the building's override on export
(#281), for the Unity decal importer (#280) to dress the wall.

**Boundary (design #276):** this module talks **only** to the server via `ServerClient`. It never
calls an AI provider (signs are `POST /ai/signs/generate`, server-mediated) and never touches Unity.

## Layout

```
ios/FacadeCanvas/
  Package.swift
  Sources/FacadeCanvas/
    Models.swift               # Codable structs mirroring server shapes (FacadeCanvas/CanvasLayer/Stroke, Sign*)
    ServerClient.swift         # the ONLY egress: POST/GET /canvas, POST /ai/signs/generate
    FacadeCanvasViewModel.swift# PencilKit-free state + save/load (unit-testable)
    StrokeConversion.swift     # PKDrawing → normalized facade-UV strokes (y-flipped)
    FacadeCanvasView.swift     # SwiftUI surface: Pencil paint + draggable placed images + AI-sign sheet
  Tests/FacadeCanvasTests/     # JSON-shape + view-model logic tests (run off-device)
```

## Build & test

This is a Swift package targeting **iOS 16+**; open it in **Xcode** and embed the library in your
app target (SwiftUI/PencilKit need the iOS SDK). The UI files are gated behind
`#if canImport(SwiftUI) && canImport(PencilKit)`, so on macOS/Linux `swift test` still compiles and
runs the model + view-model + `ServerClient` logic:

```bash
cd ios/FacadeCanvas && swift test
```

> ⚠️ **Not compiled in the pipeline environment** (no Swift toolchain / Xcode in the Unity repo).
> The wire models are matched field-for-field to the server (`server/app/models.py`) and covered by
> the tests; build in Xcode to run on device.

## Coordinate contract

Canvas coordinates are **normalized facade UV**: `x` left→right, `y` **bottom→top** (matching the
bake's facade frame, #279, and the Unity decal importer, #280). `StrokeConversion` flips PencilKit's
top-down y. So a stroke/image authored at a spot on iPad lands at the same spot on the real wall at
any building size. Server gaps this MVP works around are noted in `sdlc/#276/design.md` §8 (building
facts read G1, asset-PNG GET for preview G2).
