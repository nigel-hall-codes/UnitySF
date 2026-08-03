# #496 — Procedural street furniture: a placement system off the road network

**Status:** drafting · **Persona:** systems-architect · **Branch:** `sdlc/#496-sysdesign`

Sibling to [#452](../../../sdlc-452-procedural-building-artifacts/sdlc/%23452/design.md). #452 dressed
the buildings. The streets between them are still bare. This document specifies the placement
system that furnishes them, and deliberately specifies **only** the placement system — the
geometry is a solved problem and says so in §0.

---

## 0. The two findings that shape this design

**Finding 1 — the geometry is already built.** #453's kernels merged. A lamp post is
`Kernels.ProfileSweep(Profiles.Round, Paths.Line(0, 8m))` plus `Kernels.Box` for the head. A
hydrant is three stacked `ProfileSweep` sections. A parking meter is a `Line` sweep and a chamfered
`Box`. `IPartGenerator`, `PartParams`, `PartMeshCache`, `Detail`, `MaterialRole` → palette →
vertex colour: all merged, all reusable **without modification**. Critically, the part-local frame
`IPartGenerator` mandates — *+X along the facade, +Y up, +Z outward toward the street* — is
already the frame a piece of kerbside furniture wants (+Z = kerb normal). Nothing in the generator
seam is building-specific except the word "Building" in two type names.

**Finding 2 — the placement machinery is already built too, but it is in Python, not C#.**

The issue frames this as "none of the placement machinery applies", which is true of
`BuildingAssembler`. It is **not** true of the bake. `python/sfmap/geometry/parking.py` is a
1-per-2.25 m distribution along kerb polylines with, already implemented and already debugged:

| Machinery in `parking.py` today | Constant / function |
|---|---|
| kerb polyline offset from centerline | shares `sidewalk.py`'s `half_w` / `half_w + _WIDTH` |
| arc-length walk with seeded jitter and skipped slots | seeded by source feature id |
| intersection clearance | `_INTERSECTION_CLEARANCE_M = 6.0` |
| driveway curb-cut keep-outs, incl. **synthesised** ones for residential buildings OSM never tagged | `_DRIVEWAY_CLEARANCE`, `_DRIVEWAY_PROBE_M`, `_DRIVEWAY_DEDUPE_M`, `_RESIDENTIAL_BUILDINGS` |
| keep-out corridors from a regulation layer | `_KEEPOUT_STEP`, `_NO_PARK_CLEARANCE` |
| stacked-placement dedupe | `_DEDUPE_DIST_M` |
| road-class gating | `StreetEdge.allows_parking`, `road_allows_parking` |
| ground normal + slope conform | `_NORMAL_EPS`, `_MIN_TILT_DEG` |
| cross-chunk seam handling | margin-expanded stamp graph + exact-rect clip (`chunk.py`) |

That list *is* §3 of this design. Every hard exclusion the issue names — driveways, corners,
not standing a lamp post inside a parked car — has a working implementation forty lines away from
where the furniture solver would run. The design's job is to not rewrite it in C#.

---

## 1. Decisions

### D1 — **Positions in the Python bake. Meshes in Unity.** The #452 asymmetry argument applies to one and not the other.

This is the crux question and it wants a split answer, because #452 D1 was answering a question
about **meshes** and the interesting question here is about **positions**.

**Meshes: #452 D1 transfers unchanged, keep it.** The asymmetry is if anything more extreme.
A city has perhaps a dozen *distinct* lamp-post parameter sets and on the order of 10⁵
*placements*. Serialising vertices would grow the `.bin` with placements; generating in Unity
costs one `PartMeshCache` entry per distinct parameter set. Nothing about furniture weakens that
argument. Meshes go through `IPartGenerator` exactly as building parts do.

**Positions: the argument does not transfer, and the answer flips.** #452 never had to decide this
because a facade placement is *free* — `buildings.json` already carries the facade edge and
bearing, and `PlacePart` derives world position by arithmetic on data that was crossing the wire
anyway. A furniture position is not free. Computing one requires:

- the **kerb polyline** — the road centerline offset by `edge.width/2`, densified to `MAX_SEG_M`,
  elevation-sampled per vertex, and anchored at chunk-boundary crop points. Unity has only the raw,
  undensified, elevation-free centerline in `_names.json` (`RoadEntry.xz`);
- **ground elevation** at the seat. In Unity this is a raycast against a collider, i.e. exactly the
  cook cost #263 exists to remove, paid per item;
- **exclusion against the parked-car set**, which lives in a *different sidecar*;
- **exclusion against synthetic driveway curb cuts**, which are derived from building footprints
  and OSM `service=driveway` throats by an algorithm that took several issues to get right.

And the thing #452 D1 was protecting against — payload growth — does not bite. A furniture record
is `{"t":1,"p":[x,y,z],"r":214.5,"k":88213}` ≈ 45 bytes. `_parked.json` is already ~100 KB/chunk
(`ParkedCarStreamer.cs:48`) and nobody has complained. Furniture at comparable density is the same
order of magnitude on a payload dominated by the `.bin`.

| | Python bake (**chosen for positions**) | Unity import | Runtime |
|---|---|---|---|
| Reuses the kerb walk + exclusion set | **yes, verbatim** | no — second C# implementation of a subtle algorithm | no |
| Parked-car exclusion | in-memory, same pass, ordered | must load + spatially index `_parked.json` at import | same, per frame |
| Elevation | `hmap` in hand | collider raycast (#263 cost) | collider raycast |
| Cross-seam correctness | proven pattern (`place_parked_cars` rect-clip) | must be reinvented | must be reinvented |
| Payload | +~45 B × placements ≈ `_parked.json` scale | zero | zero |
| Iteration loop | **re-bake (minutes)** ← the cost | re-import (seconds) | live |
| Determinism | seeded `random.Random`, byte-stable sorted output | `Rng`/`SeedFor` | not reproducible across sessions without care |

**The tradeoff, named:** we trade a fast iteration loop for not writing the exclusion algorithm
twice. Changing "lamp posts every 30 m" to "every 26 m" costs a re-bake, not a re-import.

**Mitigation, and it is the reason I am comfortable with the trade:** the bake emits *seats*
generously and the consumer thins them. Every record carries its item-type index `t` and its
stable key `k`; a per-type density knob at import thins by `hash(k) < density` — the identical
trick `ParkedCarStreamer.DensityKey` already ships. So the expensive-to-change thing is the
*geometry rule* (where a seat is legal), and the cheap-to-change thing is the *aesthetic rule*
(how many of the legal seats get filled). Those are the right things to make expensive and cheap
respectively: the first is correctness, the second is taste.

**Rejected: runtime placement (the pure `ParkedCarStreamer` model).** Furniture is static, small,
and combines beautifully (D4); paying per-frame spawn cost for something that never moves is
strictly worse than paying it once. Parked cars are runtime-spawned because they are multi-material
imported prefabs that *cannot* be combined. Furniture is generated single-material geometry that
can. The #262 preference is about parked cars specifically, not a law about kerbside objects — see
D4 for where it does apply.

### D2 — The sibling to `BuildingAssembler` is `StreetFurnitureAssembler`, and it is much smaller

`BuildingAssembler` is 1009 lines because it does template matching, facade frames, floor bands,
procedural rules and palette resolution. The furniture sibling does none of that. Its whole job:

```
for each record in chunk_CC_RR_furniture.json:
    part   = ResolvePart(furnitureLibrary[record.t])          // a BuildingPart, unchanged
    mesh   = _partMeshes.GetOrCreate(part.parameters.KeyFor(part.generatorId), …)   // unchanged
    xform  = TRS(record.p, Quaternion.Euler(0, record.r, 0) · slopeTilt(record.n), one)
    accumulate into combines[record.t]
for each item type t:
    one GameObject, one combined Mesh, one MeshRenderer, colliders per D4
```

It reuses `PartGenerators.TryResolve`, `PartMeshCache`, `PartParams`, `MaterialRole` →
`NeighborhoodPalette` vertex-colour bake, and `ReleaseGeneratedMeshes` **unchanged**. It shares
nothing with `PlacePart` because it has no facade — and it needs nothing from it, because the
sidecar hands it a world transform. Estimated 150–200 lines.

The one genuinely new piece is a **furniture library**: item-type index `t` → part id. It is a
seeded weighted pick over a per-neighborhood variant list, keyed the same way #452 D5 keys
`NeighborhoodStyle` — the exact `nhood` strings, exact match. A Sunset lamp post and a
Financial District lamp post are different presets of one generator.

### D3 — The distribution model: seats along the kerb polyline, arc-length parameterised

The unit of placement is a **seat**:

```
seat = (edge_key, side, s, lateral, yaw, y, normal)
  edge_key = (osm_way_id, from_node_id, to_node_id)     # see D5
  side     ∈ {left, right}
  s        = arc length along the densified, elevation-sampled centerline
  lateral  = metres outward from the centerline
  yaw      = street tangent at s, ± the item's facing rule
```

**Lateral is tightly constrained and this is a real finding.** `sidewalk.py` builds a strip from
`half_w` to `half_w + _WIDTH` where `_WIDTH = 1.5`. The inner edge sits at `+_RAISE = 0.20 m`
(flush with the road) and the outer at `+_OUTER_RAISE = 0.05 m` — it *ramps down* to terrain.
**There are 1.5 m of sidewalk and the outer third of it is a slope.** Therefore:

- default `lateral = half_w + 0.55 m` — clear of the kerb face, well inside the flat band;
- an item whose footprint radius exceeds ~0.55 m overhangs the kerb or rides the ramp. That
  disqualifies bus shelters (~1.5 m deep) and marginalises benches (§4 ranking reflects this);
- canopies may overhang freely; they are above head height.

**Spacing.** Phase-locked, centred, endpoint-margined:

```
usable = L - 2·endMargin                        # endMargin = corner clearance, D6
n      = clamp(floor(usable / spacing), countMin, countMax)
s_i    = endMargin + (i + 0.5) · usable / n     # i ∈ [0, n)
```

Same shape as `BuildingAssembler.PlaceProcedural`'s count-from-spacing, deliberately — one mental
model for both placement systems. Jitter is applied in metres along `s` and drawn from the seat's
own seed, so rejecting one seat never shifts another (the property #459 relied on).

**Degradation on short blocks** falls out: a 14 m alley with `spacing = 30, countMin = 0` gets
`n = 0` and no lamp. That is correct, not a failure. `countMin = 1` is the knob for items that
should always appear at least once per block (a hydrant per block face).

**Degradation on tight curves.** `s` is arc length along the *densified* polyline, so curvature is
handled by construction, not by chord approximation — `densify_polyline(cl_xz, MAX_SEG_M)` already
runs in `sidewalk.py`. The one residual failure is the inside of a bend tighter than `lateral`:
consecutive offset points can converge or cross. Guard: reject a seat whose offset point is nearer
than `0.7 × spacing` to the previously accepted seat's offset point. Cheap, local, and it
degenerates to a no-op on straight streets — which is nearly all of SF.

**Side selection.** Per `(edge_key, item_type)`, seeded:
`sides = edge.width ≥ 12 m ? both : one seeded pick`. Rationale: wide streets in SF are lit and
metered on both sides, narrow residential streets typically are not. **This is a guess from
looking at SF, not from data.** It is one constant and one comparison; change it after looking at
a screenshot.

### D4 — Budget: render is baked and combined; colliders are primitives, not meshes; the runtime streamer is a documented fallback, not v1

The #262 tradeoff is the right question and it resolves *the other way* for furniture, for a
reason specific to furniture:

> Parked cars are streamed at runtime because they are multi-material imported prefabs that cannot
> be combined. Furniture is single-material generated geometry that can. A chunk's ~400 lamp posts
> become **one** mesh and **one** GameObject.

So baking furniture *reduces* the GameObject count relative to any per-item alternative, and it
adds one `CombineInstance` pass per item type per chunk — cheaper than the per-road/sidewalk/
intersection GameObjects the chunk prefab already carries (#263's complaint).

**Colliders.** The #263 cost is the **`MeshCollider` cook**, which PhysX runs inline on
`AddComponent`. A `CapsuleCollider` or `BoxCollider` is **not cooked** — it is a shape descriptor.
So:

| Item class | Render | Collider |
|---|---|---|
| standing, hittable (lamp post, utility pole, signal mast, tree trunk, hydrant, meter) | baked combined mesh | one `CapsuleCollider` on a child GO, primitive, no cook |
| low, non-hittable (newspaper box, mailbox, bench, tree canopy) | baked combined mesh | **none** |

A newspaper box does not need a collider. Say so, in the item catalogue, per item — see
[`items.md`](items.md).

The cost that *does* remain is one GameObject + one collider component per hittable item, ~200–400
per chunk. **I have not measured whether that matters and it should not be guessed** — #446's
instrumentation exists, #262/#263 are parked pending a manual Editor pass (MEMORY: #259 status).

**Fallback, specified but not built:** if the number says 400 primitive statics per chunk is too
many, spawn colliders at runtime from the same sidecar within ~60 m of the player — a
`ParkedCarStreamer` clone with the renderer half deleted, and **no frustum gate**, because a
collider behind you still has to stop you. Named here so it is a switch, not a redesign. I do not
believe it will be needed; primitive statics are cheap and PhysX's static tree is built once.

**Consequence of baking, stated because it is a real loss:** a combined mesh cannot be thinned at
runtime. The density knob (D1) is therefore an **import-time** knob, unlike
`ParkedCarStreamer.density` which is live. Changing furniture density costs a re-import (seconds),
not a re-bake (minutes) — acceptable, but it is not the live slider parked cars have.

### D5 — Determinism key: the seat, not the item, and never the chunk

#452's contract is `hash(osm_id, ruleIndex, slotIndex)`. Furniture has no `osm_id` of its own. The
stable identity is **the physical seat**, and the bake already has the exact key for a physical
piece of kerb — `sidewalk.py` keys its meshes by it:

```
key = FNV1a( osm_way_id, from_node_id, to_node_id, side, item_type, slot_index )
```

**Why not the chunk coordinate.** A road edge crossing a chunk seam appears in *both* chunks'
graphs (the margin-expanded `stamp_graph`). Keying on `(chunk, index)` would give one physical seat
two seeds and — worse — let both chunks place an item there. The proven fix is already in
`chunk.py`: build against the margin-expanded graph, then **clip placements to the exact chunk
rect**, exactly as `place_parked_cars` does. Reuse that, do not re-derive it.

Three further requirements, all learned from existing bugs:

1. **No dict/set iteration may influence a placement.** #438 (unpinned `PYTHONHASHSEED`) is open.
   Seat generation must iterate `sorted(graph.edges, key=edge_key)` and draw from a
   `random.Random(seed)` per `(edge_key, side, item_type)` — `parking.py`'s pattern.
2. **Records sorted on write** — by `(way_id, from, to, side, type, slot)` — so a re-bake of the
   same inputs is byte-identical. `write_buildings` already states this contract; match it.
3. **The seat key is serialised** as `k`, because the import-time density thin and any future
   per-item override need a stable handle that survives a re-bake.

### D6 — Exclusions are a filter chain over seats, ordered by authority

Not a set of special cases — one ordered pipeline, each stage rejecting seats. Order matters
because later placements must yield to earlier ones.

```
candidate seats
  → road-class gate        (motorway/trunk/link, is_driveway, service → no furniture at all)
  → slope gate             (grade > ~18% → no benches/boxes; posts survive)
  → corner clearance       (both endpoints, radius by item)
  → driveway curb cuts     (OSM service=driveway + synthesised residential cuts)
  → crosswalks             (NOT AVAILABLE — see the table)
  → bus stops              (NOT AVAILABLE — see the table)
  → parked cars            (place cars FIRST; furniture yields)
  → item-vs-item dedupe    (across all types, one pass, ~1.2 m)
```

**Cars are placed before furniture, and furniture yields.** Cars come from real DataSF regulation
geometry; furniture is invented. When they conflict the invented thing moves. This also makes the
check free — the car list is in memory in the same function.

Concretely, per exclusion — **what data it needs and whether the bake has it today**:

| Exclusion | Data needed | Bake has it? |
|---|---|---|
| **Driveways** | curb-cut seats, incl. residential ones OSM never tagged | **YES** — `StreetEdge.is_driveway` (`osm.py:331`), serialised as `"dw"`, plus `parking.py`'s synthetic-cut machinery. Reuse verbatim. |
| **Corners / intersections** | node XZ + degree | **YES** — `StreetGraph.nodes`, `is_intersection` (degree ≥ 2 way refs). `parking.py` already clears 6.0 m. Furniture wants **more** (~8 m): the corner is its own sidewalk fan (`build_sidewalk_corner_meshes`) and a lamp inside the radius reads wrong. Exact value is a visual call — §6 OQ5. |
| **Parked cars** | car positions | **YES** — same pass, in memory. Note the lateral geometry already separates them: cars sit at `half_w − offset` toward the carriageway, furniture at `half_w + 0.55` on the sidewalk, so the real conflict is only for overhanging items. |
| **Road class** | `highway=*` | **YES** — `StreetEdge.highway`, serialised as `"c"`. Also `allows_parking` for the freeway/trunk case. |
| **Slope** | heightfield | **YES** — `hmap`, already sampled per seat for `y`. |
| **Item-vs-item** | the seat list | **YES** — trivially, one dedupe pass (`_DEDUPE_DIST_M` pattern). |
| **Crosswalks** | `highway=crossing` nodes and/or `footway=crossing` ways | **NO.** `osm.py` reads exactly one node tag — `highway=traffic_signals` (`osm.py:309`) — and discards the rest. Crossing *ways* are not `tags.is_road`, so they never enter `highway_ways`. **Needs a bake change.** Interim proxy: corner clearance covers crosswalks at corners, which is most of them; **mid-block crossings will get a lamp post in them** and that is a known, accepted v1 defect. |
| **Bus stops / shelters / flag poles** | `highway=bus_stop`, `public_transport=platform` nodes | **NO.** Standalone POI nodes are dropped entirely — `raw_nodes` is local to `_build_graph` and only nodes referenced by highway ways survive into `StreetGraph.nodes` (`osm.py:304-322`). **Needs a new POI extraction pass.** This is why bus shelters rank last (§4). |
| **Real hydrant positions** | `emergency=fire_hydrant` nodes | **NO**, same reason. But irrelevant if we *generate* hydrants rather than import them: clearance against our own hydrants is self-consistent and free. Recommend generating. |
| **Traffic-signal locations** | `IntersectionType` per node | **Computed but not serialised.** `osm.py:317` sets `TRAFFIC_SIGNALS` on intersection nodes, and nothing writes it to a sidecar — `RoadNetwork` re-derives Signal-vs-Stop from a *road-width proxy* instead, with a comment admitting it (`RoadNetwork.cs:38-41`). One small `write_roads`-adjacent change both places real signals and removes a guess from the traffic system (#244). |
| **Building entrances / stoops** | which facade slot got a door or stoop | **NO, and structurally unavailable to the bake.** Doors and stoops are chosen in Unity by `BuildingAssembler.PlaceProcedural` from templates the bake never sees. A lamp post standing dead in front of a stoop is therefore **not preventable at the seat-solving stage.** Two mitigations: (a) a stoop projects 1.5–3 m from the building line into a 1.5 m sidewalk, so it already crosses the sidewalk regardless of furniture — this conflict pre-exists us; (b) the *importer* has both, and `StreetFurnitureAssembler` runs after `BuildingAssembler` in the same chunk import, so a post-filter against placed-stoop bounds is possible. Deferred to a child issue (§7 #12), not v1. |

**The honest summary of question 3:** four of eleven exclusions are missing data, and they cluster
into two bake changes — a POI node-retention pass (crosswalks, bus stops, and optionally real
hydrants/lamps/trees) and serialising the intersection control the bake already computes. Neither
blocks the first three items in the §4 ranking.

### D7 — Parts live in the existing library, under `Parts/Furniture/`. The folder name lies for one release.

The issue is right that "SFBuildingTemplates/Parts" would be a lie. It is the least interesting
kind of wrong, and the alternatives are worse.

| Option | Cost |
|---|---|
| (a) rename root → `SFParts/` now | cross-cutting rename of a path constant (`SFBuildingTemplatePaths.cs`, consolidated only two PRs ago in #430), every `.part.json`/`.meta` GUID path, `library.json`, the importer, and the README — while six agents have live worktrees on this tree |
| (b) sibling `SFStreetFurniture/` with its own `library.json` + importer | a second copy of the part importer and library loader, forever, for a naming reason |
| (c) **`SFBuildingTemplates/Parts/Furniture/*.part.json`, one library** (**chosen**) | the root folder name is inaccurate until (a) happens |

The deciding fact: `BuildingPart` is **not building-specific**. It is `id` + `generatorId` +
`PartParams` + `mountDepthMeters` + `submeshRoles`-from-generator. Nothing in it knows about
facades — `PlacePart` does, and furniture never calls `PlacePart`. Duplicating the library to make
a folder name true is the tail wagging the dog.

File the rename as a standalone `refactor` (§7 #13), to run when furniture has shipped and the name
is provably wrong for two consumers rather than speculatively wrong for one.

---

## 2. Data contract: `chunk_CC_RR_furniture.json`

Written by `serialize.py` beside `_names.json` and `_parked.json`; copied to Resources by
`SFMapImporterWindow` exactly as those are; read at import by `StreetFurnitureAssembler` and
(only if D4's fallback is built) at runtime.

```json
{"version":1,"items":[
  {"t":0,"p":[412.55,38.201,-88.13],"r":214.5,"k":2871994113},
  {"t":3,"p":[418.02,38.244,-86.90],"r":34.5,"k":1099823006,"n":[0.08,0.996,-0.03]}
]}
```

| Field | Meaning |
|---|---|
| `t` | item-type index into the furniture library (lamp = 0, meter = 1, …) |
| `p` | Unity world position of the item's anchor (base of the post), y already elevation-sampled and `+_RAISE`-matched to the sidewalk |
| `r` | Y heading, degrees — the street tangent ± the item's facing rule |
| `k` | the D5 seat key — the stable handle for density thinning and future overrides |
| `n` | ground normal, **omitted on flat ground** — same convention and same threshold as `_parked.json` (`_MIN_TILT_DEG`) |

Deliberately *not* in the record: item dimensions, part id, material, detail level. Those are
library/preset data resolved in Unity, so a re-import can restyle a city without a re-bake. This is
the D1 split expressed in the schema: **the bake says where and which kind; Unity says what it
looks like.**

`GeneratedAssets.RuntimeChunkFurniture(coord) => $"Generated/{ActivePreset}/{c}_furniture"`,
matching the two existing sidecars.

---

## 3. Neighborhood identity

Free, by D2's library indirection. Item type 0 ("street lamp") resolves through the same
exact-`nhood`-string weighted pick #452 D5 defined: a Sunset lamp post is a plain davit, a Noe
Valley one is a fluted post with a scrolled bracket, a Financial District one is a twin cobra head.
Same generator, different `PartParams`, different `PartMeshCache` entry.

Note this is a *stronger* identity lever than it is for buildings, because furniture is visible from
much further away than a window mullion.

---

## 4. Which items are worth it — ranked

Ranked by **street filled per triangle** × **street filled per unit of placement complexity**.
Not all ten are worth building, and two probably never are.

Triangle figures are **estimates from the kernels**, not measurements — e.g. an 8-sided
`ProfileSweep` along a 2-point `Line` with caps is 8 × 2 × 2 + 2 × 6 ≈ 44 tris. They exist to rank,
not to budget. §6 R1 says who must measure.

| Rank | Item | Est. tris (Full) | Spacing | Placement complexity | Verdict |
|---|---|---|---|---|---|
| **1** | **Street lamp** | ~90 | 30 m, side rule | corner + driveway only | **Build first.** Highest fill/tri by a distance: it is the only item that establishes *vertical* rhythm, it reads from 200 m, and it silhouettes against the sky. Also the item people notice missing. |
| **2** | **Parking meter** | ~35 | 7 m | **≈ zero — derive seats from `_parked.json`** | Its placement problem is already solved: a meter belongs exactly where a parked car belongs, offset onto the sidewalk. Very dense, so it furnishes the whole kerb line for ~5 tris/metre. Best value-per-unit-of-work in the list. |
| **3** | **Utility pole** | ~60 | 40 m, one side | corner + driveway; **pairing pass** for wires | SF's most distinctive streetscape feature is its overhead wiring. The pole is cheap; the payoff is that it unlocks #475's already-designed wire family for near-zero extra triangles. Costs one new mechanic: consecutive same-edge/same-side seats must be paired to string between. |
| **4** | **Street tree** | ~250–600 | 10 m | corner + driveway + basin | **Highest area fill in the list and the biggest budget risk in the list.** A canopy hides an empty sidewalk better than anything else here. It is also the one item that could blow the triangle budget on its own. Gate on the §6 R1 measurement; require a 2-quad billboard at `Reduced`. |
| **5** | **Fire hydrant** | ~40 | 60 m, `countMin = 1` | trivial | Iconic, cheap, sparse. Low absolute fill but excellent value/tri. **Generate, do not import** (D6). |
| **6** | **Traffic signal** | ~120 | at signalised nodes | **needs a bake change** (D6) + per-approach mast orientation | SF intersections read wrong without them, and the fix also removes `RoadNetwork`'s width-proxy guess (#244). Medium complexity: placement is at the corner fan, and each approach needs its own mast bearing. |
| **7** | **Mailbox** | ~30 | corner-clustered | corner *inclusion*, not exclusion | Cheap clutter. Inverts the corner rule (it wants to be near one), so it costs a small new mechanic for a small payoff. |
| **8** | **Newspaper box** | ~20 | corner-clustered, 2–4 | same as mailbox | Cheapest thing in the list. Only worth it once #7's corner-cluster mechanic exists — then it is nearly free. |
| **9** | **Bench** | ~80 | — | **no plausible siting data** | A bench every 30 m on a residential street is wrong, and we have no transit-stop or park-frontage data to site them correctly. Also marginal on a 1.5 m sidewalk. **Do not build until the POI pass (§7 #9) exists.** |
| **10** | **Bus shelter** | ~200 | — | **needs bus-stop data we do not have** + does not fit | Worst on both axes: highest placement-data cost (`highway=bus_stop` nodes are discarded) and ~1.5 m deep against a 1.5 m sidewalk, so it either overhangs the kerb or rides the ramp. **Defer indefinitely.** |

**Recommended first build: the street lamp (rank 1).** It is the highest-value item, its placement
needs only exclusions the bake already has, it exercises the whole pipeline end to end — bake
solver → sidecar → importer → generator → combine → collider — and it produces a visibly different
city on day one. Ranks 2 and 3 follow immediately and are then nearly free.

Per-item parameter sketches, kernels, facing rules, collider class and detail-degradation notes are
in [`items.md`](items.md). Generator-level specification belongs to the child issues.

---

## 5. What changes where

| Layer | Change |
|---|---|
| `python/sfmap/geometry/furniture.py` | **new** — the seat solver. Structured as `parking.py`'s sibling, sharing its kerb/exclusion helpers (extract the shared ones rather than copying) |
| `python/sfmap/chunk.py` | call the solver after `place_parked_cars`, pass it the car list |
| `python/sfmap/serialize.py` | `write_furniture()` — ~30 lines, mirrors `write_parked_cars` |
| `python/sfmap/osm.py` | (later) POI node retention; serialise `IntersectionType` |
| `docs/chunk-bin-format.md` / data-model | document the sidecar |
| `PipelineTypes.cs` | `RuntimeChunkFurniture(coord)` |
| `StreetFurnitureAssembler.cs` | **new**, ~180 lines |
| `SFMapImporterWindow.cs` | copy the sidecar; call the assembler in `ImportChunk` |
| `SFBuildingTemplates/Parts/Furniture/*.part.json` | the presets |
| `Buildings/Gen/*` | **unchanged** — new generators only, no seam edits |
| `BuildingAssembler.cs` | **unchanged** |
| `ParkedCarStreamer.cs` | **unchanged** |
| `ChunkStreamer.cs` | **unchanged** |

The zero-change column is the point. Everything this design adds is additive.

---

## 6. Risks

**R1 — Triangle and import budget, stacked on #452's.** This is the same exposure as #452 §6 and
it lands on the *same* unmeasured baseline. #452 already took decoration from ~60 tris/building to
~12,000; furniture adds a whole second population on top. **No number exists.** #446's
instrumentation shipped, but #262/#263 are parked pending a manual Editor/play-mode pass, so the
per-chunk import and triangle figures this decision needs are not available and must not be
guessed. **Must be measured, by whoever can run the Editor:** per representative chunk, with
furniture on and off, at each `DetailLevel` — triangle count, import wall time, chunk prefab size,
static collider count, and the `ChunkStreamer` first-visit hitch. That measurement gates the
street tree (§4 rank 4) and the D4 collider decision. Nobody should ship the tree before it exists.

**R2 — The re-bake iteration loop (D1's named cost).** Spacing and exclusion tuning costs minutes,
not seconds. Mitigated by the generous-seats + import-time-density split, but if the exclusion
*rules* themselves turn out to need many rounds of visual tuning, this will hurt. Early warning
sign: more than ~3 re-bakes to get corner clearance right. If that happens, the escape hatch is to
emit raw kerb seats and move exclusions to import — a real but expensive reversal.

**R3 — The 1.5 m sidewalk.** `_WIDTH` is a bake constant, not per-street data. Real SF sidewalks
run 3–5 m on commercial streets. Every furniture footprint decision here is constrained by a number
that is itself an approximation. Widening it is a separate change with terrain-stamping
consequences and is out of scope, but it is the constraint that kills bus shelters and cramps
benches.

**R4 — Cross-seam double placement.** The classic bug in this shape of system. The `place_parked_cars`
margin-graph + exact-rect-clip pattern is the mitigation and it is proven. **It needs a test**: bake
a 2×2 ring, assert no two furniture records across chunks are within the dedupe distance.

**R5 — Furniture standing in a stoop or doorway (D6).** Unpreventable at the bake stage by
construction. Accepted for v1; §7 #12 is the fix.

**R6 — Mid-block crosswalks.** A lamp post in a crosswalk. Real, visible, and gated on the POI pass.

**R7 — Two consumers of one sidecar** if D4's collider streamer is ever built. The import path and
the runtime path would have to agree on the density thin or colliders and renders would desync.
Mitigation if it happens: put the thin in one static helper keyed on `k`, exactly as
`DensityKey` is today.

**R8 — `PYTHONHASHSEED` (#438).** An open determinism hole in the bake. Furniture placement must
not read dict/set iteration order. Called out in D5; needs to be a review item on the solver PR.

---

## 7. Open questions

1. **Generate items, or import real positions?** SF's DataSF portal publishes a Street Tree List
   with real per-tree locations and species, in the same shape as the parking-regulations CSV this
   bake already consumes. **I have not verified that file is present in this repo's data
   directory** — I am reasoning from the parking-CSV precedent. If it is obtainable, real tree
   positions are strictly better than generated ones (they are already correctly excluded from
   driveways and crossings, by reality). **Human call:** authenticity vs. another data dependency
   and another bake input flag.
2. **Default `DetailLevel` for furniture.** Inherits #452 §7.1, still unresolved, still gated on R1.
   Furniture may want a *lower* default than buildings: an item at 30 m spacing is usually further
   from the camera than a window on the building you are driving past.
3. **Baked primitive colliders vs. a runtime collider streamer** (D4). Decided as "bake them"; the
   decision is only as good as R1's number.
4. **Density: import-time only?** D4 says yes, because a combined mesh cannot be thinned live.
   Is losing the live slider acceptable, or does one item (trees?) want to stay per-instance to
   keep it? I say accept it.
5. **Corner clearance radius.** 6 m (parking's value) or 8 m or more. This is a look-at-it call
   and wants a screenshot, not an argument.
6. **Do street lamps emit light at night?** Thousands of real-time lights is out of the question.
   Emissive material plus a projected cookie, or nothing? Adjacent, out of scope, and someone will
   ask on the first night-time screenshot.
7. **Does furniture belong in the chunk prefab or a sibling prefab?** Baking into the chunk prefab
   is simplest and is what D4 assumes. A sibling `_furniture` prefab would let furniture be
   toggled/reimported without re-saving the chunk. Probably not worth the second asset; noted
   because it is cheap to decide now and expensive later.

---

## 8. Proposed fan-out (for `/sdlc plan`)

| # | Issue | Depends on | Why this order |
|---|---|---|---|
| 1 | `feat`(bake): furniture seat solver + `chunk_CC_RR_furniture.json` — kerb walk, spacing (D3), exclusion chain (D6), seat key (D5), rect clip, byte-stable output | — | the core; everything downstream is data-driven off it |
| 2 | `feat`(unity): `StreetFurnitureAssembler` + `RuntimeChunkFurniture` + importer wiring + per-type combine + primitive colliders (D2, D4) | 1 | the seam; unblocks visible results |
| 3 | `chore`: measure — triangles, import time, prefab size, static collider count, first-visit hitch; furniture on/off × `DetailLevel` (R1) | 2 | gates 6 and the D4 collider decision; closes OQ2/OQ3 before they calcify |
| 4 | `feat`: `furniture.lamp_post` family + per-neighborhood presets | 2 | the first real item; proves the whole chain |
| 5 | `feat`: `furniture.parking_meter`, seats derived from the parked-car set | 1, 2 | near-zero placement work, high density payoff |
| 6 | `feat`: `furniture.utility_pole` + pairing pass, hooked to #475's wire family | 2 | biggest silhouette-per-triangle win after the lamp |
| 7 | `feat`: `furniture.street_tree` + billboard LOD at `Reduced` | 3 | **gated on the measurement** |
| 8 | `feat`: `furniture.hydrant` | 2 | trivial once 4 lands; parallel |
| 9 | `feat`(bake): POI node retention — `highway=crossing`, `highway=bus_stop`, optionally `emergency=fire_hydrant` / `natural=tree` | 1 | unblocks the two missing exclusions (D6) and OQ1 |
| 10 | `feat`(bake): serialise per-node `IntersectionType` into `_names.json` | — | independent; also removes `RoadNetwork`'s width-proxy guess (#244) |
| 11 | `feat`: `furniture.traffic_signal` + per-approach mast orientation | 10 | needs real signal locations |
| 12 | `fix`: post-filter furniture seats against placed stoop/door bounds at import (R5, D6) | 2, and #495 stoops | the one exclusion the bake structurally cannot do |
| 13 | `refactor`: rename `SFBuildingTemplates` → `SFParts` (D7 debt) | 4 | run once the name is provably wrong for two consumers, and once the current worktree fan-out has drained |
| 14 | `feat`: corner-cluster placement mechanic + `furniture.mailbox` / `furniture.newspaper_box` | 2 | lowest value; do last or not at all |

Issues 4, 5, 6 and 8 are genuinely parallel once 2 lands. Issue 3 gates 7. Issues 9 and 10 are
independent bake work and can start immediately.

**Not proposed, deliberately:** a bench issue (§4 rank 9 — no siting data) and a bus-shelter issue
(rank 10 — no data *and* it does not fit the sidewalk). They are in the item catalogue so the
decision is recorded, not so they get built.
