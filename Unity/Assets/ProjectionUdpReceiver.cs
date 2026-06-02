using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

/// Live-mode receiver for the Python inference pipeline (IP_webcam.py /
/// replay_offline.py via udp_sender.UdpSender).
///
/// 좌표 변환은 팀원 CubeGPSDisplay 의 정확한 역함수. 보정 상수는 씬의
/// CubeGPSDisplay 에서 자동 동기화 (단일 소스).
///
/// 시각화:
///   - 검출 명중 지점에 마커(세로 막대) 자동 생성.
///   - showDebugVisuals 가 켜져 있으면 GPS 원점 구 + 광선 LineRenderer 표시
///     (런타임 Game 뷰에서 보임). O 키로 토글.
///
/// Per-detection wire format (one UDP datagram per frame):
///   {"frame_id":int,"sys_time":double,"detections":[
///       {"class_name":str,"class_id":int,"confidence":float,
///        "u":float,"v":float,
///        "cam_lat":float,"cam_lng":float,"cam_alt":float,
///        "direction":[dx,dy,dz]}
///   ]}
public class ProjectionUdpReceiver : MonoBehaviour
{
    [Header("Network")]
    public int port = 9870;

    [Header("보정 단일 소스 (팀원 CubeGPSDisplay)")]
    [Tooltip("비우면 씬에서 자동 검색. 지정되면 anchorObject/anchor위경도/" +
             "horizontalScaleFactor/altitudeReferenceBuilding/" +
             "referenceBuildingHeightMeters 를 이 컴포넌트에서 그대로 가져온다.")]
    public CubeGPSDisplay calibration;

    [Header("보정값 (calibration 없을 때만 사용 — 팀원 씬값 기본)")]
    public Transform anchorObject;
    public double anchorLatitude = 37.384312;
    public double anchorLongitude = 126.655307;
    [Tooltip("CubeGPSDisplay.horizontalScaleFactor 와 동일 (씬값 1).")]
    public double horizontalScaleFactor = 1.0;
    [Tooltip("고도 기준 건물 Transform.")]
    public Transform altitudeReferenceBuilding;
    [Tooltip("고도 기준 건물의 실제 높이(m). 씬값 13.35.")]
    public double referenceBuildingHeightMeters = 13.35;
    [Tooltip("cam_alt 가 이 값보다 작으면 이 값으로 클램프 (GPS 음수 노이즈 → 광선 원점이 지면 밑으로 박히는 것 방지). 0 = 미사용.")]
    public double clampMinAltitudeMeters = 50.0;
    [Tooltip("최종 worldY 에 추가로 더할 안전 마진(m). 원점을 약간 더 띄워 raycast가 지면 윗쪽에서 시작하도록.")]
    public double originUpliftMeters = 0.0;

    [Header("Refs")]
    [Tooltip("비우면 멀리서도 잘 보이는 세로 막대 마커가 런타임 생성됨.")]
    public GameObject markerPrefab;
    public GpsTeleport teleport;

    [Header("추론 on/off")]
    public bool inferenceEnabled = true;
    [Tooltip("추론 on/off 토글 키.")]
    public Key inferenceToggleKey = Key.I;

    [Header("Raycast")]
    public LayerMask groundMask = ~0;
    public float maxRayDistance = 2000000f;
    [Tooltip("Physics.Raycast 가 메쉬를 못 맞췄을 때 가상의 지면 평면과 광선 교차점에 마커를 찍는다 " +
             "(MeshCollider 미배치 시에도 검출 위치 보임). 평면 Y = altRefBuilding.bounds.min.y → anchor.y → 0 순.")]
    public bool useGroundPlaneFallback = true;
    [Tooltip("광선 시작점이 들어 있는 콜라이더(=그 건물 안)의 hit 는 무시. " +
             "건물 안에서도 외벽을 통과해 바깥 지형/타 건물에 명중하게 함. " +
             "다른 건물의 충돌은 정상 처리.")]
    public bool passThroughEnclosingBuilding = true;

    [Header("Markers")]
    [Tooltip("0 = never destroy. >0 = auto destroy after seconds.")]
    public float markerLifetime = 2f;
    [Tooltip("폴백 마커 기본 굵기/배율(세로 막대). 멀리서도 보이도록 키워라.")]
    public float markerScale = 3f;

    [Header("Debug 시각화 (광선·GPS 원점, 토글 가능)")]
    [Tooltip("켜면 검출별로 GPS 원점 구 + 광선 LineRenderer 가 런타임 Game 뷰에 표시됨.")]
    public bool showDebugVisuals = true;
    [Tooltip("Debug 시각화 on/off 토글 키.")]
    public Key debugToggleKey = Key.O;
    [Tooltip("기존 DebugOrigin/DebugRay 일괄 삭제 키.")]
    public Key clearDebugKey = Key.C;
    [Tooltip("GPS 원점 구의 지름(Unity 단위).")]
    public float originSphereScale = 5f;
    [Tooltip("광선 LineRenderer 두께.")]
    public float rayLineWidth = 0.5f;
    [Tooltip("그려지는 광선의 길이(Unity 단위). 맵 스케일에 따라 크게 잡아라. 짧으면 광선이 지면 도달 전에 끊김.")]
    public float rayDrawDistance = 100000f;
    [Tooltip("0 = 영원, >0 = N초 뒤 자동 소멸. syncRayLifetimeWithMarker 가 켜져 있으면 무시되고 markerLifetime 을 사용.")]
    public float debugVisualLifetime = 2f;
    [Tooltip("켜면 광선/원점 구를 markerLifetime 과 동일한 수명으로 자동 소멸. " +
             "끄면 debugVisualLifetime 사용. 기본 ON.")]
    public bool syncRayLifetimeWithMarker = true;

    [Header("Debug Rays (Scene 뷰 전용, Debug.DrawRay)")]
    public bool drawDebugRays = false;
    public float debugRayDuration = 1f;

    UdpClient client;
    Thread thread;
    volatile bool running;
    readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
    readonly List<GameObject> debugObjects = new List<GameObject>();
    Transform debugRoot, markerRoot;

    public enum CameraOrientation { LandscapeLeft, LandscapeRight }

    [Header("Identity-quat 비교 토글 (J 키)")]
    [Tooltip("ON 이면 폰 자세 쿼터니언을 무시(identity) 한 방향으로 재계산. " +
             "회전 미적용 시 마커가 어디로 떨어지는지 시각 비교.")]
    public bool useIdentityQuat = false;
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

    // FP/오버레이용 최신 프레임 상태
    Vector3 _latestPos, _latestFwd = Vector3.forward, _latestUp = Vector3.up;
    bool _hasPose;
    DetectionInfo[] _latestDetections;

    /// FirstPersonView 등 외부 소비자용 최신 카메라 pose 조회.
    public bool TryGetLatestPose(out Vector3 pos, out Vector3 fwd, out Vector3 up)
    {
        pos = _latestPos; fwd = _latestFwd; up = _latestUp;
        return _hasPose;
    }
    public DetectionInfo[] GetLatestDetections() => _latestDetections;
    public Vector2 GetImageSize() => new Vector2(imageWidth, imageHeight);

    [System.Serializable]
    public class DetectionMsg
    {
        public string class_name;
        public int class_id;
        public float confidence;
        public float u, v;
        public float cam_lat, cam_lng, cam_alt;
        public float[] direction;
    }

    [System.Serializable]
    public class FrameMsg
    {
        public int frame_id;
        public double sys_time;
        public float[] cam_forward;
        public float[] cam_up;
        public DetectionMsg[] detections;
    }

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
            Debug.LogWarning("[UdpRx] anchorObject 없음 — 월드 원점(0,0,0) 사용. " +
                             "씬에 CubeGPSDisplay(anchorObject 지정됨)가 있거나 직접 지정하세요.");
        if (altitudeReferenceBuilding == null)
            Debug.LogWarning("[UdpRx] altitudeReferenceBuilding 없음 — 고도는 anchor.y + cam_alt 폴백.");
        if (teleport == null) teleport = FindObjectOfType<GpsTeleport>();
        try { client = new UdpClient(port); }
        catch (System.Exception e)
        {
            Debug.LogError($"[UdpRx] bind failed on port {port}: {e.Message}");
            enabled = false;
            return;
        }
        running = true;
        thread = new Thread(ReceiveLoop) { IsBackground = true };
        thread.Start();
        Debug.Log($"[UdpRx] UDP {port} listening (anchor {anchorLatitude},{anchorLongitude} " +
                  $"hScale={horizontalScaleFactor} refBldg={(altitudeReferenceBuilding ? altitudeReferenceBuilding.name : "none")} " +
                  $"refH={referenceBuildingHeightMeters}m debugViz={showDebugVisuals})");
    }

    void OnDisable()
    {
        running = false;
        try { client?.Close(); } catch { }
        try { thread?.Join(200); } catch { }
    }

    void ReceiveLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] data = client.Receive(ref any);
                queue.Enqueue(Encoding.UTF8.GetString(data));
            }
            catch (SocketException) { if (running) Debug.LogWarning("[UdpRx] socket error"); break; }
            catch (System.ObjectDisposedException) { break; }
        }
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb[inferenceToggleKey].wasPressedThisFrame)
            {
                inferenceEnabled = !inferenceEnabled;
                Debug.Log($"[UdpRx] inference {(inferenceEnabled ? "ON" : "OFF")}");
            }
            if (kb[debugToggleKey].wasPressedThisFrame)
            {
                showDebugVisuals = !showDebugVisuals;
                Debug.Log($"[UdpRx] debug visuals {(showDebugVisuals ? "ON" : "OFF")}");
            }
            if (kb[clearDebugKey].wasPressedThisFrame) ClearDebugVisuals();
            if (kb[compareToggleKey].wasPressedThisFrame)
            {
                useIdentityQuat = !useIdentityQuat;
                Debug.Log($"[UdpRx] direction mode = {(useIdentityQuat ? "IDENTITY (회전 무시)" : "WITH QUAT (정상)")}");
            }
        }

        if (diagnosticLogIntervalSec > 0f && Time.time - lastDiagLogTime >= diagnosticLogIntervalSec)
        {
            lastDiagLogTime = Time.time;
            int aliveMarkers = (markerRoot != null) ? markerRoot.childCount : 0;
            int aliveDebug   = (debugRoot  != null) ? debugRoot.childCount  : 0;
            Debug.Log($"[UdpRx] diag: spawnMarkers={markerSpawnCount} spawnDebug={debugSpawnCount} " +
                      $"clears={clearCount} alive(marker={aliveMarkers}, debug={aliveDebug})");
        }

        // 항상 큐를 비워 백로그 방지. 추론 OFF면 패킷은 버린다.
        while (queue.TryDequeue(out string json))
            if (inferenceEnabled) ProcessFrame(json);
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

    /// Identity-quaternion 가정한 방향 재계산 (회전 미적용 시 어디로 떨어지는지 비교용).
    public Vector3 ComputeIdentityDirection(float u, float v)
    {
        float fx = (imageWidth * 0.5f) / Mathf.Tan(Mathf.Deg2Rad * fovHorizontalDeg * 0.5f);
        float fy = fx;
        float cx = imageWidth * 0.5f, cy = imageHeight * 0.5f;
        Vector3 dCam = new Vector3((u - cx) / fx, (v - cy) / fy, 1f).normalized;
        Vector3 dPhone = (cameraOrientation == CameraOrientation.LandscapeLeft)
            ? new Vector3( dCam.y,  dCam.x, -dCam.z)
            : new Vector3(-dCam.y, -dCam.x, -dCam.z);
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
    /// 1) Physics.Raycast 가 메쉬 콜라이더에 명중하면 그 지점.
    /// 2) 못 맞췄으면 useGroundPlaneFallback 옵션에 따라 GroundPlaneY() 평면과의 교차점.
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

    void ProcessFrame(string json)
    {
        FrameMsg msg;
        try { msg = JsonUtility.FromJson<FrameMsg>(json); }
        catch { Debug.LogWarning($"[UdpRx] bad JSON ({json.Length} chars)"); return; }
        if (msg == null || msg.detections == null) return;

        // FP/오버레이용 최신 상태 갱신
        if (msg.detections.Length > 0)
        {
            var d0 = msg.detections[0];
            _latestPos = GpsToWorld(d0.cam_lat, d0.cam_lng, d0.cam_alt);
            if (msg.cam_forward != null && msg.cam_forward.Length >= 3)
                _latestFwd = new Vector3(msg.cam_forward[0], msg.cam_forward[1], msg.cam_forward[2]).normalized;
            if (msg.cam_up != null && msg.cam_up.Length >= 3)
                _latestUp  = new Vector3(msg.cam_up[0],      msg.cam_up[1],      msg.cam_up[2]).normalized;
            _hasPose = msg.cam_forward != null && msg.cam_up != null;
            var infos = new DetectionInfo[msg.detections.Length];
            for (int i = 0; i < infos.Length; i++)
            {
                var d = msg.detections[i];
                infos[i] = new DetectionInfo
                {
                    className = d.class_name, confidence = d.confidence,
                    u = d.u, v = d.v,
                };
            }
            _latestDetections = infos;
        }

        foreach (var d in msg.detections)
        {
            if (d.direction == null || d.direction.Length < 3) continue;

            float camLat = d.cam_lat, camLng = d.cam_lng, camAlt = d.cam_alt;
            if (teleport != null && teleport.TryGetOverride(out float oLat, out float oLng, out float oAlt))
            { camLat = oLat; camLng = oLng; camAlt = oAlt; }

            Vector3 origin = GpsToWorld(camLat, camLng, camAlt);
            Vector3 dir = useIdentityQuat
                ? ComputeIdentityDirection(d.u, d.v)
                : new Vector3(d.direction[0], d.direction[1], d.direction[2]).normalized;
            Color color = ColorFor(d.class_name);

            if (drawDebugRays)
                Debug.DrawRay(origin, dir * rayDrawDistance, color, debugRayDuration);
            if (showDebugVisuals)
                SpawnDebugVisuals(origin, dir, color);

            if (TryGetDetectionPosition(origin, dir, out Vector3 detPos))
                SpawnMarker(detPos, d.class_name, d.confidence, color);
        }
    }

    /// 세로 막대 + 발광 머티리얼로 멀리서도 보이는 마커.
    /// markerPrefab 이 있으면 그걸 Instantiate, 없으면 런타임 생성.
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
            // 폴백: 빈 root(피벗=바닥) + Capsule 자식
            go = new GameObject("DetectionMarker");
            go.transform.position = pos;
            var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.name = "Visual";
            vis.transform.SetParent(go.transform, false);
            float s = markerScale;
            vis.transform.localScale    = new Vector3(s, s * 5f, s);             // 폭 s × 높이 ~10s m
            vis.transform.localPosition = new Vector3(0f, vis.transform.localScale.y, 0f); // 피벗=바닥
            var c = vis.GetComponent<Collider>(); if (c != null) Destroy(c);
            var r = vis.GetComponent<Renderer>(); if (r != null) TintRenderer(r, color);
        }
        go.name = $"{className}_{conf:F2}";
        go.AddComponent<DetectionMarker>().Init(className, color);   // 미니맵/범주표 추적
        go.transform.SetParent(GetMarkerRoot(), true);
        if (markerLifetime > 0f) Destroy(go, markerLifetime);
        markerSpawnCount++;
    }

    /// 광선 + GPS 원점을 런타임 Game 뷰에 표시 (showDebugVisuals 토글).
    void SpawnDebugVisuals(Vector3 origin, Vector3 dir, Color color)
    {
        // 광선/원점 수명: 옵션에 따라 marker 와 동기 또는 독립.
        float life = syncRayLifetimeWithMarker ? markerLifetime : debugVisualLifetime;

        // GPS 원점 구
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "DebugOrigin";
        sphere.transform.position = origin;
        sphere.transform.localScale = Vector3.one * originSphereScale;
        var c = sphere.GetComponent<Collider>(); if (c != null) Destroy(c);
        var srend = sphere.GetComponent<Renderer>(); if (srend != null) TintRenderer(srend, color);
        sphere.transform.SetParent(GetDebugRoot(), true);
        if (life > 0f) Destroy(sphere, life);
        debugObjects.Add(sphere);

        // 광선 LineRenderer
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
        Debug.Log($"[UdpRx] cleared {n} debug visuals");
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
        var m = r.material; // instance
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
        if (m.HasProperty("_EmissionColor"))
        {
            m.SetColor("_EmissionColor", c * 1.5f);
            m.EnableKeyword("_EMISSION");
        }
    }

    static Color ColorFor(string cls) => DetectionClasses.ColorFor(cls);
}
