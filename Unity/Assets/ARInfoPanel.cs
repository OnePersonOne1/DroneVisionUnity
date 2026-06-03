using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// [Phase 3] 선택된 건물 정보를 1인칭 화면 HUD에 **색상별 다중 카드**로 표시.
///
/// BuildingInfoService.SelectionsChanged 를 구독해, 선택된 건물마다 카드를 하나씩
/// 쌓아 보여준다. 각 카드 색(제목/좌측 띠) = 그 건물의 선택 색 = 건물 풋프린트
/// 오버레이 색(1:1 매칭). X(서비스) 로 전체 해제. 화면공간이라 맵 스케일 무관.
///
/// 빈 GameObject 에 이 컴포넌트만 추가(Canvas 자동 생성, Service 자동 검색).
[DisallowMultipleComponent]
public class ARInfoPanel : MonoBehaviour
{
    [Header("Refs (비우면 자동 검색)")]
    public BuildingInfoService service;

    [Header("카드")]
    [Tooltip("폭 고정. 높이는 텍스트 preferredHeight + verticalPadding 으로 자동 산정 (cardSize.y 는 최소값으로만 작용).")]
    public Vector2 cardSize = new Vector2(560f, 116f);
    public float cardGap = 8f;
    [Tooltip("카드 위·아래 안쪽 여백 (textRect.offsetMin.y + offsetMax.y 의 절댓값 합).")]
    public float verticalPadding = 18f;
    [Tooltip("좌상단으로부터 (x, y) 픽셀 여백. 위에서 아래로 stack.")]
    public Vector2 topLeftMargin = new Vector2(20f, 20f);
    public float bottomMargin = 120f;        // legacy, 미사용 (씬 직렬화 호환).
    public float titleFontSize = 26f;
    public float bodyFontSize = 17f;
    public Color cardBackColor = new Color(0f, 0f, 0f, 0.62f);
    [Tooltip("한글 TMP 폰트. 비우면 OS 한글 폰트로 런타임 생성.")]
    public TMP_FontAsset koreanFont;
    [Tooltip("이 시간(초) 안에 결과가 오면 '조회 중…' 을 띄우지 않음(깜빡임 방지).")]
    public float loadingDelay = 0.25f;

    [Header("카드 버튼")]
    public Vector2 buttonSize = new Vector2(26f, 26f);
    public Color pinOffColor = new Color(1f, 1f, 1f, 0.55f);
    public Color pinOnColor = new Color(1f, 0.82f, 0.25f, 1f);
    public Color deleteColor = new Color(1f, 0.55f, 0.55f, 1f);
    public Color buttonBgColor = new Color(1f, 1f, 1f, 0.08f);

    Canvas _canvas;
    GameObject _loading;
    TextMeshProUGUI _loadingText;
    Coroutine _pendingLoad;
    float _nextStackY;     // 마지막 Render 직후 다음 카드/loading 이 들어갈 y (anchor=(0,1) 기준 음수).

    class Card
    {
        public GameObject root;
        public Image strip;
        public TextMeshProUGUI text;
        public Button pinBtn;
        public TextMeshProUGUI pinLabel;
        public Button deleteBtn;
        public string boundKey;        // 어느 selection 에 묶여 있는지 — listener 재바인딩 추적.
    }
    readonly List<Card> _cards = new List<Card>();

    void Start()
    {
        if (service == null) service = FindObjectOfType<BuildingInfoService>();
        BuildCanvas();
        if (service != null)
        {
            service.SelectionsChanged += Render;
            service.InfoRequested += OnRequested;
        }
        else Debug.LogWarning("[ARInfoPanel] BuildingInfoService 없음 — 표시할 정보를 받을 수 없음.");
    }

    void OnDestroy()
    {
        if (service != null)
        {
            service.SelectionsChanged -= Render;
            service.InfoRequested -= OnRequested;
        }
    }

    void BuildCanvas()
    {
        var go = new GameObject("AR Info Canvas");
        go.transform.SetParent(transform, false);
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        go.AddComponent<GraphicRaycaster>();

        _loading = NewPanel("Loading", cardBackColor);
        var lrt = (RectTransform)_loading.transform;
        lrt.sizeDelta = new Vector2(220f, 40f);
        _loadingText = NewText(_loading.transform, "<b>조회 중…</b>");
        _loading.SetActive(false);

        _nextStackY = -topLeftMargin.y;
    }

    GameObject NewPanel(string name, Color bg)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_canvas.transform, false);
        // 좌상단 정렬 — 위에서 아래로 stack.
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        var img = go.AddComponent<Image>();
        img.color = bg;
        // 카드 배경은 raycastTarget=true 로 — 카드 위에서 클릭이 뒤(3D 카메라)로 새지 않도록.
        img.raycastTarget = true;
        return go;
    }

    TextMeshProUGUI NewText(Transform parent, string s)
    {
        var go = new GameObject("Text");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(16, 8); rt.offsetMax = new Vector2(-12, -8);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.color = Color.white;
        t.fontSize = bodyFontSize;
        t.alignment = TextAlignmentOptions.TopLeft;
        t.raycastTarget = false;
        t.enableWordWrapping = true;
        var kr = koreanFont != null ? koreanFont : ResolveKoreanFont();
        if (kr != null) t.font = kr;
        t.text = s;
        return t;
    }

    Card EnsureCard(int i)
    {
        while (i >= _cards.Count)
        {
            var card = new Card();
            card.root = NewPanel($"Card_{_cards.Count}", cardBackColor);
            ((RectTransform)card.root.transform).sizeDelta = cardSize;
            // 좌측 색 띠
            var stripGo = new GameObject("Strip");
            var srt = stripGo.AddComponent<RectTransform>();
            srt.SetParent(card.root.transform, false);
            srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(0f, 1f);
            srt.pivot = new Vector2(0f, 0.5f);
            srt.sizeDelta = new Vector2(7f, 0f); srt.anchoredPosition = Vector2.zero;
            card.strip = stripGo.AddComponent<Image>();
            card.strip.raycastTarget = false;

            // 본문 텍스트 — 우측 패딩을 늘려 핀/× 버튼 자리 확보.
            card.text = NewText(card.root.transform, "");
            var trt = (RectTransform)card.text.transform;
            float btnLane = buttonSize.x * 2f + 14f;   // 두 버튼 + 사이 간격.
            trt.offsetMax = new Vector2(-btnLane, -8f);

            // 핀 버튼 (우상단 — × 보다 왼쪽).
            card.pinBtn = MakeIconButton(card.root.transform, "Pin", "P",
                rightOffset: -(buttonSize.x + 8f), topOffset: -6f, out card.pinLabel);
            // 삭제 버튼 (우상단).
            card.deleteBtn = MakeIconButton(card.root.transform, "Del", "×",
                rightOffset: -6f, topOffset: -6f, out var delLabel);
            if (delLabel != null) delLabel.color = deleteColor;

            _cards.Add(card);
        }
        return _cards[i];
    }

    Button MakeIconButton(Transform parent, string name, string label,
                          float rightOffset, float topOffset, out TextMeshProUGUI labelTmp)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.sizeDelta = buttonSize;
        rt.anchoredPosition = new Vector2(rightOffset, topOffset);

        var bg = go.AddComponent<Image>();
        bg.color = buttonBgColor;
        bg.raycastTarget = true;
        var btn = go.AddComponent<Button>();
        btn.transition = Selectable.Transition.ColorTint;
        var col = btn.colors;
        col.normalColor = Color.white;
        col.highlightedColor = new Color(1f, 1f, 1f, 1.2f);
        col.pressedColor = new Color(1f, 1f, 1f, 0.7f);
        btn.colors = col;
        btn.targetGraphic = bg;

        var lblGo = new GameObject("Label");
        var lrt = lblGo.AddComponent<RectTransform>();
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        labelTmp = lblGo.AddComponent<TextMeshProUGUI>();
        labelTmp.text = label;
        labelTmp.color = Color.white;
        labelTmp.fontSize = bodyFontSize + 1f;
        labelTmp.fontStyle = FontStyles.Bold;
        labelTmp.alignment = TextAlignmentOptions.Center;
        labelTmp.raycastTarget = false;
        var kr = koreanFont != null ? koreanFont : ResolveKoreanFont();
        if (kr != null) labelTmp.font = kr;
        return btn;
    }

    void Render(List<BuildingInfoService.Selection> sels)
    {
        if (_pendingLoad != null) { StopCoroutine(_pendingLoad); _pendingLoad = null; }
        if (_loading != null) _loading.SetActive(false);

        // 카드 누적 y 커서 — 위에서 아래로 가변 높이 stack.
        float yCursor = -topLeftMargin.y;
        for (int i = 0; i < sels.Count; i++)
        {
            var sel = sels[i];
            var card = EnsureCard(i);
            card.root.SetActive(true);
            var rt = (RectTransform)card.root.transform;

            // 폭 먼저 고정 → 텍스트 stretch 가 정상 폭을 가짐 → preferredHeight 정확.
            rt.sizeDelta = new Vector2(cardSize.x, cardSize.y);

            Color opaque = new Color(sel.color.r, sel.color.g, sel.color.b, 1f);
            card.strip.color = opaque;
            card.text.text = Format(sel.info, opaque);

            // TMP preferredHeight 강제 갱신 후 카드 높이 산정.
            card.text.ForceMeshUpdate();
            float textH = card.text.preferredHeight;
            float cardH = Mathf.Max(cardSize.y, textH + verticalPadding);
            rt.sizeDelta = new Vector2(cardSize.x, cardH);
            rt.anchoredPosition = new Vector2(topLeftMargin.x, yCursor);
            yCursor -= (cardH + cardGap);

            // 핀 상태 라벨/색.
            if (card.pinLabel != null)
            {
                card.pinLabel.text = sel.pinned ? "●" : "○";
                card.pinLabel.color = sel.pinned ? pinOnColor : pinOffColor;
            }

            // 버튼 listener 재바인딩 — pooled card 가 다른 selection 에 재사용되므로 매번 갱신.
            string keyCopy = sel.key;
            card.boundKey = keyCopy;
            if (card.pinBtn != null)
            {
                card.pinBtn.onClick.RemoveAllListeners();
                card.pinBtn.onClick.AddListener(() => { if (service != null) service.TogglePin(keyCopy); });
            }
            if (card.deleteBtn != null)
            {
                card.deleteBtn.onClick.RemoveAllListeners();
                card.deleteBtn.onClick.AddListener(() => { if (service != null) service.RemoveSelection(keyCopy); });
            }
        }
        for (int j = sels.Count; j < _cards.Count; j++)
            _cards[j].root.SetActive(false);

        _nextStackY = sels.Count > 0 ? yCursor : -topLeftMargin.y;
    }

    // v2 스키마 카드 — 6 항목을 한글 라벨로 표시:
    //   <건물명>  [종류]
    //   주소: ...
    //   층수: ...
    //   높이: ... m
    //   개요: ...
    // 값 없는 필드는 라인 자체 생략. JSON 키는 영어, 표시 라벨만 한글.
    string Format(BuildingInfoService.BuildingResult r, Color titleColor)
    {
        var info = r != null ? r.info : null;
        string title = info != null && !string.IsNullOrEmpty(info.title) ? info.title
                       : (r != null && !string.IsNullOrEmpty(r.name) ? r.name : "건물");
        string cat = info != null ? info.category : "";
        string addr = info != null ? info.address : (r != null ? r.address : "");
        string floors = info != null ? info.floors : "";
        string use = info != null ? info.use : "";
        string approval = info != null ? info.approval_date : "";
        string sum = info != null ? info.summary : "";
        float hM = info != null ? info.height_m : 0f;

        string hex = ColorUtility.ToHtmlStringRGB(titleColor);
        string labelColor = "#aaccff";    // 보조 라벨 회청색
        string valueColor = "#dddddd";    // 본문 값

        var sb = new StringBuilder();
        sb.Append($"<size={titleFontSize}><color=#{hex}><b>{title}</b></color></size>");
        if (!string.IsNullOrEmpty(cat))
            sb.Append($"  <color={labelColor}>[{cat}]</color>");

        AppendField(sb, labelColor, valueColor, "주소", addr);
        AppendField(sb, labelColor, valueColor, "층수", floors);
        AppendField(sb, labelColor, valueColor, "높이",
            hM > 0f ? hM.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " m" : null);
        AppendField(sb, labelColor, valueColor, "용도", use);
        AppendField(sb, labelColor, valueColor, "사용승인", approval);
        AppendField(sb, labelColor, valueColor, "개요", sum);

        return sb.ToString();
    }

    static void AppendField(StringBuilder sb, string labelColor, string valueColor,
                            string label, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append($"\n<color={labelColor}>{label}:</color> <color={valueColor}>{value}</color>");
    }

    void OnRequested(Vector3 hit)
    {
        if (_pendingLoad != null) StopCoroutine(_pendingLoad);
        _pendingLoad = StartCoroutine(ShowLoadingDelayed());
    }

    IEnumerator ShowLoadingDelayed()
    {
        yield return new WaitForSeconds(loadingDelay);
        if (_loading != null)
        {
            // 카드 스택 끝 (가변 높이 누적). Render 가 _nextStackY 를 갱신.
            ((RectTransform)_loading.transform).anchoredPosition =
                new Vector2(topLeftMargin.x, _nextStackY);
            _loading.SetActive(true);
        }
        _pendingLoad = null;
    }

    // 한글 글리프 TMP 폰트 — 공용 KoreanFont 로 일원화.
    static TMP_FontAsset ResolveKoreanFont() => KoreanFont.Get();
}
