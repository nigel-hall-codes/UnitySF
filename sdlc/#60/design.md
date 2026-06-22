# Design: Hash-Based Incremental Chunk Skip

**Issue:** #60 | **Parent:** #55 | **Status:** drafting

---

## Context

The pipeline regenerates every chunk on every "Generate Map" press. With timing data
from #56 now in place, the bottleneck is clear: mesh generation + Unity asset creation +
prefab save dominates per-chunk time. The full-heightmap parse already has caching
(ElevationParser binary cache). This design adds a per-chunk skip layer on top.

---

## Chosen Approach

**Per-chunk fingerprint stored in `manifest.json`, skip the entire chunk loop body if the hash matches.**

---

## Q1: What inputs determine chunk staleness?

A chunk's output is fully determined by:

| Input | How to fingerprint |
|---|---|
| OSM source file | `File.GetLastWriteTimeUtc(osmFilePath).Ticks` |
| Elevation CSV | `File.GetLastWriteTimeUtc(csvPath).Ticks` |
| `chunkSizeMeters` | value (float → formatted string) |
| `col`, `row` | already chunk-specific |
| `roadWidthMultiplier` | value |
| `defaultBuildingHeight` | value |
| `heightmapResolution` | value |
| Generator version | `const int GeneratorVersion = 1` in `GeneratedAssets` — bump manually when generation code changes |

The chunk's WorldRect is fully derived from `col`, `row`, and `chunkSizeMeters`, so it
doesn't need separate inclusion. The graph and heightmap crops are in-memory operations
on the already-parsed global data, not additional sources.

### Fingerprint construction

```csharp
static string ChunkFingerprint(
    string osmFilePath, string csvPath,
    float chunkSizeMeters, int col, int row,
    float roadWidthMultiplier, float defaultBuildingHeight,
    int heightmapResolution)
{
    long osmMtime = File.GetLastWriteTimeUtc(osmFilePath).Ticks;
    long csvMtime = File.GetLastWriteTimeUtc(csvPath).Ticks;
    string raw = $"{osmMtime}|{csvMtime}|{chunkSizeMeters:F3}|{col}|{row}" +
                 $"|{roadWidthMultiplier:F3}|{defaultBuildingHeight:F3}" +
                 $"|{heightmapResolution}|v{GeneratedAssets.GeneratorVersion}";
    // XOR-fold a 64-bit FNV-1a hash into a hex string — no System.Security.Cryptography needed
    ulong h = 14695981039346656037UL;
    foreach (char c in raw) { h ^= c; h *= 1099511628211UL; }
    return h.ToString("x16");
}
```

Using mtime rather than file content hash keeps the cost negligible — consistent with
the pattern already used in `ElevationParser.TryLoadCache`.

---

## Q2: Where is the hash stored?

**In `manifest.json`**, under a new `"chunkHashes"` object.

```json
{
  "preset": "default",
  "generated": "2026-06-21T14:00:00Z",
  "chunkSize": 2000,
  "generatorVersion": 1,
  "chunks": [...],
  "chunkHashes": {
    "chunk_00_00": "a1b2c3d4e5f6a7b8",
    "chunk_01_00": "deadbeefcafe0001"
  }
}
```

### Why not alternatives

| Option | Rejected because |
|---|---|
| Sidecar file per chunk (`.hash`) | Scattered across directory tree; won't survive "Clear Generated Assets" cleanly |
| `ChunkManifest.asset` (ScriptableObject) | Requires AssetDatabase round-trip to read at run start; heavier than needed |
| Prefab `.meta` | Would be overwritten/ignored by Unity on import |

`manifest.json` already exists, is already read-friendly, and is already overwritten per
run. The skip logic reads the **previous** manifest.json at the start of `RunGenerate`
and writes updated hashes at the end.

---

## Q3: What granularity?

**Per-chunk, all-or-nothing.** When a chunk's fingerprint matches the stored hash, skip
the entire chunk loop body (graph crop, heightmap crop, all mesh generators, terrain,
buildings, prefab save). The skipped chunk still participates in `chunkCoords` /
`chunkWorldRects` for the scene and manifest writes.

Per-stage granularity was considered and rejected:
- Would require separate fingerprints for each stage (road, terrain, building) and
  separate asset-existence checks.
- Stages share the graph crop and heightmap crop; savings would be small.
- The chunk loop body is the unit of Unity Editor progress; keeping that as the skip
  unit is the natural fit.

### What "skip" means in the scene

A skipped chunk still needs to appear in the scene. Options:
1. **Load the existing prefab** — `PrefabUtility.LoadPrefabContents` + instantiate under
   `mapRoot`. This is what the Runtime loader already does conceptually.
2. **Do nothing** — rely on the user having the scene already populated from a prior run.

**Chosen: load existing prefab (option 1).** The pipeline overwrites `ClearSceneObjects`
at the top of every run, so skipping the loop body but not instantiating the chunk would
leave holes. Loading the existing prefab keeps the scene complete.

```csharp
// Pseudo-code for skip path
if (IsChunkCurrent(coord, fingerprint, previousHashes))
{
    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GeneratedAssets.ChunkPrefabPath(coord));
    if (prefab != null)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, mapRoot.transform);
        instance.name = coord.ToString();
        chunkCoords.Add(coord);
        chunkWorldRects.Add(chunk.WorldRect);
        newHashes[coord.ToString()] = fingerprint;
    }
    // else: prefab missing → fall through to full generation
    continue; // or else fall through
}
```

---

## Q4: How does the user force a full regenerate?

**No new button.** The existing "Clear Generated Assets" button deletes
`Assets/Generated/{preset}/` entirely, which removes `manifest.json`. On the next
"Generate Map" press, no stored hashes exist → all chunks are treated as stale →
full regeneration.

This is the correct model: "Clear Generated Assets" is already the user-visible "start
fresh" action. Adding a "Force Regenerate" toggle would duplicate it confusingly.

The pipeline window should log the skip count:
```
[SFMapPipeline] 3/4 chunks skipped (up to date), 1 regenerated — 4.2s
```

---

## Implementation Issues (child issues to file)

1. **feat(pipeline): read previous `manifest.json` and extract `chunkHashes` at run start** — parse the JSON, build a `Dictionary<string, string> previousHashes` before the chunk loop.

2. **feat(pipeline): add `ChunkFingerprint()` to `GeneratedAssets` + `GeneratorVersion` constant** — static method + `const int GeneratorVersion = 1`.

3. **feat(pipeline): skip chunk loop body when fingerprint matches; instantiate existing prefab** — conditional inside the chunk loop; prefab load + instantiate on skip path.

4. **feat(pipeline): write `chunkHashes` into `manifest.json`** — extend `WriteManifest` to include the new hash map.

5. **feat(pipeline): log skip/regen counts at end of run** — update the final `Debug.Log` line.

---

## What this does NOT change

- ElevationParser cache (independent, already correct)
- ChunkManifest.asset (still written unconditionally — it's cheap)
- The "Clear Generated Assets" / "Clear Elevation Cache" buttons
- Any Runtime loader code
