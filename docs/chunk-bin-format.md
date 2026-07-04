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
- `chunk_CC_RR_buildings.json` — building classification facts (versioned; see data-model.md §1)

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
