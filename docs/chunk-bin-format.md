# Chunk `.bin` format (normative)

This is the **single normative specification** for the per-chunk `chunk_CC_RR.bin` file
written by the Python bake (`python/sfmap/serialize.py::write_chunk`) and read by the Unity
importer's `ChunkBinReader`. The two sides have no shared schema file — they agree only on
the bytes described here. Any change to this layout is a breaking change to that contract and
must update **both** implementations, this document, and the golden fixture guarded by
`python/tests/test_serialize_golden.py`.

All values are **little-endian**. There is **no alignment padding** — every field is written
immediately after the previous one, and the C# `BinaryReader` reads them back in the same
order. Sizes below are in bytes.

## File structure

```
ChunkHeader                     (40 bytes)
heightmap   float32[res * res]  (res = hmap_res from the header; row-major)
mesh_count  int32
MeshEntry × mesh_count
```

`chunk_CC_RR` in the filename is `chunk_{col:02d}_{row:02d}` (zero-padded to two digits).

### ChunkHeader — 40 bytes

`struct` format `"<IIiifffffi"`:

| Field          | Type  | Size | Notes                                             |
|----------------|-------|------|---------------------------------------------------|
| `magic`        | u32   | 4    | `0x4B4E4843` — the ASCII bytes `CHNK` little-endian |
| `version`      | u32   | 4    | currently `1`                                     |
| `chunk_col`    | i32   | 4    | chunk grid column                                 |
| `chunk_row`    | i32   | 4    | chunk grid row                                    |
| `world_x`      | f32   | 4    | chunk origin, Unity world X                       |
| `world_z`      | f32   | 4    | chunk origin, Unity world Z                       |
| `chunk_size_m` | f32   | 4    | chunk edge length in metres                       |
| `min_elev_m`   | f32   | 4    | min elevation used to de-normalise the heightmap  |
| `max_elev_m`   | f32   | 4    | max elevation used to de-normalise the heightmap  |
| `hmap_res`     | i32   | 4    | heightmap resolution N (grid is N×N)              |

### Heightmap

`float32 × (hmap_res * hmap_res)`, **row-major** (`row = south→north`, `col = west→east`),
values **normalised to `[0, 1]`**. Real-world elevation is `min_elev_m + v * (max_elev_m -
min_elev_m)`.

### mesh_count

`int32` — the number of `MeshEntry` records that follow.

### MeshEntry (repeated `mesh_count` times)

| Field        | Type          | Size            | Notes                                          |
|--------------|---------------|-----------------|------------------------------------------------|
| `mesh_type`  | u8            | 1               | `0`=ROAD, `1`=INTERSECTION, `2`=SIDEWALK, `3`=BUILDING |
| `osm_id`     | i64           | 8               | raw OSM node/way id; **signed** (may be negative) |
| `vert_count` | i32           | 4               | number of vertices                             |
| `idx_count`  | i32           | 4               | number of indices (a multiple of 3, CW winding) |
| `vertices`   | f32[]         | `vert_count*3*4`| `(x, y, z)` per vertex, Unity left-handed      |
| `normals`    | f32[]         | `vert_count*3*4`| `(nx, ny, nz)` per vertex; **all-zero** signals the importer to `RecalculateNormals` |
| `uvs`        | f32[]         | `vert_count*2*4`| `(u, v)` per vertex                            |
| `indices`    | u32[]         | `idx_count*4`   | triangle indices                               |

> **Note on the `u8` + `i64` adjacency:** `mesh_type` (1 byte) is followed *immediately* by
> `osm_id` (8 bytes) with no padding gap. The writer packs them separately (`<B` then `<q`)
> and the reader reads them sequentially, so the natural-alignment gap a C `struct` would
> insert does **not** exist on disk.

## Sidecar files (not part of the `.bin`)

`write_chunk` writes only the geometry above. Other per-chunk data is emitted as adjacent
JSON sidecars, documented in `serialize.py` docstrings (not here):

- `chunk_CC_RR_names.json` — named road centrelines (traffic system)
- `chunk_CC_RR_parked.json` — parked-car placements
- `chunk_CC_RR_buildings.json` — building classification facts (versioned; see below)

### `chunk_CC_RR_buildings.json` versions

Written by `serialize.py::write_buildings` when the bake runs with `--templates`, read by
Unity's `BuildingAssembler` (`BuildingsSidecarJson` / `BuildingFactsJson`). The per-record
field list is documented in the `write_buildings` docstring; the top-level `version` counter
is:

| `version` | Added |
|---|---|
| 1 | Original classification facts (#268): neighborhood, `building_type`, footprint shape/size, floor count, ranked `street_facades`, `footprint_hash` |
| 2 | Per-facade `edge` world-XZ endpoints, `base_y`, `facade_height_m` (#279) |
| 3 | `back_facade` / `left_facade` / `right_facade` (#407) |
| 4 | `use` + `commercial_poi_count` — the commercial signal (#486) |

Every added field is **additive and optional**: Unity deserialises with `JsonUtility`, so a
field absent from an older sidecar reads back as the C# default (`null` / `0`) and the import
still succeeds. A pre-v4 sidecar therefore imports with `use == null`, which the assembler
treats as `unknown`.

### The commercial signal (`use`, version 4)

```jsonc
"use": "mixed",                // residential | commercial | mixed | unknown
"commercial_poi_count": 2      // shop/amenity/office premises inside the footprint
```

**Why it exists.** `building_type` is the raw OSM `building=*` tag, and across the whole city
extract that tag is `yes` on 148,739 of 159,313 buildings (93.4%) — it says a building exists,
not what it is. Gating a template on `building_types: ["retail","commercial"]` therefore
reaches under 1% of buildings, and adding `"yes"` reaches 94%. `use` is the axis that actually
separates a shopfront block from a house.

**Where it comes from.** Two inputs, combined by `classify.building_use`:

1. the building way's own tags (`tags.building_use_tag`) — `building=retail|commercial|office|
   hotel|…`, a `shop`/`amenity`/`office`/`craft`/`tourism`/`leisure` tag on the way itself, or
   `building:use`;
2. **commercial POI nodes inside the footprint** (`poi.commercial_poi_counts`) — a
   point-in-polygon pass over every `shop`/`amenity`/`office`/… node in the extract.

Input 2 is not an optimisation, it is the bulk of the signal. San Francisco maps ground-floor
retail as a node dropped inside the footprint far more often than as a tag on the building way:

| signal | buildings | share of 159,313 |
|---|---|---|
| way tags alone | 1,706 | 1.07% |
| way tags **+ contained POI nodes** | 7,011 | 4.40% |

Of those 7,011 buildings, **5,305 (75.7%) carry the evidence only on a contained node** — they
are invisible to a tag read. City-wide the field comes out `unknown` 90.5%, `residential` 5.1%,
`mixed` 3.2%, `commercial` 1.2%.

**The values.**

| `use` | Meaning |
|---|---|
| `residential` | The way says it is a dwelling and nothing commercial was found. |
| `commercial` | Commercial at every floor: a single-storey shop, or a `building=retail`/`commercial`/`office`/`hotel` block. |
| `mixed` | The canonical SF block: **commercial at floor 0, residential above.** Multi-storey, with commercial evidence but no commercial way tag. |
| `unknown` | The data does not say. A bare `building=yes` with nothing inside it — 90.5% of the city. |

`commercial` and `mixed` **both** mean floor 0 is commercial; they differ only in what sits
above. That is deliberately the ground-floor fact a storefront rule needs, which is why there
is no separate `ground_floor_use` field — it would be exactly derivable from this one.

`unknown` is load-bearing, not a failure mode: `building=yes` is evidence that a building
exists, not that it is a house, and the signal refuses to invent the difference. Templates that
want "anything" simply leave the `uses` compatibility axis empty (as with every other axis);
only a template that constrains `uses` is affected.

**Does it land on the real strips?** Yes. Taking the nearest named street of every
`commercial`/`mixed` building city-wide, the top 25 streets — 1.1% of the 2,224 named streets in
the extract — hold 32% of them, and they are Mission, Geary, 24th, Clement, Haight, Irving,
Market, Taraval, Valencia, Sutter, Divisadero, Grant, Fillmore, Sacramento, Polk, 18th, 3rd,
Balboa, Post, California, 9th Ave, Chestnut, Lombard, Folsom, Bryant. Not one residential street
appears. The same asymmetry shows per chunk: the 9 Union Square chunks are 58.6%
`commercial`/`mixed`, the 24 Excelsior chunks (which clip only the tail of the Mission Street
strip) are 0.2%.

**Gating a template on it.** `Templates/*.template.json` gains a `compatibility.uses` axis
alongside `neighborhoods` / `building_types` / `footprint_shapes`, with the same
"empty = unconstrained" rule:

```jsonc
"compatibility": { "uses": ["commercial", "mixed"], "floor_count": { "min": 1, "max": 6 } }
```

Institutional buildings (school, church, hospital) carry no commercial tag and fall out as
`unknown`. This is a commercial signal, not a complete use taxonomy. `landuse=retail|commercial`
polygons are deliberately **not** consulted: they cover whole blocks, so they would smear the
signal across the residential buildings behind a commercial strip — the exact uniform sprinkle
this field exists to avoid.

## Golden fixture

`python/tests/test_serialize_golden.py` builds a deterministic tiny chunk (3×3 heightmap,
one ROAD and one BUILDING mesh — the latter with empty normals to exercise the zero-fill
branch, and a negative `osm_id`) and asserts `write_chunk` produces bytes identical to the
committed `python/tests/fixtures/chunk_01_02.bin`. After an **intentional** format change,
regenerate the golden (run from `python/`):

```bash
python -c "from tests.test_serialize_golden import write_golden; write_golden()"
```

and update the tables above so this document stays the source of truth.
