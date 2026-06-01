using UnityEngine;
using UnityEngine.InputSystem;

/// 단순 GPS 순간이동 유틸 (FPV·추론 오버라이드와 무관).
///
/// 핫키 한 번 누르면 target(보통 Cube 드론) Transform 의 position 을
/// 입력 GPS(lat/lng/alt) 의 Unity 월드 좌표로 즉시 이동시킨다.
/// 좌표 변환은 ProjectionUdpReceiver 또는 ProjectionReplay 의
/// 공개 메서드 `GpsToWorld(lat,lng,alt)` 를 그대로 사용 (= CubeGPSDisplay
/// 정합과 자동 일관).
///
/// 차이점:
///   - GpsTeleport : 토글 ON 동안 수신/CSV 의 cam GPS 를 무시·치환하는 "추론
///     오버라이드". 시점도 함께 옮길 수 있음.
///   - 이 컴포넌트  : 그냥 target.position 만 한 번 옮기는 단발성 텔레포트.
///     추론 파이프라인엔 전혀 영향 없음.
[DisallowMultipleComponent]
public class CubeGpsTeleport : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("이동할 오브젝트 (보통 Cube 드론).")]
    public Transform target;

    [Header("목표 GPS")]
    public double latitude = 37.384312;
    public double longitude = 126.655307;
    public double altitude = 0.0;
    [Tooltip("ON 이면 receiver 의 anchorLatitude/Longitude 를 사용 (= 맵 GPS origin). " +
             "altitude 는 그대로 사용.")]
    public bool useReceiverAnchor = true;

    [Header("좌표 변환 소스 (비우면 자동 검색)")]
    public ProjectionUdpReceiver receiver;
    public ProjectionReplay replay;

    [Header("Hotkey")]
    [Tooltip("이 키를 누르면 target 을 GPS 위치로 즉시 이동.")]
    public Key teleportKey = Key.G;

    void Start()
    {
        if (receiver == null) receiver = FindObjectOfType<ProjectionUdpReceiver>();
        if (replay == null)   replay   = FindObjectOfType<ProjectionReplay>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[teleportKey].wasPressedThisFrame)
            Teleport();
    }

    /// 런타임/UI/Inspector ContextMenu 에서도 호출 가능.
    [ContextMenu("Teleport Now")]
    public void Teleport()
    {
        if (target == null)
        {
            Debug.LogWarning("[CubeGpsTeleport] target 미지정.");
            return;
        }

        double lat = latitude, lng = longitude, alt = altitude;
        if (useReceiverAnchor)
        {
            if (receiver != null)
            {
                lat = receiver.anchorLatitude;
                lng = receiver.anchorLongitude;
            }
            else if (replay != null)
            {
                lat = replay.anchorLatitude;
                lng = replay.anchorLongitude;
            }
        }

        Vector3 world;
        if (receiver != null)
            world = receiver.GpsToWorld((float)lat, (float)lng, (float)alt);
        else if (replay != null)
            world = replay.GpsToWorld((float)lat, (float)lng, (float)alt);
        else
        {
            Debug.LogWarning("[CubeGpsTeleport] receiver/replay 모두 없음 — 좌표 변환 불가.");
            return;
        }

        target.position = world;
        Debug.Log($"[CubeGpsTeleport] {target.name} → " +
                  $"GPS({lat:F6},{lng:F6},{alt:F2}) → world {world}");
    }
}
