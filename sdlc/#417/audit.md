# #417 — Codebase Organization Audit & Migration Plan

Persona: techdesigner. Method: three parallel deep-reads (Unity project, `python/`, server + repo root), synthesized here. Deliverable is this artifact only; reorganization happens in the follow-up PRs listed in §5.

Guiding stance: everything below is a **refactor, not a rewrite**. Every step keeps the blast radius small, is independently revertable, and has a concrete "did behavior change?" check (§6). No new abstractions are proposed that don't already have two concrete call sites today.

---

## 1. Current file/module map

The repo is four codebases sharing one git root:

```
UnitySF/
├── My project (2)/          Unity client (~38 first-party .cs, ~8,900 lines)
├── python/                  bake pipeline (~6,900 lines: 15 library modules, 9 scripts, 5 test files)
├── server/                  FastAPI authoring server (~1,960 lines, 11 test files)
├── sdlc/#N/                 per-issue design artifacts (tracked)
├── .claude/skills/          baker / roadfixer / sdlc — operational glue
├── chunks_*/ (~25 dirs)     baked output, ~1.7 GB, untracked and NOT gitignored
├── worktrees/               26 sdlc worktrees (git auto-excluded)
└── (root strays)            *.bake.log, full_sf_map.osm hardlink (ignored),
                             canvas-design-doc.txt, mvp_building-design-doc.txt,
                             memory/, RoadFixerInbox/ (all untracked)
```

### 1.1 Unity — `My project (2)/Assets/`

| Assembly | Location | Size | Refs | Role |
|---|---|---|---|---|
| SFMap.Pipeline | `Scripts/` (root asmdef) | ~24 files | Jobs, Collections, Burst | Runtime: streaming, traffic, roads, HUD, taxi game |
| SFMap.Pipeline.Editor | `Scripts/Pipeline/Editor/` | 7 files | Pipeline, Buildings, RandomationVP | Bake→Unity importer |
| SFMap.Pipeline.Buildings | `Scripts/Pipeline/Buildings/` | 6 files | (none) | Template ScriptableObjects + wire DTOs |
| SFMap.Pipeline.Buildings.Editor | `…/Buildings/Editor/` | 1 file | Buildings | `library.json` → ScriptableObjects |
| SFMap.Graffiti | `Scripts/Graffiti/` | 3 files | Pipeline | Spray paint |
| SFMap.OnFoot | `Scripts/OnFoot/` | 2 files | Pipeline | On-foot player, mode switch |
| SFMap.Vehicles | **phantom** | 0 files | — | csproj references `Scripts/Vehicles/` which does not exist on disk |
| SFMap.Tests.Editor | `Assets/Tests/Editor/` | **0 tests** | Pipeline, nunit | Empty shell; a second legacy `Tests.asmdef` sits beside it |
| Assembly-CSharp (implicit) | `Assets/Dev/`, `Assets/PROMETEO - Car Controller/` | — | — | DeveloperMode + the entire player car controller |

Key runtime classes: `ChunkStreamer` (287 L), `ParkedCarStreamer` (407 L), `TrafficManager` (406 L) + `TrafficCar` (557 L), `RoadNetwork` (429 L), `RoadNameIndex` (141 L), `HeightField` (129 L), `CarRoadSpawner` (171 L), `PipelineTypes.cs` (`ChunkCoord`, `GeneratedAssets` path conventions). Key editor classes: `SFMapImporterWindow` (**1,008 L god class**), `BuildingAssembler` (788 L), `BuildingDecalImporter`, `BuildingTemplateLibraryImporter` (380 L).

Asset folders: `Generated/<preset>/` and `Resources/Generated/<preset>/` hold **two copies** of every baked preset (source assets vs Resources-loaded prefabs+manifest+sidecars); `SFMapData/` holds raw Python-bake **inputs** (500 MB OSM, 181 MB elevation CSV — gitignored, but three orphan `.meta` files for them are tracked) plus generated `.heightcache` files that double as runtime input; `SFBuildingTemplates/` mixes hand-authored (`library.json`, Parts/Palettes/Templates/Overrides/Signs) with importer output (`Generated/`); `_Recovery/` holds stray crash-recovery scenes.

### 1.2 Python — `python/`

Library (`sfmap/`): `projection.py` (52 L, mirrors GeoProjection.cs — the one clean module), `osm.py` (596 L — parsing + domain heuristics + geometry), `elevation.py` (424 L — heightmap + `.heightcache` cache), `stamping.py` (252 L), `classify.py` (501 L — building facts, `footprint_hash` shared with server), `chunk.py` (339 L — per-chunk orchestration), `serialize.py` (334 L — `.bin` writer + 3 JSON sidecar writers + manifest + the data types).
`geometry/`: `road.py` (459 L — road meshes **and** de-facto shared primitives), `intersection.py` (443 L), `parking.py` (751 L, largest), `building.py` (238 L), `sidewalk.py` (143 L), `neighborhood.py` (140 L).

Scripts: `sfmap_bake.py` (CLI orchestrator, tracked), `chunk_at.py` + `heightcache_guard.py` (**real tools the baker skill depends on — untracked**), `road_smoothing_tuning.py` (tracked, imported by a test), `diag_145/168/169/169b.py` + `spike_141.py` (**one-off issue-specific scripts — tracked**, hard-code absolute machine paths).

Tests (`tests/`, ~856 lines): classify, elevation smoothing, neighborhood, road smoother (+tuning lock). pytest is used but **not a declared dependency**; each test file ships its own `__main__` runner and `sys.path` shim. No coverage of: `serialize` (the C# contract!), `osm`, `chunk`, `stamping`, `parking`, `intersection`, `building`, `sidewalk`, `projection`, CLI.

### 1.3 Server — `server/`

FastAPI + SQLite, cleanly factored (`main/models/export/store/resolve/ai_signs/canvas/zones/footprint_hash`), 11 test files, own README, own working `.gitignore`. Only nit: 3 tracked `server/.pytest_cache/` files. `footprint_hash.py` deliberately re-implements `sfmap/classify.py`'s hash byte-for-byte, with a parity test. This codebase is the organizational model the other two should converge toward.

---

## 2. Pipeline stages

```
Stage 1 (python)        Stage 2 (Unity editor)         Stage 3 (Unity runtime)
sfmap_bake.py           SFMapImporterWindow            SampleScene
─────────────           ───────────────────            ───────────────────────
OSM parse (osm)         read manifest.json             ChunkStreamer ← Resources
elevation (+cache)      per chunk: .bin → Terrain      RoadNetwork/RoadNameIndex
intersections           + meshes + materials             ← _names.json
parking CSV             parked cars ← _parked.json     ParkedCarStreamer ← _parked.json
neighborhoods           building templates             TrafficManager/TrafficCar
grid sizing               ← BuildingAssembler          CarRoadSpawner, OnFoot,
per-chunk bake:           ← library.json importer      Graffiti, TaxiGame, HUDs
  crop→stamp→mesh→      prefab → Resources/Generated/
  classify→park         ChunkManifest.asset
write chunk_CC_RR.bin
+ 3 JSON sidecars              Side channel: importer ALSO does blocking HTTP
+ manifest.json                to the authoring server (localhost:8000) —
        │                      thumbnails, sidecar upload, facade backdrops
        ▼
chunks_<preset>/        Contract: --preset string must equal the importer's
(repo root, 1.7 GB)     "Preset Name" → Assets/Generated/{preset}/

Authoring loop: server (FastAPI) ⇄ iPad client; POST /export/unity materialises
Assets/SFBuildingTemplates/, consumed by BuildingTemplateLibraryImporter.
Bake feeds the server via POST /buildings/import-sidecar (footprint_hash parity).
```

Operational knowledge lives in `.claude/skills/baker/SKILL.md` and `python/RUNBOOK.md` (pyosmium install trap, the `.osm` hardlink trick, bake size baselines, heightcache semantics). There is **no root README** stitching the stages together.

---

## 3. Mixed responsibilities (ranked)

Worst first, each with the migration PR that addresses it (§5).

| # | Tangle | Evidence | PR |
|---|---|---|---|
| X1 | **`.bin` format defined twice, independently, untested on both sides.** Python `struct` strings in `serialize.py` vs hand-matched sequential `BinaryReader` in `SFMapImporterWindow.cs:278-297`; magic/version/MeshType enum duplicated by comment-coupling only. Same pattern for `.heightcache` and `projection.py`↔`GeoProjection.cs`. Highest-risk drift point in the repo. | `serialize.py`, `SFMapImporterWindow.cs` | 4, 9 |
| X2 | **1.7 GB of bake output is one `git add -A` from being committed.** `chunks_*/` and `*.bake.log` are untracked but not gitignored. | root `.gitignore` | 1 |
| X3 | **The baker skill hard-depends on four untracked files** (`chunk_at.py`, `heightcache_guard.py`, `landmarks.json`, `no_parking_roads.json`) while five dead issue-specific diag scripts *are* committed. A fresh clone cannot run `/baker around`. | `python/` | 2, 3 |
| U1 | **`SFMapImporterWindow` god class (1,008 L)**: GUI + binary parsing + terrain/mesh building + prefab writing + manifest + offscreen rendering + **synchronous HTTP uploads to hard-coded `http://localhost:8000`** with spin-wait loops. | `Pipeline/Editor/SFMapImporterWindow.cs` | 9, 10 |
| U2 | **Prometeo car controller stranded in Assembly-CSharp → string reflection in 4 runtime classes** (`TaxiGame.cs:123`, `PlayerModeController.cs:151`, `CompassHUD.cs:76`, `StreetHUD.cs:39` all `Type.GetType("CameraFollow, Assembly-CSharp")`). Plus the dead `#if false` duplicate `PrometeoCarSetup.cs` this caused. | `Assets/PROMETEO - Car Controller/` | 11 |
| U3 | **Copy-paste runtime helpers**: `EnsureCollider`/`EnsureSolidCollider` near-identical in `TrafficManager.cs:380` and `ParkedCarStreamer.cs:347`; `WorldToChunk`/origin-derivation grid math re-derived in 4 classes; `RoadNamesJson` DTO + manifest walk duplicated (divergently) between `RoadNetwork.cs:426` and `RoadNameIndex.cs:138`. | Pipeline runtime | 12 |
| U4 | **Resources/ misuse**: every preset's chunk prefabs + sidecars force-included in every build via `Resources/Generated/`, loaded through the legacy API in 5 classes. | `Assets/Resources/` | 14 (deferred) |
| U5 | **Mutable static coordination**: `GeneratedAssets.ActivePreset` written by importer, `ChunkStreamer`, and `RoadNetwork` (which steals a serialized field off another component to dodge an OnEnable/Awake race — documented smell at `RoadNetwork.cs:115-125`). | `PipelineTypes.cs` | 15 (deferred) |
| U6 | **Building template concepts represented three ways** across two assemblies (`BuildingAssembler` facts DTOs, `LibraryJson` wire DTOs, `BuildingTemplate` runtime shapes); facade-frame math duplicated between `BuildingAssembler.PlacePart` and `SFMapImporterWindow.RenderFacadeBackdrop`; `"Assets/SFBuildingTemplates"` string constant in 3 files, Overrides/ scanned twice. | Buildings assemblies | 13 |
| P1 | **`geometry/road.py` is a covert shared-primitives module**: 8 underscore-private helpers (`_sample_elevation`, `_clip_polyline_to_rect`, `_densify_polyline`, …) imported by `building`, `intersection`, `parking`, `sidewalk`, `stamping`. Any `road.py` refactor silently breaks five modules. | `geometry/road.py` | 6 |
| P2 | **Data types live inside the serializer**: `MeshType`/`MeshEntry`/`ChunkData` defined in `serialize.py`; `chunk.py` couples to them; one file mixes the binary writer with three unrelated JSON schemas + manifest. | `serialize.py` | 7 |
| P3 | **`osm.py` mixes parsing, domain heuristics, and geometry** (width tables, lane/one-way/parking rules, height inference, `crop_to_chunk`). Bonus latent bug: `osm._polyline_intersects_rect` and `road._clip_polyline_to_rect` are documented as needing to agree but live apart. | `osm.py` | 8 |
| P4 | **CLI ↔ library leakage**: `sfmap_bake.py` duplicates `_DEFAULT_HMAP_SMOOTH_M` to avoid a scipy import; real pipeline logic (`_geometry_extent`, `_chunk_list`) lives in the CLI; `heightcache_guard.py` reaches into a CLI private and hashes `elevation.py` source bytes. | `sfmap_bake.py` | 8 |
| P5 | **Config in three disconnected tiers** — CLI flags, JSON files, and ~30 hard-coded module constants (widths, smoothing, car sizes, hash grid) not overridable without editing source. "Presets" don't exist in Python at all; the target/preset system lives only in the baker skill. | `sfmap/*` | noted, deferred |
| T1 | **Zero Unity tests** (`SFMap.Tests.Editor` is an empty asmdef) and no Python coverage of serialize/osm/chunk/stamping/parking. The byte contract (X1) is untested on both sides. | tests | 4, 5, 9 |

---

## 4. Proposed folder structure

Minimal moves — names change only where a name is actively lying. Unity asset moves are deliberately few (every move churns `.meta` GUIDs and scene references).

```
UnitySF/
├── README.md                        ← NEW: the bake→import→runtime→server map; links RUNBOOK, baker skill
├── .gitignore                       ← + chunks_*/ , *.bake.log , _Recovery/
├── docs/
│   ├── chunk-bin-format.md          ← NEW: single normative .bin/.heightcache spec (X1)
│   ├── canvas-design-doc.md         ← moved from root .txt
│   └── mvp-building-design-doc.md   ← moved from root .txt
├── python/
│   ├── sfmap/
│   │   ├── types.py                 ← NEW: MeshType, MeshEntry, ChunkData (from serialize.py)
│   │   ├── serialize.py             ← writers only
│   │   ├── osm.py                   ← parsing + graph only
│   │   ├── tags.py                  ← NEW: width/lane/one-way/parking/height heuristics (from osm.py)
│   │   ├── grid.py                  ← NEW: _geometry_extent + _chunk_list (from sfmap_bake.py)
│   │   └── geometry/
│   │       ├── primitives.py        ← NEW: the 8 shared helpers (from road.py), public names
│   │       └── road.py              ← road meshes only
│   ├── tools/                       ← road_smoothing_tuning.py moves here
│   ├── tests/                       ├── + test_serialize_golden.py, test_grid.py, …
│   ├── chunk_at.py                  ← COMMITTED (currently untracked)
│   ├── heightcache_guard.py         ← COMMITTED
│   ├── landmarks.json               ← COMMITTED
│   ├── no_parking_roads.json        ← COMMITTED
│   ├── requirements-dev.txt         ← NEW: pytest
│   └── (diag_*.py, spike_141.py)    ← DELETED (git history keeps them)
├── server/                          ← unchanged (drop tracked .pytest_cache)
└── My project (2)/Assets/
    ├── Scripts/Pipeline/
    │   ├── Editor/
    │   │   ├── SFMapImporterWindow.cs    ← GUI + orchestration only (~300 L)
    │   │   ├── ChunkBinReader.cs         ← NEW: .bin parsing (X1 counterpart)
    │   │   ├── ChunkAssetBuilder.cs      ← NEW: terrain/mesh/material/prefab building
    │   │   └── AuthoringServerClient.cs  ← NEW: the HTTP uploads, base URL configurable
    │   └── RoadNamesData.cs              ← NEW: shared DTO + loader (RoadNetwork + RoadNameIndex)
    ├── PROMETEO - Car Controller/
    │   └── Prometeo.asmdef               ← NEW: kills the 4 reflection shims
    ├── Editor/PrometeoCarSetup.cs        ← now references Prometeo asmdef normally
    ├── Tests/Editor/                     ← populated; legacy Tests.asmdef deleted
    └── (deleted: Scripts/Pipeline/Editor/PrometeoCarSetup.cs stub, _Recovery/,
       orphan SFMapData *.meta, phantom SFMap.Vehicles references)
```

Explicitly **not** proposed now: moving `SFMapData/` out of `Assets/` (the heightcache doubles as a runtime asset — needs its own design), Addressables migration (U4), a Python config system (P5), merging the three building DTO families into one (U6 gets consolidation of duplicated *math and paths* only — the DTOs serve three genuinely different lifetimes: bake facts, wire format, runtime SO). Each of those is younger than two use cases or bigger than one reviewable PR.

---

## 5. Migration plan — sequenced PRs

Ordering principle: hygiene first (zero behavior risk), then the safety net (tests around the contracts), then structural moves *under* that net. Every PR is independently mergeable and revertable; none changes bake output or runtime behavior except where marked.

**Phase 0 — hygiene (no code paths touched)**

1. `chore(repo): gitignore bake output` — add `chunks_*/`, `*.bake.log`, `_Recovery/` to `.gitignore`; untrack `server/.pytest_cache/`. *(addresses X2)*
2. `chore(repo): commit baker dependencies + root README` — track `chunk_at.py`, `heightcache_guard.py`, `landmarks.json`, `no_parking_roads.json`; add root `README.md`; move the two root design `.txt`s to `docs/`. *(X3, docs gap)*
3. `chore: delete dead scripts and Unity artifacts` — remove `diag_145/168/169/169b.py`, `spike_141.py`, the `#if false` `PrometeoCarSetup.cs` stub, legacy `Tests.asmdef`, orphan `SFMapData` `.meta` files; resolve the phantom `SFMap.Vehicles` csproj (regenerate solution). *(X3, U2-adjacent)*

**Phase 1 — safety net (tests only, no production changes)**

4. `test(python): golden-file .bin round-trip` — check in a tiny fixture chunk; assert `serialize.write_chunk` output is byte-identical; document the format in `docs/chunk-bin-format.md` as the single normative spec. *(X1, T1)*
5. `test(python): declare pytest, drop per-file runners` — `requirements-dev.txt`, delete the `__main__`/`sys.path` shims (conftest already covers it). *(T1)*

**Phase 2 — Python structure (each verified by byte-identical rebake, §6)**

6. `refactor(python): extract geometry/primitives.py` — move the 8 shared helpers out of `road.py` with public names; update 5 import sites. *(P1)*
7. `refactor(python): extract sfmap/types.py` — `MeshType`/`MeshEntry`/`ChunkData` out of `serialize.py`. *(P2)*
8. `refactor(python): separate OSM parsing from heuristics; move grid logic into library` — `tags.py` out of `osm.py`; `grid.py` from the CLI; collapse the duplicated smoothing default; give `heightcache_guard` a public constant to read. *(P3, P4)*

**Phase 3 — Unity structure (each verified by re-import determinism + play-mode smoke, §6)**

9. `refactor(unity): extract ChunkBinReader from SFMapImporterWindow` + edit-mode tests reading the PR-4 golden fixture — the two sides of X1 now covered by the same fixture. *(U1, X1, T1)*
10. `refactor(unity): extract AuthoringServerClient` — HTTP code out of the importer; base URL a setting, not a literal; importer works offline (uploads become explicit/optional). *(U1, U6-adjacent)*
11. `refactor(unity): asmdef for Prometeo; delete reflection shims` — 4 call sites become direct references; keep the single `Assets/Editor/PrometeoCarSetup.cs`. *(U2)*
12. `refactor(unity): shared runtime helpers` — one `RoadNamesJson` DTO + loader; `EnsureSolidCollider` + `WorldToChunk`/origin math onto one utility (grid math belongs with `ChunkManifest`). *(U3)*
13. `refactor(unity): consolidate SFBuildingTemplates paths + facade-frame math` — one path constant, one Overrides scan, one bearing→frame function shared by `BuildingAssembler` and the backdrop renderer. *(U6, partial)*

**Phase 4 — deferred (each needs its own issue + design, not started from this plan)**

14. Resources → Addressables for streamed chunks *(U4)* — behavior-affecting, build-pipeline-affecting.
15. Retire `GeneratedAssets.ActivePreset` mutable static *(U5)* — touches the streamer/network bootstrap race; design the ownership first.
16. Python config consolidation *(P5)* — only worth it once a second consumer of the constants exists.

**Suggested capture:** PRs 1–3 as one `chore` issue each or a single umbrella; 4–5 one `test` issue; 6–8 and 9–13 one `refactor` issue per PR (they're the reviewable units).

---

## 6. "Do not break behavior" test plan

Three reusable verification harnesses, then a per-PR matrix.

**V-BAKE — byte-identical rebake (Python PRs).**
Bake a small fixed target twice at the base commit (`/baker around "20th and Kansas" --ring 1` semantics; same flags, same inputs) and `fc /b`-compare all `chunk_*.bin` + sidecar JSONs + `manifest.json` to confirm the bake is deterministic *before* trusting the harness. Then: bake at base, bake at PR head, diff byte-for-byte. Any diff fails the PR. (The PR-4 golden-file test is the fast in-repo proxy; V-BAKE is the full-pipeline check.)

**V-IMPORT — deterministic re-import (Unity editor PRs).**
On a fixed `chunks_cs150`-scale input: run the importer at base and at PR head into two preset names; compare generated asset inventories (file list, prefab count, mesh vertex/index counts, `ChunkManifest` contents) and `BuildingAssembler.LogCoverage` output. For PR 10, additionally verify import completes with the server *down* (the current behavior blocks; the new behavior must degrade gracefully — this is the one intentional behavior change, called out in that PR's description).

**V-PLAY — play-mode smoke (Unity runtime PRs).**
Enter play in `SampleScene` on an imported preset; checklist: chunks stream in/out while driving, street/compass HUD shows names, traffic spawns and yields at junctions, parked cars appear/pool, on-foot mode toggles, spray paint hits buildings, taxi loop starts. Capture the Player log; zero new exceptions/warnings vs a base-commit capture of the same drive.

**Per-PR matrix**

| PR | Verification |
|---|---|
| 1 | `git status` shows chunks_*/logs ignored; `git ls-files` count unchanged otherwise |
| 2 | fresh-clone check: `python chunk_at.py` + `heightcache_guard.py` run; README links resolve |
| 3 | Unity compiles with zero references to deleted files (grep); solution regenerates without SFMap.Vehicles |
| 4–5 | new tests pass at head; deliberately corrupt one header byte in the fixture → test fails (proves the test bites) |
| 6–8 | V-BAKE byte-identical; full pytest suite green |
| 9 | V-IMPORT identical; new edit-mode tests read the PR-4 fixture; V-BAKE fixture unchanged |
| 10 | V-IMPORT identical with server up; import succeeds with server down (documented behavior change); server pytest green |
| 11 | V-PLAY full checklist (reflection call sites were runtime paths: taxi camera, mode switch, both HUDs) |
| 12 | V-PLAY (streaming, traffic colliders, street names) + V-IMPORT unchanged |
| 13 | V-IMPORT identical incl. building coverage log; decals/backdrops pixel-compare on one building |

**Cross-cutting invariants (run at every phase boundary):** the PR-4 golden `.bin` fixture never changes hash except by an explicit format-version bump; `server/tests/test_footprint_hash.py` parity stays green; the baker skill's documented flow (`SKILL.md`) still matches reality — update it in the same PR when a path/flag it names moves.
