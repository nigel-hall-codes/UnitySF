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

## Generators that exist

| `generatorId` | Family | Parameters |
|---|---|---|
| `window.double_hung` | double-hung / slider / fixed window, with reveal, sash, casing, head and sill (#457) | documented on `DoubleHungWindowGenerator` — that XML doc is the contract, since the bag has no schema |

Four neighborhood presets ship against it, each placed by a template of the same name's
neighborhood (`Templates/*.template.json` → `compatibility.neighborhoods`):

| Part | Neighborhood (`nhood`, **exact**) | Template |
|---|---|---|
| `window_noe_2over2` | `Noe Valley` | `noe_valley_victorian` |
| `window_sunset_slider` | `Sunset/Parkside` | `sunset_parkside_postwar` |
| `window_soma_steel` | `South of Market` | `soma_industrial` |
| `window_marina_arched` | `Marina` | `marina_mediterranean` |

Neighborhood strings must match `python/data/sf_analysis_neighborhoods.geojson` character for
character — the bake passes `nhood` straight through, and several carry slashes.

> **`mountDepth_m` is now positive for openings.** A generated window builds its reveal, sash and
> glass at *negative* part-local z (into the wall) and its casing/head/sill at positive z, but the
> building mass has no hole cut in it — so the part has to be mounted proud by at least
> `revealDepth + glassInset` or the recessed half is swallowed by the wall. The shipped presets
> use `revealDepth + glassInset + 0.01`. This is an authoring convention, not something the
> generator enforces, and it has **not** been checked in the Editor.

## Palettes and slashed neighborhood names (#464)

A neighborhood is a **fact**, not an authored id — the bake copies the geojson `nhood` string
through into `BuildingFactsJson.neighborhood`, and five real SF neighborhoods contain a forward
slash (`Sunset/Parkside`, `Financial District/South Beach`, `Castro/Upper Market`,
`Oceanview/Merced/Ingleside`, `Lone Mountain/USF`). Two generated asset families are keyed by it:
`Generated/Palettes/` and `Generated/DistrictWeights/`.

- The `neighborhood` **field** always stays the slashed string verbatim. That is the lookup key —
  `BuildingAssembler.ResolvePalette` compares it ordinally against the bake's fact.
- Only the generated **file name** is sanitised, by `AssetFileName.Encode`, which percent-encodes
  anything illegal: `Sunset/Parkside` → `Generated/Palettes/Sunset%2FParkside.asset`. The encoding
  is collision-free (`%` is itself escaped, so the output is uniquely decodable) and leaves legal
  names such as `Noe Valley` untouched, so re-importing an existing library produces no churn.
- A **source** `*.palette.json` file's own name is arbitrary — the importer globs the folder and
  reads `neighborhood` from inside the file. By convention it is spelt the way
  `server/app/export.py`'s `_safe()` spells it (`Sunset_Parkside.palette.json`), so a later
  `POST /export/unity` overwrites the file instead of adding a second source for the same
  neighborhood. `_safe()` is lossy and must not be used for the generated asset path.

Palettes still ship for only some neighborhoods (`Sunset/Parkside`, plus the `Sunset` MVP sample);
the rest fall back to `BuildingAssembler`'s neutral per-role defaults rather than resolving their
own colours.
