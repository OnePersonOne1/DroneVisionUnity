using UnityEngine;
using UnityEngine.InputSystem;

public class CubeDroneController : MonoBehaviour
{
    [Header("수평 이동 속도")]
    public float moveSpeed = 500f;

    [Header("상승/하강 속도")]
    public float verticalSpeed = 300f;

    [Header("Yaw 회전 속도 (키)")]
    public float yawSpeed = 90f;

    [Header("Pitch 회전 속도 (키)")]
    public float pitchSpeed = 60f;

    [Header("Pitch 제한 각도")]
    public float minPitch = -45f;
    public float maxPitch = 45f;

    [Header("방향 반전 옵션")]
    public bool invertForwardBackward = false;
    public bool invertLeftRight = false;
    public bool invertYaw = false;
    public bool invertPitch = false;

    [Header("마우스 LMB 룩 (yaw=마우스X, pitch=마우스Y)")]
    [Tooltip("좌클릭 누른 동안 마우스 delta 를 yaw/pitch 에 누적. 1인칭/3인칭 양쪽 동일 조작. " +
             "RMB 는 strategic 명령용으로 비워둠.")]
    public bool enableMouseLook = true;
    public float mouseSensitivity = 0.3f;

    private float yawAngle = 0f;
    private float pitchAngle = 0f;

    void Start()
    {
        Vector3 initialEuler = transform.rotation.eulerAngles;
        yawAngle = initialEuler.y;
        pitchAngle = NormalizeAngle(initialEuler.x);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        float dt = Time.deltaTime;

        // ── 1. Yaw 누적 (화살표 + RMB 마우스X) ──
        float yawInput = 0f;
        if (kb.leftArrowKey.isPressed) yawInput = -1f;
        if (kb.rightArrowKey.isPressed) yawInput = 1f;
        if (invertYaw) yawInput *= -1f;
        float yawDelta = yawInput * yawSpeed * dt;

        // ── 2. Pitch 누적 (R/F + RMB 마우스Y) ──
        float pitchInput = 0f;
        if (kb.rKey.isPressed) pitchInput = -1f;   // 기수 ↑
        if (kb.fKey.isPressed) pitchInput = 1f;    // 기수 ↓
        if (invertPitch) pitchInput *= -1f;
        float pitchDelta = pitchInput * pitchSpeed * dt;

        // ── LMB 마우스 룩 (양 뷰 공통 조작 — RMB 는 strategic SetWaypoint 용으로 비움) ──
        if (enableMouseLook)
        {
            var mouse = Mouse.current;
            // UGUI 위 클릭(미니맵 드론 선택 등)은 큐브 회전에서 제외.
            bool overUI = UnityEngine.EventSystems.EventSystem.current != null &&
                          UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            if (mouse != null && mouse.leftButton.isPressed && !overUI)
            {
                Vector2 md = mouse.delta.ReadValue();
                yawDelta   += md.x * mouseSensitivity * (invertYaw ? -1f : 1f);
                pitchDelta += -md.y * mouseSensitivity * (invertPitch ? -1f : 1f);   // 위로 = 기수 ↑
            }
        }

        yawAngle += yawDelta;
        pitchAngle += pitchDelta;
        pitchAngle = Mathf.Clamp(pitchAngle, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(pitchAngle, yawAngle, 0f);

        // ── 3. WASD 수평 이동 (yaw 만 반영) ──
        float forwardInput = 0f, rightInput = 0f;
        if (kb.wKey.isPressed) forwardInput = 1f;
        else if (kb.sKey.isPressed) forwardInput = -1f;
        if (kb.dKey.isPressed) rightInput = 1f;
        else if (kb.aKey.isPressed) rightInput = -1f;
        if (invertForwardBackward) forwardInput *= -1f;
        if (invertLeftRight) rightInput *= -1f;

        Quaternion yawOnlyRotation = Quaternion.Euler(0f, yawAngle, 0f);
        // 현재 Cube 시점 기준이 반대로 잡혀 있어 forward/right 대신 back/left 사용 (기존 규약).
        Vector3 horizontalForward = yawOnlyRotation * Vector3.back;
        Vector3 horizontalRight = yawOnlyRotation * Vector3.left;
        Vector3 horizontalMove = horizontalForward * forwardInput + horizontalRight * rightInput;
        if (horizontalMove.sqrMagnitude > 1f) horizontalMove.Normalize();
        transform.position += horizontalMove * moveSpeed * dt;

        // ── 4. 상승/하강 ──
        if (kb.upArrowKey.isPressed) transform.position += Vector3.up * verticalSpeed * dt;
        if (kb.downArrowKey.isPressed) transform.position += Vector3.down * verticalSpeed * dt;
    }

    float NormalizeAngle(float angle) => angle > 180f ? angle - 360f : angle;
}
