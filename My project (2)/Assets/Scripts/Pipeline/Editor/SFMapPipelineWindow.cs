using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SFMap.Pipeline.Editor
{
    public class SFMapPipelineWindow : EditorWindow
    {
        static string OsmPath => Path.Combine(Application.dataPath, "SFMapData", "map.osm");
        static string CsvPath => Path.Combine(Application.dataPath, "SFMapData", "Elevation_Contours_20260619.csv");

        [SerializeField] float roadWidthMultiplier   = 1f;
        [SerializeField] float defaultBuildingHeight = 10f;
        [SerializeField] int   heightmapResolution   = 513;

        [MenuItem("Window/SF Map Pipeline")]
        public static void Open() => GetWindow<SFMapPipelineWindow>("SF Map Pipeline");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);
            roadWidthMultiplier   = EditorGUILayout.FloatField("Road Width Multiplier",       roadWidthMultiplier);
            defaultBuildingHeight = EditorGUILayout.FloatField("Default Building Height (m)", defaultBuildingHeight);
            heightmapResolution   = EditorGUILayout.IntField(  "Heightmap Resolution",        heightmapResolution);

            EditorGUILayout.Space();

            if (GUILayout.Button("Generate Map"))
                RunGenerate();

            if (GUILayout.Button("Clear Generated Assets"))
                ClearGenerated();
        }

        void RunGenerate()
        {
            if (GameObject.Find("SF Map") != null || GameObject.Find("Buildings") != null)
            {
                if (!EditorUtility.DisplayDialog("SF Map Pipeline",
                    "This will replace existing 'SF Map' and 'Buildings' scene objects. Continue?",
                    "Regenerate", "Cancel"))
                    return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                ClearSceneObjects();

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Parsing OSM…", 0.00f);
                var graph = OsmParser.Parse(OsmPath);

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Parsing elevation…", 0.15f);
                var heightmap = ElevationParser.Parse(CsvPath, graph.SourceBounds, heightmapResolution);

                var worldRect = GeoProjection.WorldBounds(graph.SourceBounds);
                var coord     = new ChunkCoord(0, 0);
                var chunk     = new ChunkBounds { Coord = coord, WorldRect = worldRect };

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Computing road setbacks…", 0.30f);
                var setbacks = IntersectionMeshGenerator.ComputeSetbacks(graph);

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Generating road meshes…", 0.42f);
                var roadMeshes = RoadMeshGenerator.Generate(graph, heightmap, worldRect, coord, setbacks, roadWidthMultiplier);

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Generating intersection meshes…", 0.55f);
                var intersectionMeshes = IntersectionMeshGenerator.Generate(graph, heightmap, worldRect, coord);

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Generating sidewalk meshes…", 0.67f);
                var sidewalkMeshes = SidewalkMeshGenerator.Generate(graph, heightmap, worldRect, coord);

                // Terrain is generated after all road/intersection stamps have been written back
                // into heightmap.Values, so the Unity terrain asset reflects the flattened footprints.
                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Generating terrain…", 0.78f);
                var terrainData = TerrainGenerator.Generate(heightmap, chunk, coord);

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Generating buildings…", 0.87f);
                var buildingsRoot = BuildingGenerator.Generate(graph.Buildings, heightmap, chunk, coord, defaultBuildingHeight);

                EditorUtility.DisplayProgressBar("SF Map Pipeline", "Building scene…", 0.95f);
                int objCount = PopulateScene(terrainData, heightmap, worldRect,
                    roadMeshes, intersectionMeshes, sidewalkMeshes, buildingsRoot);

                sw.Stop();
                Debug.Log($"[SFMapPipeline] Generated in {sw.Elapsed.TotalSeconds:F1}s — " +
                          $"roads:{roadMeshes.Count} intersections:{intersectionMeshes.Count} " +
                          $"sidewalks:{sidewalkMeshes.Count} buildings:{buildingsRoot.transform.childCount} " +
                          $"scene objects:{objCount}");
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

        int PopulateScene(
            TerrainData terrainData,
            HeightmapData heightmap,
            Rect worldRect,
            IReadOnlyList<Mesh> roadMeshes,
            IReadOnlyList<Mesh> intersectionMeshes,
            IReadOnlyList<Mesh> sidewalkMeshes,
            GameObject buildingsRoot)
        {
            var root  = new GameObject("SF Map");
            int count = 0;

            // Terrain
            var terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "Terrain";
            terrainGo.transform.SetParent(root.transform, false);
            terrainGo.transform.position = new Vector3(worldRect.x, heightmap.MinElevationMeters, worldRect.y);
            count++;

            // Roads and intersections share the road surface material
            var roadMat = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.RoadMaterial());

            int roadLayer = LayerMask.NameToLayer("Road");
            var roadParent = CreateChild(root, "Roads");
            foreach (var mesh in roadMeshes)
            {
                var go = PlaceMesh(mesh, roadParent, roadMat);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.layer = roadLayer;
                count++;
            }

            var intParent = CreateChild(root, "Intersections");
            foreach (var mesh in intersectionMeshes)
            {
                PlaceMesh(mesh, intParent, roadMat);
                count++;
            }

            var swMat    = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.SidewalkMaterial());
            var swParent = CreateChild(root, "Sidewalks");
            foreach (var mesh in sidewalkMeshes)
            {
                PlaceMesh(mesh, swParent, swMat);
                count++;
            }

            buildingsRoot.transform.SetParent(root.transform, false);
            count += buildingsRoot.transform.childCount;

            return count;
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

        void ClearGenerated()
        {
            ClearSceneObjects();

            if (AssetDatabase.IsValidFolder(GeneratedAssets.Root))
            {
                AssetDatabase.DeleteAsset(GeneratedAssets.Root);
                AssetDatabase.Refresh();
                Debug.Log("[SFMapPipeline] Cleared Assets/Generated/");
            }
        }

        static void ClearSceneObjects()
        {
            var sfMap = GameObject.Find("SF Map");
            if (sfMap != null) DestroyImmediate(sfMap);

            // Orphaned root in case a previous generate crashed before PopulateScene reparented it
            var orphan = GameObject.Find("Buildings");
            if (orphan != null) DestroyImmediate(orphan);
        }
    }
}
