using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// [Phase 1] 화면 중앙 픽셀이 가리키는 건물의 GPS 좌표를 구한다.
///
///   중앙 픽셀 ray (probeCamera) → Physics.Raycast(건물 메쉬) → hit.point
///   → WorldToGps(hit.point)  (맵 회전 보정 포함, CubeGPSDisplay 정합 역함수)
///   → OnGpsResolved(lat, lng, hit)  ← Phase 2/3 에서 역지오코딩·LLM·AR 패널 연결 지점
///
/// 외부 의존 없음(순수 기하). 먼저 GPS 정확도를 검증한 뒤 Phase 2(역지오코딩)/
/// Phase 3(Ollama+AR) 를 붙인다.
///
/// 트리거:
///   - B 키: on-demand 1회 조회 (기본).
///   - N 키: dwell-auto 토글. 켜지면 중앙 조준이 dwellSeconds 이상 유지될 때 자동 조회.
[DisallowMultipleComponent]
public class BuildingInfoProbe : MonoBehaviour
{
    [Header("Refs (비우면 자동 검색)")]
    [Tooltip("중앙 픽셀 ray 를 쏠 카메라. 비우면 활성 카메라(Camera.main) 사용.")]
    public Camera probeCamera;
    [Tooltip("GPS 역변환 보정 소스 (anchor/scale).")]
    public CubeGPSDisplay calibration;

    [Header("Raycast")]
    public LayerMask buildingMask = ~0;
    public float maxRayDistance = 2000000f;

    [Header("Trigger")]
    [Tooltip("on-demand 조회 키.")]
    public Key probeKey = Key.B;
    [Tooltip("dwell-auto 모드 on/off 토글 키.")]
    public Key autoToggleKey = Key.N;
    public bool autoMode = false;
    [Tooltip("auto 모드에서 중앙 조준 유지 시간(초) 후 자동 조회.")]
    public float dwellSeconds = 1.5f;
    [Tooltip("auto 모드 중복 조회 방지: 이전 결과와 이 거리(m) 이상 떨어져야 재조회.")]
    public float autoMinMoveMeters = 5f;

    [Header("표시 (선택)")]
    public TMP_Text infoText;

    [Header("명중 하이라이트 (H 토글)")]
    [Tooltip("중앙 ray 의 명중점·명중 면을 실시간 색으로 표시.")]
    public bool showHitHighlight = true;
    public Key highlightKey = Key.H;
    [Tooltip("명중점 구 색.")]
    public Color hitPointColor = Color.red;
    [Tooltip("명중 면 색(알파로 반투명).")]
    public Color hitFaceColor = new Color(1f, 0.92f, 0.2f, 0.6f);
    [Tooltip("명중점 구 지름(월드 단위).")]
    public float hitPointSize = 3f;
    [Tooltip("ON: 콜라이더의 정확한 삼각형 표시(메쉬 Read/Write 필요). 실패 시 법선 사각 패치.")]
    public bool useExactTriangle = true;
    [Tooltip("폴백 사각 패치 한 변(월드 단위).")]
    public float facePatchSize = 4f;
    [Tooltip("면/점을 표면에서 띄우는 오프셋(z-fighting 방지).")]
    public float faceOffset = 0.05f;

    // --- 보정값 미러 (calibration 에서 동기화) ---
    Transform anchorObject;
    double anchorLat = 37.384312, anchorLng = 126.655307, scaleFactor = 1.0;

    float _dwellTimer;
    bool _hadHitLastFrame;
    Vector3 _lastProbedWorld = Vector3.positiveInfinity;
    FirstPersonView _fpv;

    GameObject _hitPointGo, _hitFaceGo;
    Mesh _hitFaceMesh;
    Material _hitPointMat, _hitFaceMat;

    /// 결과 콜백 — Phase 2/3 가 구독 (역지오코딩→LLM→AR). RaycastHit 로 명중 콜라이더/높이 전달.
    public event System.Action<double, double, RaycastHit> GpsResolved;

    void Start()
    {
        if (calibration == null) calibration = FindObjectOfType<CubeGPSDisplay>();
        _fpv = FindObjectOfType<FirstPersonView>();
        SyncCalibration();
    }

    void SyncCalibration()
    {
        if (calibration == null) return;
        anchorObject = calibration.anchorObject;
        anchorLat    = calibration.anchorLatitude;
        anchorLng    = calibration.anchorLongitude;
        scaleFactor  = calibration.horizontalScaleFactor;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb[probeKey].wasPressedThisFrame) Probe();
            if (kb[autoToggleKey].wasPressedThisFrame)
            {
                autoMode = !autoMode;
                _dwellTimer = 0f;
                Debug.Log($"[BuildingProbe] auto 모드 {(autoMode ? "ON" : "OFF")}");
            }
            if (kb[highlightKey].wasPressedThisFrame)
            {
                showHitHighlight = !showHitHighlight;
                Debug.Log($"[BuildingProbe] 하이라이트 {(showHitHighlight ? "ON" : "OFF")}");
            }
        }
        if (autoMode) AutoTick();
        UpdateHighlight();
    }

    void AutoTick()
    {
        if (!TryGetCenterRaycast(out RaycastHit h))
        {
            _dwellTimer = 0f; _hadHitLastFrame = false; return;
        }
        Vector3 hit = h.point;
        // 직전 hit 과 충분히 가까우면 같은 대상으로 보고 dwell 누적
        if (_hadHitLastFrame && (hit - _lastProbedWorld).sqrMagnitude > 1e6f)
            _dwellTimer = 0f;   // 시야가 크게 움직임 → 리셋 (1e6 = 1000 unit²; 스케일 따라 조정)
        _hadHitLastFrame = true;
        _dwellTimer += Time.deltaTime;
        if (_dwellTimer >= dwellSeconds)
        {
            _dwellTimer = 0f;
            ProbeAt(h);
        }
    }

    /// 중앙 픽셀 ray 의 건물 명중점.
    public bool TryGetCenterHit(out Vector3 hit)
    {
        if (TryGetCenterRaycast(out RaycastHit h)) { hit = h.point; return true; }
        hit = Vector3.zero; return false;
    }

    /// 중앙 픽셀 ray 의 전체 RaycastHit (point/normal/collider/triangleIndex).
    public bool TryGetCenterRaycast(out RaycastHit hit)
    {
        hit = default;
        var cam = ResolveActiveCamera();
        if (cam == null) return false;
        Ray r = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        return Physics.Raycast(r, out hit, maxRayDistance, buildingMask);
    }

    /// 지금 화면을 렌더 중인 카메라. FP 모드면 십자선과 동일한 fpCamera 를 반드시 써야
    /// 중앙 ray 명중점(빨간 점)이 십자선과 일치한다. 그 외엔 인스펙터 지정 →
    /// 활성 화면 카메라(depth 최대=맨 위) → Camera.main 순.
    Camera ResolveActiveCamera()
    {
        if (_fpv == null) _fpv = FindObjectOfType<FirstPersonView>();
        if (_fpv != null && _fpv.isActive && _fpv.fpCamera != null && _fpv.fpCamera.isActiveAndEnabled)
            return _fpv.fpCamera;
        if (probeCamera != null && probeCamera.isActiveAndEnabled) return probeCamera;

        Camera best = null;
        var cams = Camera.allCameras;   // 활성 카메라만 반환
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i];
            if (c.targetTexture != null) continue;   // 화면 렌더만
            if (best == null || c.depth > best.depth) best = c;
        }
        return best != null ? best : Camera.main;
    }

    // ===== 명중점/면 하이라이트 =====
    void UpdateHighlight()
    {
        bool hasHit = TryGetCenterRaycast(out RaycastHit h);
        if (!showHitHighlight || !hasHit)
        {
            if (_hitPointGo != null) _hitPointGo.SetActive(false);
            if (_hitFaceGo != null) _hitFaceGo.SetActive(false);
            return;
        }
        EnsureVisuals();
        // 명중점
        _hitPointGo.SetActive(true);
        _hitPointGo.transform.position = h.point + CameraLift(h.point);
        _hitPointGo.transform.localScale = Vector3.one * hitPointSize;
        // 명중 면
        _hitFaceGo.SetActive(true);
        _hitFaceGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        _hitFaceGo.transform.localScale = Vector3.one;
        if (!(useExactTriangle && TryBuildExactTriangle(h)))
            BuildNormalQuad(h);
    }

    void EnsureVisuals()
    {
        if (_hitPointMat == null) _hitPointMat = MakeUnlit(hitPointColor);
        if (_hitFaceMat == null)  _hitFaceMat  = MakeUnlit(hitFaceColor);
        if (_hitPointGo == null)
        {
            _hitPointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _hitPointGo.name = "HitPoint";
            var col = _hitPointGo.GetComponent<Collider>(); if (col != null) Destroy(col);
            _hitPointGo.GetComponent<Renderer>().sharedMaterial = _hitPointMat;
        }
        else { _hitPointMat.color = hitPointColor; }
        if (_hitFaceGo == null)
        {
            _hitFaceGo = new GameObject("HitFace");
            _hitFaceGo.AddComponent<MeshFilter>();
            _hitFaceGo.AddComponent<MeshRenderer>().sharedMaterial = _hitFaceMat;
            _hitFaceMesh = new Mesh { name = "HitFaceMesh" };
            _hitFaceGo.GetComponent<MeshFilter>().sharedMesh = _hitFaceMesh;
        }
        else { _hitFaceMat.color = hitFaceColor; }
    }

    bool TryBuildExactTriangle(RaycastHit h)
    {
        var mc = h.collider as MeshCollider;
        if (mc == null || mc.sharedMesh == null || !mc.sharedMesh.isReadable || h.triangleIndex < 0)
            return false;
        var m = mc.sharedMesh;
        int[] tris = m.triangles;
        if (h.triangleIndex * 3 + 2 >= tris.Length) return false;
        Vector3[] verts = m.vertices;
        Transform t = h.collider.transform;
        int ti = h.triangleIndex * 3;
        Vector3 off = CameraLift(h.point);
        Vector3 a = t.TransformPoint(verts[tris[ti]])     + off;
        Vector3 b = t.TransformPoint(verts[tris[ti + 1]]) + off;
        Vector3 c = t.TransformPoint(verts[tris[ti + 2]]) + off;
        _hitFaceMesh.Clear();
        _hitFaceMesh.vertices  = new[] { a, b, c };
        _hitFaceMesh.triangles = new[] { 0, 1, 2, 0, 2, 1 };   // 양면
        _hitFaceMesh.RecalculateBounds();
        return true;
    }

    void BuildNormalQuad(RaycastHit h)
    {
        // 패치 한 변을 카메라 거리에 비례시켜(최소 facePatchSize) 맵 스케일/조준 거리와
        // 무관하게 화면상 일정 크기로 보이게 한다.
        var cam = ResolveActiveCamera();
        float dist = cam != null ? Vector3.Distance(cam.transform.position, h.point) : 0f;
        float s = Mathf.Max(facePatchSize, dist * 0.02f) * 0.5f;
        Quaternion rot = Quaternion.LookRotation(h.normal);
        Vector3 right = rot * Vector3.right * s;
        Vector3 up    = rot * Vector3.up * s;
        Vector3 c = h.point + CameraLift(h.point);
        _hitFaceMesh.Clear();
        _hitFaceMesh.vertices  = new[] { c - right - up, c - right + up, c + right + up, c + right - up };
        _hitFaceMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };  // 양면
        _hitFaceMesh.RecalculateBounds();
    }

    static Material MakeUnlit(Color c)
    {
        var sh = Shader.Find("Sprites/Default");   // unlit + 알파 블렌드, 전 파이프라인 존재
        if (sh == null) sh = Shader.Find("Unlit/Color");
        return new Material(sh) { color = c };
    }

    /// 명중점을 카메라 방향으로 "거리에 비례해" 띄운다. 시선 방향 오프셋이라 화면상
    /// 위치(십자선)는 그대로지만 깊이상 벽 앞으로 나와 z-fighting 으로 묻히지 않는다.
    /// 맵이 ×100000 스케일이고 조준 거리가 매우 커도 동작하도록 고정값이 아닌 거리 비례.
    public Vector3 CameraLift(Vector3 worldPoint)
    {
        var cam = ResolveActiveCamera();
        if (cam == null) return Vector3.zero;
        Vector3 toCam = cam.transform.position - worldPoint;
        float d = toCam.magnitude;
        if (d < 1e-4f) return Vector3.zero;
        return toCam / d * Mathf.Max(faceOffset, d * 0.004f);
    }

    /// Unity world → GPS. CubeGPSDisplay 정변환의 정확한 역함수이자
    /// ProjectionUdpReceiver.GpsToWorld 와 동일 규약(월드 좌표 그대로, 회전 없음).
    /// 맵은 MapRotationCalibrator 가 앵커 기준으로 물리 회전시켜 월드축=지리축을
    /// 맞추므로, 변환에 별도 회전을 적용하면 안 된다.
    public bool WorldToGps(Vector3 world, out double lat, out double lng)
    {
        SyncCalibration();
        Vector3 basePos = anchorObject != null ? anchorObject.position : Vector3.zero;
        Vector3 rel = world - basePos;
        Vector3 corrected = rel / Mathf.Max((float)scaleFactor, 1e-6f);
        GPSEncoder.SetLocalOrigin(new Vector2((float)anchorLat, (float)anchorLng));
        Vector2 g = GPSEncoder.USCToGPS(corrected);   // x = lat, y = lon
        lat = g.x; lng = g.y;
        return true;
    }

    /// GPS → Unity world (WorldToGps 의 역; 회전 없음). worldY 는 호출부가 지정(예: 명중점 Y).
    /// 건물 풋프린트 오버레이의 정점 변환용.
    public Vector3 GpsToWorld(double lat, double lng, float worldY)
    {
        SyncCalibration();
        GPSEncoder.SetLocalOrigin(new Vector2((float)anchorLat, (float)anchorLng));
        Vector3 ucs = GPSEncoder.GPSToUCS((float)lat, (float)lng);   // (x, 0, z)
        Vector3 rel = ucs * (float)scaleFactor;
        Vector3 basePos = anchorObject != null ? anchorObject.position : Vector3.zero;
        return new Vector3(basePos.x + rel.x, worldY, basePos.z + rel.z);
    }

    [ContextMenu("Probe Center Now")]
    public void Probe()
    {
        if (!TryGetCenterRaycast(out RaycastHit h))
        {
            Report("중앙 픽셀: 건물 명중 없음 (콜라이더/조준 확인)");
            return;
        }
        ProbeAt(h);
    }

    void ProbeAt(RaycastHit h)
    {
        Vector3 hit = h.point;
        WorldToGps(hit, out double lat, out double lng);
        _lastProbedWorld = hit;
        Report($"중앙 픽셀 GPS: {lat:F7}, {lng:F7}  (world {hit})");
        GpsResolved?.Invoke(lat, lng, h);   // Phase 2/3 hook
    }

    void Report(string msg)
    {
        Debug.Log($"[BuildingProbe] {msg}");
        if (infoText != null) infoText.text = msg;
    }
}
