using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SFMap.Pipeline.Editor
{
    public class SFMapPipelineWindow : EditorWindow
    {
        static string CsvPath => Path.Combine(Application.dataPath, "SFMapData", "Elevation_Contours_20260619.csv");

        [SerializeField] string osmFilePath          = "";
        [SerializeField] string presetName           = "default";
        [SerializeField] float roadWidthMultiplier   = 1f;
        [SerializeField] float defaultBuildingHeight = 10f;
        [SerializeField] int   heightmapResolution   = 513;
        [SerializeField] float chunkSizeMeters       = 2000f;
        [SerializeField] int   colMin                = 0;
        [SerializeField] int   colMax                = 0;
        [SerializeField] int   rowMin                = 0;
        [SerializeField] int   rowMax                = 0;

        [MenuItem("Window/SF Map Pipeline")]
        public static void Open() => GetWindow<SFMapPipelineWindow>("SF Map Pipeline");

        void OnEnable()
        {
            if (string.IsNullOrEmpty(osmFilePath))
                osmFilePath = Path.Combine(Application.dataPath, "SFMapData", "full_sf_map");
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Data Sources", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            osmFilePath = EditorGUILayout.TextField("OSM File", osmFilePath);
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
            {
                string dir    = Directory.Exists(Path.GetDirectoryName(osmFilePath))
                                    ? Path.GetDirectoryName(osmFilePath)
                                    : Path.Combine(Application.dataPath, "SFMapData");
                string picked = EditorUtility.OpenFilePanel("Select OSM File", dir, "osm");
                if (!string.IsNullOrEmpty(picked))
                    osmFilePath = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);
            presetName            = EditorGUILayout.TextField( "Preset Name",                 presetName);
            roadWidthMultiplier   = EditorGUILayout.FloatField("Road Width Multiplier",       roadWidthMultiplier);
            defaultBuildingHeight = EditorGUILayout.FloatField("Default Building Height (m)", defaultBuildingHeight);
            heightmapResolution   = EditorGUILayout.IntField(  "Heightmap Resolution",        heightmapResolution);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Chunk Range", EditorStyles.boldLabel);
            chunkSizeMeters = EditorGUILayout.FloatField("Chunk Size (m)", chunkSizeMeters);
            EditorGUILayout.LabelField("Chunk dimensions", $"{chunkSizeMeters:F0} m × {chunkSizeMeters:F0} m", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Col Range");
            colMin = EditorGUILayout.IntField(colMin);
            EditorGUILayout.LabelField("→", GUILayout.Width(20));
            colMax = EditorGUILayout.IntField(colMax);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Row Range");
            rowMin = EditorGUILayout.IntField(rowMin);
            EditorGUILayout.LabelField("→", GUILayout.Width(20));
            rowMax = EditorGUILayout.IntField(rowMax);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            if (GUILayout.Button("Generate Map"))
                RunGenerate();

            if (GUILayout.Button("Clear Generated Assets"))
                ClearGenerated();

            if (GUILayout.Button("Clear Elevation Cache"))
                ElevationParser.ClearCache(CsvPath, heightmapResolution);
        }

        void RunGenerate()
        {
            GeneratedAssets.ActivePreset = presetName;

            colMax = Mathf.Max(colMax, colMin);
            rowMax = Mathf.Max(rowMax, rowMin);
            int totalChunks = (colMax - colMin + 1) * (rowMax - rowMin + 1);

            if (GameObject.Find("SF Map") != null || GameObject.Find("Buildings") != null)
            {
                if (!EditorUtility.DisplayDialog("SF Map Pipeline",
                    "This will replace existing 'SF Map' and 'Buildings' scene objects. Continue?",
                    "Regenerate", "Cancel"))
                    return;
            }

            var sw      = Stopwatch.StartNew();
            var swStage = Stopwatch.StartNew();
            try
            {
                ClearSceneObjects();

                swStage.Restart();
                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Parsing OSM…", 0.00f);
                var fullGraph = OsmParser.Parse(osmFilePath);
                Debug.Log($"[SFMapPipeline] OSM parse: {swStage.Elapsed.TotalSeconds:F3}s");

                swStage.Restart();
                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Parsing elevation…", 0.03f);
                var fullHeightmap = ElevationParser.Parse(CsvPath, fullGraph.SourceBounds, heightmapResolution);
                Debug.Log($"[SFMapPipeline] Elevation parse: {swStage.Elapsed.TotalSeconds:F3}s");

                swStage.Restart();
                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Computing intersection polygons…", 0.06f);
                var polygons = IntersectionMeshGenerator.ComputePolygons(fullGraph);
                Debug.Log($"[SFMapPipeline] Intersection polygons: {swStage.Elapsed.TotalSeconds:F3}s");

                swStage.Restart();
                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Computing road boundaries…", 0.08f);
                var boundaries = IntersectionMeshGenerator.ComputeBoundaries(fullGraph, polygons);
                Debug.Log($"[SFMapPipeline] Road boundaries: {swStage.Elapsed.TotalSeconds:F3}s");

                EnsureResourcesFolder();

                var mapRoot        = new GameObject("SF Map");
                var chunkCoords    = new List<ChunkCoord>(totalChunks);
                var chunkWorldRects = new List<Rect>(totalChunks);
                bool vehiclePlaced = false;
                int  totalObjects  = 0;
                int  chunkIdx      = 0;

                for (int row = rowMin; row <= rowMax; row++)
                {
                    for (int col = colMin; col <= colMax; col++)
                    {
                        chunkIdx++;
                        var coord  = new ChunkCoord(col, row);
                        var chunk  = new ChunkBounds(coord, chunkSizeMeters);
                        float t0   = 0.10f + (chunkIdx - 1f) / totalChunks * 0.85f;
                        float span = 0.85f / totalChunks;
                        string lbl = $"chunk {chunkIdx}/{totalChunks} ({coord})";

                        var graph     = fullGraph.CropToChunk(chunk);
                        var heightmap = fullHeightmap.CropToChunk(chunk, heightmapResolution);

                        var swChunk = Stopwatch.StartNew();

                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Roads — {lbl}",         t0 + span * 0.00f);
                        var roadMeshes = RoadMeshGenerator.Generate(graph, heightmap, chunk.WorldRect, coord, boundaries, roadWidthMultiplier);
                        Debug.Log($"[SFMapPipeline] {coord} roads: {swStage.Elapsed.TotalSeconds:F3}s");

                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Intersections — {lbl}", t0 + span * 0.20f);
                        var intersectionMeshes = IntersectionMeshGenerator.Generate(graph, polygons, heightmap, chunk.WorldRect, coord);
                        Debug.Log($"[SFMapPipeline] {coord} intersections: {swStage.Elapsed.TotalSeconds:F3}s");

                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Sidewalks — {lbl}",     t0 + span * 0.35f);
                        var sidewalkMeshes = SidewalkMeshGenerator.Generate(graph, heightmap, chunk.WorldRect, coord, boundaries);
                        Debug.Log($"[SFMapPipeline] {coord} sidewalks: {swStage.Elapsed.TotalSeconds:F3}s");

                        // Terrain after road stamps so the asset reflects flattened footprints.
                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Terrain — {lbl}",       t0 + span * 0.55f);
                        var terrainData = TerrainGenerator.Generate(heightmap, chunk, coord);
                        Debug.Log($"[SFMapPipeline] {coord} terrain: {swStage.Elapsed.TotalSeconds:F3}s");

                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Buildings — {lbl}",     t0 + span * 0.75f);
                        var buildingsRoot = BuildingGenerator.Generate(graph.Buildings, heightmap, chunk, coord, defaultBuildingHeight);
                        Debug.Log($"[SFMapPipeline] {coord} buildings: {swStage.Elapsed.TotalSeconds:F3}s");

                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Scene — {lbl}",         t0 + span * 0.88f);
                        var (count, chunkRoot) = PopulateChunk(mapRoot, coord, graph, terrainData, heightmap, chunk.WorldRect,
                            roadMeshes, intersectionMeshes, sidewalkMeshes, buildingsRoot, ref vehiclePlaced);
                        totalObjects += count;
                        Debug.Log($"[SFMapPipeline] {coord} scene: {swStage.Elapsed.TotalSeconds:F3}s");

                        swStage.Restart();
                        EditorUtility.DisplayProgressBar("SF Map Pipeline", $"Prefab — {lbl}",        t0 + span * 0.96f);
                        PrefabUtility.SaveAsPrefabAsset(chunkRoot, GeneratedAssets.ChunkPrefabPath(coord));
                        Debug.Log($"[SFMapPipeline] {coord} prefab save: {swStage.Elapsed.TotalSeconds:F3}s");

                        Debug.Log($"[SFMapPipeline] {coord} total: {swChunk.Elapsed.TotalSeconds:F3}s");

                        chunkCoords.Add(coord);
                        chunkWorldRects.Add(chunk.WorldRect);
                    }
                }

                EditorSceneManager.SaveOpenScenes();
                WriteManifest(fullGraph.SourceBounds, chunkSizeMeters, chunkCoords, chunkWorldRects, fullHeightmap.MinElevationMeters);
                SaveChunkManifest(chunkSizeMeters, fullHeightmap.MinElevationMeters, chunkCoords, chunkWorldRects);

                sw.Stop();
                Debug.Log($"[SFMapPipeline] Generated {totalChunks} chunk(s) in {sw.Elapsed.TotalSeconds:F1}s — scene objects:{totalObjects}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SFMapPipeline] Generation failed: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        (int count, GameObject chunkRoot) PopulateChunk(
            GameObject mapRoot,
            ChunkCoord coord,
            StreetGraph graph,
            TerrainData terrainData,
            HeightmapData heightmap,
            Rect worldRect,
            IReadOnlyList<Mesh> roadMeshes,
            IReadOnlyList<Mesh> intersectionMeshes,
            IReadOnlyList<Mesh> sidewalkMeshes,
            GameObject buildingsRoot,
            ref bool vehiclePlaced)
        {
            int count = 0;

            var chunkRoot = new GameObject(coord.ToString());
            chunkRoot.transform.SetParent(mapRoot.transform, false);

            var terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = $"Terrain {coord}";
            terrainGo.transform.SetParent(chunkRoot.transform, false);
            terrainGo.transform.position = new Vector3(worldRect.x, heightmap.MinElevationMeters, worldRect.y);
            count++;

            var roadMat   = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.RoadMaterial());
            int roadLayer = LayerMask.NameToLayer("Road");
            var roadParent = CreateChild(chunkRoot, $"Roads {coord}");
            foreach (var mesh in roadMeshes)
            {
                var go = PlaceMesh(mesh, roadParent, roadMat);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.layer = roadLayer;
                count++;
            }

            var intParent = CreateChild(chunkRoot, $"Intersections {coord}");
            foreach (var mesh in intersectionMeshes)
            {
                PlaceMesh(mesh, intParent, roadMat);
                count++;
            }

            var swMat    = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.SidewalkMaterial());
            var swParent = CreateChild(chunkRoot, $"Sidewalks {coord}");
            foreach (var mesh in sidewalkMeshes)
            {
                PlaceMesh(mesh, swParent, swMat);
                count++;
            }

            buildingsRoot.name = $"Buildings {coord}";
            buildingsRoot.transform.SetParent(chunkRoot.transform, false);
            count += buildingsRoot.transform.childCount;

            if (!vehiclePlaced)
            {
                PlaceVehicle(graph, worldRect);
                vehiclePlaced = true;
            }

            return (count, chunkRoot);
        }

        static void PlaceVehicle(StreetGraph graph, Rect worldRect)
        {
            var edge = PickSpawnEdge(graph, worldRect);
            if (edge == null)
            {
                Debug.LogWarning("[SFMapPipeline] No residential spawn edge found — vehicle not placed.");
                return;
            }

            // Wheel radius is the half-height of the pivot above the road surface.
            const float wheelRadius  = 0.35f;
            const float spawnOffset  = 0.1f;
            var spawnPos             = edge.Centerline[0];
            spawnPos.y              += wheelRadius + spawnOffset;

            var forward = edge.Centerline[1] - edge.Centerline[0];
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            var spawnRot = Quaternion.LookRotation(forward.normalized, Vector3.up);

            PlaceholderCarSetup.CreateAt(spawnPos, spawnRot);
        }

        static StreetEdge PickSpawnEdge(StreetGraph graph, Rect worldRect)
        {
            var center = new Vector3(
                worldRect.x + worldRect.width  * 0.5f,
                0f,
                worldRect.y + worldRect.height * 0.5f);

            StreetEdge best     = null;
            float      bestDist = float.MaxValue;

            foreach (var edge in graph.Edges)
            {
                if (edge.Type != HighwayType.Residential) continue;
                if (edge.Centerline == null || edge.Centerline.Length < 2) continue;

                var p    = edge.Centerline[0];
                var dx   = p.x - center.x;
                var dz   = p.z - center.z;
                float d2 = dx * dx + dz * dz;

                if (d2 < bestDist) { bestDist = d2; best = edge; }
            }

            return best;
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
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static void EnsureResourcesFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder("Assets/Resources/Generated"))
                AssetDatabase.CreateFolder("Assets/Resources", "Generated");
            string presetFolder = $"Assets/Resources/Generated/{GeneratedAssets.ActivePreset}";
            if (!AssetDatabase.IsValidFolder(presetFolder))
                AssetDatabase.CreateFolder("Assets/Resources/Generated", GeneratedAssets.ActivePreset);
        }

        void SaveChunkManifest(float chunkSize, float minElevation, List<ChunkCoord> coords, List<Rect> worldRects)
        {
            var manifest = ScriptableObject.CreateInstance<ChunkManifest>();
            manifest.preset          = presetName;
            manifest.chunkSizeMeters = chunkSize;
            manifest.minElevation    = minElevation;
            manifest.chunks          = new ChunkManifestEntry[coords.Count];
            for (int i = 0; i < coords.Count; i++)
            {
                manifest.chunks[i] = new ChunkManifestEntry
                {
                    col    = coords[i].Col,
                    row    = coords[i].Row,
                    worldX = worldRects[i].x,
                    worldZ = worldRects[i].y,
                };
            }

            string path = GeneratedAssets.ChunkManifestPath();
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(manifest, path);
            AssetDatabase.SaveAssets();
        }

        void WriteManifest(OsmBounds bounds, float chunkSize, List<ChunkCoord> chunks, List<Rect> worldRects, float minElevation)
        {
            string dir = Path.Combine(Application.dataPath, "Generated", presetName);
            Directory.CreateDirectory(dir);

            var parts = new List<string>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                var r = worldRects[i];
                parts.Add($"{{\"col\": {c.Col}, \"row\": {c.Row}, \"worldX\": {r.x:F3}, \"worldZ\": {r.y:F3}}}");
            }
            string chunkArr = string.Join(", ", parts);

            string json = "{\n" +
                $"  \"preset\": \"{presetName}\",\n" +
                $"  \"generated\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\",\n" +
                $"  \"chunkSize\": {(int)Mathf.Round(chunkSize)},\n" +
                $"  \"chunks\": [{chunkArr}],\n" +
                $"  \"osmFile\": \"{Path.GetFileName(osmFilePath)}\",\n" +
                $"  \"osmBounds\": {{ \"minLat\": {bounds.MinLat:F6}, \"maxLat\": {bounds.MaxLat:F6}, \"minLon\": {bounds.MinLon:F6}, \"maxLon\": {bounds.MaxLon:F6} }},\n" +
                $"  \"roadWidthMultiplier\": {roadWidthMultiplier:F1},\n" +
                $"  \"defaultBuildingHeight\": {defaultBuildingHeight:F1},\n" +
                $"  \"heightmapResolution\": {heightmapResolution},\n" +
                $"  \"minElevation\": {minElevation:F3}\n" +
                "}";
            File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
            AssetDatabase.ImportAsset(GeneratedAssets.ManifestPath());
        }

        void ClearGenerated()
        {
            GeneratedAssets.ActivePreset = presetName;
            ClearSceneObjects();

            string presetDir = GeneratedAssets.Root;
            if (AssetDatabase.IsValidFolder(presetDir))
            {
                AssetDatabase.DeleteAsset(presetDir);
                AssetDatabase.Refresh();
                Debug.Log($"[SFMapPipeline] Cleared {presetDir}");
            }
        }

        static void ClearSceneObjects()
        {
            var sfMap = GameObject.Find("SF Map");
            if (sfMap != null) DestroyImmediate(sfMap);

            // Orphaned root in case a previous generate crashed before PopulateScene reparented it
            var orphan = GameObject.Find("Buildings");
            if (orphan != null) DestroyImmediate(orphan);

            var car = GameObject.Find("PlaceholderCar");
            if (car != null) DestroyImmediate(car);
        }
    }
}
