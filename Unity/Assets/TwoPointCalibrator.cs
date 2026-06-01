using UnityEngine;
using UnityEngine.InputSystem;

/// 2점 GPS 캘리브레이션.
///
/// 현재 1점 정합(anchorObject ↔ anchorLat/Lng 만 고정) 은 위치만 잡아주고
/// **스케일·회전(정북)** 은 검증 불가. 2점을 주면 다음을 자동 계산해 적용한다:
///   1) anchor = point1 (위치)
///   2) horizontalScaleFactor = Unity 거리 / 실제 미터 거리  (스케일)
///   3) mapTransform 을 point1 기준으로 Y 회전  (정북 정렬)
///
/// 동일 길이의 두 평면 직선(Unity vs 실제)을 일치시키는 평면 Helmert 변환과 같다.
///
/// 사용 절차
///   1) 맵 위에서 식별 가능한 점 2개를 골라 그 위치에 빈 GameObject 두 개 두기
///      (또는 기존 객체 사용).
///   2) 이 컴포넌트의 point1Transform / point2Transform 에 드래그, 각각의
///      실제 위경도를 입력.
///   3) Edit 모드에서 ContextMenu "Calibrate Two Points" 또는 Play 중 `K` 키.
///   4) 결과 (rotation, scale) 가 콘솔에 찍히고 CubeGPSDisplay/MapRotationCalibrator
///      에 즉시 반영. 씬 저장하면 영구.
[DisallowMultipleComponent]
public class TwoPointCalibrator : MonoBehaviour
{
    [Header("점 1 (앵커가 됨)")]
    public Transform point1Transform;
    public double point1Latitude;
    public double point1Longitude;

    [Header("점 2 (정합 대조점)")]
    public Transform point2Transform;
    public double point2Latitude;
    public double point2Longitude;

    [Header("적용 대상 (비우면 자동 검색)")]
    [Tooltip("CubeGPSDisplay — anchorObject/위경도/horizontalScaleFactor 를 갱신.")]
    public CubeGPSDisplay calibration;
    [Tooltip("회전시킬 맵 루트. 비우면 anchorObject.root 자동.")]
    public Transform mapTransform;
    [Tooltip("UnityToGPSConverter 도 같이 갱신(있으면).")]
    public UnityToGPSConverter unityToGps;
    [Tooltip("MapRotationCalibrator — 회전 적용 후 새 상태를 '초기값' 으로 갈무리.")]
    public MapRotationCalibrator rotationCalibrator;

    [Header("실행")]
    [Tooltip("Play 중 이 키로도 캘리브레이션 실행.")]
    public Key calibrateKey = Key.K;

    [Header("최근 결과 (read-only)")]
    public float computedYawDeg;
    public float computedScale;
    public float realMetersDistance;
    public float unityDistance;

    void Start()
    {
        if (calibration == null) calibration = FindObjectOfType<CubeGPSDisplay>();
        if (unityToGps == null) unityToGps = FindObjectOfType<UnityToGPSConverter>();
        if (rotationCalibrator == null) rotationCalibrator = FindObjectOfType<MapRotationCalibrator>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[calibrateKey].wasPressedThisFrame) Calibrate();
    }

    [ContextMenu("Calibrate Two Points")]
    public void Calibrate()
    {
        if (point1Transform == null || point2Transform == null)
        {
            Debug.LogWarning("[TwoPointCalibrator] point1/point2 Transform 미지정");
            return;
        }
        if (calibration == null) calibration = FindObjectOfType<CubeGPSDisplay>();
        if (calibration == null)
        {
            Debug.LogWarning("[TwoPointCalibrator] CubeGPSDisplay 없음");
            return;
        }
        if (mapTransform == null)
        {
            // altRefBuilding 은 맵 프리팹 내부 객체이므로 그 root 가 실제 맵 루트.
            // point1 이 맵 외부 top-level 이면 point1.root == point1 이라 회전이 적용되지 않음.
            if (calibration.altitudeReferenceBuilding != null)
                mapTransform = calibration.altitudeReferenceBuilding.root;
            else
                mapTransform = (point1Transform.root != null) ? point1Transform.root : point1Transform;
        }
        if (mapTransform == point1Transform)
        {
            Debug.LogWarning("[TwoPointCalibrator] mapTransform 이 Point1 자기 자신으로 잡힘 → 회전이 무의미. " +
                             "인스펙터의 Map Transform 슬롯에 'GPS Manager' 같은 맵 루트를 직접 드래그하거나, " +
                             "CubeGPSDisplay.altitudeReferenceBuilding 슬롯이 맵 내부 객체를 가리키도록 설정.");
        }
        Debug.Log($"[TwoPointCalibrator] mapTransform={mapTransform.name}, pivot=Point1({point1Transform.name})");

        // 1) Unity XZ 평면에서의 P1→P2 방향과 거리
        Vector3 u = point2Transform.position - point1Transform.position;
        Vector2 uXZ = new Vector2(u.x, u.z);
        float uDist = uXZ.magnitude;
        if (uDist < 1e-4f)
        {
            Debug.LogWarning("[TwoPointCalibrator] point1/point2 Unity 위치가 너무 가까움");
            return;
        }
        float uBearingDeg = Mathf.Atan2(uXZ.x, uXZ.y) * Mathf.Rad2Deg; // +Z=북, +X=동 기준 시계방향 베어링

        // 2) 실제 GPS 거리/방향 — GPSEncoder 의 metersPerLat/Lon 그대로 사용
        GPSEncoder.SetLocalOrigin(new Vector2((float)point1Latitude, (float)point1Longitude));
        Vector3 enuMeters = GPSEncoder.GPSToUCS((float)point2Latitude, (float)point2Longitude);
        // enuMeters: x = (lon2-lon1)*mPerLon (East), z = (lat2-lat1)*mPerLat (North), y = 0
        Vector2 rMeters = new Vector2(enuMeters.x, enuMeters.z);
        float rDist = rMeters.magnitude;
        if (rDist < 1e-4f)
        {
            Debug.LogWarning("[TwoPointCalibrator] point1/point2 의 실제 GPS 가 너무 가까움");
            return;
        }
        float rBearingDeg = Mathf.Atan2(rMeters.x, rMeters.y) * Mathf.Rad2Deg;

        // 3) 회전 = 실제 베어링 - Unity 베어링 (Unity Y축 양의 회전 = 위에서 봤을 때 시계방향)
        float deltaYawDeg = Mathf.DeltaAngle(uBearingDeg, rBearingDeg);
        // 4) 스케일: corrected = rel / scaleFactor (corrected 가 미터)
        //    scaleFactor = Unity 거리 / 실제 거리 (= Unity units per meter)
        float newScale = uDist / rDist;

        // 5) 적용
        calibration.anchorObject = point1Transform;
        calibration.anchorLatitude = point1Latitude;
        calibration.anchorLongitude = point1Longitude;
        calibration.horizontalScaleFactor = newScale;
        if (unityToGps != null)
        {
            unityToGps.anchorObject = point1Transform;
            unityToGps.anchorLatitude = (float)point1Latitude;
            unityToGps.anchorLongitude = (float)point1Longitude;
        }
        // 6) 맵 회전 (point1 을 축으로 Y축 회전 → point1 의 Unity 위치는 그대로)
        mapTransform.RotateAround(point1Transform.position, Vector3.up, deltaYawDeg);
        // 7) MapRotationCalibrator 의 "초기값" 도 새 상태로 갈무리
        if (rotationCalibrator != null) rotationCalibrator.SnapshotAsInitial();

        computedYawDeg     = deltaYawDeg;
        computedScale      = newScale;
        realMetersDistance = rDist;
        unityDistance      = uDist;

        Debug.Log($"[TwoPointCalibrator] 적용 완료\n" +
                  $"  P1 Unity={point1Transform.position}  GPS=({point1Latitude:F6},{point1Longitude:F6})\n" +
                  $"  P2 Unity={point2Transform.position}  GPS=({point2Latitude:F6},{point2Longitude:F6})\n" +
                  $"  Unity 거리={uDist:F3}  실제={rDist:F3} m  → scale={newScale:F6} units/m\n" +
                  $"  Unity bearing={uBearingDeg:F2}°  real bearing={rBearingDeg:F2}°  → ΔYaw={deltaYawDeg:F2}°");
    }
}
