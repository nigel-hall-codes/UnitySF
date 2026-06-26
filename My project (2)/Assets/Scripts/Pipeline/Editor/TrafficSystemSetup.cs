#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFMap.Pipeline.Editor
{
    /// <summary>
    /// Menu: SFMap ▸ Add Traffic System.
    /// Drops a configured <see cref="TrafficManager"/> into the scene and auto-wires the
    /// low-poly car prefabs so ambient traffic works without manual setup. The runtime
    /// <see cref="RoadNetwork"/> bootstraps itself, so this is the only wiring needed.
    /// </summary>
    public static class TrafficSystemSetup
    {
        const string CarPrefabFolder = "Assets/Awb-Free Low Poly Vehicles/Prefabs";

        [MenuItem("SFMap/Add Traffic System")]
        static void AddTrafficSystem()
        {
            var existing = Object.FindFirstObjectByType<TrafficManager>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[TrafficManager] Scene already has a traffic manager — selected it.");
                return;
            }

            var go = new GameObject("Traffic");
            var mgr = go.AddComponent<TrafficManager>();
            mgr.carPrefabs = LoadCarPrefabs();

            if (mgr.carPrefabs.Length == 0)
                Debug.LogWarning($"[TrafficManager] No car prefabs found under '{CarPrefabFolder}'. " +
                                 "Assign some in the Inspector.", go);
            else
                Debug.Log($"[TrafficManager] Added with {mgr.carPrefabs.Length} car prefab(s). Press Play " +
                          "with a ChunkStreamer in the scene to see traffic.", go);

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Add Traffic System");
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        static GameObject[] LoadCarPrefabs()
        {
            var list = new List<GameObject>();
            if (!AssetDatabase.IsValidFolder(CarPrefabFolder))
                return list.ToArray();

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { CarPrefabFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("Air Plane")) continue; // not road traffic
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) list.Add(prefab);
            }
            return list.ToArray();
        }
    }
}
#endif
