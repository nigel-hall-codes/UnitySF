# SFBuildingTemplates — authored building library (design #266)

This folder is the **one shared artifact** between the authoring loop (iPad → Home PC
server → AI) and the offline generation loop (Python bake → Unity import). The server's
`POST /export/unity` (#274) writes JSON + PNG here; a Unity importer converts them to
ScriptableObjects the assembler (#270) consumes. See `sdlc/#266/data-model.md` §2–3.

## Layout

```
SFBuildingTemplates/
  library.json                       # manifest: version, exportedAt, neighborhoods[]
  Parts/      <id>.part.json         # PartDef → BuildingPart SO (generatorId + parameters)
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

## Parts are generated, not authored (design #452 D2, #454)

A part carries **no geometry**. It names an `IPartGenerator` and gives it numbers:

```jsonc
{
  "id": "window_sunset_2x3",
  "category": "Window",
  "generatorId": "window.double_hung",     // which generator builds it
  "parameters": [                          // its parameter block — a flat, name-keyed bag
    { "name": "w",             "value": 1.2 },
    { "name": "frameRole",     "text":  "Metal" },   // `text` = symbolic value (enum name)
    { "name": "detail",        "text":  "Full" }     // optional; the DetailLevel budget knob
  ],
  "anchor": "BottomCenter",
  "mountDepth_m": -0.08
}
```

- Every value is either numeric (`value`) or symbolic (`text`, which wins when non-empty).
- Names are matched **case-sensitively**; an unrecognised name silently takes the generator's
  default, so the parameter names a generator reads are part of its documented contract.
- Parameter **order does not matter** — the mesh cache canonicalises it, so two parts written in
  different orders share one generated mesh.
- There is no `roleSubmeshes`: the generator emits each submesh's material role, so role tagging
  cannot drift from the geometry. There is no `glb`, and **no glTF package is required** by
  anything in this project.
- An empty or unknown `generatorId` means the part is **skipped** (with one warning per part id)
  when a building is assembled. There is no placeholder geometry.

> **Current state:** no generators exist yet — the first family (`window.double_hung`) is #457.
> Until it lands, the bundled part's `generatorId` is empty, so templated buildings render as
> bare mass with no artifacts. That is expected, not a regression.
