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
    public Vector2 cardSize = new Vector2(560f, 116f);
    public float cardGap = 8f;
    public float bottomMargin = 120f;
    public float titleFontSize = 26f;
    public float bodyFontSize = 17f;
    public Color cardBackColor = new Color(0f, 0f, 0f, 0.62f);
    [Tooltip("한글 TMP 폰트. 비우면 OS 한글 폰트로 런타임 생성.")]
    public TMP_FontAsset koreanFont;
    [Tooltip("이 시간(초) 안에 결과가 오면 '조회 중…' 을 띄우지 않음(깜빡임 방지).")]
    public float loadingDelay = 0.25f;

    Canvas _canvas;
    GameObject _loading;
    TextMeshProUGUI _loadingText;
    Coroutine _pendingLoad;

    class Card
    {
        public GameObject root;
        public Image strip;
        public TextMeshProUGUI text;
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
    }

    GameObject NewPanel(string name, Color bg)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_canvas.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        var img = go.AddComponent<Image>();
        img.color = bg;
        img.raycastTarget = false;
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
            var root = NewPanel($"Card_{_cards.Count}", cardBackColor);
            ((RectTransform)root.transform).sizeDelta = cardSize;
            // 좌측 색 띠
            var stripGo = new GameObject("Strip");
            var srt = stripGo.AddComponent<RectTransform>();
            srt.SetParent(root.transform, false);
            srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(0f, 1f);
            srt.pivot = new Vector2(0f, 0.5f);
            srt.sizeDelta = new Vector2(7f, 0f); srt.anchoredPosition = Vector2.zero;
            var strip = stripGo.AddComponent<Image>();
            strip.raycastTarget = false;
            var text = NewText(root.transform, "");
            _cards.Add(new Card { root = root, strip = strip, text = text });
        }
        return _cards[i];
    }

    void Render(List<BuildingInfoService.Selection> sels)
    {
        if (_pendingLoad != null) { StopCoroutine(_pendingLoad); _pendingLoad = null; }
        if (_loading != null) _loading.SetActive(false);

        for (int i = 0; i < sels.Count; i++)
        {
            var sel = sels[i];
            var card = EnsureCard(i);
            card.root.SetActive(true);
            ((RectTransform)card.root.transform).anchoredPosition =
                new Vector2(0f, bottomMargin + i * (cardSize.y + cardGap));
            Color opaque = new Color(sel.color.r, sel.color.g, sel.color.b, 1f);
            card.strip.color = opaque;
            card.text.text = Format(sel.info, opaque);
        }
        for (int j = sels.Count; j < _cards.Count; j++)
            _cards[j].root.SetActive(false);
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
            // 카드 스택 위에 표시
            int n = service != null ? service.Selections.Count : 0;
            ((RectTransform)_loading.transform).anchoredPosition =
                new Vector2(0f, bottomMargin + n * (cardSize.y + cardGap));
            _loading.SetActive(true);
        }
        _pendingLoad = null;
    }

    // 한글 글리프 TMP 폰트 — 공용 KoreanFont 로 일원화.
    static TMP_FontAsset ResolveKoreanFont() => KoreanFont.Get();
}
