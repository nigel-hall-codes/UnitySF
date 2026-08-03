using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SFMap.Pipeline
{
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int Col;
        public readonly int Row;

        public ChunkCoord(int col, int row) { Col = col; Row = row; }

        public bool Equals(ChunkCoord other) => Col == other.Col && Row == other.Row;
        public override bool Equals(object obj) => obj is ChunkCoord o && Equals(o);
        public override int GetHashCode() => unchecked((Col * 397) ^ Row);
        public override string ToString() => $"chunk_{Col:00}_{Row:00}";
    }

    public static class GeneratedAssets
    {
        // Which baked map is live. Intentionally empty so nothing resolves to a real
        // preset by accident: it MUST be set explicitly by whoever owns the map —
        // ChunkStreamer (from its serialized `preset`) at runtime, or the importer
        // windows in the Editor. A stray read before that resolves to a non-existent
        // "Generated//..." path and fails loudly (null manifest) instead of silently
        // loading the wrong map's data.
        public static string ActivePreset = "";

        public static string Root          => $"Assets/Generated/{ActivePreset}";
        public static string ResourcesRoot => $"Assets/Resources/Generated/{ActivePreset}";

        public static string ChunkDir(ChunkCoord c)                  => $"{Root}/{c}";
        public static string TerrainAsset(ChunkCoord c)              => $"{ChunkDir(c)}/Terrain.asset";
        public static string TerrainBaseLayer()                      => $"{Root}/Materials/TerrainBaseLayer.terrainlayer";
        public static string RoadMesh(ChunkCoord c, long id)         => $"{ChunkDir(c)}/Roads/road_{id}.mesh";
        public static string IntersectionMesh(ChunkCoord c, long id) => $"{ChunkDir(c)}/Intersections/intersection_{id}.mesh";
        public static string SidewalkMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Sidewalks/sidewalk_{id}.mesh";
        public static string BuildingMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Buildings/building_{id}.mesh";
        // A templated building's collision proxy: the undecorated mass mesh, saved separately from
        // the rendered (decorated) BuildingMesh so its MeshCollider cooks the wall planes only and
        // not every muntin bar or cornice bracket (#455, cook cost measured in #263).
        public static string BuildingCollisionMesh(ChunkCoord c, long id)
            => $"{ChunkDir(c)}/Buildings/building_{id}_collision.mesh";
        // Combined static geometry — one mesh per chunk per type (see SFMapImporterWindow).
        public static string BuildingsCombinedMesh(ChunkCoord c)     => $"{ChunkDir(c)}/Buildings/buildings_combined.mesh";
        public static string IntersectionsCombinedMesh(ChunkCoord c) => $"{ChunkDir(c)}/Intersections/intersections_combined.mesh";
        public static string RoadMaterial()                          => $"{Root}/Materials/RoadSurface.mat";
        public static string SidewalkMaterial()                      => $"{Root}/Materials/SidewalkSurface.mat";
        public static string BuildingMaterial()                      => $"{Root}/Materials/Building.mat";
        // The preset manifest SFMapPresetsWindow scans for. Its presence in Assets/Generated/<preset>/
        // is what makes a preset listable and loadable at all, so the importer writes it at the end
        // of every successful run (#469) — before that it only ever read the source bake's copy.
        public static string ManifestPath()                          => $"{Root}/manifest.json";

        // Runtime Resources paths (prefab per chunk + manifest ScriptableObject)
        public static string ChunkPrefabPath(ChunkCoord c)  => $"{ResourcesRoot}/{c}.prefab";
        public static string ChunkManifestPath()            => $"{ResourcesRoot}/ChunkManifest.asset";

        // Paths passed to Resources.Load at runtime (no "Assets/Resources/" prefix, no extension)
        public static string RuntimeChunkPrefab(ChunkCoord c) => $"Generated/{ActivePreset}/{c}";
        public static string RuntimeChunkManifest()           => $"Generated/{ActivePreset}/ChunkManifest";

        // Road name sidecar — TextAsset imported from chunk_CC_RR_names.json
        public static string ChunkRoadNamesAsset(ChunkCoord c) => $"{ResourcesRoot}/{c}_names.json";
        public static string RuntimeChunkRoadNames(ChunkCoord c) => $"Generated/{ActivePreset}/{c}_names";

        // Parked-car position sidecar — TextAsset imported from chunk_CC_RR_parked.json
        public static string ChunkParkedCarsAsset(ChunkCoord c)   => $"{ResourcesRoot}/{c}_parked.json";
        public static string RuntimeChunkParkedCars(ChunkCoord c) => $"Generated/{ActivePreset}/{c}_parked";

        /// <summary>
        /// EVERY asset root an import writes into for the active preset. A "clear this preset"
        /// must delete all of them together (#494): the meshes/terrain live under <see cref="Root"/>
        /// while the chunk prefabs and ChunkManifest.asset — which stores the incremental-import
        /// fingerprints (#261) — live under <see cref="ResourcesRoot"/>. Clearing only the first
        /// left every fingerprint and prefab in place, so the next import matched every chunk,
        /// skipped every chunk, and shipped prefabs referencing meshes that had just been deleted.
        /// Anything added here must also be picked up by the clear, which is the point of the list.
        /// </summary>
        public static string[] PresetRoots() => new[] { Root, ResourcesRoot };
    }

    /// <summary>
    /// JSON layout of <c>manifest.json</c>: written by <c>python/sfmap/serialize.py write_manifest()</c>
    /// for a bake, read by <c>SFMapImporterWindow</c> as its chunk list, and written again by that
    /// importer into <c>Assets/Generated/&lt;preset&gt;/</c> so <c>SFMapPresetsWindow</c> can list and
    /// load the preset (#469). Shared here rather than duplicated per-window on purpose: the browser
    /// keeping its own private copy of this shape is how the contract drifted unnoticed. Extra fields
    /// the bake emits (chunksX, osmBounds, …) are simply ignored by JsonUtility.
    /// </summary>
    [Serializable]
    public class PresetManifestJson
    {
        // MUST equal the Assets/Generated/<dir> folder name this file sits in. SFMapPresetsWindow
        // assigns it to BOTH GeneratedAssets.ActivePreset and ChunkStreamer.preset on load, so a
        // value that disagrees with the folder points every subsequent asset lookup somewhere else.
        public string preset;
        public string generated;      // ISO-8601 UTC, shown as the browser's subtitle
        public float  chunkSize;
        public PresetManifestChunkJson[] chunks;
        public float  minElevation;
    }

    [Serializable]
    public class PresetManifestChunkJson
    {
        public int   col;
        public int   row;
        public float worldX;
        public float worldZ;
    }

    /// <summary>
    /// Pure construction/validation for <see cref="PresetManifestJson"/>, kept out of the Editor
    /// window so it can be exercised without an AssetDatabase (see PresetManifestTests).
    /// </summary>
    public static class PresetManifests
    {
        public const string GeneratedFormat = "yyyy-MM-ddTHH:mm:ssZ";

        /// <summary>
        /// Build the manifest to write into <c>Assets/Generated/&lt;presetName&gt;/manifest.json</c>.
        /// <paramref name="presetName"/> — the importer's own preset, i.e. the folder being written
        /// into — always wins over <paramref name="source"/>.preset; see
        /// <see cref="PresetNameMismatchWarning"/>. Chunks come from what was actually imported
        /// (including chunks reused by the incremental skip), not from the source's full list, so a
        /// bake whose .bin files are partly missing lists only the chunks that really exist.
        /// </summary>
        public static PresetManifestJson Build(string presetName,
                                               PresetManifestJson source,
                                               IList<ChunkManifestEntry> imported,
                                               float minElevation,
                                               DateTime generatedUtc)
        {
            int n = imported?.Count ?? 0;
            var chunks = new PresetManifestChunkJson[n];
            for (int i = 0; i < n; i++)
            {
                var e = imported[i];
                chunks[i] = new PresetManifestChunkJson
                {
                    col = e.col, row = e.row, worldX = e.worldX, worldZ = e.worldZ,
                };
            }

            return new PresetManifestJson
            {
                preset       = presetName,
                generated    = generatedUtc.ToUniversalTime()
                                           .ToString(GeneratedFormat, CultureInfo.InvariantCulture),
                chunkSize    = source?.chunkSize ?? 0f,
                chunks       = chunks,
                minElevation = minElevation,
            };
        }

        /// <summary>
        /// The warning text for a manifest whose <c>preset</c> disagrees with the folder it belongs
        /// to, or null when there is nothing to warn about. Worded to hold on both sides — the
        /// importer writing a manifest and the browser reading one — because the resolution is the
        /// same either way: the folder wins. Silent before (#469), so a manifest could load a preset
        /// under one name and then resolve every asset path under another with no diagnostic.
        /// </summary>
        public static string PresetNameMismatchWarning(string folderName, string manifestPreset)
        {
            if (string.IsNullOrEmpty(manifestPreset)) return null;
            if (string.Equals(folderName, manifestPreset, StringComparison.Ordinal)) return null;
            return $"Preset name mismatch: folder Assets/Generated/{folderName}/ vs manifest.json " +
                   $"preset \"{manifestPreset}\". The folder name wins — ChunkStreamer and every " +
                   $"asset path resolve under \"{folderName}\". Rename one of them if that is wrong.";
        }
    }

    /// <summary>
    /// The incremental-import (#261) reuse decision, isolated from the Editor window so it can be
    /// tested. Reuse requires a matching fingerprint AND that BOTH halves of a chunk's output are
    /// still on disk — the prefab under Assets/Resources/Generated/ and the meshes/terrain under
    /// Assets/Generated/. Checking only the prefab was #494: "Clear Generated Assets" removed the
    /// second half, the guard still passed, and every chunk was skipped into a broken preset.
    /// </summary>
    public static class ChunkFreshness
    {
        public static bool CanReuse(string priorFingerprint,
                                    string currentFingerprint,
                                    bool prefabExists,
                                    bool generatedAssetsExist)
            => !string.IsNullOrEmpty(priorFingerprint)
               && !string.IsNullOrEmpty(currentFingerprint)
               && string.Equals(priorFingerprint, currentFingerprint, StringComparison.Ordinal)
               && prefabExists
               && generatedAssetsExist;
    }

    // JSON layout produced by python/sfmap/serialize.py write_parked_cars().
    // Shared here (not Editor-only) so ParkedCarStreamer can deserialise at runtime.
    [Serializable]
    public class ParkedCarsJson { public ParkedCarJson[] cars; }

    [Serializable]
    public class ParkedCarJson
    {
        public float[] p;   // world position [x, y, z]
        public float   r;   // heading in degrees about +Y
        public float   m;   // [0,1) model selector → floor(m * prefabCount) = index
        public string  s;   // nearest street name (may be empty)
        public long    id;  // source OSM regulation feature id
        public float[] n;   // ground normal [x, y, z] for slope tilt; null/empty → level

        /// <summary>
        /// World rotation for this car: heading <see cref="r"/> about +Y, then tilted so
        /// its up-axis matches the baked ground normal <see cref="n"/> — so on a hill the
        /// car rests on the grade instead of sitting level with one side buried in the road.
        /// Falls back to a level heading when no normal was baked (flat ground) or it is
        /// degenerate. Shared by the runtime streamer and the prefab-bake importer so both
        /// orient cars identically.
        /// </summary>
        public Quaternion Rotation()
        {
            var heading = Quaternion.Euler(0f, r, 0f);
            if (n == null || n.Length < 3) return heading;
            var up = new Vector3(n[0], n[1], n[2]);
            if (up.sqrMagnitude < 1e-6f) return heading;
            up.Normalize();
            var fwd = Vector3.ProjectOnPlane(heading * Vector3.forward, up);
            if (fwd.sqrMagnitude < 1e-6f) return heading;
            return Quaternion.LookRotation(fwd.normalized, up);
        }
    }
}
