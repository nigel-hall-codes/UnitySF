// Moved to Assets/Editor/PrometeoCarSetup.cs — this file is intentionally empty.
// The SFMap.Pipeline.Editor asmdef cannot reference Assembly-CSharp (where PrometeoCarController lives).
#if false
public static class PrometeoCarSetup
{
    [MenuItem("Prometeo/Create Placeholder Car")]
    static void Create()
    {
        var car = BuildCar(new Vector3(0, 5f, 0));
        AttachCamera(car);
        Selection.activeGameObject = car;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[Prometeo] Placeholder car created at (0,5,0). Drive with WASD + Space.");
    }

    static GameObject BuildCar(Vector3 position)
    {
        var root = new GameObject("PrometeoPlaceholderCar");
        root.transform.position = position;

        var rb  = root.AddComponent<Rigidbody>();
        rb.mass = 1200f;

        // Chassis collider
        var chassis    = root.AddComponent<BoxCollider>();
        chassis.center = new Vector3(0, 0f, 0);
        chassis.size   = new Vector3(1.6f, 0.5f, 4.0f);

        // Visual body
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = new Vector3(1.6f, 0.5f, 4.0f);
        Object.DestroyImmediate(body.GetComponent<BoxCollider>());

        // Wheels (WheelCollider GO + visual mesh GO are always separate in Prometeo)
        float wx = 0.85f, wz = 1.5f;
        WheelCollider wcFL, wcFR, wcRL, wcRR;
        GameObject    mFL,  mFR,  mRL,  mRR;
        CreateWheel(root, "FrontLeft",  new Vector3(-wx, 0,  wz), out wcFL, out mFL);
        CreateWheel(root, "FrontRight", new Vector3( wx, 0,  wz), out wcFR, out mFR);
        CreateWheel(root, "RearLeft",   new Vector3(-wx, 0, -wz), out wcRL, out mRL);
        CreateWheel(root, "RearRight",  new Vector3( wx, 0, -wz), out wcRR, out mRR);

        var ctrl = root.AddComponent<PrometeoCarController>();
        ctrl.frontLeftCollider  = wcFL;
        ctrl.frontRightCollider = wcFR;
        ctrl.rearLeftCollider   = wcRL;
        ctrl.rearRightCollider  = wcRR;
        ctrl.frontLeftMesh  = mFL;
        ctrl.frontRightMesh = mFR;
        ctrl.rearLeftMesh   = mRL;
        ctrl.rearRightMesh  = mRR;
        ctrl.bodyMassCenter = new Vector3(0, -0.3f, 0);
        // Disable optional features so no unassigned references cause errors
        ctrl.useEffects      = false;
        ctrl.useUI           = false;
        ctrl.useSounds       = false;
        ctrl.useTouchControls = false;

        return root;
    }

    static void CreateWheel(GameObject car, string label, Vector3 localPos,
                            out WheelCollider wc, out GameObject mesh)
    {
        // WheelCollider (physics, no renderer)
        var wcGo = new GameObject($"{label}_WC");
        wcGo.transform.SetParent(car.transform, false);
        wcGo.transform.localPosition = localPos;

        wc = wcGo.AddComponent<WheelCollider>();
        wc.radius             = 0.35f;
        wc.suspensionDistance = 0.2f;

        // Visual mesh — cylinder rotated to match wheel orientation
        mesh      = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        mesh.name = $"{label}_Mesh";
        mesh.transform.SetParent(car.transform, false);
        mesh.transform.localPosition = localPos;
        mesh.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        mesh.transform.localScale    = new Vector3(0.7f, 0.15f, 0.7f); // diameter = radius*2
        Object.DestroyImmediate(mesh.GetComponent<CapsuleCollider>());
    }

    static void AttachCamera(GameObject car)
    {
        var camGo = new GameObject("PrometeoCamera");
        camGo.AddComponent<Camera>();
        camGo.AddComponent<AudioListener>();
        var follow    = camGo.AddComponent<PrometeoFollowCamera>();
        follow.target = car.transform;
    }
}
#endif
