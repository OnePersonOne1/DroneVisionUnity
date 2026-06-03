using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DroneSim.C2
{
    /// <summary>
    /// /assess 결과(상황 브리핑) 표시 패널. ARInfoPanel(건물 카드) 과 시각적으로 구분.
    ///
    /// 표시: risk 뱃지 색(low=초록/mid=주황/high=빨강) + 시간대 라벨 + briefing 본문 + top 건물 + provenance footer.
    /// DraggableHud 래퍼 — 토글(기본 F12) / 드래그 / 접기 / 리사이즈. Shift+F12 = 기본 크기 리셋.
    /// </summary>
    [DisallowMultipleComponent]
    public class BriefingPanel : MonoBehaviour
    {
        [Header("Refs (비우면 자동)")]
        public SituationAssessClient client;

        [Header("패널")]
        public Vector2 panelSize = new Vector2(540f, 360f);
        public Vector2 panelMargin = new Vector2(20f, 20f);
        [Tooltip("0=좌하 / 1=우상 / 2=좌상 / 3=우하 — 시작 위치만.")]
        public int initialCorner = 3;
        public Color backColor = new Color(0.05f, 0.05f, 0.06f, 0.78f);
        public TMP_FontAsset koreanFont;

        [Header("토글")]
        public Key toggleKey = Key.F12;

        [Header("폰트 크기")]
        [Tooltip("risk 뱃지 라벨.")]
        public float badgeFontSize = 16f;
        [Tooltip("상단 시간/시간대/확산 행.")]
        public float headerFontSize = 16f;
        [Tooltip("브리핑 본문 + priority 목록.")]
        public float bodyFontSize = 15f;
        [Tooltip("priority 부가설명 (용도·재실·거리).")]
        public float bodySubFontSize = 12f;
        [Tooltip("provenance 푸터.")]
        public float footerFontSize = 11f;

        [Header("색")]
        public Color riskLowColor  = new Color(0.30f, 0.85f, 0.30f, 1f);
        public Color riskMidColor  = new Color(1.00f, 0.65f, 0.20f, 1f);
        public Color riskHighColor = new Color(1.00f, 0.30f, 0.30f, 1f);
        public Color titleColor    = Color.white;
        public Color labelColor    = new Color(0.70f, 0.80f, 1.00f, 1f);
        public Color valueColor    = new Color(0.90f, 0.90f, 0.90f, 1f);
        public Color footnoteColor = new Color(0.60f, 0.60f, 0.65f, 1f);

        Canvas _canvas;
        RectTransform _panelRt;
        Image _riskBadge;
        TextMeshProUGUI _riskBadgeText;
        TextMeshProUGUI _headerText;
        TextMeshProUGUI _bodyText;
        TextMeshProUGUI _footerText;

        void Start()
        {
            if (client == null) client = FindObjectOfType<SituationAssessClient>();
            BuildUI();
            if (client != null)
            {
                client.AssessmentUpdated += Render;
                if (client.Latest != null) Render(client.Latest);
            }
            else
            {
                Debug.LogWarning("[BriefingPanel] SituationAssessClient 없음.");
            }
        }

        void OnDestroy()
        {
            if (client != null) client.AssessmentUpdated -= Render;
        }

        void BuildUI()
        {
            var canvasGo = new GameObject("Briefing Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.targetDisplay = 0;
            _canvas.sortingOrder = 96;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            var panel = NewRect("BriefingPanel", _canvas.transform);
            Vector2 a, pos;
            switch (initialCorner)
            {
                case 0:  a = new Vector2(0,0); pos = new Vector2( panelMargin.x,  panelMargin.y); break;
                case 1:  a = new Vector2(1,1); pos = new Vector2(-panelMargin.x, -panelMargin.y); break;
                case 2:  a = new Vector2(0,1); pos = new Vector2( panelMargin.x, -panelMargin.y); break;
                default: a = new Vector2(1,0); pos = new Vector2(-panelMargin.x,  panelMargin.y); break;
            }
            panel.anchorMin = panel.anchorMax = panel.pivot = a;
            panel.sizeDelta = panelSize;
            panel.anchoredPosition = pos;
            _panelRt = panel;
            var bg = panel.gameObject.AddComponent<Image>();
            bg.color = backColor; bg.raycastTarget = true;

            // 상단 risk 뱃지 (좌상단).
            var badgeRt = NewRect("RiskBadge", panel);
            badgeRt.anchorMin = badgeRt.anchorMax = new Vector2(0, 1);
            badgeRt.pivot = new Vector2(0, 1);
            badgeRt.sizeDelta = new Vector2(82, 32);
            badgeRt.anchoredPosition = new Vector2(12, -12);
            _riskBadge = badgeRt.gameObject.AddComponent<Image>();
            _riskBadge.color = riskLowColor; _riskBadge.raycastTarget = false;
            _riskBadgeText = NewTmp(badgeRt, "Lbl", Vector2.zero, Vector2.one, Vector2.zero,
                                    Vector2.zero, Vector2.zero);
            _riskBadgeText.fontSize = badgeFontSize; _riskBadgeText.alignment = TextAlignmentOptions.Center;
            _riskBadgeText.color = Color.black; _riskBadgeText.fontStyle = FontStyles.Bold;
            _riskBadgeText.text = "—";

            // 헤더 우측 — 시간대 + 화재/연기 수.
            _headerText = NewTmp(panel, "Header",
                new Vector2(0,1), new Vector2(1,1), new Vector2(0,1),
                offsetMin: new Vector2(100, -46), offsetMax: new Vector2(-12, -10));
            _headerText.fontSize = headerFontSize; _headerText.alignment = TextAlignmentOptions.TopLeft;
            _headerText.color = labelColor;

            // 본문 — briefing + priority list.
            _bodyText = NewTmp(panel, "Body",
                new Vector2(0,0), new Vector2(1,1), new Vector2(0,1),
                offsetMin: new Vector2(12, 30), offsetMax: new Vector2(-12, -52));
            _bodyText.fontSize = bodyFontSize;
            _bodyText.alignment = TextAlignmentOptions.TopLeft;
            _bodyText.enableWordWrapping = true;
            _bodyText.text = "대기 중… (모의 화재를 주입하면 자동으로 평가됩니다.)";

            // Footer — provenance.
            _footerText = NewTmp(panel, "Footer",
                new Vector2(0,0), new Vector2(1,0), new Vector2(0,0),
                offsetMin: new Vector2(12, 8), offsetMax: new Vector2(-12, 28));
            _footerText.fontSize = footerFontSize; _footerText.alignment = TextAlignmentOptions.Left;
            _footerText.color = footnoteColor;

            // DraggableHud 래퍼.
            var hud = panel.gameObject.AddComponent<DraggableHud>();
            hud.windowTitle = "Situation Briefing";
            hud.toggleKey = toggleKey;
            hud.resizable = true;
            hud.minSize = new Vector2(380, 240);
            hud.maxSize = new Vector2(1400, 900);
            hud.resetSizeKey = toggleKey;
            hud.resetSizeRequiresShift = true;
        }

        public void Render(SituationAssessClient.AssessResult res)
        {
            if (res == null)
            {
                // 화재 0건 또는 리셋 상태 — 대기 메시지.
                _riskBadge.color = footnoteColor;
                _riskBadgeText.text = "—";
                _headerText.text = "<color=#aaaaaa>대기 중</color>";
                _bodyText.text = "모의 화재를 주입하면 자동으로 평가됩니다.  ( ;  fire  /  '  smoke  /  Del  clear )";
                _footerText.text = "";
                return;
            }
            if (res.failed)
            {
                _riskBadge.color = footnoteColor;
                _riskBadgeText.text = "오류";
                _headerText.text = "<color=#ff7878>요청 실패</color>";
                _bodyText.text = !string.IsNullOrEmpty(res.errorText)
                    ? $"<color=#ff7878>/assess 오류</color>\n{res.errorText}"
                    : "<color=#ff7878>/assess 응답 없음 (info_server 가 떠 있는지 확인).</color>";
                _footerText.text = "";
                return;
            }

            // Risk 뱃지.
            string rl = res.risk != null ? res.risk.level : "?";
            _riskBadgeText.text = LevelKo(rl);
            _riskBadge.color = ColorForLevel(rl);

            // Header.
            var p = res.provenance;
            string timeBucketKo = p != null ? (p.time_label_ko ?? p.time_bucket ?? "?") : "?";
            string simTime = p != null ? p.sim_time : "";
            int fc = p != null ? p.fire_count : 0;
            int sc = p != null ? p.smoke_count : 0;
            _headerText.text =
                $"<b>{simTime}</b>\n" +
                $"<color=#cfd0d4>{timeBucketKo}</color>  ·  화재 {fc}건 · 연기 {sc}건  ·  확산 <b>{SpreadingKo(res.spreading)}</b>";

            // Body.
            var sb = new StringBuilder();
            string lc = ColorHex(labelColor);
            string vc = ColorHex(valueColor);
            sb.Append($"<color={vc}>{Escape(res.briefing) ?? ""}</color>");

            // 가까운 119 안전센터 top-5.
            if (res.nearest_fire_stations != null && res.nearest_fire_stations.Count > 0)
            {
                sb.Append("\n\n<color=").Append(lc).Append("><b>가까운 119 안전센터 (top-5)</b></color>");
                int n = Mathf.Min(5, res.nearest_fire_stations.Count);
                for (int i = 0; i < n; i++)
                {
                    var s = res.nearest_fire_stations[i];
                    string name = !string.IsNullOrEmpty(s.name) ? s.name : "(이름미상)";
                    string vtypes = s.vehicle_types != null && s.vehicle_types.Count > 0
                                    ? string.Join(",", s.vehicle_types) : "-";
                    sb.Append("\n");
                    sb.Append($"  <color={lc}>{i + 1}.</color> ");
                    sb.Append($"<b>{Escape(name)}</b>  ");
                    sb.Append($"<size={bodySubFontSize}><color={lc}>");
                    sb.Append($"{s.distance_m:F0}m · {s.vehicle_total}대");
                    if (vtypes != "-") sb.Append($" · [{Escape(vtypes)}]");
                    sb.Append("</color></size>");
                }
            }

            // 권장 차량 (룰 기반).
            if (res.vehicle_recommendation != null && res.vehicle_recommendation.Count > 0)
            {
                sb.Append("\n\n<color=").Append(lc).Append("><b>권장 차량 (룰 산출)</b></color>");
                foreach (var n in res.vehicle_recommendation)
                {
                    string mark = n.ok ? "<color=#7fdf7f>✓</color>" : "<color=#ff8080>⚠</color>";
                    sb.Append("\n  ").Append(mark).Append(' ');
                    sb.Append($"<b>{Escape(n.type)}</b>  ");
                    sb.Append($"<size={bodySubFontSize}><color={lc}>");
                    sb.Append($"필요 {n.needed} · 가용 {n.available} · {Escape(n.reason)}");
                    sb.Append("</color></size>");
                }
            }

            // Hazard flags 요약.
            if (res.hazard_flags != null)
            {
                var hf = res.hazard_flags;
                var marks = new List<string>();
                if (hf.highrise)    marks.Add("고층");
                if (hf.industrial)  marks.Add("산업/위험물");
                if (hf.educational) marks.Add("교육시설");
                if (hf.crowded)     marks.Add("인원밀집");
                if (hf.multi_fire)  marks.Add("다중화재");
                if (hf.hot_zone)    marks.Add("핫존");
                if (marks.Count > 0)
                {
                    sb.Append($"\n\n<size={bodySubFontSize}><color={lc}>현장 특성: {string.Join(", ", marks)}</color></size>");
                }
            }

            if (res.priority_buildings != null && res.priority_buildings.Count > 0)
            {
                sb.Append("\n\n<color=").Append(lc).Append("><b>우선 주목 건물</b></color>");
                int n = Mathf.Min(5, res.priority_buildings.Count);
                for (int i = 0; i < n; i++)
                {
                    var b = res.priority_buildings[i];
                    string occLevel = b.occupancy != null ? b.occupancy.level : "-";
                    int count = b.occupancy != null ? b.occupancy.count_est : 0;
                    sb.Append("\n");
                    sb.Append($"  <color={lc}>{i + 1}.</color> ");
                    sb.Append($"<b>{Escape(b.title)}</b>  ");
                    sb.Append($"<size={bodySubFontSize}><color={lc}>");
                    if (!string.IsNullOrEmpty(b.use)) sb.Append(Escape(b.use)).Append(" · ");
                    sb.Append($"재실 {LevelKo(occLevel)}({count}명) · {b.min_dist_m:F0}m");
                    sb.Append("</color></size>");
                }
            }
            _bodyText.text = sb.ToString();

            // Footer — provenance.
            if (p != null)
            {
                int bk = p.building_keys != null ? p.building_keys.Count : 0;
                int dt = p.detection_ids != null ? p.detection_ids.Count : 0;
                _footerText.text =
                    $"근거: {bk} 건물 · 화재 ID {dt} · 반경 {p.radius_m:F0}m · briefing={p.briefing_source}";
            }
        }

        Color ColorForLevel(string lvl)
        {
            if (lvl == "high") return riskHighColor;
            if (lvl == "mid")  return riskMidColor;
            return riskLowColor;
        }

        static string LevelKo(string lvl)
        {
            if (lvl == "high") return "고";
            if (lvl == "mid")  return "중";
            if (lvl == "low")  return "저";
            return lvl ?? "-";
        }

        static string SpreadingKo(string s)
        {
            if (s == "spreading") return "확산 중";
            if (s == "potential") return "확산 가능";
            if (s == "localized") return "국지적";
            return s ?? "-";
        }

        static string ColorHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // TMP 의 <…> 태그가 사용자 콘텐츠에 끼지 않도록 < 만 치환.
            return s.Replace("<", "<​");
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
            t.color = titleColor; t.fontSize = 14;
            t.alignment = TextAlignmentOptions.Left;
            t.raycastTarget = false;
            var kr = koreanFont != null ? koreanFont : KoreanFont.Get();
            if (kr != null) t.font = kr;
            return t;
        }
    }
}
