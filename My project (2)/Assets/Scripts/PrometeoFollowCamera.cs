using UnityEngine;

public class PrometeoFollowCamera : MonoBehaviour
{
    public Transform target;
    public float distance = 7f;
    public float height = 3f;
    public float smoothSpeed = 5f;

    void LateUpdate()
    {
        if (!target) return;
        Vector3 desired = target.position - target.forward * distance + Vector3.up * height;
        transform.position = Vector3.Lerp(transform.position, desired, Time.deltaTime * smoothSpeed);
        transform.LookAt(target.position + Vector3.up * 1f);
    }
}
