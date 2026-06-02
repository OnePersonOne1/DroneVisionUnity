using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// 검출 클래스 범주 표(legend): 클래스별 색 스와치 + 한글 라벨 + 실시간 개수.
/// 색은 DetectionClasses(마커/미니맵과 동일 소스). 화면 우상단에 표시(기본). 드래그/접기/토글 키.
/// 빈 GameObject 에 이 컴포넌트만 추가(Canvas 자동 생성).
[DisallowMultipleComponent]
public class ClassLegend : MonoBehaviour
{
    [Header("위치/크기")]
    public Vector2 panelMargin = new Vector2(16f, 16f);
    public float width = 170f;
    public float rowHeight = 26f;
    public float swatchSize = 16f;
    public float fontSize = 16f;
    public Color backColor = new Color(0f, 0f, 0f, 0.55f);
    [Tooltip("각 클래스의 현재 검출 개수도 표시.")]
    public bool showCounts = true;
    public TMP_FontAsset koreanFont;
    [Tooltip("0=좌하 / 1=우상(기본) / 2=좌상 / 3=우하 — 시작 위치만. 이후 드래그로 자유 이동.")]
    public int initialCorner = 1;
    [Tooltip("토글 키 — 패널 보이기/숨기기.")]
    public Key toggleKey = Key.L;

    readonly List<TextMeshProUGUI> _texts = new List<TextMeshProUGUI>();

    void Start() { BuildUI(); }

    void BuildUI()
    {
        var canvasGo = new GameObject("Class Legend Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        var entries = DetectionClasses.All;
        float h = entries.Length * rowHeight + 12f;

        var panel = NewRect("LegendPanel", canvas.transform);
        Vector2 a; Vector2 pos;
        switch (initialCorner)
        {
            case 0:  a = new Vector2(0f, 0f); pos = new Vector2( panelMargin.x,  panelMargin.y); break;
            case 2:  a = new Vector2(0f, 1f); pos = new Vector2( panelMargin.x, -panelMargin.y); break;
            case 3:  a = new Vector2(1f, 0f); pos = new Vector2(-panelMargin.x,  panelMargin.y); break;
            default: a = new Vector2(1f, 1f); pos = new Vector2(-panelMargin.x, -panelMargin.y); break;  // 우상단
        }
        panel.anchorMin = panel.anchorMax = panel.pivot = a;
        panel.sizeDelta = new Vector2(width, h);
        panel.anchoredPosition = pos;
        var bg = panel.gameObject.AddComponent<Image>();
        bg.color = backColor; bg.raycastTarget = true;   // 드래그 hit 받게.

        var kr = koreanFont != null ? koreanFont : KoreanFont.Get();
        for (int i = 0; i < entries.Length; i++)
        {
            float top = -(i * rowHeight) - 6f;
            // 색 스와치
            var sw = NewRect("Swatch", panel);
            sw.anchorMin = sw.anchorMax = sw.pivot = new Vector2(0f, 1f);
            sw.sizeDelta = new Vector2(swatchSize, swatchSize);
            sw.anchoredPosition = new Vector2(8f, top - (rowHeight - swatchSize) * 0.5f);
            var swImg = sw.gameObject.AddComponent<Image>();
            swImg.color = new Color(entries[i].color.r, entries[i].color.g, entries[i].color.b, 1f);
            swImg.raycastTarget = false;
            // 라벨
            var tr = NewRect("Label", panel);
            tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0f, 1f);
            tr.sizeDelta = new Vector2(width - swatchSize - 20f, rowHeight);
            tr.anchoredPosition = new Vector2(8f + swatchSize + 6f, top);
            var t = tr.gameObject.AddComponent<TextMeshProUGUI>();
            t.color = Color.white;
            t.fontSize = fontSize;
            t.alignment = TextAlignmentOptions.Left;
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            if (kr != null) t.font = kr;
            t.text = entries[i].ko;
            _texts.Add(t);
        }

        // 드래그+토글+접기 래퍼 — 모든 자식 추가 후 부착해야 한다.
        var hud = panel.gameObject.AddComponent<DraggableHud>();
        hud.windowTitle = "범례";
        hud.toggleKey = toggleKey;
    }

    RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    void Update()
    {
        if (!showCounts || _texts.Count == 0) return;
        var entries = DetectionClasses.All;
        var counts = new int[entries.Length];
        var all = DetectionMarker.All;
        for (int i = 0; i < all.Count; i++)
        {
            var m = all[i];
            if (m == null) continue;
            for (int k = 0; k < entries.Length; k++)
                if (entries[k].name == m.className) { counts[k]++; break; }
        }
        for (int i = 0; i < _texts.Count && i < entries.Length; i++)
            _texts[i].text = $"{entries[i].ko}  ({counts[i]})";
    }
}
