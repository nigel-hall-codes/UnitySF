# Design: San Francisco Building Template Pipeline

**Issue:** #266
**Phase:** drafting
**Date:** 2026-06-30
**Persona:** systems-architect
**Input:** issue #266 body (captured from "SF Building Template Pipeline — Final Design Document v1")
**Companion:** `data-model.md` (concrete schemas, API surface, ScriptableObjects)

---

## Scope

This document defines how the proposed **iPad → Home PC Server → Unity** building-authoring
pipeline integrates with the *existing* offline OSM→chunk→Unity generator in this repo.

It draws the component boundaries, names the integration seams (by file), and specifies the
two systems given the highest design risk: the **placement / metadata model** and the
**procedural generation (assembly) pipeline**. The iPad and server are designed only to the
depth needed to fix their contract with Unity.

C# type definitions, the sidecar/asset schemas, and the HTTP API surface are in `data-model.md`.

---

## The central tension (and the decision that resolves it)

The existing pipeline (issue #2 design) commits to: *"in-editor, offline, no external services …
no runtime HTTP."* The PDF proposes a home-PC server, an iPad client, and AI calls.

These are reconciled by a single rule:

> **The server/iPad/AI loop is an *authoring* system. Its only contract with Unity is a
> versioned, on-disk asset library exported into the repo. Unity never talks to the network.**

So there are two independent loops, joined at one seam (the export):

```
  AUTHORING LOOP (online, occasional)              GENERATION LOOP (offline, deterministic)
  ┌─────────────────────────────────────┐         ┌──────────────────────────────────────┐
  │ iPad ──▶ Home PC Server ──▶ AI       │  drop   │ OSM + elevation ──▶ Python bake ──▶   │
  │  (author parts, templates,    │  ────────▶ │ .bin + sidecars ──▶ Unity import ──▶  │
  │   signs, placement rules)     export        │ assembled prefabs ──▶ ChunkStreamer   │
  └─────────────────────────────────────┘         └──────────────────────────────────────┘
                    │                                              ▲
                    └─────── Assets/SFBuildingTemplates/ ──────────┘
                              (the ONLY shared artifact)
```

Everything in the authoring loop exists to produce, and the generation loop only ever consumes,
the contents of `Assets/SFBuildingTemplates/`. This preserves the offline/deterministic/
git-diffable guarantees of the existing pipeline while admitting the new authoring tools.

---

## Principles

1. **Two authorities, one boundary.** Python owns *geometry + classification* (it has the
   footprint, terrain, and road graph). Unity owns *assembly* (it has the template library and
   prefab system). Neither reaches into the other's domain.
2. **Python emits facts, not decisions.** The bake does not choose a template — it cannot, it
   has no template library. It emits classification facts; Unity matches and assembles.
3. **Coverage-scaled cost.** A building with a matching template becomes an individual prefab
   (richer, heavier). A building with no match keeps the existing merged-mesh path (cheap).
   Cost grows only as the library grows. This is the load-bearing performance decision.
4. **Roles, never colors.** Authored assets carry material *roles* (Base, Accent1, Accent2,
   Glass, Metal, Sign). Unity resolves roles to colors at import via neighborhood palettes,
   seeded deterministically, and **bakes the result into vertex colors** — reusing the existing
   `SFMap/BuildingVertexColor` shader so templated buildings stay batchable (see §Material roles
   for why per-renderer `MaterialPropertyBlock` is the wrong tool here).
5. **Deterministic from a stable key.** Template match, procedural placement jitter, and palette
   resolution are all seeded from `osm_id` (+ `footprint_hash` for overrides). Re-bake and
   re-import produce byte-identical assets. Preserves git-diffability (issue #2, principle 4/5).
6. **Additive to the proven spine.** The CHNK `.bin` format, chunk prefab hierarchy, and
   `ChunkStreamer` are unchanged. New data rides in a new sidecar; new behavior lives in new
   import code behind a feature gate, with the old merged path as fallback.

---

## Component map & boundaries

```
┌──────────────┐   GLB+meta    ┌────────────────────┐   POST /export/unity   ┌─────────────────┐
│  iPad Editor │ ────────────▶ │  Home PC Server    │ ─────────────────────▶ │ Assets/         │
│ (authoring   │ ◀──────────── │  FastAPI + SQLite  │   (asset drop)         │ SFBuildingTemp- │
│  UI only)    │   templates,  │  + asset store     │                        │ lates/          │
│              │   parts,      │  + AI provider      │                        │ (parts, signs,  │
│              │   signs       │    abstraction      │                        │  templates,     │
└──────────────┘               └────────────────────┘                        │  palettes)      │
       │  never talks to AI directly │  only server calls AI                  └────────┬────────┘
       └─────────────────────────────┘                                                 │ consumed by
                                                                                        ▼ Unity import
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ EXISTING GENERATION SPINE (unchanged spine, extended payload)                                     │
│                                                                                                   │
│  OSM + elevation + neighborhoods.geojson                                                          │
│        │                                                                                          │
│        ▼  python/sfmap_bake.py                                                                    │
│  ┌──────────────────────────┐                                                                     │
│  │ Python bake              │  per building, in addition to today's mass mesh, emits a            │
│  │  • build mass mesh (as today)   CLASSIFICATION RECORD (neighborhood, type, footprint           │
│  │  • classify building            shape, w/d/h, floor count, front-facade orientation,           │
│  │  • emit facts             │     footprint_hash) into a new sidecar.                            │
│  └──────────────────────────┘                                                                     │
│        │  chunk_CC_RR.bin  +  chunk_CC_RR_buildings.json (NEW)                                     │
│        ▼  SFMapImporterWindow.cs                                                                   │
│  ┌──────────────────────────┐                                                                     │
│  │ Unity import / Assembler │  per building:                                                       │
│  │  match → assemble → seed │   match template by facts → assemble parts onto mass →              │
│  │   palette → prefab        │   apply building-specific overrides → resolve palette →            │
│  │  (fallback: merge as today)   save nested prefab.  No match ⇒ old merged path.                 │
│  └──────────────────────────┘                                                                     │
│        │  chunk_CC_RR.prefab (templated buildings = nested prefab instances)                      │
│        ▼  ChunkStreamer.cs  (UNCHANGED)                                                            │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Boundary contracts (the only things that cross a component line):**

| Boundary | Artifact | Owner | Consumer |
|---|---|---|---|
| iPad ↔ Server | HTTP/JSON + GLB/PNG uploads | iPad | Server |
| Server ↔ AI | provider-abstracted request | Server | external AI |
| Server → Unity | `Assets/SFBuildingTemplates/` asset drop | Server (`/export/unity`) | Unity import |
| Python → Unity | `chunk_CC_RR_buildings.json` sidecar (NEW) + `.bin` (as today) | Python bake | Unity import |
| Unity import → runtime | chunk prefab (as today) | Importer | `ChunkStreamer` |

---

## Integration seams (named files)

| Existing file | Change | Why |
|---|---|---|
| `python/sfmap/osm.py` (`BuildingWay`) | + `footprint_hash`; carry through `building_type` | Stable override key; classification input |
| `python/sfmap/chunk.py` | classify each building; collect classification records | Per-chunk orchestration already iterates buildings |
| `python/sfmap/geometry/building.py` | unchanged (still builds the mass) | Proven terrain-adapted mass; do not disturb |
| `python/sfmap/serialize.py` | write new sidecar `chunk_CC_RR_buildings.json` | Mirrors existing `_names`/`_parked` sidecar pattern |
| `python/sfmap_bake.py` | + `--neighborhoods <geojson>`, + `--templates` gate | New input + opt-in |
| `SFMapImporterWindow.cs` (~line 280–351) | branch: templated → assemble per-building prefab; else merge (today) | The assembly fork |
| `PipelineTypes.cs` (`GeneratedAssets`) | + paths for per-building prefab, template library | Asset path helpers already centralized here |
| `ChunkManifest.cs` / `ChunkStreamer.cs` | **none** | Spine already streams nested-prefab chunks |
| *new* `Assets/Scripts/Pipeline/Buildings/*` | Assembler, template SOs, palette resolver, override store | New behavior, new assembly |
| *new* `Assets/SFBuildingTemplates/` | server-exported library (parts, signs, templates, palettes) | The authoring/generation seam |

**Precedent:** parked cars are already baked as nested prefab instances under
`ParkedCars chunk_CC_RR`. Templated buildings follow the identical mechanism under
`Buildings chunk_CC_RR`, so `ChunkStreamer` needs no change. (See memory:
parked-cars-vs-traffic-spawn-model — same spawn model applies here.)

---

## Building generation: end-to-end resolution for one building

```
PYTHON (bake)                                  UNITY (import / Assembler)
─────────────                                  ──────────────────────────
footprint, height, building_type, terrain
        │
        ├─▶ build mass mesh (geometry/building.py, unchanged) ──────────────▶ MeshType BUILDING in .bin
        │
        └─▶ classify:
              neighborhood   = point-in-polygon(centroid, neighborhoods.geojson)
              footprint_shape = shapeClassifier(footprint)      # rect | L | corner | irregular
              width,depth     = oriented bbox of footprint
              floor_count     = round(height / 3.0)             # PDF: 3.0 m / floor
              front_facade    = footprint edge nearest/most-parallel to nearest road edge
              footprint_hash  = hash(quantized footprint vertices)
                    │
                    └─▶ ClassificationRecord ───────────────────▶ chunk_CC_RR_buildings.json (NEW)
                                                                          │
                                                                          ▼
                                                   1. MATCH: pick BuildingTemplate whose
                                                      compatibility (neighborhood, type, shape,
                                                      w/d/h band) admits this building.
                                                      Tie-break seeded by osm_id. No match ⇒ FALLBACK.
                                                          │
                                                   2. ASSEMBLE (order from PDF):
                                                      mass(from .bin) → roof trim → doors → garages
                                                      → windows → bay windows → storefronts → signs
                                                      Each placement resolved against THIS building's
                                                      real w/d/h and front_facade (see Placement Model).
                                                          │
                                                   3. OVERRIDE: if a BuildingSpecific override matches
                                                      (osm_id AND footprint_hash), apply it last.
                                                          │
                                                   4. PALETTE: resolve each material role → color via
                                                      the building's NeighborhoodPalette, seeded by
                                                      osm_id; write via MaterialPropertyBlock.
                                                          │
                                                   5. SAVE: nested prefab under Buildings chunk_CC_RR.
```

**FALLBACK path (no matching template, or `--templates` off):** the building contributes its
mass to `buildings_combined.mesh` with a palette vertex color — exactly today's behavior.
This is what makes a 200-building chunk affordable when only 6 templates exist.

---

## Placement / metadata model  (high rigor)

A **Part** (window, door, sign, …) is an authored GLB with submeshes tagged by material role.
A **Template** is a recipe: a set of **placement directives** that say *which* parts go *where*
on a building of this style. A placed instance, once resolved, is a **PlacementRecord** (the
normalized metadata from the PDF). Schemas live in `data-model.md`; the *model* is here.

### Facade & coordinate frame (must be defined first)

Every placement is expressed in a building-relative frame, never world space:

- **Facade** ∈ {`Front`, `Back`, `Left`, `Right`, `Roof`}. Street-facing facades are **defined by
  Python** (it owns the road graph): each footprint edge is scored by proximity + parallelism to
  the nearest road, and the sidecar carries a **ranked list** `street_facades[]`, primary first.
  `Front` = primary. **Corner buildings face two streets** (the PDF's "Mission Corner Store" is
  exactly this case), so the model must not assume a single street frontage — a template may place
  storefronts/signs on every `street_facades` entry via `facade: "Street"` (expands to all ranked
  street facades) or target `Front` for primary-only. Without this fact, signs/doors land on the
  wrong side — it is the single most important thing the sidecar carries.
- **Horizontal position** `x ∈ [0,1]`: 0 = left end of the facade, 0.5 = center, 1 = right end
  (PDF convention). Resolved against the building's *real* facade width, so the same template
  fits a 6 m and a 12 m frontage.
- **Vertical**: `floor` (int, 0 = ground) selects a band of height `floorHeight` (3.0 m);
  `y ∈ [0,1]` positions within the floor band, or `floor = All` repeats per floor.
- **scale, rotation**: local to the part, applied after placement.

### The three placement modes

| Mode | Scope / key | Authored where | Adapts to dims? | Use |
|---|---|---|---|---|
| **Exact Layout** | per template | template skeleton | scales normalized coords | the fixed bones of a style (one centered door, ground-floor storefront) |
| **Procedural Rule** | per template | template rules | yes — count derived from dims | the believable variety (windows every ~3 m across the frontage) |
| **Building-Specific** | `(osm_id, footprint_hash)` | override store | n/a (exact) | landmarks / hero buildings only |

**Exact Layout** — a fixed list of `(part, facade, floor, x, y, scale, rotation, roles)`.
Reproduced identically every time the template is used; only the normalized coords stretch to the
real facade. Deterministic by construction.

**Procedural Rule** — the engine of "thousands of believable buildings." A rule carries:
- a **zone**: facade(s) + floor range + horizontal span `[x0,x1]`;
- a **repeat**: spacing (meters) or count, with a derived count
  `n = clamp(floor(spanMeters / spacing), min, max)`;
- **constraints**: min spacing, edge margins, align-to-floor-line, avoid-overlap with Exact parts;
- **probability**: 0–1 per candidate slot;
- **randomization**: jitter ranges (position/scale/rotation) and part-variant choice.

Every random draw (count rounding tie-break, probability test, jitter, variant pick) is drawn
from a PRNG **seeded by `hash(osm_id, ruleIndex, slotIndex)`**. Therefore a wider building
deterministically gets more windows, and the *same* building always gets the *same* windows.

**Building-Specific** — an exact placement set (and/or part swaps) keyed by `osm_id` **and**
`footprint_hash`. The hash guard means: if OSM reuses the id for a different building, or the
footprint is materially edited, the override stops matching and the importer logs a warning
rather than dressing the wrong building. Landmarks only — not a general escape hatch.

### Resolution order (deterministic)

```
templateExact  ─┐
templateRules  ─┼─▶ candidate placements ─▶ apply BuildingSpecific (add / replace / suppress)
                │                                   │
                │   overrides win on conflict;      ▼
                └── seeded by osm_id            final PlacementRecord list ─▶ instantiate + palette
```

Conflict rule: Building-Specific > Exact > Procedural. Within Procedural, constraints resolve
in rule-declaration order (earlier rules reserve their slots first).

---

## Procedural generation (assembly) pipeline  (high rigor)

The **Assembler** runs once per templated building at import time. It is a pure function of
`(massMesh, ClassificationRecord, BuildingTemplate, overrides, NeighborhoodPalette, seed)`.

```
Assemble(building):
  root ← new GameObject(building.osm_id)
  add MeshFilter(massMesh) + MeshRenderer(baseMaterial)        # the Python mass, as-is
  stages = [ Roof, Door, Garage, Window, BayWindow, Storefront, Sign ]   # PDF order
  placements = ResolvePlacements(template, overrides, building, seed)     # see Placement Model
  for stage in stages:
     for p in placements where p.partCategory == stage:
        child ← Instantiate(p.part.prefab)
        Frame(child, building, p)        # normalized → local meters using real w/d/h + facade frame
        if p.part.isSign:
           ApplySignTexture(child, p.signAsset)        # AI PNG on its own material (NOT combinable)
        else:
           BakeRoleColors(child, p.roles, palette, seed)  # role → color → VERTEX COLORS
        parent child under root
  # non-sign parts + mass share SFMap/BuildingVertexColor ⇒ optional import mesh-combine, batchable.
  # sign parts stay separate (unique textures) — the natural batching break, atlas-candidate.
  SaveNestedPrefab(root, Buildings chunk_CC_RR)
```

**Why this staging order matters:** later stages depend on earlier geometry (storefronts occupy
ground-floor zones that windows must avoid; signs attach above storefronts). The order is the
PDF's and is encoded as a fixed stage list, not per-template, so all buildings compose
consistently.

**Performance posture (ties into active view-streaming work):**
- Templated buildings are individual prefabs → larger chunk prefabs → heavier streaming payload.
  This directly interacts with issues #202 / #208 / #210 (view-based streaming, parked-car
  streaming cost). The fallback-merge path keeps un-templated buildings off this budget.
- Mitigations available without redesign: (a) share one `MaterialPropertyBlock`-driven material
  across all parts (no material explosion); (b) optional per-chunk static-batch / mesh-combine of
  a templated building's parts into a single building mesh at import (loses per-part identity at
  runtime, keeps it at author time); (c) cap templated-building count per chunk with explicit
  `log()` of what was left merged (no silent truncation).
- Decision: ship per-part prefabs first (authoring fidelity), keep (b) as a documented import
  toggle for when streaming cost demands it. Do not pre-optimize.

---

## Material roles & neighborhood palettes

The current system is `hash(osm_id) % 7 → one of 7 pastel colors`, baked to vertex color, one
material per chunk. The new model generalizes it:

```
role (Base|Accent1|Accent2|Glass|Metal|Sign)
        × NeighborhoodPalette (Sunset warm/cream/muted; Mission bold/high-contrast; FiDi glass/steel/concrete)
        × seed(osm_id)
   ─────────────────────────────────────────────▶ resolved Color, BAKED INTO VERTEX COLORS
```

- `Glass`/`Metal` may be neighborhood-independent constants; `Base`/`Accent*` are sampled from
  the neighborhood's constrained ranges (PDF: "randomized within neighborhood constraints").
- `Sign` role is not a color — sign submeshes get the authored/AI PNG texture (own material).
- **Resolved colors are written into vertex colors at import**, per part submesh, then the part
  meshes carry the existing `SFMap/BuildingVertexColor` shader. **Do not use a per-renderer
  `MaterialPropertyBlock`** for the color: an MPB makes each renderer a unique batch and would
  produce one draw call per part across thousands of buildings (a draw-call *explosion*, the
  opposite of the goal). Vertex-color baking keeps one shared material, so a templated building's
  non-sign parts + mass can be mesh-combined at import and static-batched at runtime.
- This makes the templated path a strict superset of the existing one: the current
  `buildings_combined.mesh` already bakes a palette color into vertex colors via the same shader.
  Templated buildings just bake *per-role* colors onto richer geometry.
- Un-templated fallback buildings keep their single palette-derived vertex color, so both paths
  read from the *same* `NeighborhoodPalette` SO and stay visually coherent.
- **Signs are the batching break.** They alone need unique textures; everything else is vertex-
  colored and combinable. So a building splits into a combinable vertex-colored shell (mass +
  non-sign parts) and a small set of sign quads (separate, atlas-candidate — see open questions).

---

## Server & iPad (contract depth only)

- **Server** (FastAPI, SQLite→Postgres later, on-disk asset store): source of truth for parts,
  templates, signs, neighborhoods, palettes, building-specific overrides. Only the server calls
  AI providers (provider abstraction: ChatGPT image gen / Nano Banana / future). API surface in
  `data-model.md`.
- **iPad**: authoring UI only. Browses buildings, traces facades, authors parts/templates/rules,
  *requests* AI signs (server fulfills), saves back. Never touches AI or Unity directly.
- **The export** (`POST /export/unity`): writes a versioned asset drop into
  `Assets/SFBuildingTemplates/`. A Unity-side importer converts exported template/palette JSON to
  ScriptableObjects and registers GLB parts / PNG signs. This is the *entire* coupling between the
  authoring world and the generation world. Requires a glTF import package
  (glTFast or UnityGLTF) — flagged as a dependency, not chosen here.

---

## MVP slice (the thin vertical, mapped to seams)

PDF's minimal loop, expressed as the smallest change that exercises every boundary exactly once:

1. Author **one window Part** on iPad → save GLB+roles to server.
2. `POST /export/unity` → `Assets/SFBuildingTemplates/Parts/window_*.glb` + a one-rule
   **Template** ("any building, one window, Front, floor 0, x=0.5, Exact").
3. Bake with `--templates`: Python emits `chunk_CC_RR_buildings.json` with classification +
   `front_facade` for each building.
4. Unity import: Assembler matches the trivial template, places the window on one building's
   front facade, resolves a Base color from the neighborhood palette, saves the nested prefab.
5. `ChunkStreamer` streams it unchanged.

Everything else (doors, garages, bay windows, storefronts, signs, procedural rules, the full
neighborhood template set, building-specific overrides) is additive on top of this loop and maps
to follow-up leaf issues at plan time.

---

## Design review (analyze pass) — decisions & accepted risks

An adversarial pass against the two high-rigor areas and the Python/Unity boundary. Findings that
changed the design are folded in above; the load-bearing decisions are recorded here.

**D1 — Color via vertex baking, not MaterialPropertyBlock. (corrected)**
The draft's "MPB ⇒ no draw-call explosion" was backwards: an MPB defeats SRP/static batching and
yields one draw call per part. Resolved-role colors are baked into vertex colors against the
existing `SFMap/BuildingVertexColor` shader, keeping templated buildings combinable/batchable and
making the templated path a superset of today's. *Why it matters:* a full SF bake is thousands of
buildings; this is the difference between a few hundred draw calls and tens of thousands.

**D2 — Street facades are ranked, not singular. (corrected)**
A single `front_facade` mis-models corner buildings — the exact "Mission Corner Store" case the
PDF names. Python emits `street_facades[]` (ranked); templates target `Front` (primary) or
`Street` (all ranked). *Why it matters:* without it, corner storefronts/signs face an alley.

**D3 — `footprint_hash` is a three-party shared contract. (gap closed)**
The hash is computed by the Python bake but the *server* authors the building-specific overrides
that must match it. So the hash algorithm (quantization grid + canonical vertex ordering + hash
fn) is a spec shared by Python **and** the server, and both must derive footprints from the same
OSM source. Specified as a normative algorithm in `data-model.md §6`, not a Python implementation
detail. *Why it matters:* a mismatch silently disables every override.

**D4 — Stuck-on parts, no boolean openings. (accepted risk)**
Parts mount onto the solid Python mass wall; walls are not cut. For the project's low-poly art
style this reads correctly and avoids CSG/retriangulation of the terrain-adapted mass. Parts carry
a `mountDepth` (inset for windows, protrude for bay windows) to prevent z-fighting. *Accepted:*
anyone expecting punched openings will be surprised — documented, not a bug.

**D5 — Template variety is gated by height/floor data quality. (accepted risk + dependency)**
`floor_count = round(height/3.0)`, but OSM `height` is frequently absent and `building.py` defaults
to 10 m → most buildings collapse to ~3 floors, flattening any compatibility band keyed on floors.
Mitigation path: `building_type`→typical-height priors before defaulting. *Accepted for MVP;*
flagged as a data-quality dependency that caps achievable variety.

**D6 — Coverage is observable only at import; log it. (process)**
Because Python emits facts and Unity matches, template coverage is unknown until import. The
importer must `log` templated-vs-fallback counts per chunk (no silent truncation), so coverage is
visible without reverse-engineering the prefab.

**D7 — Per-building-prefab cost vs view-streaming. (deferred, measured)**
Templated buildings enlarge chunk prefabs, touching #202/#208/#210. Decision stands: ship per-part
prefabs first; the import mesh-combine toggle (now cleanly enabled by D1's vertex-color baking) is
the lever to pull *after measuring*, not before.

---

## What this design leaves open (deferred to plan / implementation)

- **glTF import package choice** (glTFast vs UnityGLTF) and its license/runtime footprint.
- **Footprint-shape classifier** thresholds (what counts as L vs corner vs irregular).
- **Neighborhood source**: confirm an authoritative SF neighborhood GeoJSON and its license; how
  to handle buildings outside any neighborhood polygon (default neighborhood?).
- **`footprint_hash` quantization** grid default is `0.25 m` (`data-model.md §6.1`); the value is
  a tradeoff (override robustness vs sensitivity) and may need tuning against real edit patterns.
- **Template compatibility scoring** when multiple templates match (pure seeded random vs a
  weighted score) — start with seeded-random, revisit if results look monotonous.
- **Roof geometry**: trim/cornice as parts is in-scope; *pitched* roof shapes (mass change) are
  deferred — likely a Python mass-generation extension, not a part.
- **Streaming-cost ceiling**: the per-building-prefab budget vs view-streaming work (#202/#208/
  #210) — measure before enabling import mesh-combine (toggle b above).
- **Sign atlasing**: many unique AI PNGs per chunk could blow texture memory; atlas strategy TBD.
- **Server schema** (SQLite vs Postgres) and asset versioning model — authoring-side, not on the
  Unity critical path; depth deferred.
```
