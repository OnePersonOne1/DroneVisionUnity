using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// 검출 한 건의 화면 오버레이용 정보 (수신부 → FirstPersonView 공용).
[System.Serializable]
public struct DetectionInfo
{
    public string className;
    public float confidence;
    public float u, v;   // 캡처 이미지 픽셀 좌표
}

/// 1인칭(FP) 시점 + 검출 픽셀 라벨 오버레이.
///
/// - **F 키** 또는 인스펙터 `Is Active` 체크로 켜고 끔.
/// - ON 일 때: 폰 카메라 자세(센서 회전벡터 기반)를 받아 FP 카메라 transform 에 적용,
///   Screen-Space Overlay Canvas 에 검출별 라벨을 (u,v) 픽셀 위치에 표시.
/// - OFF 일 때: FP 카메라 비활성화 + 라벨 숨김. (followCamera 가 지정돼 있으면 함께 토글.)
///
/// 사용:
///   1) Hierarchy 에 빈 GameObject → 이 컴포넌트 추가.
///   2) `Fp Camera` 비워두면 자식에 자동 생성. 직접 지정해도 됨.
///   3) `Follow Camera` 에 기존 추적 카메라 드래그(선택). FP 켜질 때 자동으로 끔.
///   4) F 키로 토글.
public class FirstPersonView : MonoBehaviour
{
    [Header("References (비우면 자동 검색/생성)")]
    public Camera fpCamera;
    public Camera followCamera;
    public ProjectionUdpReceiver udpReceiver;
    public ProjectionReplay replay;

    [Header("Toggle")]
    public bool isActive = false;
    [Tooltip("1인칭 시점 토글 키. (F 는 CubeDroneController 의 pitch-down 과 겹쳐 V 로 변경)")]
    public Key toggleKey = Key.V;

    public enum ViewMode { CubeFree, SensorFollow }

    [Header("시점 모드")]
    [Tooltip("CubeFree = 큐브 1인칭 자유시점(마우스 룩 + 큐브 따라 이동). " +
             "SensorFollow = 폰 센서 자세 그대로 추종(검출 픽셀 라벨 정합용).")]
    public ViewMode viewMode = ViewMode.CubeFree;
    [Tooltip("모드 전환 키.")]
    public Key modeToggleKey = Key.M;
    [Tooltip("CubeFree 에서 카메라가 따라갈 대상(보통 Cube). 비우면 CubeGPSDisplay.cubeObject 자동.")]
    public Transform followTarget;
    [Tooltip("followTarget 기준 카메라 위치 오프셋(월드 단위).")]
    public Vector3 eyeOffset = Vector3.zero;
    [Tooltip("마우스 룩 감도.")]
    public float mouseSensitivity = 2f;
    [Tooltip("ON 이면 우클릭 드래그 중에만 시점 회전(에디터 친화). OFF 면 항상 회전.")]
    public bool requireRightMouseToLook = true;

    [Header("CubeFree 이동 (FPS식, 카메라 기준)")]
    [Tooltip("ON 이면 CubeFree 동안 WASD 가 카메라 시선 기준으로 followTarget 을 직접 이동 " +
             "(그동안 CubeDroneController 비활성). OFF 면 이동에 관여 안 함.")]
    public bool driveTargetInFreeMode = true;
    [Tooltip("이동 속도(unit/s). 맵 스케일에 맞춰 조절.")]
    public float moveSpeed = 500f;
    [Tooltip("상승/하강 키 (조작법.md 와 동일하게 ↑/↓).")]
    public Key upKey = Key.UpArrow;
    public Key downKey = Key.DownArrow;
    [Tooltip("ON 이면 followTarget(큐브)도 카메라 yaw 를 향하도록 회전.")]
    public bool rotateTargetToView = true;
    [Tooltip("CubeFree 동안 비활성화할 CubeDroneController (비우면 followTarget 에서 자동).")]
    public CubeDroneController droneController;

    [Header("중앙 조준점 (Crosshair)")]
    public bool showCrosshair = true;
    public float crosshairSize = 18f;
    public float crosshairThickness = 2f;
    public Color crosshairColor = Color.white;
    GameObject _crosshairRoot;

    [Header("FP Camera")]
    [Tooltip("Python UDP_CAM_FOV_H_DEG 와 동일하게 맞추면 픽셀 → 화면 매핑이 정확해짐.")]
    public float fovHorizontalDeg = 84f;
    [Tooltip("FP 카메라의 near/far clip plane.")]
    public float nearClip = 0.3f;
    public float farClip = 1000000f;

    [Header("픽셀 라벨 오버레이")]
    public bool showLabels = true;
    public float labelFontSize = 18f;
    [Tooltip("라벨 박스 크기(폭, 높이).")]
    public Vector2 labelBoxSize = new Vector2(220f, 30f);

    [Header("내부 건물 숨김 (FP에서 카메라가 안에 있는 건물만 안 보이게)")]
    [Tooltip("ON 이면 FP 카메라가 AABB 안에 들어간 건물 메쉬 중 부피가 가장 작은 1개의 Renderer 를 끔.")]
    public bool hideEnclosingBuilding = true;
    [Tooltip("후보 건물 루트. 비우면 씬 전체 Renderer 자동 스캔.")]
    public Transform buildingsRoot;
    [Tooltip("AABB 안쪽으로 N미터 inset 후 검사 (표면 근처 flicker 방지).")]
    public float boundsInsetMeters = 0.5f;
    [Tooltip("자동 스캔에서 부피 미달 메쉬(마커·아이콘 등) 제외 임계값.")]
    public float minBoundsVolume = 1f;

    Canvas overlayCanvas;
    RectTransform labelRoot;
    readonly List<GameObject> labelPool = new List<GameObject>();
    int activeLabelCount;

    readonly List<Renderer> _buildingCandidates = new List<Renderer>();
    Renderer _hiddenRenderer;

    float _yaw, _pitch;

    void Start()
    {
        if (udpReceiver == null) udpReceiver = FindObjectOfType<ProjectionUdpReceiver>();
        if (replay == null)      replay      = FindObjectOfType<ProjectionReplay>();

        if (fpCamera == null)
        {
            var camGo = new GameObject("FP Camera");
            camGo.transform.SetParent(transform, false);
            fpCamera = camGo.AddComponent<Camera>();
        }
        fpCamera.nearClipPlane = nearClip;
        fpCamera.farClipPlane  = farClip;
        fpCamera.enabled = isActive;
        if (followCamera != null) followCamera.enabled = !isActive;

        // Canvas
        var canvasGo = new GameObject("FP Overlay Canvas");
        canvasGo.transform.SetParent(transform, false);
        overlayCanvas = canvasGo.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        // Label root (Canvas 좌상단 앵커)
        var rootGo = new GameObject("Labels");
        rootGo.transform.SetParent(canvasGo.transform, false);
        labelRoot = rootGo.AddComponent<RectTransform>();
        labelRoot.anchorMin = labelRoot.anchorMax = new Vector2(0f, 1f);
        labelRoot.pivot     = new Vector2(0f, 1f);
        labelRoot.anchoredPosition = Vector2.zero;
        labelRoot.sizeDelta = Vector2.zero;
        labelRoot.gameObject.SetActive(isActive && showLabels);

        CollectBuildingCandidates();

        if (followTarget == null)
        {
            var cg = FindObjectOfType<CubeGPSDisplay>();
            if (cg != null) followTarget = cg.cubeObject;
        }
        if (droneController == null && followTarget != null)
            droneController = followTarget.GetComponent<CubeDroneController>();
        Vector3 e0 = fpCamera.transform.eulerAngles;
        _yaw = e0.y; _pitch = e0.x;

        BuildCrosshair(overlayCanvas.transform);
        if (_crosshairRoot != null) _crosshairRoot.SetActive(showCrosshair);   // 십자선은 양쪽 시점 표시
    }

    void BuildCrosshair(Transform canvasParent)
    {
        _crosshairRoot = new GameObject("Crosshair");
        var rt = _crosshairRoot.AddComponent<RectTransform>();
        rt.SetParent(canvasParent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero;
        MakeBar(rt, new Vector2(crosshairSize, crosshairThickness));   // 가로
        MakeBar(rt, new Vector2(crosshairThickness, crosshairSize));   // 세로
    }

    void MakeBar(Transform parent, Vector2 size)
    {
        var go = new GameObject("Bar");
        var img = go.AddComponent<Image>();   // sprite 없으면 흰 사각형으로 렌더
        img.color = crosshairColor;
        img.raycastTarget = false;
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size; rt.anchoredPosition = Vector2.zero;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb[toggleKey].wasPressedThisFrame) SetActive(!isActive);
            if (kb[modeToggleKey].wasPressedThisFrame)
            {
                viewMode = (viewMode == ViewMode.CubeFree) ? ViewMode.SensorFollow : ViewMode.CubeFree;
                Debug.Log($"[FP] mode = {viewMode}");
            }
        }

        ApplyDroneControllerState();
        if (!isActive) return;

        if (viewMode == ViewMode.CubeFree)
        {
            UpdateFreeCube();
            HideAllLabels();                       // 픽셀 라벨은 SensorFollow 에서만 정확
        }
        else
        {
            UpdatePose();
            if (showLabels) UpdateLabels(); else HideAllLabels();
        }
        UpdateEnclosingHide();
    }

    /// 큐브 1인칭 자유시점: 위치는 followTarget 추종, 방향은 마우스 룩(센서 무시).
    void UpdateFreeCube()
    {
        var kb = Keyboard.current;

        // 1) 마우스 룩 (시선)
        var mouse = Mouse.current;
        if (mouse != null)
        {
            bool look = !requireRightMouseToLook || mouse.rightButton.isPressed;
            if (look)
            {
                Vector2 d = mouse.delta.ReadValue();
                _yaw   += d.x * mouseSensitivity * 0.1f;
                _pitch -= d.y * mouseSensitivity * 0.1f;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            }
        }

        // 2) 카메라 시선(yaw) 기준 WASD 이동 → followTarget 직접 이동 (FPS식)
        if (driveTargetInFreeMode && followTarget != null && kb != null)
        {
            float mv = 0f, mh = 0f, mu = 0f;
            if (kb.wKey.isPressed) mv += 1f;
            if (kb.sKey.isPressed) mv -= 1f;
            if (kb.dKey.isPressed) mh += 1f;
            if (kb.aKey.isPressed) mh -= 1f;
            if (kb[upKey].isPressed)   mu += 1f;
            if (kb[downKey].isPressed) mu -= 1f;
            Quaternion yawOnly = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 move = yawOnly * new Vector3(mh, 0f, mv) + Vector3.up * mu;
            if (move.sqrMagnitude > 1f) move.Normalize();
            followTarget.position += move * moveSpeed * Time.deltaTime;
            if (rotateTargetToView) followTarget.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        // 3) 카메라 위치/회전 적용
        if (followTarget != null)
            fpCamera.transform.position = followTarget.position + eyeOffset;
        fpCamera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        fpCamera.fieldOfView = HorizontalToVerticalFov(fovHorizontalDeg, Mathf.Max(fpCamera.aspect, 0.01f));
    }

    /// CubeFree 자유주행 중에는 CubeDroneController 비활성(WASD 이중 처리 방지), 그 외엔 활성.
    void ApplyDroneControllerState()
    {
        if (droneController == null) return;
        bool fpDriving = isActive && viewMode == ViewMode.CubeFree && driveTargetInFreeMode;
        bool shouldEnable = !fpDriving;
        if (droneController.enabled != shouldEnable)
            droneController.enabled = shouldEnable;
    }

    public void SetActive(bool on)
    {
        isActive = on;
        if (fpCamera != null) fpCamera.enabled = on;
        if (followCamera != null) followCamera.enabled = !on;
        if (labelRoot != null) labelRoot.gameObject.SetActive(on && showLabels);
        if (_crosshairRoot != null) _crosshairRoot.SetActive(showCrosshair);   // 십자선은 양쪽 시점 표시
        if (!on && _hiddenRenderer != null)
        {
            _hiddenRenderer.enabled = true;
            _hiddenRenderer = null;
        }
        ApplyDroneControllerState();
        Debug.Log($"[FP] {(on ? "ON" : "OFF")}");
    }

    void UpdatePose()
    {
        Vector3 pos = Vector3.zero, fwd = Vector3.forward, up = Vector3.up;
        bool have = (udpReceiver != null && udpReceiver.TryGetLatestPose(out pos, out fwd, out up))
                 || (replay     != null && replay.TryGetLatestPose(out pos, out fwd, out up));
        if (!have) return;
        fpCamera.transform.position = pos;
        fpCamera.transform.rotation = Quaternion.LookRotation(fwd, up);
        fpCamera.fieldOfView = HorizontalToVerticalFov(fovHorizontalDeg, Mathf.Max(fpCamera.aspect, 0.01f));
    }

    void UpdateLabels()
    {
        DetectionInfo[] dets = null;
        Vector2 imgSize = new Vector2(3840, 2160);
        if (udpReceiver != null && udpReceiver.TryGetLatestPose(out _, out _, out _))
        {
            dets = udpReceiver.GetLatestDetections();
            imgSize = udpReceiver.GetImageSize();
        }
        else if (replay != null && replay.TryGetLatestPose(out _, out _, out _))
        {
            dets = replay.GetLatestDetections();
            imgSize = replay.GetImageSize();
        }
        if (dets == null) { HideAllLabels(); return; }

        float canvasW = Screen.width;
        float canvasH = Screen.height;
        if (imgSize.x <= 0 || imgSize.y <= 0) { HideAllLabels(); return; }

        for (int i = 0; i < dets.Length; i++)
        {
            var go = EnsureLabel(i);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text     = $"{dets[i].className} {dets[i].confidence:F2}";
            tmp.color    = ColorFor(dets[i].className);
            tmp.fontSize = labelFontSize;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = labelBoxSize;
            float uNorm = dets[i].u / imgSize.x;
            float vNorm = dets[i].v / imgSize.y;
            rt.anchoredPosition = new Vector2(uNorm * canvasW, -vNorm * canvasH);
        }
        for (int i = dets.Length; i < activeLabelCount; i++)
            labelPool[i].SetActive(false);
        activeLabelCount = dets.Length;
    }

    void HideAllLabels()
    {
        for (int i = 0; i < activeLabelCount; i++)
            labelPool[i].SetActive(false);
        activeLabelCount = 0;
    }

    GameObject EnsureLabel(int i)
    {
        while (i >= labelPool.Count)
        {
            var go = new GameObject($"Label_{labelPool.Count}");
            go.transform.SetParent(labelRoot, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = labelBoxSize;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text      = "";
            tmp.fontSize  = labelFontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget      = false;
            // 외곽선 효과로 어두운 배경에서도 잘 보이게
            tmp.fontStyle = FontStyles.Bold;
            labelPool.Add(go);
        }
        labelPool[i].SetActive(true);
        return labelPool[i];
    }

    static float HorizontalToVerticalFov(float hFov, float aspect)
    {
        float h = Mathf.Deg2Rad * hFov * 0.5f;
        return Mathf.Atan(Mathf.Tan(h) / aspect) * 2f * Mathf.Rad2Deg;
    }

    /// 후보 건물(렌더러) 캐시. buildingsRoot 가 있으면 그 하위만, 없으면 씬 전체 스캔.
    /// 부피·MeshFilter·레이어 필터로 마커·아이콘 등 제외.
    void CollectBuildingCandidates()
    {
        _buildingCandidates.Clear();
        Renderer[] all = buildingsRoot != null
            ? buildingsRoot.GetComponentsInChildren<Renderer>(true)
            : FindObjectsOfType<Renderer>();
        foreach (var r in all)
        {
            if (r == null) continue;
            if (r.GetComponent<MeshFilter>() == null) continue;
            Vector3 s = r.bounds.size;
            if (s.x * s.y * s.z < minBoundsVolume) continue;
            _buildingCandidates.Add(r);
        }
        Debug.Log($"[FP] 건물 후보 {_buildingCandidates.Count}개 캐시 (root={(buildingsRoot ? buildingsRoot.name : "scene")})");
    }

    /// 매 프레임: FP 카메라 위치가 AABB 안에 들어간 후보 중 부피 최소 1개의 Renderer 를 끔.
    void UpdateEnclosingHide()
    {
        if (!hideEnclosingBuilding)
        {
            if (_hiddenRenderer != null) { _hiddenRenderer.enabled = true; _hiddenRenderer = null; }
            return;
        }
        if (fpCamera == null || _buildingCandidates.Count == 0) return;

        Vector3 p = fpCamera.transform.position;
        Renderer best = null;
        float bestVol = float.MaxValue;
        for (int i = 0; i < _buildingCandidates.Count; i++)
        {
            var r = _buildingCandidates[i];
            if (r == null) continue;
            var b = r.bounds;
            if (boundsInsetMeters > 0f) b.Expand(-2f * boundsInsetMeters);
            if (b.size.x <= 0f || b.size.y <= 0f || b.size.z <= 0f) continue;
            if (!b.Contains(p)) continue;
            float vol = b.size.x * b.size.y * b.size.z;
            if (vol < bestVol) { bestVol = vol; best = r; }
        }
        if (best == _hiddenRenderer) return;
        if (_hiddenRenderer != null) _hiddenRenderer.enabled = true;
        _hiddenRenderer = best;
        if (best != null) best.enabled = false;
    }

    static Color ColorFor(string cls) => DetectionClasses.ColorFor(cls);
}
