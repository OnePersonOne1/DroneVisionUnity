using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

public class CubeGPSDisplay : MonoBehaviour
{
    [Header("수평 좌표 기준 오브젝트")]
    public Transform anchorObject;

    [Header("Anchor Object의 실제 EPSG:4326 좌표")]
    public double anchorLatitude = 37.384312;
    public double anchorLongitude = 126.655307;

    [Header("GPS 좌표로 변환할 Cube")]
    public Transform cubeObject;

    [Header("화면에 표시할 TextMeshPro")]
    public TMP_Text gpsText;

    [Header("GPS HUD 패널 (표시/크기)")]
    [Tooltip("GPS 상태 패널 루트. 비우면 gpsText 의 부모를 자동 사용.")]
    public GameObject gpsPanel;
    [Tooltip("패널 표시/숨김 토글 키.")]
    public Key togglePanelKey = Key.P;
    public bool panelVisible = true;
    [Tooltip("패널 크기 배율(1=원본). 0.5 = 50%.")]
    [Range(0.1f, 1.5f)] public float panelScale = 0.5f;
    [Tooltip("GPS 캔버스를 Screen-Space Overlay 로 강제 → 1인칭(FP) 카메라에서도 보임. " +
             "레이아웃이 틀어지면 끄세요.")]
    public bool forceOverlayCanvas = true;

    [Header("수평 스케일 보정 계수")]
    public double horizontalScaleFactor = 1.0;

    [Header("고도 기준 건물")]
    public Transform altitudeReferenceBuilding;

    [Header("고도 기준 건물의 실제 높이(m)")]
    public double referenceBuildingHeightMeters = 13.35;

    [Header("현재 GPS 결과")]
    public double currentLatitude;
    public double currentLongitude;
    public double currentAltitude;

    [Header("디버그용")]
    public double buildingBaseY;
    public double buildingTopY;
    public double unityHeight;
    public double unityUnitsPerMeter;

    bool _forcedLogged;

    void Start()
    {
        if (gpsPanel == null && gpsText != null)
            gpsPanel = gpsText.transform.parent != null
                ? gpsText.transform.parent.gameObject : gpsText.gameObject;
        if (!forceOverlayCanvas)
            Debug.Log("[CubeGPSDisplay] forceOverlayCanvas=false — FP 에서 GPS 안 보이면 인스펙터에서 체크하세요.");
        ForceOverlayIfNeeded();
        ApplyPanel();
    }

    void ForceOverlayIfNeeded()
    {
        if (!forceOverlayCanvas) return;
        Canvas canvas = gpsPanel != null ? gpsPanel.GetComponentInParent<Canvas>() : null;
        if (canvas == null && gpsText != null) canvas = gpsText.canvas;
        if (canvas == null) return;
        canvas = canvas.rootCanvas;
        if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            if (!_forcedLogged)
            {
                _forcedLogged = true;
                Debug.Log($"[CubeGPSDisplay] GPS 캔버스 ScreenSpaceOverlay 전환 (name={canvas.name}).");
            }
        }
    }

    void Update()
    {
        HandlePanelToggle();
        ForceOverlayIfNeeded();   // 매 프레임 idempotent — 다른 코드가 캔버스를 되돌려도 다시 강제
        UpdateGPSPanel();
    }

    void HandlePanelToggle()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[togglePanelKey].wasPressedThisFrame)
        {
            panelVisible = !panelVisible;
            ApplyPanel();
            Debug.Log($"[CubeGPSDisplay] GPS 패널 {(panelVisible ? "ON" : "OFF")}");
        }
    }

    void ApplyPanel()
    {
        if (gpsPanel == null) return;
        gpsPanel.transform.localScale = Vector3.one * panelScale;
        // 패널이 이 컴포넌트가 붙은 오브젝트 자신이면 비활성화하면 토글이 멈추므로 스킵.
        if (gpsPanel != gameObject) gpsPanel.SetActive(panelVisible);
    }

    void UpdateGPSPanel()
    {
        if (anchorObject == null || cubeObject == null || gpsText == null)
        {
            return;
        }

        // -----------------------------
        // 1. 위도 / 경도 계산
        // -----------------------------
        Vector3 relativePosition = cubeObject.position - anchorObject.position;
        Vector3 correctedRelativePosition = relativePosition / (float)horizontalScaleFactor;

        GPSEncoder.SetLocalOrigin(new Vector2(
            (float)anchorLatitude,
            (float)anchorLongitude
        ));

        Vector2 gps = GPSEncoder.USCToGPS(correctedRelativePosition);

        currentLatitude = gps.x;
        currentLongitude = gps.y;

        // -----------------------------
        // 2. 고도 계산
        // 기준 건물 바닥 = 0m
        // 기준 건물 옥상 = 13.35m
        // -----------------------------
        currentAltitude = CalculateRelativeAltitude();

        // -----------------------------
        // 3. HUD 출력
        // -----------------------------
        gpsText.text =
            "<b>GPS STATUS</b>\n" +
            "----------------------\n" +
            $"Lat : {currentLatitude:F8}\n" +
            $"Lon : {currentLongitude:F8}\n" +
            $"Alt : {currentAltitude:F1} m";
    }

    double CalculateRelativeAltitude()
    {
        if (altitudeReferenceBuilding == null)
        {
            return cubeObject.position.y;
        }

        Renderer renderer = altitudeReferenceBuilding.GetComponent<Renderer>();

        if (renderer == null)
        {
            return cubeObject.position.y;
        }

        buildingBaseY = renderer.bounds.min.y;
        buildingTopY = renderer.bounds.max.y;
        unityHeight = buildingTopY - buildingBaseY;

        if (unityHeight <= 0.0001)
        {
            return cubeObject.position.y;
        }

        // Unity 상 건물 높이 / 실제 건물 높이
        // 즉, Unity 몇 unit이 실제 1m인지 계산
        unityUnitsPerMeter = unityHeight / referenceBuildingHeightMeters;

        // Cube의 현재 Y가 기준 건물 바닥으로부터 얼마나 높은지 실제 m로 환산
        double altitudeMeters =
            (cubeObject.position.y - buildingBaseY) / unityUnitsPerMeter;

        return altitudeMeters;
    }
}