using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DroneSim.C2
{
    /// <summary>
    /// SimClock 제어 패널 — DraggableHud 래퍼.
    ///
    /// 표시: 현재 sim_time (HH:mm, 한글 시간대 라벨) + 프리셋 5 버튼 + 수동 시:분 입력.
    /// 토글 키: 기본 Z. 드래그/접기/리사이즈 + Shift+Z 로 기본 크기 리셋.
    /// </summary>
    [DisallowMultipleComponent]
    public class SimClockUI : MonoBehaviour
    {
        [Header("Refs (비우면 자동)")]
        public SimClock clock;

        [Header("패널")]
        public Vector2 panelSize = new Vector2(360f, 200f);
        public Vector2 panelMargin = new Vector2(20f, 20f);
        [Tooltip("0=좌하 / 1=우상 / 2=좌상 / 3=우하 — 시작 위치만, 드래그로 이동.")]
        public int initialCorner = 2;
        public Color backColor = new Color(0f, 0f, 0f, 0.62f);
        public TMP_FontAsset koreanFont;

        [Header("토글")]
        public Key toggleKey = Key.Z;

        Canvas _canvas;
        RectTransform _panelRt;
        TextMeshProUGUI _statusText;

        void Start()
        {
            if (clock == null) clock = FindObjectOfType<SimClock>();
            if (clock == null)
            {
                // 자동 생성 — 빈 GO 에 SimClock 만 부착해 다른 컴포넌트도 접근 가능.
                var go = new GameObject("SimClock");
                clock = go.AddComponent<SimClock>();
            }
            BuildUI();
            clock.OnTimeChanged += RefreshStatus;
            RefreshStatus();
        }

        void OnDestroy()
        {
            if (clock != null) clock.OnTimeChanged -= RefreshStatus;
        }

        void Update() { RefreshStatus(); }   // 실시간 모드일 때 분 업데이트.

        void BuildUI()
        {
            var canvasGo = new GameObject("SimClock Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.targetDisplay = 0;
            _canvas.sortingOrder = 95;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            var panel = NewRect("SimClockPanel", _canvas.transform);
            Vector2 a, pos;
            switch (initialCorner)
            {
                case 0:  a = new Vector2(0,0); pos = new Vector2( panelMargin.x,  panelMargin.y); break;
                case 1:  a = new Vector2(1,1); pos = new Vector2(-panelMargin.x, -panelMargin.y); break;
                case 3:  a = new Vector2(1,0); pos = new Vector2(-panelMargin.x,  panelMargin.y); break;
                default: a = new Vector2(0,1); pos = new Vector2( panelMargin.x, -panelMargin.y); break;
            }
            panel.anchorMin = panel.anchorMax = panel.pivot = a;
            panel.sizeDelta = panelSize;
            panel.anchoredPosition = pos;
            _panelRt = panel;
            var bg = panel.gameObject.AddComponent<Image>();
            bg.color = backColor; bg.raycastTarget = true;

            // 상태 텍스트.
            _statusText = NewTmp(panel, "Status",
                new Vector2(0,1), new Vector2(1,1), new Vector2(0,1),
                offsetMin: new Vector2(10, -56), offsetMax: new Vector2(-10, -10));
            _statusText.fontSize = 18; _statusText.alignment = TextAlignmentOptions.TopLeft;

            // 프리셋 버튼 5개 — 한 줄.
            string[] labels = { "실시간", "08:00", "12:00", "18:00", "02:00" };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                MakeButton(panel, labels[i], new Vector2(10 + i * 68, -100),
                    new Vector2(58, 32), () => {
                        if (idx == 0) clock.ClearOverride();
                        else clock.Preset(idx - 1);
                    });
            }

            // 수동 입력 라벨 + h/m InputField + Apply.
            MakeLabel(panel, "수동:", new Vector2(10, -150), new Vector2(50, 28));
            var hField = MakeInputField(panel, new Vector2(64, -150), new Vector2(50, 28), "HH");
            var mField = MakeInputField(panel, new Vector2(120, -150), new Vector2(50, 28), "MM");
            MakeButton(panel, "적용", new Vector2(178, -150), new Vector2(60, 32), () => {
                if (int.TryParse(hField.text, out int h) && int.TryParse(mField.text, out int m))
                    clock.SetOverride(h, m);
            });
            MakeButton(panel, "해제", new Vector2(244, -150), new Vector2(60, 32),
                () => clock.ClearOverride());

            // DraggableHud 래퍼 — 모든 자식 추가 후 부착.
            var hud = panel.gameObject.AddComponent<DraggableHud>();
            hud.windowTitle = "Sim Clock";
            hud.toggleKey = toggleKey;
            hud.resizable = true;
            hud.minSize = new Vector2(280, 180);
            hud.maxSize = new Vector2(800, 500);
        }

        void RefreshStatus()
        {
            if (clock == null || _statusText == null) return;
            var dt = clock.NowKst();
            string mode = clock.useOverride ? "<color=#ffd03c>OVERRIDE</color>" : "<color=#9cf09c>실시간</color>";
            string bucket = TimeBucketLabel(dt.Hour, dt.Minute);
            _statusText.text =
                $"[{mode}]  {dt:HH:mm}\n<size=14>(KST · {bucket})</size>";
        }

        static string TimeBucketLabel(int hour, int minute)
        {
            float h = hour + (minute / 60f);
            if (7 <= h && h < 9.5f)    return "출근 시간대";
            if (11.5 <= h && h < 13.5) return "점심 시간대";
            if (17 <= h && h < 20)     return "퇴근 시간대";
            if (h >= 22 || h < 6)      return "심야";
            return "주간 업무 시간";
        }

        // ── UI 헬퍼 ─────────────────────────────────────────────────────
        RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        TextMeshProUGUI NewTmp(Transform parent, string name,
                               Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
                               Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.color = Color.white; t.fontSize = 16;
            t.alignment = TextAlignmentOptions.Left;
            t.raycastTarget = false;
            var kr = koreanFont != null ? koreanFont : KoreanFont.Get();
            if (kr != null) t.font = kr;
            return t;
        }

        void MakeLabel(Transform parent, string text, Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject("Label");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.color = Color.white; t.fontSize = 16; t.alignment = TextAlignmentOptions.Left;
            t.raycastTarget = false;
            var kr = koreanFont != null ? koreanFont : KoreanFont.Get();
            if (kr != null) t.font = kr;
            t.text = text;
        }

        Button MakeButton(Transform parent, string label, Vector2 anchoredPos, Vector2 size, Action onClick)
        {
            var go = new GameObject($"Btn_{label}");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.18f, 0.22f, 0.30f, 0.92f);
            img.raycastTarget = true;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var lblGo = new GameObject("Label");
            var lrt = lblGo.AddComponent<RectTransform>();
            lrt.SetParent(rt, false);
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var t = lblGo.AddComponent<TextMeshProUGUI>();
            t.color = Color.white; t.fontSize = 14;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            var kr = koreanFont != null ? koreanFont : KoreanFont.Get();
            if (kr != null) t.font = kr;
            t.text = label;
            return btn;
        }

        TMP_InputField MakeInputField(Transform parent, Vector2 anchoredPos, Vector2 size, string placeholder)
        {
            var go = new GameObject($"IF_{placeholder}");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.10f);
            bg.raycastTarget = true;
            var field = go.AddComponent<TMP_InputField>();

            // Text Area + Text + Placeholder children.
            var areaGo = new GameObject("Text Area");
            var art = areaGo.AddComponent<RectTransform>();
            art.SetParent(rt, false);
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(6, 2); art.offsetMax = new Vector2(-6, -2);
            areaGo.AddComponent<RectMask2D>();

            var textGo = new GameObject("Text");
            var trt = textGo.AddComponent<RectTransform>();
            trt.SetParent(art, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            var txt = textGo.AddComponent<TextMeshProUGUI>();
            txt.color = Color.white; txt.fontSize = 16; txt.alignment = TextAlignmentOptions.Left;
            txt.raycastTarget = false;
            var kr = koreanFont != null ? koreanFont : KoreanFont.Get();
            if (kr != null) txt.font = kr;

            var phGo = new GameObject("Placeholder");
            var prt = phGo.AddComponent<RectTransform>();
            prt.SetParent(art, false);
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            var pht = phGo.AddComponent<TextMeshProUGUI>();
            pht.color = new Color(1f, 1f, 1f, 0.45f); pht.fontSize = 16;
            pht.alignment = TextAlignmentOptions.Left;
            pht.raycastTarget = false;
            pht.text = placeholder;
            if (kr != null) pht.font = kr;

            field.textViewport = art;
            field.textComponent = txt;
            field.placeholder = pht;
            field.contentType = TMP_InputField.ContentType.IntegerNumber;
            field.characterLimit = 2;
            return field;
        }
    }
}
