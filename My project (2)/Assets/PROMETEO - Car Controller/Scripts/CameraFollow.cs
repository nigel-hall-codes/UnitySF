using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraFollow : MonoBehaviour {

	public Transform carTransform;
	[Range(1, 10)]
	public float followSpeed = 2;
	[Range(1, 10)]
	public float lookSpeed = 5;
	[Range(10, 360)]
	public float orbitSpeed = 120f;

	Vector3 initialCameraPosition;
	Vector3 initialCarPosition;
	Vector3 absoluteInitCameraPosition;
	float _orbitYaw = 0f;

	void Start(){
		initialCameraPosition = gameObject.transform.position;
		initialCarPosition = carTransform.position;
		absoluteInitCameraPosition = initialCameraPosition - initialCarPosition;
	}

	void FixedUpdate()
	{
		// Accumulate orbit yaw from right analog stick
		float lookX = 0f;
		if (HasAxis("CameraLookX"))
			lookX = Input.GetAxis("CameraLookX");
		_orbitYaw += lookX * orbitSpeed * Time.fixedDeltaTime;

		// Compute orbit position around car
		Vector3 orbitOffset = Quaternion.AngleAxis(_orbitYaw, Vector3.up) * absoluteInitCameraPosition;
		Vector3 _targetPos = carTransform.position + orbitOffset;
		transform.position = Vector3.Lerp(transform.position, _targetPos, followSpeed * Time.deltaTime);

		// Look at car
		Vector3 _lookDirection = carTransform.position - transform.position;
		Quaternion _rot = Quaternion.LookRotation(_lookDirection, Vector3.up);
		transform.rotation = Quaternion.Lerp(transform.rotation, _rot, lookSpeed * Time.deltaTime);
	}

	static bool HasAxis(string axisName)
	{
		try { Input.GetAxis(axisName); return true; }
		catch { return false; }
	}

}
