// Menu: Prometeo / Create Car
// Instantiates the Prometheus prefab, wires a follow camera, and sets the car up to
// drop onto a road that streams in at runtime (via ChunkStreamer + CarRoadSpawner) —
// no full static map import required.
// Also registers Xbox trigger axes in the InputManager (Gas / Brake).
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SFMap.Pipeline;

public static class PrometeoCarSetup
{
    const string PrefabPath = "Assets/PROMETEO - Car Controller/Prefabs/Prometheus.prefab";
    const string Preset     = "default";

    [MenuItem("Prometeo/Create Car")]
    static void Create()
    {
        RegisterTriggerAxes();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[Prometeo] Could not find prefab at {PrefabPath}");
            return;
        }

        var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        car.transform.position = Vector3.zero; // CarRoadSpawner repositions onto a road on Play.

        var ctrl = car.GetComponent<PrometeoCarController>();
        if (ctrl != null) ctrl.useGamepad = true;

        SetupRoadStreaming(car);
        AttachCamera(car);

        Selection.activeGameObject = car;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[Prometeo] Car created. On Play it streams the map and drops onto a road — no static import needed. " +
                  "Xbox: right trigger = throttle, left trigger = brake, left stick = steer, A = handbrake.");
    }

    [MenuItem("Prometeo/Add Road Streaming To Car")]
    static void AddRoadStreaming()
    {
        var car = Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<PrometeoCarController>() != null
            ? Selection.activeGameObject
            : Object.FindFirstObjectByType<PrometeoCarController>()?.gameObject;

        if (car == null)
        {
            Debug.LogWarning("[Prometeo] No Prometeo car in the scene. Use Prometeo > Create Car first.");
            return;
        }

        SetupRoadStreaming(car);
        Selection.activeGameObject = car;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[Prometeo] Road streaming set up. On Play the car drops onto a streamed road.");
    }

    // Adds the runtime spawner to the car and ensures a ChunkStreamer exists that
    // streams chunks around the car (so the chunk under the spawn anchor loads).
    static void SetupRoadStreaming(GameObject car)
    {
        var spawner = car.GetComponent<CarRoadSpawner>();
        if (spawner == null) spawner = car.AddComponent<CarRoadSpawner>();
        spawner.spawnAnchor = Vector3.zero;

        var streamer = Object.FindFirstObjectByType<ChunkStreamer>();
        if (streamer == null)
        {
            var go = new GameObject("ChunkStreamer");
            streamer = go.AddComponent<ChunkStreamer>();
            streamer.preset = Preset;
        }
        // Chunks are parented to the streamer with worldPositionStays:false, so it must
        // sit at the origin or every streamed chunk is offset by its transform.
        streamer.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        streamer.target = car.transform; // stream around the car so its chunk loads
    }

    static void AttachCamera(GameObject car)
    {
        var existing = Object.FindFirstObjectByType<CameraFollow>();
        if (existing != null && existing.carTransform == car.transform)
            return; // already wired to this car

        var camGo = new GameObject("PrometeoCamera");
        camGo.AddComponent<Camera>();
        camGo.AddComponent<AudioListener>();
        camGo.transform.position = car.transform.position + new Vector3(0, 3f, -7f);
        var follow          = camGo.AddComponent<CameraFollow>();
        follow.carTransform = car.transform;
    }

    // Registers "Gas" (right trigger, Joystick Axis 10) and "Brake" (left trigger, Joystick Axis 9).
    // Safe to call repeatedly — skips axes that already exist.
    static void RegisterTriggerAxes()
    {
        var path = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "..", "ProjectSettings", "InputManager.asset"));
        var text = System.IO.File.ReadAllText(path).Replace("\r\n", "\n");

        var toAdd = new System.Text.StringBuilder();

        void EnsureJoy(string name, int axisIndex)
        {
            if (text.Contains($"m_Name: {name}") && text.Contains($"    axis: {axisIndex}\n"))
                return;
            toAdd.Append(JoyAxisYaml(name, axisIndex));
            Debug.Log($"[Prometeo] Registered joystick axis '{name}' (axis {axisIndex})");
        }

        EnsureJoy("Gas",   9); // Joystick Axis 10 = right trigger
        EnsureJoy("Brake", 8); // Joystick Axis 9  = left trigger

        if (toAdd.Length == 0) return;

        const string anchor = "  m_UsePhysicalKeys:";
        int insertAt = text.IndexOf(anchor);
        if (insertAt < 0) { Debug.LogError("[Prometeo] Could not find InputManager anchor."); return; }

        text = text.Insert(insertAt, toAdd.ToString());
        System.IO.File.WriteAllText(path, text);
        AssetDatabase.Refresh();
    }

    static string JoyAxisYaml(string name, int axisIndex) =>
        string.Join("\n", new[] {
            "  - serializedVersion: 3",
            $"    m_Name: {name}",
            "    descriptiveName: ",
            "    descriptiveNegativeName: ",
            "    negativeButton: ",
            "    positiveButton: ",
            "    altNegativeButton: ",
            "    altPositiveButton: ",
            "    gravity: 3",
            "    dead: 0.19",
            "    sensitivity: 1",
            "    snap: 0",
            "    invert: 0",
            "    type: 2",
            $"    axis: {axisIndex}",
            "    joyNum: 0",
            "",
        });
}
#endif
