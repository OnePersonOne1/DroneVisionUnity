using UnityEngine;

public class DroneFollowCamera : MonoBehaviour
{
    [Header("따라갈 큐브")]
    public Transform target;

    [Header("큐브 뒤쪽 거리")]
    public float backDistance = 500f;

    [Header("큐브보다 위에 있을 높이")]
    public float height = 200f;

    [Header("부드럽게 따라갈지")]
    public bool smoothFollow = false;

    public float followSpeed = 10f;
    public float rotationSpeed = 10f;

    [Header("앞/뒤 방향 반전")]
    public bool reverseDirection = true;

    void LateUpdate()
    {
        if (target == null) return;
        float dt = Time.deltaTime;

        // 큐브의 뒤쪽 방향 — RMB 회전은 CubeDroneController 가 cube.rotation 을 갱신하므로
        // 카메라는 cube 의 forward/back 만 따라가면 자동으로 RMB 회전 반영됨.
        Vector3 backDirection = reverseDirection ? target.forward : -target.forward;

        Vector3 desiredPosition = target.position + backDirection * backDistance + Vector3.up * height;

        if (smoothFollow)
            transform.position = Vector3.Lerp(transform.position, desiredPosition, followSpeed * dt);
        else
            transform.position = desiredPosition;

        Vector3 lookDirection = target.position - transform.position;
        if (lookDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(lookDirection, Vector3.up);
            if (smoothFollow)
                transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, rotationSpeed * dt);
            else
                transform.rotation = desiredRotation;
        }
    }
}
