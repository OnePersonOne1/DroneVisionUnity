using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

/// projection_<session>.csv 를 직접 읽어 각 검출을 인천 메시에 Raycast 하고
/// 명중점에 마커를 생성한다. 좌표 변환은 ProjectionUdpReceiver 와 동일 (팀원
/// CubeGPSDisplay 정확한 역함수, 보정값 자동 동기화). 디버그 시각화도 동일
/// 방식으로 광선·GPS 원점을 표시한다 (O 키 또는 인스펙터 토글).
///
/// CSV 컬럼 (projection_pipeline.py OUT_HEADERS):
///   0 frame_id 1 sys_time 2 class_id 3 class_name 4 confidence
///   5 u 6 v 7 cam_lat 8 cam_lng 9 cam_alt 10 dx 11 dy 12 dz
public class ProjectionReplay : MonoBehaviour
{
    [Header("Inputs")]
    [Tooltip("Absolute path to projection_<session>.csv")]
    public string projectionCsvPath;
    [Tooltip("비우면 멀리서도 잘 보이는 세로 막대 마커가 런타임 생성됨.")]
    public GameObject markerPrefab;
    public GpsTeleport teleport;

    [Header("보정 단일 소스 (팀원 CubeGPSDisplay)")]
    public CubeGPSDisplay calibration;

    [Header("보정값 (calibration 없을 때만 사용 — 팀원 씬값 기본)")]
    public Transform anchorObject;
    public double anchorLatitude = 37.384312;
    public double anchorLongitude = 126.655307;
    public double horizontalScaleFactor = 1.0;
    public Transform altitudeReferenceBuilding;
    public double referenceBuildingHeightMeters = 13.35;
    [Tooltip("cam_alt 가 이 값보다 작으면 이 값으로 클램프 (GPS 음수 노이즈 보호). 0 = 미사용.")]
    public double clampMinAltitudeMeters = 50.0;
    [Tooltip("최종 worldY 에 추가로 더할 안전 마진(m).")]
    public double originUpliftMeters = 0.0;

    [Header("Raycast")]
    public LayerMask groundMask = ~0;
    public float maxRayDistance = 2000000f;
    [Tooltip("Physics.Raycast 가 메쉬를 못 맞췄을 때 가상의 지면 평면과 광선 교차점에 마커를 찍는다 " +
             "(MeshCollider 미배치 시에도 검출 위치 보임). 평면 Y = altRefBuilding.bounds.min.y → anchor.y → 0 순.")]
    public bool useGroundPlaneFallback = true;
    [Tooltip("광선 시작점이 들어 있는 콜라이더(=그 건물 안)의 hit 는 무시. " +
             "건물 안에서도 외벽을 통과해 바깥 지형/타 건물에 명중하게 함.")]
    public bool passThroughEnclosingBuilding = true;

    [Header("Filter")]
    public string[] classFilter;

    [Header("Pacing (CSV → 시간 흐름)")]
    [Tooltip("0 = CSV의 sys_time 간격대로 재현 (라이브 흐름). >0 = 고정 Hz.")]
    public float rateHz = 5f;
    [Tooltip("끝나면 처음부터 다시 재생.")]
    public bool loop = false;
    [Tooltip("0 = 무제한, >0 = 그 프레임 수까지만.")]
    public int maxFrames = 0;

    [Header("Markers")]
    [Tooltip("0 = never destroy")]
    public float markerLifetime = 0f;
    public float markerScale = 3f;

    [Header("Debug 시각화 (광선·GPS 원점)")]
    public bool showDebugVisuals = true;
    public Key debugToggleKey = Key.O;
    [Tooltip("기존 DebugOrigin/DebugRay 일괄 삭제 키.")]
    public Key clearDebugKey = Key.C;
    public float originSphereScale = 5f;
    public float rayLineWidth = 0.5f;
    [Tooltip("그려지는 광선의 길이(Unity 단위). 짧으면 광선이 지면 도달 전에 끊김.")]
    public float rayDrawDistance = 100000f;
    [Tooltip("0 = 영원, >0 = N초 뒤 자동 소멸. syncRayLifetimeWithMarker 가 켜져 있으면 무시되고 markerLifetime 을 사용.")]
    public float debugVisualLifetime = 0f;
    [Tooltip("켜면 광선/원점 구를 markerLifetime 과 동일한 수명으로 자동 소멸. " +
             "끄면 debugVisualLifetime 사용. 기본 ON.")]
    public bool syncRayLifetimeWithMarker = true;

    [Header("Debug Rays (Scene 뷰 전용, Debug.DrawRay)")]
    public bool drawDebugRays = false;
    public float debugRayDuration = 5f;

    readonly System.Collections.Generic.List<GameObject> debugObjects = new System.Collections.Generic.List<GameObject>();
    Transform debugRoot, markerRoot;

    public enum CameraOrientation { LandscapeLeft, LandscapeRight }

    [Header("Identity-quat 비교 토글 (J 키)")]
    [Tooltip("ON 이면 CSV 의 dx/dy/dz 대신 폰 자세를 무시한 'identity quaternion' 방향으로 재계산. " +
             "회전 미적용 시 마커가 어디로 떨어지는지 시각 비교.")]
    public bool useIdentityQuat = false;
    [Tooltip("Identity-quat 비교 토글 키.")]
    public Key compareToggleKey = Key.J;
    [Tooltip("카메라 수평 FOV (Python UDP_CAM_FOV_H_DEG 와 동일).")]
    public float fovHorizontalDeg = 84f;
    [Tooltip("카메라 마운트 (Python UDP_ORIENTATION 와 동일).")]
    public CameraOrientation cameraOrientation = CameraOrientation.LandscapeLeft;

    [Header("진단 로깅")]
    [Tooltip("이 주기(초)마다 spawn/clear 카운터를 콘솔에 찍는다. 0 = 꺼짐.")]
    public float diagnosticLogIntervalSec = 2f;
    int markerSpawnCount, debugSpawnCount, clearCount;
    float lastDiagLogTime;

    [Header("FP / Overlay (이미지 해상도)")]
    [Tooltip("Python UDP_CAM_WIDTH 와 동일. 검출 픽셀 (u,v) → 화면 매핑에 사용.")]
    public int imageWidth = 3840;
    public int imageHeight = 2160;

    Vector3 _latestPos, _latestFwd = Vector3.forward, _latestUp = Vector3.up;
    bool _hasPose;
    DetectionInfo[] _latestDetections;

    public bool TryGetLatestPose(out Vector3 pos, out Vector3 fwd, out Vector3 up)
    {
        pos = _latestPos; fwd = _latestFwd; up = _latestUp;
        return _hasPose;
    }
    public DetectionInfo[] GetLatestDetections() => _latestDetections;
    public Vector2 GetImageSize() => new Vector2(imageWidth, imageHeight);

    void SyncCalibration()
    {
        if (calibration == null) return;
        anchorObject                  = calibration.anchorObject;
        anchorLatitude                = calibration.anchorLatitude;
        anchorLongitude               = calibration.anchorLongitude;
        horizontalScaleFactor         = calibration.horizontalScaleFactor;
        altitudeReferenceBuilding     = calibration.altitudeReferenceBuilding;
        referenceBuildingHeightMeters = calibration.referenceBuildingHeightMeters;
    }

    void Start()
    {
        if (calibration == null) calibration = FindObjectOfType<CubeGPSDisplay>();
        SyncCalibration();
        if (anchorObject == null)
            Debug.LogWarning("[ProjectionReplay] anchorObject 없음 — 월드 원점(0,0,0) 사용.");
        if (altitudeReferenceBuilding == null)
            Debug.LogWarning("[ProjectionReplay] altitudeReferenceBuilding 없음 — 고도는 anchor.y + cam_alt 폴백.");
        if (teleport == null) teleport = FindObjectOfType<GpsTeleport>();
        if (string.IsNullOrEmpty(projectionCsvPath) || !File.Exists(projectionCsvPath))
        {
            Debug.LogError($"[ProjectionReplay] CSV not found: '{projectionCsvPath}'");
            return;
        }
        StartCoroutine(Replay());
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb[debugToggleKey].wasPressedThisFrame)
        {
            showDebugVisuals = !showDebugVisuals;
            Debug.Log($"[ProjectionReplay] debug visuals {(showDebugVisuals ? "ON" : "OFF")}");
        }
        if (kb[clearDebugKey].wasPressedThisFrame) ClearDebugVisuals();
        if (kb[compareToggleKey].wasPressedThisFrame)
        {
            useIdentityQuat = !useIdentityQuat;
            Debug.Log($"[ProjectionReplay] direction mode = {(useIdentityQuat ? "IDENTITY (회전 무시)" : "WITH QUAT (정상)")}");
        }

        if (diagnosticLogIntervalSec > 0f && Time.time - lastDiagLogTime >= diagnosticLogIntervalSec)
        {
            lastDiagLogTime = Time.time;
            int aliveMarkers = (markerRoot != null) ? markerRoot.childCount : 0;
            int aliveDebug   = (debugRoot  != null) ? debugRoot.childCount  : 0;
            Debug.Log($"[ProjectionReplay] diag: spawnMarkers={markerSpawnCount} spawnDebug={debugSpawnCount} " +
                      $"clears={clearCount} alive(marker={aliveMarkers}, debug={aliveDebug})");
        }
    }

    float ComputeWorldY(float camAlt, float anchorY)
    {
        if (altitudeReferenceBuilding == null) return anchorY + camAlt;
        var r = altitudeReferenceBuilding.GetComponent<Renderer>();
        if (r == null) return anchorY + camAlt;
        float baseY = r.bounds.min.y;
        float topY  = r.bounds.max.y;
        float h     = topY - baseY;
        if (h <= 1e-4f || referenceBuildingHeightMeters <= 0) return anchorY + camAlt;
        float unitsPerMeter = h / (float)referenceBuildingHeightMeters;
        return baseY + camAlt * unitsPerMeter;
    }

    /// 가상의 지면 평면 Y (altRefBuilding bounds 우선, 없으면 anchor.y, 없으면 0).
    float GroundPlaneY()
    {
        if (altitudeReferenceBuilding != null)
        {
            var r = altitudeReferenceBuilding.GetComponent<Renderer>();
            if (r != null) return r.bounds.min.y;
        }
        if (anchorObject != null) return anchorObject.position.y;
        return 0f;
    }

    /// 광선과 지형/지면 평면의 교차점을 구해 검출 위치를 반환.
    bool TryGetDetectionPosition(Vector3 origin, Vector3 dir, out Vector3 pos)
    {
        if (passThroughEnclosingBuilding)
        {
            var hits = Physics.RaycastAll(origin, dir, maxRayDistance, groundMask);
            if (hits != null && hits.Length > 0)
            {
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    var c = hits[i].collider;
                    if (c != null && c.bounds.Contains(origin)) continue; // 그 건물 안 → 통과
                    pos = hits[i].point;
                    return true;
                }
            }
        }
        else if (Physics.Raycast(origin, dir, out RaycastHit hit, maxRayDistance, groundMask))
        {
            pos = hit.point;
            return true;
        }

        if (useGroundPlaneFallback && Mathf.Abs(dir.y) > 1e-6f)
        {
            float planeY = GroundPlaneY();
            float t = (planeY - origin.y) / dir.y;
            if (t > 0f)
            {
                pos = origin + dir * t;
                return true;
            }
        }
        pos = Vector3.zero;
        return false;
    }

    /// Identity-quaternion 가정한 방향 재계산: 픽셀 → 카메라 ray → 폰 ray
    /// (회전 행렬을 단위행렬로) → ENU = phone → Unity 축.
    /// 비교용. 회전을 적용하지 않으면 마커가 어디 떨어지는지 직관적으로 보여줌.
    public Vector3 ComputeIdentityDirection(float u, float v)
    {
        float fx = (imageWidth * 0.5f) / Mathf.Tan(Mathf.Deg2Rad * fovHorizontalDeg * 0.5f);
        float fy = fx;                                                  // 정사각 픽셀 가정
        float cx = imageWidth * 0.5f, cy = imageHeight * 0.5f;
        Vector3 dCam = new Vector3((u - cx) / fx, (v - cy) / fy, 1f).normalized;  // OpenCV cam frame
        Vector3 dPhone = (cameraOrientation == CameraOrientation.LandscapeLeft)
            ? new Vector3( dCam.y,  dCam.x, -dCam.z)                    // R_phone_cam (landscape-left)
            : new Vector3(-dCam.y, -dCam.x, -dCam.z);                   // landscape-right
        // identity quat → d_world_enu = d_phone, 그 다음 enu_to_unity: (e,n,u) → (e,u,n)
        return new Vector3(dPhone.x, dPhone.z, dPhone.y).normalized;
    }

    public Vector3 GpsToWorld(float lat, float lng, float alt)
    {
        SyncCalibration();
        GPSEncoder.SetLocalOrigin(new Vector2((float)anchorLatitude, (float)anchorLongitude));
        Vector3 corrected = GPSEncoder.GPSToUCS(lat, lng);
        Vector3 rel = corrected * (float)horizontalScaleFactor;
        Vector3 basePos = anchorObject != null ? anchorObject.position : Vector3.zero;
        float effAlt = (clampMinAltitudeMeters > 0 && alt < (float)clampMinAltitudeMeters)
            ? (float)clampMinAltitudeMeters : alt;
        float worldY = ComputeWorldY(effAlt, basePos.y) + (float)originUpliftMeters;
        return new Vector3(basePos.x + rel.x, worldY, basePos.z + rel.z);
    }

    class DetRow
    {
        public string className;
        public float conf;
        public float u, v;
        public float camLat, camLng, camAlt;
        public Vector3 dir;
    }
    class FrameGroup
    {
        public int frameId;
        public double sysTime;
        public Vector3 camForward = Vector3.forward;
        public Vector3 camUp = Vector3.up;
        public bool hasPose;
        public List<DetRow> dets = new List<DetRow>();
    }

    IEnumerator Replay()
    {
        // === Phase 1: CSV 전체 로드 + frame_id 그룹핑 (1회) ===
        var filter = new HashSet<string>(classFilter ?? new string[0]);
        bool useFilter = filter.Count > 0;
        var groupMap = new Dictionary<int, FrameGroup>();
        int rowsRead = 0;
        using (var sr = new StreamReader(projectionCsvPath))
        {
            sr.ReadLine(); // header
            while (!sr.EndOfStream)
            {
                string line = sr.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] p = line.Split(',');
                if (p.Length < 13) continue;
                string className = p[3].Trim();
                if (useFilter && !filter.Contains(className)) continue;
                if (!int.TryParse(p[0].Trim(), out int fid)) continue;
                double sysTime = 0;
                double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sysTime);
                if (!groupMap.TryGetValue(fid, out var grp))
                {
                    grp = new FrameGroup { frameId = fid, sysTime = sysTime };
                    groupMap[fid] = grp;
                }
                grp.dets.Add(new DetRow
                {
                    className = className,
                    conf      = ParseF(p[4]),
                    u         = ParseF(p[5]),
                    v         = ParseF(p[6]),
                    camLat    = ParseF(p[7]),
                    camLng    = ParseF(p[8]),
                    camAlt    = ParseF(p[9]),
                    dir       = new Vector3(ParseF(p[10]), ParseF(p[11]), ParseF(p[12])).normalized,
                });
                if (!grp.hasPose && p.Length >= 19)
                {
                    try
                    {
                        grp.camForward = new Vector3(ParseF(p[13]), ParseF(p[14]), ParseF(p[15])).normalized;
                        grp.camUp      = new Vector3(ParseF(p[16]), ParseF(p[17]), ParseF(p[18])).normalized;
                        grp.hasPose    = true;
                    }
                    catch { }
                }
                rowsRead++;
            }
        }
        var frames = new List<FrameGroup>(groupMap.Values);
        frames.Sort((a, b) => a.frameId.CompareTo(b.frameId));
        if (frames.Count == 0)
        {
            Debug.LogWarning($"[ProjectionReplay] 유효 프레임 0 (filter={(useFilter ? "ON" : "OFF")}, rowsRead={rowsRead})");
            yield break;
        }
        double baseSys = frames[0].sysTime;
        Debug.Log($"[ProjectionReplay] loaded {frames.Count} frames, {rowsRead} detections, " +
                  $"pacing: {(rateHz > 0f ? rateHz + " Hz" : "original sys_time timing")}, loop={loop}");

        // === Phase 2: 시간 흐름대로 페이싱 재생 ===
        int passNum = 0;
        while (true)
        {
            passNum++;
            float t0 = Time.time;
            int processedFrames = 0;
            int spawned = 0, missed = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                float target = rateHz > 0f
                    ? t0 + i * (1f / rateHz)
                    : t0 + (float)(frames[i].sysTime - baseSys);
                while (Time.time < target) yield return null;

                // FP/오버레이용 최신 상태 갱신 (프레임 단위)
                if (frames[i].dets.Count > 0)
                {
                    var d0 = frames[i].dets[0];
                    _latestPos = GpsToWorld(d0.camLat, d0.camLng, d0.camAlt);
                    if (frames[i].hasPose)
                    {
                        _latestFwd = frames[i].camForward;
                        _latestUp  = frames[i].camUp;
                        _hasPose   = true;
                    }
                    var infos = new DetectionInfo[frames[i].dets.Count];
                    for (int k = 0; k < infos.Length; k++)
                    {
                        var dr = frames[i].dets[k];
                        infos[k] = new DetectionInfo { className = dr.className, confidence = dr.conf, u = dr.u, v = dr.v };
                    }
                    _latestDetections = infos;
                }

                foreach (var d in frames[i].dets)
                {
                    float camLat = d.camLat, camLng = d.camLng, camAlt = d.camAlt;
                    if (teleport != null && teleport.TryGetOverride(out float oLat, out float oLng, out float oAlt))
                    { camLat = oLat; camLng = oLng; camAlt = oAlt; }

                    Vector3 origin = GpsToWorld(camLat, camLng, camAlt);
                    Color color = ColorFor(d.className);
                    Vector3 dirRay = useIdentityQuat ? ComputeIdentityDirection(d.u, d.v) : d.dir;

                    if (drawDebugRays)
                        Debug.DrawRay(origin, dirRay * rayDrawDistance, color, debugRayDuration);
                    if (showDebugVisuals)
                        SpawnDebugVisuals(origin, dirRay, color);

                    if (TryGetDetectionPosition(origin, dirRay, out Vector3 detPos))
                    {
                        SpawnMarker(detPos, d.className, d.conf, color);
                        spawned++;
                    }
                    else
                    {
                        missed++;
                    }
                }
                processedFrames++;
                if (maxFrames > 0 && processedFrames >= maxFrames) break;
            }
            Debug.Log($"[ProjectionReplay] pass {passNum} done: frames={processedFrames} spawned={spawned} missed={missed}");
            if (!loop) yield break;
        }
    }

    void SpawnMarker(Vector3 pos, string className, float conf, Color color)
    {
        GameObject go;
        if (markerPrefab != null)
        {
            go = Instantiate(markerPrefab, pos, Quaternion.identity);
            TintRecursive(go, color);
        }
        else
        {
            go = new GameObject("DetectionMarker");
            go.transform.position = pos;
            // 클래스별 모양: fire=Cube · smoke=반투명 Cylinder · 그 외=Capsule.
            var st = DetectionMarkerVisual.Settings.Default;
            st.capsuleScale = markerScale;
            DetectionMarkerVisual.BuildForClass(go.transform, className, color, st);
        }
        go.name = $"{className}_{conf:F2}";
        go.AddComponent<DetectionMarker>().Init(className, color);   // 미니맵/범주표 추적
        go.transform.SetParent(GetMarkerRoot(), true);
        if (markerLifetime > 0f) Destroy(go, markerLifetime);
        markerSpawnCount++;
    }

    void SpawnDebugVisuals(Vector3 origin, Vector3 dir, Color color)
    {
        float life = syncRayLifetimeWithMarker ? markerLifetime : debugVisualLifetime;

        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "DebugOrigin";
        sphere.transform.position = origin;
        sphere.transform.localScale = Vector3.one * originSphereScale;
        var c = sphere.GetComponent<Collider>(); if (c != null) Destroy(c);
        var srend = sphere.GetComponent<Renderer>(); if (srend != null) TintRenderer(srend, color);
        sphere.transform.SetParent(GetDebugRoot(), true);
        if (life > 0f) Destroy(sphere, life);
        debugObjects.Add(sphere);

        var lineGo = new GameObject("DebugRay");
        var lr = lineGo.AddComponent<LineRenderer>();
        lr.startWidth = rayLineWidth;
        lr.endWidth   = rayLineWidth;
        lr.positionCount = 2;
        lr.SetPosition(0, origin);
        lr.SetPosition(1, origin + dir.normalized * rayDrawDistance);
        lr.startColor = color;
        lr.endColor   = color;
        var lmat = new Material(Shader.Find("Sprites/Default"));
        lmat.color = color;
        lr.material = lmat;
        lineGo.transform.SetParent(GetDebugRoot(), true);
        if (life > 0f) Destroy(lineGo, life);
        debugObjects.Add(lineGo);
        debugSpawnCount++;
    }

    /// 기존 DebugOrigin/DebugRay 시각물 즉시 일괄 삭제 (C 키).
    void ClearDebugVisuals()
    {
        int n = 0;
        foreach (var go in debugObjects)
        {
            if (go != null) { Destroy(go); n++; }
        }
        debugObjects.Clear();
        clearCount++;
        Debug.Log($"[ProjectionReplay] cleared {n} debug visuals");
    }

    /// Debug 시각물 부모 (수신부 GameObject 하위 'Debug Visuals' 폴더, lazy 생성).
    Transform GetDebugRoot()
    {
        if (debugRoot == null)
        {
            var go = new GameObject("Debug Visuals");
            go.transform.SetParent(transform, false);
            debugRoot = go.transform;
        }
        return debugRoot;
    }

    /// 마커 부모 (수신부 GameObject 하위 'Detection Markers' 폴더, lazy 생성).
    Transform GetMarkerRoot()
    {
        if (markerRoot == null)
        {
            var go = new GameObject("Detection Markers");
            go.transform.SetParent(transform, false);
            markerRoot = go.transform;
        }
        return markerRoot;
    }

    static void TintRecursive(GameObject go, Color c)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>()) TintRenderer(r, c);
    }

    static void TintRenderer(Renderer r, Color c)
    {
        var m = r.material;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
        if (m.HasProperty("_EmissionColor"))
        {
            m.SetColor("_EmissionColor", c * 1.5f);
            m.EnableKeyword("_EMISSION");
        }
    }

    static float ParseF(string s) => float.Parse(s.Trim(), CultureInfo.InvariantCulture);

    static Color ColorFor(string cls) => DetectionClasses.ColorFor(cls);
}
