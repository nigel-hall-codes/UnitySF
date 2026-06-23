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

            if (m.chunks == null || m.chunks.Length == 0)
                Debug.LogWarning($"[PresetBrowser] Manifest for \"{m.preset}\" has no chunks.");

            // Persist the preset choice in the ChunkStreamer so reopening Unity streams the right set.
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            if (streamer != null)
            {
                streamer.preset = m.preset;
                EditorUtility.SetDirty(streamer);
            }
            else
            {
                Debug.LogWarning($"[PresetBrowser] No ChunkStreamer found in scene — preset field not updated.");
            }

            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[PresetBrowser] Switched to preset \"{m.preset}\".");
        }
    }
}
