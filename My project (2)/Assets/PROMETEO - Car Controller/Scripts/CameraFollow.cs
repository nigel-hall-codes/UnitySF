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
	[Range(2, 30)]
	public float distance = 10f;
	[Range(-5, 20)]
	public float height = 5f;
	public bool autoRotate = true;

	Vector3 _offsetDirFlat;
	float _orbitYaw = 0f;
	float _freePitch = 0f;

	// Capture the camera->car offset in Awake, before any Start() runs. CarRoadSpawner
	// teleports the car to its spawn point in Start(); if we captured here in Start() the
	// two would race, and reading carTransform.position after the teleport would bake a
	// huge offset into absoluteInitCameraPosition (camera ends up far from the car instead
	// of behind it). Awake always precedes every Start, so the authored offset is used.
	void Awake(){
		Vector3 initialCameraPosition = gameObject.transform.position;
		Vector3 initialCarPosition = carTransform.position;
		Vector3 absoluteInitCameraPosition = initialCameraPosition - initialCarPosition;
		Vector3 flat = new Vector3(absoluteInitCameraPosition.x, 0f, absoluteInitCameraPosition.z);
		distance = flat.magnitude;
		_offsetDirFlat = flat.magnitude > 0.001f ? flat.normalized : Vector3.back;
		height = absoluteInitCameraPosition.y;
	}

	void FixedUpdate()
	{
		float lookX = HasAxis("CameraLookX") ? Input.GetAxis("CameraLookX") : 0f;
		float lookY = HasAxis("CameraLookY") ? Input.GetAxis("CameraLookY") : 0f;

		_orbitYaw += lookX * orbitSpeed * Time.fixedDeltaTime;

		if (autoRotate)
		{
			Vector3 orbitOffset = Quaternion.AngleAxis(_orbitYaw, Vector3.up) * (_offsetDirFlat * distance) + Vector3.up * height;
			transform.position = Vector3.Lerp(transform.position, carTransform.position + orbitOffset, followSpeed * Time.deltaTime);

			Vector3 _lookDirection = carTransform.position - transform.position;
			transform.rotation = Quaternion.Lerp(transform.rotation,
				Quaternion.LookRotation(_lookDirection, Vector3.up), lookSpeed * Time.deltaTime);
		}
		else
		{
			_freePitch = Mathf.Clamp(_freePitch - lookY * orbitSpeed * Time.fixedDeltaTime, -80f, 80f);

			Vector3 orbitOffset = Quaternion.Euler(-_freePitch, _orbitYaw, 0f) * (_offsetDirFlat * distance) + Vector3.up * height;
			transform.position = Vector3.Lerp(transform.position, carTransform.position + orbitOffset, followSpeed * Time.deltaTime);

			transform.rotation = Quaternion.LookRotation(carTransform.position - transform.position, Vector3.up);
		}
	}

	static bool HasAxis(string axisName)
	{
		try { Input.GetAxis(axisName); return true; }
		catch { return false; }
	}

}
