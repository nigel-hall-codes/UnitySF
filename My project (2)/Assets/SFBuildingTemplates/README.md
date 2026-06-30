# SFBuildingTemplates — authored building library (design #266)

This folder is the **one shared artifact** between the authoring loop (iPad → Home PC
server → AI) and the offline generation loop (Python bake → Unity import). The server's
`POST /export/unity` (#274) writes JSON + GLB + PNG here; a Unity importer converts them to
ScriptableObjects the assembler (#270) consumes. See `sdlc/#266/data-model.md` §2–3.

## Layout

```
SFBuildingTemplates/
  library.json                       # manifest: version, exportedAt, neighborhoods[]
  Parts/      <id>.part.json         # PartDef → BuildingPart SO
              <id>.glb               # authored geometry (submeshes tagged by material role)
  Palettes/   <neighborhood>.palette.json   # PaletteDef → NeighborhoodPalette SO
  Templates/  <id>.template.json     # TemplateDef → BuildingTemplate SO
  Overrides/  <osm_id>.override.json # BuildingSpecificDef (consumed by #273/#278)
  Generated/  …                      # SOs produced by the importer (regenerable; do not hand-edit)
```

## Importing

Run **`SFMap ▸ Rebuild Building Template Library`** (menu). It parses every `*.part.json`,
`*.palette.json`, and `*.template.json` and writes/updates the matching ScriptableObjects under
`Generated/`. The conversion is explicit and idempotent — re-run it whenever the library changes.

A bundled sample (`window_sunset_2x3` part, `Sunset` palette, `trivial_window` template) imports
cleanly out of the box and demonstrates the round-trip.

## glTF import (closing the #266 open question)

GLB parts are imported by **glTFast** — `com.unity.cloud.gltfast`, the Khronos/Unity-backed,
MIT-licensed, actively-maintained importer (chosen over the unmaintained UnityGLTF). With it
installed, dropping `Parts/<id>.glb` makes the GLB load as a `GameObject`, which the importer
wires into the part's `prefab` field.

The importer **does not hard-depend** on glTFast: it references the imported GLB by asset path,
so if the package is absent a part's `prefab` is simply left null (with a warning) and everything
else still imports. To enable GLB geometry, add glTFast via **Window ▸ Package Manager ▸ + ▸ Add
package by name** → `com.unity.cloud.gltfast`, or add this line to `Packages/manifest.json`
(confirm the version against your Unity version):

```jsonc
"com.unity.cloud.gltfast": "6.9.0"
```

> The manifest edit is intentionally left to you — it is the one change that affects package
> resolution for the whole project, so it is not made automatically by this feature.
