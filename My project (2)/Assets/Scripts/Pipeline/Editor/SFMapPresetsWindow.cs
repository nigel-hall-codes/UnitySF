using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SFMap.Pipeline.Editor
{
    public class SFMapPresetsWindow : EditorWindow
    {
        [Serializable]
        class ManifestChunk { public int col; public int row; public float worldX; public float worldZ; }

        [Serializable]
        class PresetManifest
        {
            public string preset;
            public string generated;
            public ManifestChunk[] chunks;
            public float minElevation;
        }

        List<PresetManifest> _presets = new List<PresetManifest>();
        Vector2 _scroll;

        [MenuItem("Window/SF Map Preset Browser")]
        public static void Open() => GetWindow<SFMapPresetsWindow>("Preset Browser");

        void OnEnable() => Refresh();

        void Refresh()
        {
            _presets.Clear();
            string generatedPath = Path.Combine(Application.dataPath, "Generated");
            if (!Directory.Exists(generatedPath)) return;

            foreach (var dir in Directory.GetDirectories(generatedPath))
            {
                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var m = JsonUtility.FromJson<PresetManifest>(File.ReadAllText(manifestPath));
                    if (m != null && !string.IsNullOrEmpty(m.preset))
                        _presets.Add(m);
                }
                catch { }
            }
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Saved Presets", EditorStyles.boldLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                Refresh();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            if (_presets.Count == 0)
            {
                EditorGUILayout.HelpBox("No presets found in Assets/Generated/.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var m in _presets)
            {
                EditorGUILayout.BeginHorizontal(GUI.skin.box);
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(m.preset, EditorStyles.boldLabel);
                int chunkCount = m.chunks?.Length ?? 0;
                EditorGUILayout.LabelField(
                    $"{m.generated}   {chunkCount} chunk{(chunkCount != 1 ? "s" : "")}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                if (GUILayout.Button("Load", GUILayout.Width(60), GUILayout.ExpandHeight(true)))
                    LoadPreset(m);
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(2);
            }
            EditorGUILayout.EndScrollView();
        }

        static void LoadPreset(PresetManifest m)
        {
            if (!EditorUtility.DisplayDialog("Load Preset",
                $"Load preset \"{m.preset}\"? Current scene objects will be replaced.",
                "Load", "Cancel"))
                return;

            GeneratedAssets.ActivePreset = m.preset;

            foreach (var n in new[] { "SF Map", "Buildings", "PlaceholderCar" })
            {
                var found = GameObject.Find(n);
                if (found != null) DestroyImmediate(found);
            }

            var root = new GameObject("SF Map");

            if (m.chunks == null || m.chunks.Length == 0)
            {
                Debug.LogWarning($"[PresetBrowser] Manifest for \"{m.preset}\" has no chunks.");
                EditorSceneManager.SaveOpenScenes();
                return;
            }

            var roadMat   = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.RoadMaterial());
            var swMat     = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.SidewalkMaterial());
            int roadLayer = LayerMask.NameToLayer("Road");

            foreach (var c in m.chunks)
            {
                var coord    = new ChunkCoord(c.col, c.row);
                string chunkDir = GeneratedAssets.ChunkDir(coord);

                var td = AssetDatabase.LoadAssetAtPath<TerrainData>(GeneratedAssets.TerrainAsset(coord));
                if (td != null)
                {
                    var tgo = Terrain.CreateTerrainGameObject(td);
                    tgo.name = $"Terrain {coord}";
                    tgo.transform.SetParent(root.transform, false);
                    tgo.transform.position = new Vector3(c.worldX, m.minElevation, c.worldZ);
                }

                var roadParent = CreateChild(root, $"Roads {coord}");
                foreach (var mesh in LoadMeshes(chunkDir + "/Roads"))
                {
                    var go = PlaceMesh(mesh, roadParent, roadMat);
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    go.layer = roadLayer;
                }

                var intParent = CreateChild(root, $"Intersections {coord}");
                foreach (var mesh in LoadMeshes(chunkDir + "/Intersections"))
                    PlaceMesh(mesh, intParent, roadMat);

                var swParent = CreateChild(root, $"Sidewalks {coord}");
                foreach (var mesh in LoadMeshes(chunkDir + "/Sidewalks"))
                    PlaceMesh(mesh, swParent, swMat);

                var bldParent = CreateChild(root, $"Buildings {coord}");
                foreach (var mesh in LoadMeshes(chunkDir + "/Buildings"))
                {
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(bldParent.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh  = mesh;
                    go.AddComponent<MeshRenderer>();
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
            }

            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[PresetBrowser] Loaded preset \"{m.preset}\".");
        }

        static IEnumerable<Mesh> LoadMeshes(string assetFolder)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { assetFolder }))
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                if (mesh != null) yield return mesh;
            }
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
            go.AddComponent<MeshFilter>().sharedMesh      = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }
    }
}
