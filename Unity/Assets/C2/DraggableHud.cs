using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// UGUI 패널을 **드래그·접기·키 토글** 가능한 작은 윈도우로 만든다.
///
/// 사용: 임의 UGUI 패널(RectTransform + Graphic) 에 부착하면 동적으로 상단 헤더 바를
/// 만들어주고, 헤더를 드래그하면 패널 전체가 이동한다. 헤더의 `_` 버튼은 접기,
/// `×` 버튼(있을 때)은 숨김. `toggleKey` 가 None 이 아니면 그 키로 보이기/숨기기.
///
/// 토글 키는 패널이 비활성화돼도 작동해야 하므로 별도 정적 모니터 GO 가 매 프레임
/// 등록된 DraggableHud 전부의 키를 체크 (자기 GO 가 disabled 여도 enabled 로 전환).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class DraggableHud : MonoBehaviour, IDragHandler, IBeginDragHandler
{
    [Header("창 메타")]
    public string windowTitle = "Window";
    [Tooltip("None 이면 토글 키 없음. 누르면 패널 보이기/숨기기.")]
    public Key toggleKey = Key.None;

    [Header("헤더 바")]
    public float headerHeight = 22f;
    public Color headerColor = new Color(0.15f, 0.2f, 0.28f, 0.95f);
    public Color headerTextColor = Color.white;
    public int headerFontSize = 14;

    [Header("초기 상태")]
    public bool startCollapsed = false;

    RectTransform _rt;
    RectTransform _headerRt;
    RectTransform _bodyHolder;   // 본문(접힐 때 비활성화 대상)
    float _expandedHeight;
    bool _collapsed;
    Vector2 _dragStartPanelPos;
    Vector2 _dragStartMouse;

    // 정적 모니터 — 토글 키 폴링.
    static GameObject _monitorGo;
    // 비활성 HUD 도 토글 키로 다시 켤 수 있어야 하므로, OnDisable 시에도 리스트에서 빼지 않는다.
    static readonly List<DraggableHud> _allKnown = new List<DraggableHud>();

    void Awake()
    {
        _rt = (RectTransform)transform;
        _expandedHeight = _rt.sizeDelta.y;
        BuildHeader();
        if (startCollapsed) SetCollapsed(true);
        if (!_allKnown.Contains(this)) _allKnown.Add(this);
        EnsureMonitor();
    }

    void OnDestroy()
    {
        _allKnown.Remove(this);
    }

    static void EnsureMonitor()
    {
        if (_monitorGo != null) return;
        _monitorGo = new GameObject("[DraggableHud Monitor]");
        Object.DontDestroyOnLoad(_monitorGo);
        _monitorGo.hideFlags = HideFlags.HideAndDontSave;
        _monitorGo.AddComponent<Monitor>();
    }

    void BuildHeader()
    {
        // 자식 reparent — 본문 holder 를 만들고 기존 자식을 그 안으로 옮긴다.
        // 이후 헤더와 본문 holder 가 panel 의 직속 자식.
        var existingChildren = new List<Transform>();
        for (int i = 0; i < _rt.childCount; i++) existingChildren.Add(_rt.GetChild(i));

        var bodyGo = new GameObject("Body");
        _bodyHolder = bodyGo.AddComponent<RectTransform>();
        _bodyHolder.SetParent(_rt, false);
        _bodyHolder.anchorMin = new Vector2(0, 0);
        _bodyHolder.anchorMax = new Vector2(1, 1);
        _bodyHolder.offsetMin = Vector2.zero;
        _bodyHolder.offsetMax = new Vector2(0, -headerHeight);
        foreach (var c in existingChildren) c.SetParent(_bodyHolder, false);

        // 헤더.
        var hdrGo = new GameObject("Header");
        _headerRt = hdrGo.AddComponent<RectTransform>();
        _headerRt.SetParent(_rt, false);
        _headerRt.anchorMin = new Vector2(0, 1);
        _headerRt.anchorMax = new Vector2(1, 1);
        _headerRt.pivot = new Vector2(0.5f, 1f);
        _headerRt.sizeDelta = new Vector2(0, headerHeight);
        _headerRt.anchoredPosition = Vector2.zero;
        var hdrImg = hdrGo.AddComponent<Image>();
        hdrImg.color = headerColor;
        hdrImg.raycastTarget = true;
        // 헤더 자체에 DraggableHud 의 IDragHandler 가 동작하도록 하려면, 헤더가 EventSystem 클릭/드래그
        // 대상이어야 함. 헤더에 별도 HeaderDragForwarder 를 붙여 부모로 forward.
        hdrGo.AddComponent<HeaderDragForwarder>().target = this;

        // 제목.
        var titleGo = new GameObject("Title");
        var trt = titleGo.AddComponent<RectTransform>();
        trt.SetParent(_headerRt, false);
        trt.anchorMin = new Vector2(0, 0);
        trt.anchorMax = new Vector2(1, 1);
        trt.offsetMin = new Vector2(8f, 0f);
        trt.offsetMax = new Vector2(-44f, 0f);   // 우측 버튼 공간.
        var t = titleGo.AddComponent<TextMeshProUGUI>();
        t.text = windowTitle;
        t.color = headerTextColor;
        t.fontSize = headerFontSize;
        t.alignment = TextAlignmentOptions.MidlineLeft;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        var f = KoreanFont.Get();
        if (f != null) t.font = f;

        // 접기 버튼 `_`.
        var collapseBtn = MakeHeaderButton("CollapseBtn", "−", -22f, () => SetCollapsed(!_collapsed));
        // 숨김 버튼 `×` (toggleKey 있는 경우만).
        if (toggleKey != Key.None)
            MakeHeaderButton("CloseBtn", "×", -4f, () => gameObject.SetActive(false));
    }

    Button MakeHeaderButton(string name, string label, float rightOffset, System.Action onClick)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_headerRt, false);
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(18f, 18f);
        rt.anchoredPosition = new Vector2(rightOffset, 0f);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.0f);    // 투명 hit area
        img.raycastTarget = true;
        var btn = go.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(() => onClick?.Invoke());

        var lblGo = new GameObject("Label");
        var lrt = lblGo.AddComponent<RectTransform>();
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        var txt = lblGo.AddComponent<TextMeshProUGUI>();
        txt.text = label;
        txt.color = headerTextColor;
        txt.fontSize = headerFontSize + 4;
        txt.alignment = TextAlignmentOptions.Center;
        txt.raycastTarget = false;
        return btn;
    }

    public void OnBeginDrag(PointerEventData e)
    {
        _dragStartPanelPos = _rt.anchoredPosition;
        _dragStartMouse = e.position;
    }

    public void OnDrag(PointerEventData e)
    {
        Vector2 delta = e.position - _dragStartMouse;
        var canvas = GetComponentInParent<Canvas>();
        float s = canvas != null ? canvas.scaleFactor : 1f;
        if (s < 1e-3f) s = 1f;
        _rt.anchoredPosition = _dragStartPanelPos + delta / s;
    }

    public void SetCollapsed(bool yes)
    {
        _collapsed = yes;
        if (_bodyHolder != null) _bodyHolder.gameObject.SetActive(!yes);
        Vector2 size = _rt.sizeDelta;
        if (yes)
        {
            _expandedHeight = size.y;
            size.y = headerHeight;
        }
        else
        {
            size.y = _expandedHeight;
        }
        _rt.sizeDelta = size;
    }

    /// <summary>Monitor 가 호출 — 자기 GO 가 비활성화면 활성화로 토글, 활성화면 비활성화.</summary>
    internal void HandleToggleKey()
    {
        gameObject.SetActive(!gameObject.activeSelf);
    }

    class Monitor : MonoBehaviour
    {
        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            // 비활성 패널까지 검사하기 위해 registry 와 활성 객체 검사를 따로 — 한 번 더 정적 리스트가 필요.
            // 본 구현에서는 활성 객체만 등록되니, 비활성 토글-온 처리를 위해 별도 'all known' 리스트 사용.
            for (int i = 0; i < _allKnown.Count; i++)
            {
                var h = _allKnown[i];
                if (h == null) continue;
                if (h.toggleKey == Key.None) continue;
                if (kb[h.toggleKey].wasPressedThisFrame)
                    h.HandleToggleKey();
            }
        }
    }

    /// <summary>헤더의 클릭/드래그 이벤트를 부모 DraggableHud 로 forward — 헤더가 자체 IDragHandler 가 되어
    /// 부모의 OnBeginDrag/OnDrag 를 invoke.</summary>
    class HeaderDragForwarder : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public DraggableHud target;
        public void OnBeginDrag(PointerEventData e) { if (target != null) target.OnBeginDrag(e); }
        public void OnDrag(PointerEventData e) { if (target != null) target.OnDrag(e); }
    }
}
