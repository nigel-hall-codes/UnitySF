using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using SFMap.Pipeline;
using SFMap.Pipeline.Buildings;

namespace SFMap.Pipeline.Editor
{
    // The manifest.json layout (produced by python/sfmap/serialize.py write_manifest) now lives in
    // SFMap.Pipeline.PresetManifestJson — shared with SFMapPresetsWindow, which reads the copy this
    // importer writes into Assets/Generated/<preset>/ (#469). One declaration, so the two windows
    // cannot drift apart again.

    public class SFMapImporterWindow : EditorWindow
    {
        // .bin format constants, MeshType, and the byte-level parse live in
        // SFMap.Pipeline.ChunkBinReader (runtime assembly) so the edit-mode tests can
        // exercise the same parser against the Python golden fixture (#426).

        [SerializeField] string chunkDir    = "";
        [SerializeField] string presetName  = "default";
        [SerializeField] bool   bakeParkedCars = true;

        // Authoring-asset uploads (#447): per-building thumbnail (#369) + facade backdrop (#407)
        // renders and their PUTs, plus the per-chunk buildings sidecar POST (#384). Default OFF —
        // city-wide these are O(10^5) GPU readbacks + blocking HTTP round-trips repeated every
        // re-import, and the server/iOS app already fall back gracefully on missing assets. The
        // iPad-authoring workflow turns it on. When on, RunImport probes the server ONCE up front
        // and clears _uploadAuthoring for the whole run if it's unreachable, so a down server
        // costs one warning instead of a render+encode+failed-PUT stall per building.
        [SerializeField] bool   uploadAuthoringAssets = false;

        // Incremental-import generator version (#261). Folded into every chunk fingerprint, so
        // BUMP THIS whenever an import-code change alters a chunk's baked output (mesh build,
        // collider setup, terrain, prefab layout, palette maths, …). A bump invalidates every
        // stored fingerprint, forcing a one-time full re-import that re-establishes the baseline.
        const int GeneratorVersion = 1;

        // 7-color pastel palette for buildings. Each building picks one by hashing
        // its osm_id and the colour is baked into the building's vertex colors, so
        // a whole chunk of buildings shares one material (and one draw call) while
        // staying varied. Adding colours here needs no other pipeline changes.
        static readonly Color[] BuildingPalette =
        {
            new Color(0.847f, 0.655f, 0.694f), // dusty rose
            new Color(0.710f, 0.776f, 0.631f), // sage green
            new Color(0.953f, 0.914f, 0.824f), // warm cream
            new Color(0.682f, 0.776f, 0.910f), // sky blue
            new Color(0.886f, 0.647f, 0.494f), // terracotta
            new Color(0.788f, 0.722f, 0.878f), // lavender
            new Color(0.961f, 0.953f, 0.925f), // off-white
        };

        // Vehicle prefabs used for parked cars, indexed by the python model selector
        // (m in [0,1) → floor(m * length)). Curated to street vehicles; the plane and
        // monster truck from the asset pack are intentionally excluded. The python
        // bake stays decoupled from this list — it only emits a float, so reordering
        // or adding prefabs here needs no re-bake.
        static readonly string[] CarPrefabPaths =
        {
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Classic Car_9.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Hatchback Car_15.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/N_Muscle Car_10.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Sport Car_39.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/N Van_10.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Pick Up_11.prefab",
        };

        // The Awb vehicles are authored a bit oversized for this map; halve them.
        // The python placement footprint (sfmap/geometry/parking.py) is scaled to
        // match, so spacing stays dense without overlap.
        const float ParkedCarScale = 0.5f;

        // Authoring-server uploads (#369/#407 thumbnails + backdrops, #266 sidecar) now go
        // through SFMap.Pipeline.Editor.AuthoringServerClient; its BaseUrl (default
        // http://localhost:8000) matches ios/FacadeCanvas/Tests/.../FacadeCanvasTests.swift.
        const int    ThumbSize     = 512;

        // Facade backdrops (#407): fixed vertical resolution, width derived per building
        // from the wall's real aspect ratio (see RenderFacadeBackdrop).
        const int FacadeBackdropHeight   = 768;
        const int FacadeBackdropMinWidth = 128;
        const int FacadeBackdropMaxWidth = 1536;

        // --------------------------------------------------------------- timing
        // Stage 2 had no instrumentation (#260): it logged only a chunk count, so
        // every guess about what's slow was unverifiable. These accumulate per-op
        // wall-clock for one import run; RunImport prints the breakdown at the end.
        class ImportTimings
        {
            public int    chunks;
            public double binParseMs;    // .bin open + header + heightmap parse
            public double terrainMs;     // TerrainData build + SetHeights + asset
            public double meshBuildMs;   // BuildMesh (verts/normals/RecalculateNormals)
            public double colliderMs;    // MeshCollider cook (sharedMesh assignment)
            public double parkedCarsMs;  // parked-car prefab instantiation
            public double savePrefabMs;  // SaveAsPrefabAsset
            public double finalSaveMs;   // closing SaveAssets + Refresh

            // Authoring/templated-bake stages added after #260 (#446). These were all
            // untimed, so on a templated bake they hid inside "unaccounted" — the likely
            // #1 cost (per-building thumbnail/backdrop renders + blocking HTTP uploads)
            // was invisible. Render and upload are split so a server-down bake (render
            // only, uploads fail fast) vs server-up bake are separable in the breakdown.
            public double jsonSidecarMs;    // _names.json / _parked.json copy + ImportAsset
            public double sidecarPostMs;    // buildings sidecar POST (UploadBuildingsSidecar, #384)
            public double thumbRenderMs;    // building thumbnail render (#369)
            public double thumbUploadMs;    // building thumbnail upload PUT (#369)
            public double backdropRenderMs; // facade backdrop render (#407)
            public double backdropUploadMs; // facade backdrop upload PUT (#407)
            public double assembleMs;       // BuildingAssembler.Assemble templated pass (#266)
            public double decalImportMs;    // BuildingDecalImporter.Import (#280)
        }

        ImportTimings      _timings;
        readonly Stopwatch _opWatch = new Stopwatch();

        // Resolved once per run from uploadAuthoringAssets AND a live server probe (#447): the
        // gate on every render/upload site below, so "toggle off" and "server down" both skip
        // the render+encode, not just the PUT.
        bool _uploadAuthoring;

        // One shared op-watch is enough: the timed regions never overlap in time
        // (each chunk runs parse → terrain → mesh → collider → cars → save in turn).
        void StartOp() => _opWatch.Restart();
        void StopOp(ref double bucketMs) => bucketMs += _opWatch.Elapsed.TotalMilliseconds;

        // Per-operation breakdown so #259's profile-gated work targets the real
        // bottleneck. "unaccounted" = total minus the timed buckets (e.g. the
        // deferred StopAssetEditing import, per-mesh array reads, folder setup).
        static void LogImportTimings(ImportTimings t, double totalMs)
        {
            int    n = Math.Max(t.chunks, 1);
            double accounted = t.binParseMs + t.terrainMs + t.meshBuildMs + t.colliderMs +
                               t.parkedCarsMs + t.savePrefabMs + t.finalSaveMs +
                               t.jsonSidecarMs + t.sidecarPostMs + t.thumbRenderMs +
                               t.thumbUploadMs + t.backdropRenderMs + t.backdropUploadMs +
                               t.assembleMs + t.decalImportMs;
            double unaccounted = Math.Max(totalMs - accounted, 0.0);

            string Row(string label, double ms) =>
                $"  {label,-16}: {ms,9:F1}ms  {(totalMs > 0 ? ms / totalMs * 100 : 0),5:F1}%  mean {ms / n,7:F2}ms";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[SFMapImporter] Import timing over {t.chunks} chunk(s) — " +
                          $"total {totalMs:F1}ms ({totalMs / n:F1}ms/chunk):");
            sb.AppendLine(Row(".bin read+parse", t.binParseMs));
            sb.AppendLine(Row("terrain build",   t.terrainMs));
            sb.AppendLine(Row("mesh build",      t.meshBuildMs));
            sb.AppendLine(Row("collider cook",   t.colliderMs));
            sb.AppendLine(Row("parked cars",     t.parkedCarsMs));
            sb.AppendLine(Row("json sidecars",   t.jsonSidecarMs));
            sb.AppendLine(Row("sidecar POST",    t.sidecarPostMs));
            sb.AppendLine(Row("thumb render",    t.thumbRenderMs));
            sb.AppendLine(Row("thumb upload",    t.thumbUploadMs));
            sb.AppendLine(Row("backdrop render", t.backdropRenderMs));
            sb.AppendLine(Row("backdrop upload", t.backdropUploadMs));
            sb.AppendLine(Row("assemble",        t.assembleMs));
            sb.AppendLine(Row("decal import",    t.decalImportMs));
            sb.AppendLine(Row("save prefab",     t.savePrefabMs));
            sb.AppendLine(Row("final save",      t.finalSaveMs));
            sb.Append    (Row("unaccounted",     unaccounted));
            Debug.Log(sb.ToString());
        }

        [MenuItem("Window/SF Map Importer")]
        public static void Open() => GetWindow<SFMapImporterWindow>("SF Map Importer");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            chunkDir = EditorGUILayout.TextField("Chunk Directory", chunkDir);
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
            {
                string picked = EditorUtility.OpenFolderPanel("Select Chunk Directory", chunkDir, "");
                if (!string.IsNullOrEmpty(picked))
                    chunkDir = picked;
            }
            EditorGUILayout.EndHorizontal();

            presetName = EditorGUILayout.TextField("Preset Name", presetName);
            bakeParkedCars = EditorGUILayout.Toggle("Bake Parked Cars into Prefab", bakeParkedCars);
            uploadAuthoringAssets = EditorGUILayout.Toggle(
                new GUIContent("Upload Building Authoring Assets",
                    "Render + upload per-building thumbnails and facade backdrops and POST the " +
                    "buildings sidecar to the authoring server. Off for normal bakes; on for the " +
                    "iPad-authoring workflow. Skipped entirely if the server is unreachable."),
                uploadAuthoringAssets);

            EditorGUILayout.Space();
            if (GUILayout.Button("Import Chunks"))
                RunImport();
            if (GUILayout.Button("Clear Generated Assets"))
                ClearGenerated();
        }

        void RunImport()
        {
            GeneratedAssets.ActivePreset = presetName;

            string manifestPath = Path.Combine(chunkDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"[SFMapImporter] manifest.json not found at: {manifestPath}");
                return;
            }

            var manifest = JsonUtility.FromJson<PresetManifestJson>(File.ReadAllText(manifestPath));
            if (manifest?.chunks == null || manifest.chunks.Length == 0)
            {
                Debug.LogError("[SFMapImporter] manifest.json parsed but has no chunks.");
                return;
            }

            // A source manifest whose preset disagrees with the folder we're importing into is easy
            // to create by hand and used to fail silently (#469): the browser would load the preset
            // under one name and then resolve every asset path under the other.
            string mismatch = PresetManifests.PresetNameMismatchWarning(presetName, manifest.preset);
            if (mismatch != null)
                Debug.LogWarning($"[SFMapImporter] {mismatch}");

            _timings = new ImportTimings();
            var totalWatch = Stopwatch.StartNew();

            // Resolve the authoring-upload gate ONCE for the whole run (#447): only if the toggle
            // is on AND a single probe finds the server listening. A down server here means we
            // skip the renders too, not just the doomed PUTs — the expensive part is the GPU
            // readback + JPEG encode, which is pointless with nowhere to send the result.
            _uploadAuthoring = uploadAuthoringAssets && AuthoringServerClient.IsReachable();
            if (!uploadAuthoringAssets)
                Debug.Log("[SFMapImporter] Building authoring uploads OFF — skipping thumbnail/" +
                          "backdrop renders and sidecar POST this run.");
            else if (!_uploadAuthoring)
                Debug.LogWarning($"[SFMapImporter] Authoring server unreachable at " +
                                 $"{AuthoringServerClient.BaseUrl} — skipping all building renders/" +
                                 $"uploads this run (toggle is on, server is down).");

            EnsureTopLevelFolders();
            EnsureMaterials();

            // Facade-decal overrides are map-wide: load them ONCE here and reuse for every chunk,
            // instead of re-scanning + re-parsing Overrides/*.override.json per chunk (#295).
            var decalOverrides = BuildingDecalImporter.LoadDecalOverrides();

            var mapRoot    = new GameObject("SF Map");
            var coordList  = new List<ChunkManifestEntry>();
            float globalMinElev = float.MaxValue;

            // Incremental import (#261): reuse chunks whose inputs are byte-for-byte unchanged since
            // the last import. The preamble folds in everything map-wide that shapes output — the
            // generator version, preset, the parked-car and authoring toggles (both change per-chunk
            // work), and a hash of the whole SFBuildingTemplates authoring library — so a change to
            // any of them re-imports every chunk. Per-chunk inputs (.bin + sidecars) are hashed in
            // the loop. Any uncertainty (no prior fingerprint, changed input, missing prefab) falls
            // through to a full import, so the worst case is a redundant rebuild, never a stale asset.
            var priorEntries = LoadPriorChunkEntries();
            string fpPreamble = $"v{GeneratorVersion}|preset={presetName}|park={bakeParkedCars}" +
                                $"|auth={uploadAuthoringAssets}|lib={HashAuthoringLibrary()}";
            int skippedChunks = 0;

            try
            {
                for (int i = 0; i < manifest.chunks.Length; i++)
                {
                    var mc    = manifest.chunks[i];
                    var coord = new ChunkCoord(mc.col, mc.row);
                    EditorUtility.DisplayProgressBar("SF Map Importer",
                        $"Importing {coord}…", (float)i / manifest.chunks.Length);

                    string binPath = Path.Combine(chunkDir, $"chunk_{mc.col:00}_{mc.row:00}.bin");
                    if (!File.Exists(binPath))
                    {
                        Debug.LogWarning($"[SFMapImporter] Missing .bin: {binPath}");
                        continue;
                    }

                    // Incremental skip (#261): reuse this chunk if its inputs are unchanged since the
                    // last import AND BOTH halves of its output survive — the prefab under
                    // Assets/Resources/Generated/ and the chunk's assets under Assets/Generated/.
                    // The prefab alone was not enough (#494): a "Clear Generated Assets" wiped only
                    // the latter, so the guard still passed and every chunk was skipped, leaving
                    // prefabs pointing at meshes that had just been deleted. Terrain.asset stands in
                    // for the whole chunk directory because ImportChunk writes it unconditionally for
                    // every chunk, before any mesh. Carry the prior entry forward so the manifest
                    // stays complete, then continue — skipping the mesh/terrain/collider bake, the
                    // prefab save, AND the #447 authoring uploads (no re-render of unchanged
                    // buildings). mapRoot is torn down at the end, so nothing needs instantiating.
                    string fingerprint = ComputeChunkFingerprint(chunkDir, coord, fpPreamble);
                    if (priorEntries.TryGetValue((mc.col, mc.row), out var priorEntry)
                        && ChunkFreshness.CanReuse(
                               priorEntry.fingerprint, fingerprint,
                               prefabExists:         File.Exists(GeneratedAssets.ChunkPrefabPath(coord)),
                               generatedAssetsExist: File.Exists(GeneratedAssets.TerrainAsset(coord))))
                    {
                        coordList.Add(new ChunkManifestEntry
                        {
                            col     = mc.col,
                            row     = mc.row,
                            worldX  = mc.worldX,
                            worldZ  = mc.worldZ,
                            minElev = priorEntry.minElev,
                            fingerprint = fingerprint,
                        });
                        if (priorEntry.minElev < globalMinElev)
                            globalMinElev = priorEntry.minElev;
                        skippedChunks++;
                        continue;
                    }

                    float chunkMinElev = ImportChunk(binPath, coord, mc.worldX, mc.worldZ, mapRoot);
                    if (chunkMinElev < globalMinElev)
                        globalMinElev = chunkMinElev;

                    StartOp();
                    ImportRoadNames(chunkDir, coord);
                    ImportParkedCarsJson(chunkDir, coord);
                    StopOp(ref _timings.jsonSidecarMs);

                    var chunkRootGo = mapRoot.transform.Find(coord.ToString()).gameObject;
                    if (bakeParkedCars)
                    {
                        StartOp();
                        ImportParkedCars(chunkDir, coord, chunkRootGo);
                        StopOp(ref _timings.parkedCarsMs);
                    }

                    // Facade decals (#280): alpha-textured quads from building-specific overrides,
                    // nested like parked cars so they bake into the chunk prefab. No-op without a
                    // sidecar or facadeDecals. Overrides are loaded once above and batched internally.
                    StartOp();
                    BuildingDecalImporter.Import(chunkDir, coord, chunkRootGo, decalOverrides);
                    StopOp(ref _timings.decalImportMs);

                    StartOp();
                    PrefabUtility.SaveAsPrefabAsset(chunkRootGo,
                        GeneratedAssets.ChunkPrefabPath(coord));
                    StopOp(ref _timings.savePrefabMs);

                    coordList.Add(new ChunkManifestEntry
                    {
                        col     = mc.col,
                        row     = mc.row,
                        worldX  = mc.worldX,
                        worldZ  = mc.worldZ,
                        minElev = chunkMinElev,
                        fingerprint = fingerprint,
                    });
                }

                if (globalMinElev == float.MaxValue) globalMinElev = 0f;
                SaveChunkManifest(manifest, coordList, globalMinElev);
                // Written unconditionally, not only when chunks were rebuilt: an all-skipped
                // re-import of a preset produced before #469 is exactly how a preset with no
                // manifest.json gets one, without paying for a full rebuild.
                WritePresetManifest(manifest, coordList, globalMinElev);

                StartOp();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                StopOp(ref _timings.finalSaveMs);

                // Split built-vs-reused rather than reporting one total (#494): "4 chunks, 4 reused"
                // and "4 chunks, 4 built" used to read the same at a glance, which is how an import
                // that skipped everything and produced nothing passed for a success.
                int builtChunks = coordList.Count - skippedChunks;
                if (coordList.Count == 0)
                    Debug.LogError($"[SFMapImporter] Imported NO chunks for preset '{presetName}' — " +
                                   $"every chunk in manifest.json was missing its .bin. Nothing was " +
                                   $"written to {GeneratedAssets.Root}.");
                else
                    Debug.Log($"[SFMapImporter] Imported {coordList.Count} chunk(s) for preset " +
                              $"'{presetName}': {builtChunks} built, {skippedChunks} unchanged and reused.");

                totalWatch.Stop();
                _timings.chunks = coordList.Count;
                LogImportTimings(_timings, totalWatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SFMapImporter] Import failed: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DestroyImmediate(mapRoot);
            }
        }

        // Returns the chunk's min elevation (for the global ChunkManifest).
        float ImportChunk(string binPath, ChunkCoord coord, float worldX, float worldZ, GameObject mapRoot)
        {
            StartOp();
            // Byte-level parse (header + heightmap + all mesh entries) lives in the shared
            // runtime-side reader; this method just builds Unity assets from the result.
            ChunkBinData chunk = ChunkBinReader.Read(binPath);
            int   hmapRes    = chunk.HmapRes;
            float chunkSizeM = chunk.ChunkSizeM;
            float minElevM   = chunk.MinElevM;
            float maxElevM   = chunk.MaxElevM;
            float[,] heights2D = chunk.Heights;
            StopOp(ref _timings.binParseMs);

            EnsureChunkFolder(coord);
            var baseLayer = EnsureBaseTerrainLayer();

            // Bulk-create this chunk's terrain + mesh assets inside one StartAssetEditing
            // block. Without it the AssetDatabase imports each road/sidewalk/intersection
            // mesh asset individually, which makes a multi-chunk import slow; StopAssetEditing
            // imports them all in a single pass. (Buildings are combined, so they contribute
            // just one asset per chunk.)
            TerrainData terrainData = null;

            // Roads, sidewalks, and intersections stay one-asset-and-GameObject-per-mesh so
            // each is individually selectable; all carry per-mesh colliders, with roads and
            // intersections also on the Road layer.
            var roadMeshes         = new List<Mesh>();
            var sidewalkMeshes     = new List<Mesh>();
            var intersectionMeshes = new List<Mesh>();
            // Buildings are non-interactive static geometry: built in memory (no per-mesh
            // asset), then merged into one combined mesh per chunk.
            var buildingParts     = new List<Mesh>();
            Mesh combinedBuildings = null;

            // Buildings-tab thumbnails (#384): the server only accepts a building's thumbnail PUT
            // once that building's facts row exists in its DB (#318 guard) — so the chunk's sidecar
            // must be imported before any RenderAndUploadBuildingThumbnail call below runs. This was
            // previously never wired up, so every thumbnail PUT 404'd silently. Gated with the
            // thumbnail/backdrop uploads (#447): the sidecar only exists to seed the server's
            // facts rows for those uploads, so there's nothing to POST when they're skipped.
            if (_uploadAuthoring)
            {
                StartOp();
                UploadBuildingsSidecar(Path.GetDirectoryName(binPath), coord);
                StopOp(ref _timings.sidecarPostMs);
            }

            // Building Assembler (design #266): if this chunk has a classification sidecar AND a
            // template library exists, buildings that match a template fork off into individual
            // nested prefabs; everything else stays on the merged path below. Null ⇒ today's
            // behaviour (no sidecar / no templates), so an un-templated bake is unchanged.
            var assembler = BuildingAssembler.TryCreate(Path.GetDirectoryName(binPath), coord);
            var templated = new List<(long osmId, Mesh mesh, BuildingFactsJson facts, BuildingTemplate tpl)>();

            AssetDatabase.StartAssetEditing();
            try
            {
                StartOp();
                terrainData = new TerrainData();
                terrainData.heightmapResolution = hmapRes;
                terrainData.size = new Vector3(chunkSizeM, Mathf.Max(maxElevM - minElevM, 1f), chunkSizeM);
                terrainData.SetHeights(0, 0, heights2D);
                terrainData.terrainLayers = new[] { baseLayer };
                CreateOrReplaceAsset(terrainData, GeneratedAssets.TerrainAsset(coord));
                StopOp(ref _timings.terrainMs);

                // ---- Mesh entries ----
                foreach (var cm in chunk.Meshes)
                {
                    var  meshType = cm.Type;
                    long osmId    = cm.OsmId;
                    var  verts    = cm.Vertices;
                    var  normals  = cm.Normals;
                    var  uvs      = cm.Uvs;
                    var  indices  = cm.Indices;

                    if (verts.Length == 0 || indices.Length == 0) continue;

                    StartOp();
                    var mesh = BuildMesh(MeshName(meshType, osmId), verts, normals, uvs, indices);
                    StopOp(ref _timings.meshBuildMs);

                    switch (meshType)
                    {
                        case ChunkMeshType.Road:
                            CreateOrReplaceAsset(mesh, GeneratedAssets.RoadMesh(coord, osmId));
                            roadMeshes.Add(mesh);
                            break;
                        case ChunkMeshType.Sidewalk:
                            CreateOrReplaceAsset(mesh, GeneratedAssets.SidewalkMesh(coord, osmId));
                            sidewalkMeshes.Add(mesh);
                            break;
                        case ChunkMeshType.Intersection:
                            CreateOrReplaceAsset(mesh, GeneratedAssets.IntersectionMesh(coord, osmId));
                            intersectionMeshes.Add(mesh);
                            break;
                        case ChunkMeshType.Building:
                            // Buildings-tab thumbnails (#369): render + upload a snapshot for every
                            // building — templated or merged-fallback — right here, while its raw
                            // mesh (world-space per the comment above CombineParts) and bounds are
                            // still available per-building, before it forks below or gets merged.
                            // Best-effort: never aborts the import (see the try/catch inside).
                            // Gated by _uploadAuthoring (#447) so the render+encode is skipped, not
                            // just the PUT, on a normal (upload-off / server-down) bake.
                            if (_uploadAuthoring)
                                RenderAndUploadBuildingThumbnail(osmId, mesh, _timings);

                            if (assembler != null &&
                                assembler.TryMatch(osmId, out var bFacts, out var bTpl))
                            {
                                // Templated: the assembler resolves role colours, bakes them, and
                                // saves the combined per-building mesh asset — so no fallback bake
                                // here. Just set the mass mesh aside. (This is unconditional — the
                                // templated geometry is built regardless of authoring uploads; only
                                // the backdrop render/upload below is gated.)
                                templated.Add((osmId, mesh, bFacts, bTpl));

                                // Facade backdrops (#407): needs the sidecar's facade edge geometry,
                                // which only templated buildings carry (same scope as facade decals
                                // placed via FacadesFor) — best-effort, mirrors the thumbnail above.
                                if (_uploadAuthoring)
                                    RenderAndUploadFacadeBackdrops(osmId, mesh, bFacts, _timings);
                            }
                            else
                            {
                                // Fallback (merged path, as today): bake the building's vertex
                                // colour. Prefer the neighborhood palette (when the sidecar gives
                                // this building a neighborhood) so templated and fallback buildings
                                // stay coherent (#272); otherwise the legacy 7-colour pastel palette.
                                Color32 c = (assembler != null && assembler.TryFallbackColor(osmId, out var pc))
                                    ? pc : (Color32)BuildingPalette[(int)(Math.Abs(osmId) % BuildingPalette.Length)];
                                var colors = new Color32[verts.Length];
                                for (int v = 0; v < verts.Length; v++) colors[v] = c;
                                mesh.SetColors(colors);
                                buildingParts.Add(mesh);
                            }
                            break;
                    }
                }

                // Merge the static, non-interactive building geometry into one mesh per
                // chunk. Vertices are already world-space (GameObjects sit at the origin),
                // so combine without transforms — placement is byte-identical.
                combinedBuildings = CombineParts(buildingParts, $"buildings_{coord}",
                                                 GeneratedAssets.BuildingsCombinedMesh(coord));
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // ---- Build GameObject hierarchy ----
            var chunkRoot = new GameObject(coord.ToString());
            chunkRoot.transform.SetParent(mapRoot.transform, false);

            var terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = $"Terrain {coord}";
            terrainGo.transform.SetParent(chunkRoot.transform, false);
            terrainGo.transform.position = new Vector3(worldX, minElevM, worldZ);

            var roadMat = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.RoadMaterial());
            var swMat   = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.SidewalkMaterial());
            var bldgMat = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.BuildingMaterial());
            int roadLayer = LayerMask.NameToLayer("Road");

            // Roads: one GameObject per mesh, each with a collider on the Road layer.
            var roadParent = CreateChild(chunkRoot, $"Roads {coord}");
            foreach (var mesh in roadMeshes)
            {
                var go = PlaceMesh(mesh, roadParent, roadMat);
                StartOp();
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                StopOp(ref _timings.colliderMs);
                go.layer = roadLayer;
            }

            // Sidewalks get a collider so cars can't drive through them, but stay off the
            // Road layer so traffic raycasts don't treat the kerb as a drivable surface.
            var swParent = CreateChild(chunkRoot, $"Sidewalks {coord}");
            foreach (var mesh in sidewalkMeshes)
            {
                var go = PlaceMesh(mesh, swParent, swMat);
                StartOp();
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                StopOp(ref _timings.colliderMs);
            }

            // Intersections: one GameObject per mesh, like roads (collider + Road layer),
            // so each junction is individually selectable in the Hierarchy.
            var intParent = CreateChild(chunkRoot, $"Intersections {coord}");
            foreach (var mesh in intersectionMeshes)
            {
                var go = PlaceMesh(mesh, intParent, roadMat);
                StartOp();
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                StopOp(ref _timings.colliderMs);
                go.layer = roadLayer;
            }

            // Buildings: the un-templated buildings stay one combined mesh per chunk; templated
            // ones become individual nested prefabs alongside it under the same parent (the
            // parked-car nesting precedent), so ChunkStreamer streams them unchanged.
            var bldgGo = CreateChild(chunkRoot, $"Buildings {coord}");
            if (combinedBuildings != null)
            {
                bldgGo.AddComponent<MeshFilter>().sharedMesh = combinedBuildings;
                bldgGo.AddComponent<MeshRenderer>().sharedMaterial = bldgMat;
                // On-foot raycasts (graffiti spray, #390) need to hit facades, so the combined
                // building mesh gets a MeshCollider on the Default layer — one cook per chunk,
                // matching the roads/sidewalks pattern above. Traffic/car raycasts use the Road
                // layer, so this stays clear of them.
                StartOp();
                bldgGo.AddComponent<MeshCollider>().sharedMesh = combinedBuildings;
                StopOp(ref _timings.colliderMs);
            }
            if (assembler != null)
            {
                // Batch the per-building combined-mesh CreateAsset writes into one import pass. This
                // assemble pass runs in the post-hierarchy phase (after the chunk's own StartAssetEditing
                // block has already closed), so without this wrapper each templated building's mesh
                // imported one-by-one — the #295 hotspot on dense template chunks.
                if (templated.Count > 0)
                {
                    StartOp();
                    AssetDatabase.StartAssetEditing();
                    try
                    {
                        foreach (var t in templated)
                            assembler.Assemble(bldgGo.transform, coord, t.osmId, t.mesh, t.facts, t.tpl, bldgMat);
                    }
                    finally
                    {
                        AssetDatabase.StopAssetEditing();
                    }
                    StopOp(ref _timings.assembleMs);
                }
                assembler.LogCoverage(coord, buildingParts.Count);   // fallback = merged buildings
                // The chunk's generated part meshes were cloned into each building's combine, so
                // the originals are done (#454).
                assembler.ReleaseGeneratedMeshes();
            }

            return minElevM;
        }

        static void ImportRoadNames(string chunkDir, ChunkCoord coord)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_names.json");
            if (!File.Exists(src)) return;

            string dst = GeneratedAssets.ChunkRoadNamesAsset(coord);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: true);
            AssetDatabase.ImportAsset(dst);
        }

        static void ImportParkedCarsJson(string chunkDir, ChunkCoord coord)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_parked.json");
            if (!File.Exists(src)) return;

            string dst = GeneratedAssets.ChunkParkedCarsAsset(coord);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: true);
            AssetDatabase.ImportAsset(dst);
        }

        // Instantiate parked-car prefabs from chunk_CC_RR_parked.json under a
        // "ParkedCars {coord}" parent on the chunk root, so they bake into the chunk
        // prefab (as nested prefab instances) and stream in with the rest of the chunk.
        // Cars are grouped under a child per street name so a future tool can add or
        // remove a street's cars by toggling one GameObject. No-op when the sidecar is
        // absent (no --parking source) or empty.
        void ImportParkedCars(string chunkDir, ChunkCoord coord, GameObject chunkRoot)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_parked.json");
            if (!File.Exists(src)) return;

            var data = JsonUtility.FromJson<ParkedCarsJson>(File.ReadAllText(src));
            if (data?.cars == null || data.cars.Length == 0) return;

            var prefabs = LoadCarPrefabs();
            if (prefabs.Length == 0)
            {
                Debug.LogWarning("[SFMapImporter] No parked-car prefabs found — skipping parked cars. " +
                                 "Expected the 'Awb-Free Low Poly Vehicles' asset under Assets/.");
                return;
            }

            var carsParent = CreateChild(chunkRoot, $"ParkedCars {coord}");
            var streetGroups = new Dictionary<string, Transform>();
            int placed = 0;

            foreach (var car in data.cars)
            {
                if (car.p == null || car.p.Length < 3) continue;

                int idx = Mathf.Clamp(Mathf.FloorToInt(car.m * prefabs.Length), 0, prefabs.Length - 1);
                var prefab = prefabs[idx];
                if (prefab == null) continue;

                string street = string.IsNullOrEmpty(car.s) ? "(unknown)" : car.s;
                if (!streetGroups.TryGetValue(street, out var group))
                {
                    group = CreateChild(carsParent, street).transform;
                    streetGroups[street] = group;
                }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(group, false);
                go.transform.localPosition = new Vector3(car.p[0], car.p[1], car.p[2]);
                go.transform.localRotation = car.Rotation();
                go.transform.localScale = prefab.transform.localScale * ParkedCarScale;
                placed++;
            }

            if (placed > 0)
                Debug.Log($"[SFMapImporter] {coord}: placed {placed} parked car(s) across {streetGroups.Count} street(s).");
        }

        // Load (and cache for this import run) the curated vehicle prefabs. Missing
        // entries are dropped so a partial asset install still places what it can.
        GameObject[] _carPrefabCache;
        GameObject[] LoadCarPrefabs()
        {
            if (_carPrefabCache != null) return _carPrefabCache;
            var list = new List<GameObject>(CarPrefabPaths.Length);
            foreach (var path in CarPrefabPaths)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) list.Add(go);
                else Debug.LogWarning($"[SFMapImporter] Parked-car prefab not found: {path}");
            }
            _carPrefabCache = list.ToArray();
            return _carPrefabCache;
        }

        void SaveChunkManifest(PresetManifestJson src, List<ChunkManifestEntry> entries, float minElev)
        {
            var asset = ScriptableObject.CreateInstance<ChunkManifest>();
            // presetName, not src.preset: this asset's folder is Assets/Resources/Generated/<presetName>/
            // and ChunkStreamer resolves everything else relative to it, so the two must agree (#469).
            // A disagreement was already warned about at the top of RunImport.
            asset.preset          = presetName;
            asset.chunkSizeMeters = src.chunkSize;
            asset.minElevation    = minElev;
            asset.chunks          = entries.ToArray();

            string path = GeneratedAssets.ChunkManifestPath();
            CreateOrReplaceAsset(asset, path);
        }

        // Write Assets/Generated/<presetName>/manifest.json (#469). Without it SFMapPresetsWindow
        // skips the directory entirely — a 108-chunk, 39-minute import completed "successfully" and
        // was then unloadable, the browser reporting only the generic "No presets found". This is a
        // plain File.Write + ImportAsset rather than a copy of the source manifest, because the
        // preset field must name THIS folder and the chunk list must be what was actually imported.
        void WritePresetManifest(PresetManifestJson src, List<ChunkManifestEntry> entries, float minElev)
        {
            string path = GeneratedAssets.ManifestPath();
            try
            {
                var pm = PresetManifests.Build(presetName, src, entries, minElev, DateTime.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(pm, prettyPrint: true));
                AssetDatabase.ImportAsset(path);
            }
            catch (Exception e)
            {
                // Loud: a missing manifest is precisely the silent, expensive failure #469 is about.
                Debug.LogError($"[SFMapImporter] Failed to write {path}: {e.Message}. The preset will " +
                               $"not appear in the Preset Browser until this file exists.");
            }
        }

        // -------------------------------------------- incremental-import fingerprints (#261)

        // Prior run's chunk entries keyed by (col,row), from the ChunkManifest asset written last
        // time for the CURRENT preset (ActivePreset is already set). Empty on a first import or when
        // the manifest is absent — every chunk then falls through to a full import.
        static Dictionary<(int, int), ChunkManifestEntry> LoadPriorChunkEntries()
        {
            var map = new Dictionary<(int, int), ChunkManifestEntry>();
            var prior = AssetDatabase.LoadAssetAtPath<ChunkManifest>(GeneratedAssets.ChunkManifestPath());
            if (prior?.chunks != null)
                foreach (var e in prior.chunks)
                    map[(e.col, e.row)] = e;
            return map;
        }

        // Hash of every file under Assets/SFBuildingTemplates/ (templates, overrides, palettes,
        // parts, signs, library.json) — the map-wide authoring inputs that shape templated output
        // without living in any chunk's .bin. Conservative on purpose: a change to any one of them
        // re-imports the whole map. .meta files are excluded (Unity import metadata, not bake input).
        static string HashAuthoringLibrary()
        {
            using var sha = SHA256.Create();
            string root = SFBuildingTemplatePaths.LibraryRoot;
            if (Directory.Exists(root))
            {
                foreach (string file in Directory
                             .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                             .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f, StringComparer.Ordinal))
                {
                    byte[] rel = System.Text.Encoding.UTF8.GetBytes(file.Replace('\\', '/'));
                    sha.TransformBlock(rel, 0, rel.Length, null, 0);
                    byte[] data = File.ReadAllBytes(file);
                    sha.TransformBlock(data, 0, data.Length, null, 0);
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "");
        }

        // SHA-256 over a chunk's own inputs — its .bin and the three sidecars that shape its output
        // (buildings classification, parked cars, road names) — prefixed with the map-wide preamble.
        // A one-byte presence marker per file makes "sidecar absent" hash differently from "present
        // but empty", so adding or deleting a sidecar always dirties the chunk.
        static string ComputeChunkFingerprint(string chunkDir, ChunkCoord coord, string preamble)
        {
            using var sha = SHA256.Create();
            void Feed(byte[] b) { sha.TransformBlock(b, 0, b.Length, null, 0); }
            void FeedFile(string path)
            {
                if (File.Exists(path)) { Feed(new byte[] { 1 }); Feed(File.ReadAllBytes(path)); }
                else Feed(new byte[] { 0 });
            }

            Feed(System.Text.Encoding.UTF8.GetBytes(preamble));
            string stem = $"chunk_{coord.Col:00}_{coord.Row:00}";
            FeedFile(Path.Combine(chunkDir, $"{stem}.bin"));
            FeedFile(Path.Combine(chunkDir, $"{stem}_buildings.json"));
            FeedFile(Path.Combine(chunkDir, $"{stem}_parked.json"));
            FeedFile(Path.Combine(chunkDir, $"{stem}_names.json"));
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "");
        }

        // ------------------------------------------------------------------ helpers

        static void EnsureMaterials()
        {
            EnsureFolder(GeneratedAssets.Root, "Materials");

            string roadPath = GeneratedAssets.RoadMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(roadPath) == null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0.25f, 0.25f, 0.25f);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0f);
                AssetDatabase.CreateAsset(mat, roadPath);
            }

            string swPath = GeneratedAssets.SidewalkMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(swPath) == null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0.75f, 0.75f, 0.75f);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0f);
                AssetDatabase.CreateAsset(mat, swPath);
            }

            // One shared building material. Per-building colour comes from vertex
            // colors (baked in ImportChunk), so the BuildingVertexColor shader tints
            // albedo by vertex colour and a whole chunk renders in one draw call.
            string bldgPath = GeneratedAssets.BuildingMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(bldgPath) == null)
            {
                var shader = Shader.Find("SFMap/BuildingVertexColor");
                if (shader == null)
                {
                    Debug.LogError("[SFMapImporter] Shader 'SFMap/BuildingVertexColor' not found " +
                                   "(expected at Assets/Shaders/BuildingVertexColor.shader).");
                    return;
                }
                var mat = new Material(shader) { color = Color.white };
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0.05f);
                AssetDatabase.CreateAsset(mat, bldgPath);
            }
        }

        static void EnsureTopLevelFolders()
        {
            EnsureFolder("Assets", "Generated");
            EnsureFolder("Assets/Generated", GeneratedAssets.ActivePreset);
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "Generated");
            EnsureFolder("Assets/Resources/Generated", GeneratedAssets.ActivePreset);
        }

        static TerrainLayer EnsureBaseTerrainLayer()
        {
            string path = GeneratedAssets.TerrainBaseLayer();
            var existing = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (existing != null)
                return existing;

            EnsureFolder(GeneratedAssets.Root, "Materials");

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
            tex.SetPixel(0, 0, new Color(0.42f, 0.47f, 0.38f));
            tex.Apply();
            tex.name = "BaseColor";

            var layer = new TerrainLayer { tileSize = new Vector2(10, 10) };
            AssetDatabase.CreateAsset(layer, path);
            AssetDatabase.AddObjectToAsset(tex, path);
            layer.diffuseTexture = tex;
            EditorUtility.SetDirty(layer);
            AssetDatabase.SaveAssets();
            return layer;
        }

        static void EnsureChunkFolder(ChunkCoord coord)
        {
            string root   = GeneratedAssets.Root;
            string parent = $"{root}/{coord}";
            EnsureFolder(root, coord.ToString());
            EnsureFolder(parent, "Roads");
            EnsureFolder(parent, "Intersections");
            EnsureFolder(parent, "Sidewalks");
            EnsureFolder(parent, "Buildings");
        }

        static void EnsureFolder(string parent, string name)
        {
            string path = $"{parent}/{name}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        static void CreateOrReplaceAsset(UnityEngine.Object obj, string path)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null)
                AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
        }

        static string MeshName(ChunkMeshType t, long osmId) => t switch
        {
            ChunkMeshType.Road         => $"road_{osmId}",
            ChunkMeshType.Intersection => $"intersection_{osmId}",
            ChunkMeshType.Sidewalk     => $"sidewalk_{osmId}",
            ChunkMeshType.Building     => $"building_{osmId}",
            _                     => $"mesh_{osmId}",
        };

        static Mesh BuildMesh(string name, Vector3[] verts, Vector3[] normals, Vector2[] uvs, int[] indices)
        {
            var mesh = new Mesh { name = name };
            mesh.indexFormat = verts.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(indices, 0);
            mesh.SetUVs(0, uvs);

            if (AllZero(normals))
                mesh.RecalculateNormals();
            else
                mesh.SetNormals(normals);

            mesh.RecalculateBounds();
            return mesh;
        }

        // Merge in-memory part meshes into a single chunk mesh asset and return it
        // (null when there are no parts). Parts are combined without transforms — their
        // vertices are already world-space — so the result sits at the origin exactly
        // like the originals. Source normals/UVs/colors are preserved (no recalc, which
        // would average across touching buildings). The temporary parts are released.
        static Mesh CombineParts(List<Mesh> parts, string name, string assetPath)
        {
            if (parts.Count == 0)
                return null;

            var instances = new CombineInstance[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                instances[i] = new CombineInstance { mesh = parts[i], subMeshIndex = 0 };

            var combined = new Mesh
            {
                name        = name,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, // combined verts exceed 65535
            };
            combined.CombineMeshes(instances, mergeSubMeshes: true, useMatrices: false);
            combined.RecalculateBounds();

            foreach (var p in parts)
                DestroyImmediate(p);

            CreateOrReplaceAsset(combined, assetPath);
            return combined;
        }

        // ------------------------------------------------------- buildings sidecar import (#384)

        // Best-effort, matching UploadBuildingThumbnail below: POST the chunk's raw
        // chunk_CC_RR_buildings.json sidecar (same file BuildingAssembler.TryCreate reads) to the
        // server so it upserts a facts row per building. Its field names already mirror the
        // server's SidecarDoc/BuildingFacts models 1:1 (both mirror python/sfmap/serialize.py), so
        // the file's bytes are POSTed as-is — no Unity-side reserialization needed. A no-op when
        // the chunk has no sidecar (un-classified bake, same as BuildingAssembler.TryCreate).
        static void UploadBuildingsSidecar(string chunkDir, ChunkCoord coord)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_buildings.json");
            if (!File.Exists(src)) return;

            try
            {
                byte[] body = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(src));
                AuthoringServerClient.Post("/buildings/import-sidecar", body, "application/json",
                                           $"{coord}: buildings sidecar import");
            }
            catch (Exception e)
            {
                // Reading the sidecar must never abort the import (matches pre-#427 behaviour).
                Debug.LogWarning($"[SFMapImporter] {coord}: buildings sidecar import failed: {e.Message}");
            }
        }

        // ------------------------------------------------------- building thumbnails (#369)

        // Best-effort: render an offscreen snapshot of this building and PUT it to the
        // server. Any failure (server not running, non-2xx response, render hiccup) is
        // logged as a warning and swallowed — most local bakes run without the server up,
        // and a missing thumbnail is exactly the placeholder-swatch fallback the iOS app
        // and server already handle correctly.
        // `timings` splits the render cost from the blocking upload PUT (#446): on a
        // server-down bake the render still runs but the PUT fails fast, so keeping them
        // in separate buckets tells #259 how much of this stage is CPU vs. network.
        static void RenderAndUploadBuildingThumbnail(long osmId, Mesh mesh, ImportTimings timings)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                byte[] jpg = RenderBuildingThumbnail(mesh, osmId);
                timings.thumbRenderMs += sw.Elapsed.TotalMilliseconds;
                if (jpg == null) return;
                sw.Restart();
                UploadBuildingThumbnail(osmId, jpg);
                timings.thumbUploadMs += sw.Elapsed.TotalMilliseconds;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SFMapImporter] Building {osmId}: thumbnail render/upload failed: {e.Message}");
            }
        }

        // Spawns a throwaway camera + GameObject, both moved into an isolated Editor preview
        // scene (review #369: a plain cullingMask isn't enough — the import scene already
        // contains previously-imported chunks' geometry at real world coordinates, so a mask
        // alone can't stop the camera from rendering into/through neighboring buildings; a
        // preview scene guarantees the camera sees only what we put in it, regardless of what
        // else exists in the map). Renders `mesh` (already world-space, per CombineParts'
        // comment) from a 3/4 angle framing its bounds, reads the result back into a Texture2D,
        // and JPEG-encodes it. Closing the preview scene tears down everything spawned into it
        // — nothing leaks into the chunk prefab or the real scene.
        static byte[] RenderBuildingThumbnail(Mesh mesh, long osmId)
        {
            Bounds bounds = mesh.bounds;
            if (bounds.size.sqrMagnitude < 1e-6f) return null;

            var previewScene   = default(Scene);
            bool previewOpen   = false;
            Material   mat     = null;
            RenderTexture rt   = null;
            Texture2D  tex     = null;
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                previewOpen  = true;

                var meshGo = new GameObject($"ThumbMesh_{osmId}") { hideFlags = HideFlags.HideAndDontSave };
                meshGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                mat = new Material(Shader.Find("Unlit/Color"))
                {
                    color = BuildingPalette[(int)(Math.Abs(osmId) % BuildingPalette.Length)],
                };
                meshGo.AddComponent<MeshRenderer>().sharedMaterial = mat;
                SceneManager.MoveGameObjectToScene(meshGo, previewScene);

                var camGo = new GameObject($"ThumbCam_{osmId}") { hideFlags = HideFlags.HideAndDontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.scene           = previewScene; // renders ONLY this preview scene's contents
                cam.clearFlags      = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.82f, 0.82f, 0.82f, 1f);
                cam.fieldOfView     = 40f;
                cam.nearClipPlane   = 0.05f;
                SceneManager.MoveGameObjectToScene(camGo, previewScene);

                // Frame the bounds from a 3/4 angle: back off far enough along that
                // direction that the bounding sphere fits inside the vertical FOV.
                float radius   = Mathf.Max(bounds.extents.magnitude, 0.5f);
                float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
                Vector3 dir    = new Vector3(1f, 0.6f, 1f).normalized;
                camGo.transform.position = bounds.center + dir * distance;
                camGo.transform.LookAt(bounds.center, Vector3.up);
                cam.farClipPlane = distance + radius * 2f + 10f;

                rt = new RenderTexture(ThumbSize, ThumbSize, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(ThumbSize, ThumbSize, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, ThumbSize, ThumbSize), 0, 0);
                tex.Apply();

                return ImageConversion.EncodeToJPG(tex, 85);
            }
            finally
            {
                // Restore whatever was active before we touched it — never hardcode null,
                // or we silently break whatever rendering/Editor GUI runs next this frame.
                RenderTexture.active = prevActive;
                if (tex != null) DestroyImmediate(tex);
                if (rt != null)
                {
                    rt.Release();
                    DestroyImmediate(rt);
                }
                if (mat != null) DestroyImmediate(mat);
                // Closing the preview scene destroys every GameObject moved into it
                // (meshGo, camGo) — no separate DestroyImmediate needed for those.
                if (previewOpen) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        // Blocking PUT (Editor tooling only): RunImport's loop is synchronous, so we spin on
        // isDone rather than pulling async/await or a coroutine into the rest of the flow.
        // `timeout` bounds the spin: a stalled (not merely refused) server would otherwise hang
        // the Editor UI thread indefinitely with no way to recover short of force-killing Unity.
        static void UploadBuildingThumbnail(long osmId, byte[] jpgBytes)
        {
            AuthoringServerClient.Put($"/buildings/{osmId}/thumb", jpgBytes, "image/jpeg",
                                      $"Building {osmId}: thumbnail upload");
        }

        // ------------------------------------------------------- facade backdrops (#407)

        // Best-effort: render a straight-on elevation of every facade this building's
        // sidecar carries edge geometry for (front/back/left/right) and PUT each to the
        // canvas backdrop endpoint. Unlike the whole-building thumbnail above (#369, a 3/4
        // angle over the flat-colour mass), this is an orthographic per-wall capture, so it
        // can serve as a real drawing reference on every facade instead of only front/street
        // (the restriction #392 had to add for lack of exactly this). Any failure (server
        // down, render hiccup) is logged and swallowed per-facade — same reasoning as
        // RenderAndUploadBuildingThumbnail: a missing backdrop is the "returns nil
        // gracefully" fallback the client already handles.
        // `timings` splits render from the blocking upload PUT, accumulated across all
        // four facades — same server-up/down separation as the thumbnail path (#446).
        static void RenderAndUploadFacadeBackdrops(long osmId, Mesh mesh, BuildingFactsJson facts, ImportTimings timings)
        {
            var sw = new Stopwatch();
            void TryOne(Facade facade, StreetFacadeJson f)
            {
                if (f == null) return;
                try
                {
                    sw.Restart();
                    byte[] jpg = RenderFacadeBackdrop(mesh, osmId, facts, f);
                    timings.backdropRenderMs += sw.Elapsed.TotalMilliseconds;
                    if (jpg == null) return;
                    sw.Restart();
                    UploadFacadeBackdrop(osmId, facade, jpg);
                    timings.backdropUploadMs += sw.Elapsed.TotalMilliseconds;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SFMapImporter] Building {osmId} facade {facade}: " +
                                     $"backdrop render/upload failed: {e.Message}");
                }
            }

            TryOne(Facade.Front, BuildingAssembler.PrimaryFacade(facts));
            TryOne(Facade.Back,  facts.back_facade);
            TryOne(Facade.Left,  facts.left_facade);
            TryOne(Facade.Right, facts.right_facade);
        }

        // Isolated-preview-scene capture (same reasoning as RenderBuildingThumbnail: a plain
        // cullingMask can't stop the camera seeing other chunks' geometry at real world
        // coordinates), but framed orthographically and dead-on to the wall instead of a 3/4
        // perspective — a true elevation, which is the whole point of #407 over reusing the
        // thumbnail. The render texture's aspect matches the wall's real width:height so the
        // building's actual proportions come through instead of being squashed to a square.
        static byte[] RenderFacadeBackdrop(Mesh mesh, long osmId, BuildingFactsJson facts, StreetFacadeJson f)
        {
            if (f.edge == null || f.edge.Length < 4) return null;
            Vector3 a = new Vector3(f.edge[0], facts.base_y, f.edge[1]);
            Vector3 b = new Vector3(f.edge[2], facts.base_y, f.edge[3]);
            float wallLen = Vector3.Distance(a, b);
            float wallHeight = Mathf.Max(facts.facade_height_m, 1f);
            if (wallLen < 1e-3f) return null;

            var previewScene   = default(Scene);
            bool previewOpen   = false;
            Material   mat     = null;
            RenderTexture rt   = null;
            Texture2D  tex     = null;
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                previewOpen  = true;

                var meshGo = new GameObject($"BackdropMesh_{osmId}") { hideFlags = HideFlags.HideAndDontSave };
                meshGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                mat = new Material(Shader.Find("Unlit/Color"))
                {
                    color = BuildingPalette[(int)(Math.Abs(osmId) % BuildingPalette.Length)],
                };
                meshGo.AddComponent<MeshRenderer>().sharedMaterial = mat;
                SceneManager.MoveGameObjectToScene(meshGo, previewScene);

                var camGo = new GameObject($"BackdropCam_{osmId}") { hideFlags = HideFlags.HideAndDontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.scene            = previewScene; // renders ONLY this preview scene's contents
                cam.clearFlags       = CameraClearFlags.SolidColor;
                cam.backgroundColor  = new Color(0.82f, 0.82f, 0.82f, 1f);
                cam.orthographic     = true;
                cam.orthographicSize = wallHeight * 0.5f; // Unity's orthographicSize is half-height
                cam.nearClipPlane    = 0.05f;
                SceneManager.MoveGameObjectToScene(camGo, previewScene);

                // Dead-on to the wall: position along its outward normal (straight from the
                // sidecar bearing, same convention BuildingAssembler.PlacePart uses), level
                // (Vector3.up), centred on the wall both along its width and its height.
                Vector3 center  = (a + b) * 0.5f + Vector3.up * (wallHeight * 0.5f);
                Vector3 outward = FacadeFrame.OutwardNormal(f.bearing_deg);
                float distance  = Mathf.Max(wallLen, wallHeight) + 5f;
                camGo.transform.position = center + outward * distance;
                camGo.transform.LookAt(center, Vector3.up);
                cam.farClipPlane = distance + 5f;

                int height = FacadeBackdropHeight;
                int width  = Mathf.Clamp(Mathf.RoundToInt(height * (wallLen / wallHeight)),
                                          FacadeBackdropMinWidth, FacadeBackdropMaxWidth);
                cam.aspect = (float)width / height;

                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                return ImageConversion.EncodeToJPG(tex, 85);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (tex != null) DestroyImmediate(tex);
                if (rt != null)
                {
                    rt.Release();
                    DestroyImmediate(rt);
                }
                if (mat != null) DestroyImmediate(mat);
                if (previewOpen) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        // Blocking PUT, same pattern as UploadBuildingThumbnail. `facade` stringifies to
        // "Front"/"Back"/"Left"/"Right" — the exact facade key convention the canvas
        // endpoint and its tests already use (server/tests/test_canvas.py).
        static void UploadFacadeBackdrop(long osmId, Facade facade, byte[] jpgBytes)
        {
            AuthoringServerClient.Put($"/canvas/{osmId}/{facade}/backdrop", jpgBytes, "image/jpeg",
                                      $"Building {osmId}: facade {facade} backdrop upload");
        }

        static bool AllZero(Vector3[] vecs)
        {
            foreach (var v in vecs)
                if (v.sqrMagnitude > 1e-8f) return false;
            return true;
        }

        static GameObject CreateChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static GameObject PlaceMesh(Mesh mesh, GameObject parent, Material mat)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return go;
        }

        // "Clear Generated Assets" means forget this preset entirely, so it deletes BOTH roots an
        // import writes into (#494). Clearing only Assets/Generated/<preset>/ left the chunk prefabs
        // and — critically — ChunkManifest.asset, which holds the incremental fingerprints (#261),
        // under Assets/Resources/Generated/<preset>/. Every fingerprint then still matched, every
        // chunk was skipped, and the next import logged success over a preset whose prefabs pointed
        // at meshes that no longer existed. The fingerprints must go with the assets they describe.
        void ClearGenerated()
        {
            GeneratedAssets.ActivePreset = presetName;

            int cleared = 0;
            foreach (string dir in GeneratedAssets.PresetRoots())
            {
                if (!AssetDatabase.IsValidFolder(dir)) continue;
                if (AssetDatabase.DeleteAsset(dir))
                {
                    cleared++;
                    Debug.Log($"[SFMapImporter] Cleared {dir}");
                }
                else
                {
                    // Half a clear is the #494 failure mode, so never let it pass quietly.
                    Debug.LogError($"[SFMapImporter] Failed to clear {dir} — delete it by hand before " +
                                   $"re-importing '{presetName}', or the import will reuse stale chunks.");
                }
            }

            if (cleared > 0) AssetDatabase.Refresh();
            else Debug.Log($"[SFMapImporter] Nothing to clear for preset '{presetName}'.");
        }
    }
}
