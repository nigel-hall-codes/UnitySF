# #496 — Street furniture item catalogue

Companion to [`design.md`](design.md). One row per item: what kernels build it, what placement
parameters it needs, whether it collides, and how it degrades under `DetailLevel`.

**Scope boundary:** this is placement- and budget-level detail. Vertex-level construction order,
profile point lists and per-neighborhood presets belong to the child issues, exactly as #452
pushed those into `generators.md` for one family and left the other nine to their own issues.

Triangle figures are **estimates derived from the kernels**, not measurements. An 8-sided
`ProfileSweep` along a 2-point `Line` with both caps is `8 × 1 × 2 + 2 × 6 ≈ 28` triangles; a
`Box` is 12. They exist to rank items against each other (design §4) and to size the risk in
design §6 R1. They are not a budget. See design §6 R1 for what must actually be measured.

---

## Shared placement parameters

Every item's library entry carries these; they are the inputs to design §D3's seat solver.

| Parameter | Meaning |
|---|---|
| `spacing_m` | nominal arc-length pitch along the kerb |
| `countMin` / `countMax` | floor and cap per block face — `countMin ≥ 1` means "always at least one" |
| `sides` | `both` \| `seeded_one` \| `wide_only` (both iff `edge.width ≥ 12 m`) |
| `lateral_m` | metres outward from the road centerline, **beyond** `edge.width/2`. Default `0.55`; the sidewalk is only 1.5 m wide and its outer third ramps down (design §D3) |
| `facing` | `along` (yaw = tangent) \| `to_street` (yaw = −kerb normal) \| `to_building` |
| `cornerClear_m` | clearance from each edge endpoint |
| `jitter_m` | seeded along-kerb offset |
| `classes` | permitted `highway=*` values |
| `maxGrade` | reject seats steeper than this |
| `collider` | `none` \| `capsule(r,h)` \| `box(w,h,d)` |

---

## 1. Street lamp — `furniture.lamp_post` · **build first**

| | |
|---|---|
| Kernels | `ProfileSweep(Round8, Line(0→h))` shaft · `ProfileSweep` or `Polyline` davit arm · `Box` luminaire head · optional `ProfileSweep(Ogee)` base collar |
| Params | `height_m` 7–9 · `shaftRadius_m` 0.08–0.14 · `taper` · `armLength_m` 0–1.6 · `armRise_m` · `headStyle` (cobra \| teardrop \| acorn \| twin) · `baseCollar` bool · `fluted` bool |
| Placement | `spacing_m` 30 · `countMin` 0 · `sides` wide_only · `lateral_m` 0.55 · `facing` along (arm points at the carriageway) · `cornerClear_m` 8 · `classes` all but motorway/trunk/service/driveway |
| Collider | `capsule(0.15, height)` — a lamp post must stop a car |
| Roles | shaft `Metal`, head `Metal`, glazing `Glass` |
| Est. tris | ~90 Full · ~50 Reduced (chamfer profile, no collar) · ~14 Flat (shaft box + head box) |
| Degradation | shaft profile Round8 → `Detail.Sectioned` chamfer; drop the base collar; drop the arm's mitre segments |
| Notes | The one item that establishes vertical rhythm and silhouette. Design §4 rank 1. Night lighting is design §7 OQ6 — **not** a real-time `Light` per post. |

## 2. Parking meter — `furniture.parking_meter`

| | |
|---|---|
| Kernels | `ProfileSweep(Round6, Line)` post · chamfered `Box` head · `Box` display inset |
| Params | `height_m` 1.2–1.4 · `postRadius_m` 0.03 · `headStyle` (single \| twin \| pay_station) · `headSize` |
| Placement | **seats derived from `chunk_CC_RR_parked.json`**, one per car seat, projected from the car's kerb position onto the sidewalk at `lateral_m` 0.5. No independent solver pass. `spacing_m` unused. |
| Collider | `none` — a meter is knee-high and hitting one should not stop a car |
| Roles | post `Metal`, head `Accent2`, display `Glass` |
| Est. tris | ~35 Full · ~20 Reduced · ~2 Flat |
| Degradation | drop the display inset; Round6 → chamfer |
| Notes | Best value-per-unit-of-work in the catalogue: its placement problem is already solved by the parked-car pass. Design §4 rank 2. Suppress on residential classes where SF has no meters — gate on `highway_class ∈ {primary, secondary, tertiary}` plus a commercial signal if #486 lands one. |

## 3. Utility pole — `furniture.utility_pole`

| | |
|---|---|
| Kernels | `ProfileSweep(Round8, Line)` tapered trunk · `Box` crossarms · `Box`/`ProfileSweep` transformer cans · (wires: **#475's existing wire family**, not this generator) |
| Params | `height_m` 9–12 · `baseRadius_m` 0.18 · `taper` 0.6 · `crossarmCount` 1–3 · `crossarmSpan_m` 1.8–2.4 · `transformer` bool |
| Placement | `spacing_m` 40 · `sides` seeded_one (poles run one side of a street) · `lateral_m` 0.4 · `facing` along · `cornerClear_m` 6 |
| Collider | `capsule(0.2, height)` |
| Roles | trunk `Base` (wood), crossarms `Base`, cans `Metal` |
| Est. tris | ~60 Full (excl. wires) |
| Degradation | drop transformers, then crossarms to a single bar, then the trunk to a tapered box |
| Notes | **Needs one new placement mechanic:** consecutive accepted seats on the same `(edge_key, side)` must be emitted as an ordered run so the wire family can span between them. That pairing is the only reason this ranks 3 and not 2. Design §4 rank 3. |

## 4. Street tree — `furniture.street_tree`

| | |
|---|---|
| Kernels | `ProfileSweep(Round6, Polyline)` trunk with a slight lean · canopy: either a low-poly icosphere-ish `Box`-cluster or a crossed-quad billboard set · `Box` frame tree grate |
| Params | `trunkHeight_m` 2.5–4 · `trunkRadius_m` 0.1–0.2 · `canopyRadius_m` 1.5–3 · `canopyStyle` (ficus \| palm \| plane \| conifer) · `lean_deg` · `grate` bool |
| Placement | `spacing_m` 10 · `sides` both · `lateral_m` 0.7 · `facing` along · `cornerClear_m` 8 · `maxGrade` — none, trees are fine on hills |
| Collider | `capsule(0.2, trunkHeight)` on the **trunk only**; the canopy must never collide |
| Roles | trunk `Base`, canopy `Accent1` (a palette green), grate `Metal` |
| Est. tris | ~250–600 Full · **~8 Reduced (crossed billboards)** · 2 Flat |
| Degradation | **mandatory and load-bearing.** `Reduced` must drop to crossed quads. A design that only has a Full canopy is not shippable at 10 m spacing. |
| Notes | Highest area fill and the single biggest budget risk (design §6 R1). Gated on the measurement. Design §7 OQ1 asks whether real DataSF tree positions should replace generated ones — if so, `t` records come from the CSV and the seat solver skips this type entirely. |

## 5. Fire hydrant — `furniture.hydrant`

| | |
|---|---|
| Kernels | three stacked `ProfileSweep(Round8, Line)` sections (barrel, bonnet, cap) · `Box`×2 side nozzles · `ProfileSweep` flange |
| Params | `height_m` 0.75–0.9 · `barrelRadius_m` 0.09 · `bonnetStyle` · `nozzles` 2–3 |
| Placement | `spacing_m` 60 · `countMin` 1 (one per block face) · `sides` both · `lateral_m` 0.5 · `facing` to_street · `cornerClear_m` 6 |
| Collider | `capsule(0.12, height)` — low, but SF drivers do hit them |
| Roles | body `Accent2` (SF hydrants are colour-coded by main pressure — a palette hook worth taking) |
| Est. tris | ~40 Full · ~22 Reduced · 2 Flat |
| Notes | **Generate, do not import.** `emergency=fire_hydrant` nodes are discarded by `osm.py`; generating makes clearance self-consistent for free (design §D6). Design §4 rank 5. |

## 6. Traffic signal — `furniture.traffic_signal`

| | |
|---|---|
| Kernels | `ProfileSweep(Round8, Line)` mast · `ProfileSweep(Polyline)` mast arm with a mitred elbow · `Box` signal heads ×3 lenses · `Box` pedestrian head · `Box` controller cabinet |
| Params | `mastHeight_m` 5–7 · `armLength_m` 4–9 · `heads` 1–3 · `pedHead` bool · `cabinet` bool |
| Placement | **not kerb-spaced.** One per signalised approach at a signalised node: seat at the corner-fan radius on the near-right corner of each incoming edge, `facing` = the reverse bearing of that approach. |
| Collider | `capsule(0.15, mastHeight)` on the mast; heads and arm not collidable |
| Roles | mast `Metal`, heads `Base`, lenses `Glass` (emissive later) |
| Est. tris | ~120 Full |
| Notes | **Blocked on a bake change** — `osm.py:317` computes `IntersectionType.TRAFFIC_SIGNALS` and nothing serialises it, so `RoadNetwork` re-derives Signal-vs-Stop from a road-width proxy (`RoadNetwork.cs:38-41`). Serialising it fixes both this and #244's guess. Design §8 issues 10 + 11. |

## 7. Mailbox — `furniture.mailbox`

| | |
|---|---|
| Kernels | `ProfileSweep(Arc)` domed lid over a `Box` body · `Box`×2 legs · `Box` chute flap |
| Params | `width_m` 0.5–0.8 · `height_m` 1.1 · `legs` bool · `style` (usps_relay \| usps_collection) |
| Placement | **corner-clustered, not spaced.** Wants a new mechanic: seats *inside* `cornerClear_m` on a subset of corners, seeded so ~1 in 6 corners gets one. |
| Collider | `none` |
| Roles | body `Accent1` (USPS blue), lid `Accent1` |
| Est. tris | ~30 Full · ~14 Reduced |
| Notes | Design §4 rank 7. Only worth building alongside #8 so the corner-cluster mechanic pays for itself twice. |

## 8. Newspaper box — `furniture.newspaper_box`

| | |
|---|---|
| Kernels | chamfered `Box` body · `Box` window · `Box`×2 legs |
| Params | `width_m` 0.35 · `height_m` 1.0 · `slantedTop` bool |
| Placement | corner-clustered, 2–4 in a row at 0.4 m pitch. Shares #7's mechanic exactly. |
| Collider | `none` |
| Roles | body `Accent2` (heavily varied per instance — this is the item that adds colour noise), window `Glass` |
| Est. tris | ~20 Full |
| Notes | Cheapest thing in the catalogue. Design §4 rank 8. |

## 9. Bench — `furniture.bench` · **not proposed for build**

| | |
|---|---|
| Kernels | `ProfileSweep(Line)` slats ×5 · `Box`/`Lattice` end frames |
| Params | `length_m` 1.6–2.0 · `slats` 4–6 · `back` bool · `armrests` bool |
| Placement | **no plausible siting data.** A bench every 30 m on a residential street is wrong; benches belong at transit stops and park frontages, and the bake retains neither. Also ~0.7 m deep against a 1.5 m sidewalk. |
| Collider | `box` if ever built |
| Est. tris | ~80 Full |
| Notes | Design §4 rank 9. Recorded so the decision is on paper. Revisit only after the POI extraction pass (design §8 issue 9). |

## 10. Bus shelter — `furniture.bus_shelter` · **deferred indefinitely**

| | |
|---|---|
| Kernels | `PanelGrid` glazed walls · `ProfileSweep(Arc)` roof · `Box` bench · `Box` frame posts |
| Params | `length_m` 3–5 · `depth_m` 1.4 · `height_m` 2.6 · `roofStyle` · `endPanel` bool |
| Placement | needs `highway=bus_stop` / `public_transport=platform` nodes, which `osm.py` discards along with every other standalone POI node (`osm.py:304-322`). |
| Collider | `box` shell if ever built |
| Est. tris | ~200 Full |
| Notes | Worst on both ranking axes: no placement data **and** ~1.4 m deep against a 1.5 m sidewalk whose outer third ramps down (design §6 R3). Design §4 rank 10. |

---

## Cross-item notes

**Dedupe.** All types share one final dedupe pass (design §D6), ~1.2 m, applied after every
type-specific pass, in a fixed type order so the result is deterministic. Order = catalogue order:
a lamp post beats a meter beats a pole beats a tree, and everything beats a newspaper box.

**Colliders.** Four items collide (lamp, pole, tree trunk, hydrant) plus signals; five do not.
All colliders are **primitives, never `MeshCollider`** — a capsule costs no PhysX cook, which is
the entire point of #263. See design §D4.

**Roles and palettes.** Every item routes through the existing `MaterialRole` → `NeighborhoodPalette`
vertex-colour bake with **no changes to that system**, same as #452 D4. `Metal` carries most of the
furniture, which is a hint that #452 §7 OQ3 (is the role vocabulary rich enough?) may want a
`Paint` role — furniture is painted metal, structurally different from a building's metal trim.
Noted there, not decided here.
