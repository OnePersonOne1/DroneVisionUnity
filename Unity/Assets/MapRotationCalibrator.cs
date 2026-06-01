using UnityEngine;
using UnityEngine.InputSystem;

/// 맵 회전 실시간 보정.
///
/// `<` (= `,`) / `>` (= `.`) 키를 누르고 있는 동안 맵을 **anchorObject 기준
/// 수직(Y) 축으로 회전**시킨다. 앵커의 Unity 위치가 회전축 위에 있으므로
/// 앵커 위치는 그대로 유지 → GPS↔Unity 정합은 깨지지 않고 모델의 정북
/// 방향만 사용자가 직접 맞출 수 있다.
///
/// `/` 키로 초기 회전·위치로 즉시 복원.
[DisallowMultipleComponent]
public class MapRotationCalibrator : MonoBehaviour
{
    [Header("타깃 (비우면 자동 검색)")]
    [Tooltip("회전시킬 맵 루트. 비우면 anchorObject.root.")]
    public Transform mapTransform;
    [Tooltip("회전 중심. 비우면 CubeGPSDisplay.anchorObject 자동.")]
    public Transform pivot;
    [Tooltip("calibration 자동 검색 소스.")]
    public CubeGPSDisplay calibration;

    [Header("회전 키 & 속도")]
    [Tooltip("'<' (Shift 없이 ',' 키) — 반시계 회전.")]
    public Key rotateCcwKey = Key.Comma;
    [Tooltip("'>' (Shift 없이 '.' 키) — 시계 회전.")]
    public Key rotateCwKey  = Key.Period;
    [Tooltip("'/' 키 — 초기 회전/위치로 복원.")]
    public Key resetKey     = Key.Slash;
    [Tooltip("키를 누르고 있을 때 1초당 회전 도수.")]
    public float degreesPerSecond = 10f;
    [Tooltip("회전 축 (기본 Y = 정북 정렬).")]
    public Vector3 rotationAxis = Vector3.up;

    [Header("진행 상태 (read-only)")]
    [Tooltip("초기값 대비 누적 회전 도수.")]
    public float totalYawDeg;

    Vector3 _initialPos;
    Quaternion _initialRot;
    bool _saved;

    void Start()
    {
        if (calibration == null) calibration = FindObjectOfType<CubeGPSDisplay>();
        if (pivot == null && calibration != null) pivot = calibration.anchorObject;
        if (mapTransform == null)
        {
            // altRefBuilding 은 맵 프리팹 내부 객체이므로 그 root 가 실제 맵 루트(GPS Manager).
            // anchor/pivot 이 맵 외부 top-level GameObject 인 경우엔 root=self 라 회전이 무의미.
            if (calibration != null && calibration.altitudeReferenceBuilding != null)
                mapTransform = calibration.altitudeReferenceBuilding.root;
            else if (pivot != null) mapTransform = pivot.root;
        }
        // 안전장치: mapTransform 이 결국 pivot 자신이면 회전은 no-op → 경고.
        if (mapTransform != null && pivot != null && mapTransform == pivot)
            Debug.LogWarning("[MapRot] mapTransform 이 pivot 과 같음 → 회전이 적용되지 않을 수 있음. " +
                             "인스펙터에서 mapTransform 에 'GPS Manager' 같은 맵 루트를 직접 드래그하세요.");

        if (mapTransform == null)
        {
            Debug.LogWarning("[MapRot] mapTransform 미지정 — 인스펙터에 직접 드래그하세요.");
            return;
        }
        if (pivot == null)
            Debug.LogWarning("[MapRot] pivot 미지정 — mapTransform 자기 위치 기준 회전 (앵커 GPS 매핑이 어긋날 수 있음).");

        // 초기값 스냅샷 (리셋용)
        _initialPos = mapTransform.position;
        _initialRot = mapTransform.rotation;
        _saved = true;
        Debug.Log($"[MapRot] 초기값 저장 (map={mapTransform.name}, pivot={(pivot ? pivot.name : "self")})");
    }

    void Update()
    {
        if (mapTransform == null) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        float dir = 0f;
        if (kb[rotateCcwKey].isPressed) dir -= 1f;
        if (kb[rotateCwKey].isPressed)  dir += 1f;
        if (dir != 0f)
        {
            float delta = dir * degreesPerSecond * Time.deltaTime;
            Vector3 p = pivot != null ? pivot.position : mapTransform.position;
            mapTransform.RotateAround(p, rotationAxis, delta);
            totalYawDeg += delta;
        }

        if (kb[resetKey].wasPressedThisFrame) ResetRotation();
    }

    /// 초기 위치/회전으로 복원. (런타임 / ContextMenu 모두 가능)
    [ContextMenu("Reset Rotation")]
    public void ResetRotation()
    {
        if (!_saved || mapTransform == null) return;
        mapTransform.position = _initialPos;
        mapTransform.rotation = _initialRot;
        totalYawDeg = 0f;
        Debug.Log("[MapRot] 초기값으로 복원");
    }

    /// 현재 상태를 새 "초기값"으로 갈무리. 다음 Reset 부턴 여기로 되돌아옴.
    [ContextMenu("Set Current As Initial")]
    public void SnapshotAsInitial()
    {
        if (mapTransform == null) return;
        _initialPos = mapTransform.position;
        _initialRot = mapTransform.rotation;
        totalYawDeg = 0f;
        _saved = true;
        Debug.Log($"[MapRot] 현재 상태를 새 초기값으로 갈무리 ({mapTransform.name})");
    }
}
