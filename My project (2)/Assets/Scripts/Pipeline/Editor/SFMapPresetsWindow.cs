using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SFMap.Pipeline.Editor
{
    public class SFMapPresetsWindow : EditorWindow
    {
        // The manifest shape is SFMap.Pipeline.PresetManifestJson, shared with the importer that
        // writes these files. This window used to declare its own private copy, which is how the
        // importer came to never write one at all without anything noticing (#469).
        List<PresetManifestJson> _presets = new List<PresetManifestJson>();
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
                    var m = JsonUtility.FromJson<PresetManifestJson>(File.ReadAllText(manifestPath));
                    if (m == null || string.IsNullOrEmpty(m.preset)) continue;

                    // The preset name drives every asset path once loaded, so a manifest that
                    // disagrees with the folder it sits in would resolve assets somewhere else
                    // entirely. The importer warns when it writes one (#469); warn again here,
                    // because these files can also be hand-edited or copied between folders — and
                    // take the folder name as authoritative, since that is where the assets are.
                    string dirName  = new DirectoryInfo(dir).Name;
                    string mismatch = PresetManifests.PresetNameMismatchWarning(dirName, m.preset);
                    if (mismatch != null)
                    {
                        Debug.LogWarning($"[PresetBrowser] {mismatch}");
                        m.preset = dirName;
                    }

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

        static void LoadPreset(PresetManifestJson m)
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
