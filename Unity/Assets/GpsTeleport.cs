using UnityEngine;
using UnityEngine.InputSystem;

/// GPS 순간이동(오버라이드).
///
/// 토글이 켜져 있으면 수신되는(또는 CSV의) cam GPS를 무시하고, 여기 지정한
/// latitude/longitude/altitude 를 추론 입력으로 사용한다 → 모든 검출이 그
/// 좌표 기준으로 맵에 표시된다. 선택적으로 drone(시점 오브젝트)도 해당 위치로
/// 텔레포트시켜 화면이 그쪽으로 이동한다.
///
/// 토글: 인스펙터 체크박스 overrideEnabled, 또는 toggleKey(기본 T).
/// 좌표 변환은 ProjectionUdpReceiver.GpsToWorld 를 재사용(인천 GPSEncoder 보정).
public class GpsTeleport : MonoBehaviour
{
    [Header("토글")]
    public bool overrideEnabled = false;
    [Tooltip("오버라이드 on/off 토글 키 (New Input System).")]
    public Key toggleKey = Key.T;

    [Header("목표 GPS (오버라이드 ON일 때 사용)")]
    public double latitude = 37.384312;
    public double longitude = 126.655307;
    public double altitude = 0.0;

    [Header("선택: 이 오브젝트를 목표 위치로 텔레포트 (시점 이동)")]
    public Transform drone;
    public bool teleportDroneWhenEnabled = true;

    [Header("좌표 변환 소스 (비우면 자동 검색)")]
    public ProjectionUdpReceiver receiver;

    [Header("Cube 현재 GPS 복사 (Y 키)")]
    [Tooltip("Cube 의 현재 표시 GPS(CubeGPSDisplay.currentLat/Lng/currentAltitude)를 이 컴포넌트의 latitude/longitude/altitude 로 즉시 복사.")]
    public CubeGPSDisplay cubeGpsSource;
    public Key copyFromCubeKey = Key.Y;

    bool _prevEnabled;
    double _pLat, _pLng, _pAlt;

    void Start()
    {
        if (receiver == null) receiver = FindObjectOfType<ProjectionUdpReceiver>();
        if (cubeGpsSource == null) cubeGpsSource = FindObjectOfType<CubeGPSDisplay>();
        _prevEnabled = false; // Start에서 켜져 있으면 1회 적용되도록
        _pLat = _pLng = _pAlt = double.NaN;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey].wasPressedThisFrame)
        {
            overrideEnabled = !overrideEnabled;
            Debug.Log($"[GpsTeleport] override {(overrideEnabled ? "ON" : "OFF")} " +
                      $"@ ({latitude}, {longitude}, {altitude})");
        }
        if (kb != null && kb[copyFromCubeKey].wasPressedThisFrame) CopyFromCube();

        bool coordsChanged = latitude != _pLat || longitude != _pLng || altitude != _pAlt;
        if (overrideEnabled && (!_prevEnabled || coordsChanged))
            ApplyTeleport();

        _prevEnabled = overrideEnabled;
        _pLat = latitude; _pLng = longitude; _pAlt = altitude;
    }

    /// 수신부에서 호출: 오버라이드가 켜져 있으면 true + 목표 좌표 반환.
    public bool TryGetOverride(out float lat, out float lng, out float alt)
    {
        lat = (float)latitude; lng = (float)longitude; alt = (float)altitude;
        return overrideEnabled;
    }

    /// 런타임/UI에서 좌표 설정용.
    public void SetTarget(double lat, double lng, double alt)
    {
        latitude = lat; longitude = lng; altitude = alt;
    }

    /// Cube 의 현재 표시 GPS 를 이 컴포넌트의 lat/lng/alt 로 복사. Y 키 / ContextMenu.
    [ContextMenu("Copy GPS From Cube")]
    public void CopyFromCube()
    {
        if (cubeGpsSource == null) cubeGpsSource = FindObjectOfType<CubeGPSDisplay>();
        if (cubeGpsSource == null) { Debug.LogWarning("[GpsTeleport] CubeGPSDisplay 없음"); return; }
        latitude  = cubeGpsSource.currentLatitude;
        longitude = cubeGpsSource.currentLongitude;
        altitude  = cubeGpsSource.currentAltitude;
        Debug.Log($"[GpsTeleport] Cube 현재 GPS 복사 → ({latitude}, {longitude}, {altitude})");
    }

    void ApplyTeleport()
    {
        if (!teleportDroneWhenEnabled || drone == null || receiver == null) return;
        drone.position = receiver.GpsToWorld((float)latitude, (float)longitude, (float)altitude);
        Debug.Log($"[GpsTeleport] drone -> {drone.position}");
    }
}
