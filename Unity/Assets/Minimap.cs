using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// 탑다운 미니맵: 드론 위의 직교 카메라를 RenderTexture 에 렌더해 화면 우상단에 표시.
///
/// - 북(+Z)-업 고정. 카메라는 드론의 X,Z 를 따라가며(고도 무관) 위에서 내려다봄.
/// - **드론 위치 = 중앙의 원**(점). **시야 = 활성 카메라의 FOV 를 지면에 투영한 영역**
///   (사다리꼴)을 반투명으로 그려, 카메라가 어디를·얼마나 보고 있는지 표시.
/// - 빈 GameObject 에 이 컴포넌트만 추가(카메라/Canvas 자동 생성). drone 비우면
///   CubeGPSDisplay.cubeObject 자동.
[DisallowMultipleComponent]
public class Minimap : MonoBehaviour
{
    [Header("대상 (비우면 CubeGPSDisplay.cubeObject 자동)")]
    public Transform drone;

    [Header("미니맵 카메라")]
    [Tooltip("보여줄 반경(월드 단위 ≈ m). 클수록 더 넓게(줌아웃).")]
    public float viewRadius = 600f;
    [Tooltip("드론 위로 카메라를 띄울 높이. 건물보다 충분히 높게.")]
    public float cameraHeight = 2000f;
    public float farClip = 12000f;
    public LayerMask renderLayers = ~0;
    public Color backgroundColor = new Color(0.10f, 0.12f, 0.15f, 1f);

    [Header("HUD 패널 (좌하단)")]
    public int textureSize = 256;
    public Vector2 panelSize = new Vector2(260f, 260f);
    public Vector2 panelMargin = new Vector2(16f, 16f);
    [Tooltip("0=좌하단(기본), 1=우상단, 2=좌상단, 3=우하단 — 시작 위치만. 이후 드래그로 자유 이동.")]
    public int initialCorner = 0;

    [Header("드론 위치 마커 (원)")]
    public Color markerColor = Color.red;
    public float markerRadius = 6f;

    [Header("시야(FOV) 표시")]
    public bool showFov = true;
    public Color fovColor = new Color(1f, 0.95f, 0.3f, 0.35f);
    [Tooltip("시야가 지면에 닿는 최대 거리(m). 0 = viewRadius×1.5 자동.")]
    public float fovMaxRangeMeters = 0f;
    [Tooltip("FOV 수평 화각(도). 센서 FOV 와 맞추면 됨.")]
    public float fovAngleDeg = 84f;
    public float fovAspect = 1.7777f;
    [Tooltip("드론 전방이 반대로 보이면 체크.")]
    public bool invertFovDirection = false;

    [Header("선택 건물 마커 (B로 추출, X로 해제)")]
    [Tooltip("BuildingInfoService 비우면 자동 검색.")]
    public BuildingInfoService infoService;
    public float selMarkerRadius = 5f;

    [Header("검출 물체 마커 (추론/projection)")]
    [Tooltip("클래스 색으로 미니맵에 표시. 마커 수명에 따라 자동 생성/소멸.")]
    public bool showDetections = true;
    public float detMarkerRadius = 4f;

    [Header("줌 (마우스 휠 — 패널 위에서)")]
    public bool enableWheelZoom = true;
    public float zoomFactor = 1.2f;
    public float minViewRadius = 30f;
    public float maxViewRadius = 100000f;

    Camera _cam;
    RenderTexture _rt;
    RectTransform _panelRt;
    UIShape _circle, _fov;
    readonly System.Collections.Generic.List<UIShape> _selDots = new System.Collections.Generic.List<UIShape>();
    System.Collections.Generic.List<BuildingInfoService.Selection> _sels;
    readonly System.Collections.Generic.List<UIShape> _detDots = new System.Collections.Generic.List<UIShape>();

    void Start()
    {
        if (drone == null)
        {
            var cg = FindObjectOfType<CubeGPSDisplay>();
            if (cg != null) drone = cg.cubeObject;
        }
        BuildCamera();
        BuildHud();
        if (infoService == null) infoService = FindObjectOfType<BuildingInfoService>();
        if (infoService != null) infoService.SelectionsChanged += OnSelections;
        if (drone == null)
            Debug.LogWarning("[Minimap] 드론 transform 없음 — drone 을 지정하세요.");
        // 명령 입력 자동 부착 (Display 3 와 같은 RMB SetWaypoint + 전체 드론 시각화).
        if (GetComponent<DroneSim.C2.MinimapCommandInput>() == null)
            gameObject.AddComponent<DroneSim.C2.MinimapCommandInput>();
    }

    void OnDestroy()
    {
        if (infoService != null) infoService.SelectionsChanged -= OnSelections;
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
    }

    void OnSelections(System.Collections.Generic.List<BuildingInfoService.Selection> sels)
    {
        _sels = sels;
        for (int i = 0; i < sels.Count; i++)
        {
            var dot = EnsureSelDot(i);
            dot.color = new Color(sels[i].color.r, sels[i].color.g, sels[i].color.b, 1f);
            SetDisk(dot, selMarkerRadius, 18);
        }
        for (int j = sels.Count; j < _selDots.Count; j++)
            _selDots[j].gameObject.SetActive(false);
    }

    UIShape EnsureSelDot(int i)
    {
        while (i >= _selDots.Count)
            _selDots.Add(MakeShape($"SelDot_{_selDots.Count}", Color.white));
        _selDots[i].gameObject.SetActive(true);
        return _selDots[i];
    }

    void BuildCamera()
    {
        _rt = new RenderTexture(textureSize, textureSize, 16) { name = "MinimapRT" };
        var camGo = new GameObject("Minimap Camera");
        camGo.transform.SetParent(transform, false);
        _cam = camGo.AddComponent<Camera>();
        _cam.orthographic = true;
        _cam.orthographicSize = viewRadius;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = backgroundColor;
        _cam.cullingMask = renderLayers;
        _cam.nearClipPlane = 0.3f;
        _cam.farClipPlane = farClip;
        _cam.targetTexture = _rt;
        _cam.depth = -10;
        _cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward); // 북(+Z)=위
    }

    void BuildHud()
    {
        var canvasGo = new GameObject("Minimap Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        var panelGo = new GameObject("MinimapPanel");
        _panelRt = panelGo.AddComponent<RectTransform>();
        _panelRt.SetParent(canvas.transform, false);
        // 시작 코너 — 0=좌하 / 1=우상 / 2=좌상 / 3=우하.
        Vector2 a; Vector2 pos;
        switch (initialCorner)
        {
            case 1:  a = new Vector2(1f, 1f); pos = new Vector2(-panelMargin.x, -panelMargin.y); break;
            case 2:  a = new Vector2(0f, 1f); pos = new Vector2( panelMargin.x, -panelMargin.y); break;
            case 3:  a = new Vector2(1f, 0f); pos = new Vector2(-panelMargin.x,  panelMargin.y); break;
            default: a = new Vector2(0f, 0f); pos = new Vector2( panelMargin.x,  panelMargin.y); break;
        }
        _panelRt.anchorMin = _panelRt.anchorMax = _panelRt.pivot = a;
        _panelRt.sizeDelta = panelSize;
        _panelRt.anchoredPosition = pos;
        // 드래그 가능하게 — raycastTarget=true 로.
        var raw = panelGo.AddComponent<RawImage>();
        raw.texture = _rt;
        raw.raycastTarget = true;

        // 그리기 순서: 맵(패널) → FOV → 위치원. (자식이 부모 위에, 뒤 자식이 앞 자식 아래)
        _fov = MakeShape("FovShape", fovColor);
        _circle = MakeShape("DroneCircle", markerColor);
        SetDisk(_circle, markerRadius, 24);   // 위치원은 정적

        // 드래그+토글+접기 래퍼 — 모든 자식이 _panelRt 에 부착된 다음에 부착해야 한다.
        // 미니맵은 좌하단 고정 + 크기 조절 가능. Shift+M = 기본 크기로 리셋.
        var hud = panelGo.AddComponent<DraggableHud>();
        hud.windowTitle = "Minimap";
        hud.toggleKey = Key.M;
        hud.lockPosition = true;
        hud.resizable = true;
        hud.minSize = new Vector2(150f, 150f);
        hud.maxSize = new Vector2(1200f, 1200f);
        hud.resetSizeKey = Key.M;
        hud.resetSizeRequiresShift = true;
    }

    UIShape MakeShape(string name, Color c)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_panelRt, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = panelSize;
        rt.anchoredPosition = Vector2.zero;
        var s = go.AddComponent<UIShape>();
        s.color = c;
        s.raycastTarget = false;
        return s;
    }

    void Update()
    {
        // 마우스가 미니맵 패널 위일 때만 휠 줌.
        if (!enableWheelZoom || _panelRt == null) return;
        var mouse = Mouse.current;
        if (mouse == null) return;
        Vector2 screen = mouse.position.ReadValue();
        if (!RectTransformUtility.RectangleContainsScreenPoint(_panelRt, screen, null)) return;
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll > 0f) viewRadius = Mathf.Max(minViewRadius, viewRadius / zoomFactor);
        else if (scroll < 0f) viewRadius = Mathf.Min(maxViewRadius, viewRadius * zoomFactor);
    }

    void LateUpdate()
    {
        if (drone == null || _cam == null) return;
        Vector3 p = drone.position;
        _cam.transform.position = new Vector3(p.x, p.y + cameraHeight, p.z);
        _cam.orthographicSize = viewRadius;
        UpdateFov();
        UpdateSelDots();
        UpdateDetDots();
    }

    void UpdateDetDots()
    {
        var all = DetectionMarker.All;
        Vector3 dp = drone.position;
        int shown = showDetections ? all.Count : 0;
        for (int i = 0; i < shown; i++)
        {
            var m = all[i];
            if (m == null) continue;
            Vector3 wp = m.transform.position;
            float dx = wp.x - dp.x, dz = wp.z - dp.z;
            bool inView = Mathf.Abs(dx) <= viewRadius && Mathf.Abs(dz) <= viewRadius;
            var dot = EnsureDetDot(i);
            dot.gameObject.SetActive(inView);
            if (inView)
            {
                dot.color = new Color(m.color.r, m.color.g, m.color.b, 1f);
                ((RectTransform)dot.transform).anchoredPosition = WorldOffsetToUi(dx, dz);
            }
        }
        for (int j = shown; j < _detDots.Count; j++)
            _detDots[j].gameObject.SetActive(false);
    }

    UIShape EnsureDetDot(int i)
    {
        while (i >= _detDots.Count)
        {
            var s = MakeShape($"DetDot_{_detDots.Count}", Color.white);
            SetDisk(s, detMarkerRadius, 14);
            _detDots.Add(s);
        }
        return _detDots[i];
    }

    void UpdateSelDots()
    {
        if (_sels == null || drone == null) return;
        Vector3 dp = drone.position;
        for (int i = 0; i < _sels.Count && i < _selDots.Count; i++)
        {
            float dx = _sels[i].world.x - dp.x, dz = _sels[i].world.z - dp.z;
            bool inView = Mathf.Abs(dx) <= viewRadius && Mathf.Abs(dz) <= viewRadius;
            _selDots[i].gameObject.SetActive(inView);
            if (inView)
                ((RectTransform)_selDots[i].transform).anchoredPosition = WorldOffsetToUi(dx, dz);
        }
    }

    void UpdateFov()
    {
        if (_fov == null) return;
        if (!showFov || drone == null) { _fov.SetMesh(null, null); return; }
        // 드론 자세(전방/우/상) 기반으로 FOV 절두체를 지면에 투영. 렌더 카메라(3인칭 추적
        // 카메라는 뒤를 봄)가 아니라 드론 방향을 쓰므로 시점과 무관하게 일관.
        Vector3 fwd = drone.forward, right = drone.right, up = drone.up;
        if (invertFovDirection) { fwd = -fwd; right = -right; }
        float maxR = fovMaxRangeMeters > 0f ? fovMaxRangeMeters : viewRadius * 1.5f;
        var ground = new Plane(Vector3.up, new Vector3(0f, GroundY(), 0f));
        float hHalf = fovAngleDeg * 0.5f;
        float vHalf = fovAngleDeg / Mathf.Max(0.1f, fovAspect) * 0.5f;
        int[] sx = { -1, 1, 1, -1 }, sy = { -1, -1, 1, 1 };
        var ui = new Vector2[4];
        Vector3 dp = drone.position;
        for (int i = 0; i < 4; i++)
        {
            Vector3 d = Quaternion.AngleAxis(sx[i] * hHalf, up) *
                        (Quaternion.AngleAxis(-sy[i] * vHalf, right) * fwd);
            Ray r = new Ray(dp, d);
            Vector3 gp = (ground.Raycast(r, out float ent) && ent <= maxR)
                ? r.GetPoint(ent) : r.GetPoint(maxR);
            ui[i] = WorldOffsetToUi(gp.x - dp.x, gp.z - dp.z);
        }
        _fov.SetMesh(ui, new int[] { 0, 1, 2, 0, 2, 3 });
    }

    Vector2 WorldOffsetToUi(float dx, float dz)
    {
        // 라이브 panel rect size 사용 — 사용자가 그립으로 resize 했을 때 자동 추종.
        Vector2 size = _panelRt != null ? _panelRt.rect.size : panelSize;
        float hx = size.x * 0.5f, hy = size.y * 0.5f;
        return new Vector2(
            Mathf.Clamp(dx / viewRadius * hx, -hx, hx),
            Mathf.Clamp(dz / viewRadius * hy, -hy, hy));   // 북(+Z)=위(+Y)
    }

    void SetDisk(UIShape s, float radius, int seg)
    {
        var v = new Vector2[seg + 1];
        var t = new int[seg * 3];
        v[0] = Vector2.zero;
        for (int i = 0; i < seg; i++)
        {
            float a = 2f * Mathf.PI * i / seg;
            v[i + 1] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }
        for (int i = 0; i < seg; i++)
        {
            t[i * 3] = 0; t[i * 3 + 1] = 1 + i; t[i * 3 + 2] = 1 + ((i + 1) % seg);
        }
        s.SetMesh(v, t);
    }

    float GroundY()
    {
        if (drone != null &&
            Physics.Raycast(drone.position + Vector3.up * 5f, Vector3.down, out RaycastHit h, 100000f))
            return h.point.y;
        return drone != null ? drone.position.y : 0f;
    }
}
