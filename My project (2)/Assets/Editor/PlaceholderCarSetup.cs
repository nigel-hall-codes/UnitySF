// Menu: RVP / Create Placeholder Car
// Creates a minimal driveable RVP car above the scene origin.
// Requires Unity 2022.3+ and the RVP source in Assets/ThirdParty/RandomationVP.
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using RVP;

public static class PlaceholderCarSetup
{
    [MenuItem("RVP/Create Placeholder Car")]
    static void Create()
    {
        EnsureLayer("Ignore Wheel Cast");
        EnsureLayer("Detachable Part");
        EnsureTag("Underside");
        EnsureTag("Pop Tire");
        EnsureInputButton("e");
        EnsureInputButton("q");

        CreateGlobalControl();

        var car = BuildCarHierarchy();
        CreateCamera(car);

        Selection.activeGameObject = car;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[RVP] Placeholder car created at (0, 5, 0). Hit Play to test.");
    }

    static void CreateCamera(GameObject car)
    {
        var camGo = new GameObject("Camera");
        camGo.AddComponent<Camera>();
        camGo.AddComponent<AudioListener>();
        var cc          = camGo.AddComponent<CameraControl>();
        cc.target       = car.transform;
        cc.height       = 3f;
        cc.distance     = 7f;
        cc.stayFlat     = true;
        cc.castMask     = LayerMask.GetMask("Default", "Terrain");
        camGo.AddComponent<BasicCameraInput>();
    }

    // -------------------------------------------------------------------------

    static void CreateGlobalControl()
    {
        if (Object.FindObjectOfType<GlobalControl>() != null)
            return;

        var go = new GameObject("GlobalControl");
        var gc = go.AddComponent<GlobalControl>();
        gc.wheelCastMask    = LayerMask.GetMask("Default", "Terrain");
        gc.groundMask        = LayerMask.GetMask("Default", "Terrain");
        gc.tireMarkLength    = 32;
        gc.tireMarkGap       = 0.2f;
        gc.tireMarkHeight    = 0.02f;
        gc.tireFadeTime      = 10f;
        gc.quickRestart      = false; // avoids requiring a "Restart" input axis

        var frictionless = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(
            "Assets/ThirdParty/RandomationVP/PhysicMaterials/Frictionless.physicMaterial");
        if (frictionless != null)
            gc.frictionlessMat = frictionless;
    }

    static GameObject BuildCarHierarchy()
    {
        // Root ----------------------------------------------------------------
        var car = new GameObject("PlaceholderCar");
        car.transform.position = new Vector3(0, 5, 0);

        var rb     = car.AddComponent<Rigidbody>();
        rb.mass    = 1200f;
        rb.centerOfMass = new Vector3(0, -0.3f, 0);

        // BoxCollider on root represents the chassis for ground collision
        var chassis       = car.AddComponent<BoxCollider>();
        chassis.center    = new Vector3(0, 0.1f, 0);
        chassis.size      = new Vector3(1.6f, 0.6f, 4.0f);

        var vp = car.AddComponent<VehicleParent>();

        // Normal orientation object -------------------------------------------
        var normGo       = new GameObject("NormalOrientation");
        normGo.transform.SetParent(car.transform, false);
        vp.norm          = normGo.transform;

        // Visual body (box mesh, no collider — chassis BoxCollider covers it) --
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(car.transform, false);
        body.transform.localPosition = new Vector3(0, 0.1f, 0);
        body.transform.localScale    = new Vector3(1.6f, 0.6f, 4.0f);
        Object.DestroyImmediate(body.GetComponent<BoxCollider>());

        // Drivetrain ----------------------------------------------------------
        // GetTopmostParentComponent only walks *parent* transforms, so the motor
        // and transmission must be on a child of the VehicleParent root.
        var drivetrainGo = new GameObject("Drivetrain");
        drivetrainGo.transform.SetParent(car.transform, false);

        var trans            = drivetrainGo.AddComponent<GearboxTransmission>();
        trans.gears          = DefaultGears();
        trans.startGear      = 2;   // index 2 = 1st gear
        trans.skipNeutral    = true;
        trans.shiftDelay     = 10f;
        trans.shiftThreshold = 2f;
        trans.autoCalculateRpmRanges = true;

        var motor              = drivetrainGo.AddComponent<GasMotor>();
        motor.transmission     = trans;
        motor.inertia          = 0.5f;
        motor.canReverse       = true;
        motor.driveDividePower = 3f;

        // Input — also uses GetTopmostParentComponent, so put on a child too.
        var inputGo           = new GameObject("Input");
        inputGo.transform.SetParent(car.transform, false);
        var input             = inputGo.AddComponent<BasicInput>();
        input.accelAxis       = "Vertical";
        input.brakeAxis       = "Vertical";
        input.steerAxis       = "Horizontal";
        input.ebrakeAxis      = "Jump";      // spacebar
        input.upshiftButton   = "e";
        input.downshiftButton = "q";

        // Wheels --------------------------------------------------------------
        float wx = 0.85f;
        float wz = 1.5f;
        var suspFL = CreateSuspension(car, "Suspension_FL", new Vector3(-wx,  0,  wz), steer: true);
        var suspFR = CreateSuspension(car, "Suspension_FR", new Vector3( wx,  0,  wz), steer: true);
        var suspRL = CreateSuspension(car, "Suspension_RL", new Vector3(-wx,  0, -wz), steer: false);
        var suspRR = CreateSuspension(car, "Suspension_RR", new Vector3( wx,  0, -wz), steer: false);

        // Opposite-wheel links for solid-axle camber correction
        suspFL.oppositeWheel = suspFR;
        suspFR.oppositeWheel = suspFL;
        suspRL.oppositeWheel = suspRR;
        suspRR.oppositeWheel = suspRL;

        // Rear-wheel drive
        motor.outputDrives = new DriveForce[]
        {
            suspRL.GetComponent<DriveForce>(),
            suspRR.GetComponent<DriveForce>()
        };

        vp.engine = motor;

        return car;
    }

    static Suspension CreateSuspension(GameObject carRoot, string goName,
                                       Vector3 localPos, bool steer)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(carRoot.transform, false);
        go.transform.localPosition = localPos;

        // Suspension expects forward = toward wheel centre (outward laterally).
        // Positive X side faces right; negative X side faces left.
        float yaw = localPos.x > 0 ? 90f : -90f;
        go.transform.localRotation = Quaternion.Euler(0, yaw, 0);

        go.AddComponent<DriveForce>();
        var susp = go.AddComponent<Suspension>();

        susp.suspensionDistance = 0.25f;
        susp.generateHardCollider = true;

        if (steer)
        {
            susp.steerRangeMin = -35f;
            susp.steerRangeMax =  35f;
            susp.steerFactor   =  1f;
        }

        // Wheel child
        var wheelGo = new GameObject("Wheel");
        wheelGo.transform.SetParent(go.transform, false);
        var wheel        = wheelGo.AddComponent<Wheel>();
        wheel.tireRadius = 0.35f;
        wheel.rimRadius  = 0.25f;
        wheel.rimWidth   = 0.15f;
        susp.wheel       = wheel;

        // Rim child — Wheel.Start() calls tr.GetChild(0) to get the rim transform.
        // A cylinder gives a visual stand-in; the Wheel script only needs the transform.
        var rimGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        rimGo.name = "Rim";
        rimGo.transform.SetParent(wheelGo.transform, false);
        rimGo.transform.localRotation = Quaternion.Euler(0, 0, 90f);
        rimGo.transform.localScale    = new Vector3(wheel.rimRadius * 2f, wheel.rimWidth, wheel.rimRadius * 2f);
        Object.DestroyImmediate(rimGo.GetComponent<CapsuleCollider>());

        return susp;
    }

    static Gear[] DefaultGears() => new Gear[]
    {
        new Gear { ratio = -3.5f },   // Reverse
        new Gear { ratio =  0.0f },   // Neutral
        new Gear { ratio =  3.5f },   // 1st
        new Gear { ratio =  2.2f },   // 2nd
        new Gear { ratio =  1.6f },   // 3rd
        new Gear { ratio =  1.2f },   // 4th
        new Gear { ratio =  1.0f },   // 5th
    };

    // -------------------------------------------------------------------------

    static void EnsureTag(string tagName)
    {
        var tagManager = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var tags = tagManager.FindProperty("tags");

        for (int i = 0; i < tags.arraySize; i++)
        {
            if (tags.GetArrayElementAtIndex(i).stringValue == tagName)
                return;
        }

        tags.InsertArrayElementAtIndex(tags.arraySize);
        tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tagName;
        tagManager.ApplyModifiedProperties();
        Debug.Log($"[RVP] Added tag '{tagName}'");
    }

    static void EnsureLayer(string layerName)
    {
        var tagManager = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");

        for (int i = 0; i < layers.arraySize; i++)
        {
            if (layers.GetArrayElementAtIndex(i).stringValue == layerName)
                return;
        }

        for (int i = 8; i < layers.arraySize; i++)
        {
            var slot = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(slot.stringValue))
            {
                slot.stringValue = layerName;
                tagManager.ApplyModifiedProperties();
                Debug.Log($"[RVP] Added layer '{layerName}'");
                return;
            }
        }

        Debug.LogWarning($"[RVP] Could not add layer '{layerName}': no empty user-layer slots.");
    }

    static void EnsureInputButton(string buttonName)
    {
        var inputManager = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset")[0]);
        var axes = inputManager.FindProperty("m_Axes");

        for (int i = 0; i < axes.arraySize; i++)
        {
            if (axes.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue == buttonName)
                return;
        }

        axes.InsertArrayElementAtIndex(axes.arraySize);
        var entry = axes.GetArrayElementAtIndex(axes.arraySize - 1);
        entry.FindPropertyRelative("m_Name").stringValue            = buttonName;
        entry.FindPropertyRelative("descriptiveName").stringValue    = "";
        entry.FindPropertyRelative("descriptiveNegativeName").stringValue = "";
        entry.FindPropertyRelative("negativeButton").stringValue     = "";
        entry.FindPropertyRelative("positiveButton").stringValue     = buttonName;
        entry.FindPropertyRelative("altNegativeButton").stringValue  = "";
        entry.FindPropertyRelative("altPositiveButton").stringValue  = "";
        entry.FindPropertyRelative("gravity").floatValue             = 1000f;
        entry.FindPropertyRelative("dead").floatValue                = 0.001f;
        entry.FindPropertyRelative("sensitivity").floatValue         = 1000f;
        entry.FindPropertyRelative("snap").boolValue                 = false;
        entry.FindPropertyRelative("invert").boolValue               = false;
        entry.FindPropertyRelative("type").intValue                  = 0; // Key/Mouse button
        entry.FindPropertyRelative("axis").intValue                  = 0;
        entry.FindPropertyRelative("joyNum").intValue                = 0;
        inputManager.ApplyModifiedProperties();
        Debug.Log($"[RVP] Added input button '{buttonName}'");
    }
}
#endif
