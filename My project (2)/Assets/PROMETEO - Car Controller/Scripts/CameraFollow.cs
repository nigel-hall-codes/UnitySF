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
	public bool autoRotate = true;

	Vector3 initialCameraPosition;
	Vector3 initialCarPosition;
	Vector3 absoluteInitCameraPosition;
	float _orbitYaw = 0f;
	float _freePitch = 0f;

	void Start(){
		initialCameraPosition = gameObject.transform.position;
		initialCarPosition = carTransform.position;
		absoluteInitCameraPosition = initialCameraPosition - initialCarPosition;
	}

	void FixedUpdate()
	{
		float lookX = HasAxis("CameraLookX") ? Input.GetAxis("CameraLookX") : 0f;
		float lookY = HasAxis("CameraLookY") ? Input.GetAxis("CameraLookY") : 0f;

		_orbitYaw += lookX * orbitSpeed * Time.fixedDeltaTime;

		if (autoRotate)
		{
			Vector3 orbitOffset = Quaternion.AngleAxis(_orbitYaw, Vector3.up) * absoluteInitCameraPosition;
			transform.position = Vector3.Lerp(transform.position, carTransform.position + orbitOffset, followSpeed * Time.deltaTime);

			Vector3 _lookDirection = carTransform.position - transform.position;
			transform.rotation = Quaternion.Lerp(transform.rotation,
				Quaternion.LookRotation(_lookDirection, Vector3.up), lookSpeed * Time.deltaTime);
		}
		else
		{
			_freePitch = Mathf.Clamp(_freePitch - lookY * orbitSpeed * Time.fixedDeltaTime, -80f, 80f);

			Vector3 orbitOffset = Quaternion.Euler(-_freePitch, _orbitYaw, 0f) * absoluteInitCameraPosition;
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
