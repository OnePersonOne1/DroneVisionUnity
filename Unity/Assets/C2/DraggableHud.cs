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

    [Header("이동/리사이즈")]
    [Tooltip("켜면 헤더 드래그로 위치 변경 불가 — 단, 접기/토글/리사이즈는 정상 동작.")]
    public bool lockPosition = false;
    [Tooltip("켜면 우상단 그립 드래그로 패널 크기 조절. 부모 pivot=(0,0) 가정 (그렇지 않은 경우 동작 비표준).")]
    public bool resizable = false;
    public Vector2 minSize = new Vector2(120f, 100f);
    public Vector2 maxSize = new Vector2(2000f, 2000f);
    public float resizeGripSizePx = 18f;
    [Tooltip("이 키 누르면 패널 크기를 초기 크기로 리셋. None 이면 비활성. " +
             "resetSizeRequiresShift=true 면 Shift 와 함께 눌러야.")]
    public Key resetSizeKey = Key.None;
    public bool resetSizeRequiresShift = false;

    public RectTransform ResizeGripRt { get; private set; }
    Vector2 _initialSize;     // Start 시점의 sizeDelta — resetSizeKey 가 복귀시킬 기본값.

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
        if (!_allKnown.Contains(this)) _allKnown.Add(this);
        EnsureMonitor();
    }

    // Start 에서 빌드해야 호출자가 AddComponent 후 설정한 필드 (windowTitle, resizable, toggleKey,
    // startCollapsed, headerColor 등) 가 적용된 상태에서 헤더/그립이 만들어진다.
    void Start()
    {
        if (_headerRt == null) BuildHeader();
        if (resizable && ResizeGripRt == null) BuildResizeGrip();
        _initialSize = _rt.sizeDelta;   // resetSizeKey 가 복귀시킬 기본 크기 캡처.
        if (startCollapsed) SetCollapsed(true);
    }

    /// <summary>패널 크기를 Start 시점의 초기 크기로 복귀. 접혀있으면 펴서 복귀.</summary>
    public void ResetSize()
    {
        if (_collapsed) SetCollapsed(false);
        _rt.sizeDelta = _initialSize;
        _expandedHeight = _initialSize.y;
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
        if (lockPosition) return;
        _dragStartPanelPos = _rt.anchoredPosition;
        _dragStartMouse = e.position;
    }

    public void OnDrag(PointerEventData e)
    {
        if (lockPosition) return;
        Vector2 delta = e.position - _dragStartMouse;
        var canvas = GetComponentInParent<Canvas>();
        float s = canvas != null ? canvas.scaleFactor : 1f;
        if (s < 1e-3f) s = 1f;
        _rt.anchoredPosition = _dragStartPanelPos + delta / s;
    }

    void BuildResizeGrip()
    {
        var go = new GameObject("ResizeGrip");
        ResizeGripRt = go.AddComponent<RectTransform>();
        ResizeGripRt.SetParent(_rt, false);
        // 부모 pivot=(0,0) 인 패널이 sizeDelta 증가로 위·오른쪽으로 확장하므로
        // 그립은 **우상단** (anchor=(1,1)) 에 두어야 마우스를 따라 자연스럽게 이동.
        // 헤더 아래(body 안쪽 우상단)에 위치 — 헤더 버튼(×, −) 과 겹치지 않게 anchoredPosition.y = -headerHeight.
        ResizeGripRt.anchorMin = new Vector2(1f, 1f);
        ResizeGripRt.anchorMax = new Vector2(1f, 1f);
        ResizeGripRt.pivot = new Vector2(1f, 1f);
        ResizeGripRt.sizeDelta = new Vector2(resizeGripSizePx, resizeGripSizePx);
        ResizeGripRt.anchoredPosition = new Vector2(0f, -headerHeight);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.35f, 0.55f, 0.80f, 0.85f);
        img.raycastTarget = true;

        go.AddComponent<ResizeGripHandler>().target = this;

        // 시각적 ◢ 모양 라벨.
        var lblGo = new GameObject("Label");
        var lrt = lblGo.AddComponent<RectTransform>();
        lrt.SetParent(ResizeGripRt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        var txt = lblGo.AddComponent<TextMeshProUGUI>();
        txt.text = "◢";
        txt.color = Color.white;
        txt.fontSize = 14;
        txt.alignment = TextAlignmentOptions.Center;
        txt.raycastTarget = false;
    }

    Vector2 _resizeStartSize;
    Vector2 _resizeStartMouse;

    void OnResizeBegin(PointerEventData e)
    {
        _resizeStartSize = _rt.sizeDelta;
        _resizeStartMouse = e.position;
    }

    void OnResizeDrag(PointerEventData e)
    {
        var canvas = GetComponentInParent<Canvas>();
        float s = canvas != null ? canvas.scaleFactor : 1f;
        if (s < 1e-3f) s = 1f;
        Vector2 delta = (e.position - _resizeStartMouse) / s;
        // 부모 pivot=(0,0) 가정 — 우하단 그립을 끄는 방향:
        //   오른쪽 (+x) → 폭 증가
        //   아래 (-y) → 높이 증가 (그립이 좌표 아래쪽으로 이동 = 패널 높이가 그만큼 줄어드는 게 직관 — 하지만
        //               부모 anchor=(0,0) pivot=(0,0) 면 sizeDelta 증가 = 위로만 확장.
        //   직관: 그립을 위로 드래그 = 패널 위쪽 끝이 위로 → 높이 증가. 따라서 +y 가 높이 증가.
        Vector2 newSize = new Vector2(
            Mathf.Clamp(_resizeStartSize.x + delta.x, minSize.x, maxSize.x),
            Mathf.Clamp(_resizeStartSize.y + delta.y, minSize.y, maxSize.y));
        _rt.sizeDelta = newSize;
        if (!_collapsed) _expandedHeight = newSize.y;
    }

    public void SetCollapsed(bool yes)
    {
        _collapsed = yes;
        if (_bodyHolder != null) _bodyHolder.gameObject.SetActive(!yes);
        if (ResizeGripRt != null) ResizeGripRt.gameObject.SetActive(!yes);
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
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            // 비활성 패널까지 검사하기 위해 registry 와 활성 객체 검사를 따로 — 한 번 더 정적 리스트가 필요.
            // 본 구현에서는 활성 객체만 등록되니, 비활성 토글-온 처리를 위해 별도 'all known' 리스트 사용.
            for (int i = 0; i < _allKnown.Count; i++)
            {
                var h = _allKnown[i];
                if (h == null) continue;

                // Reset 키 — 토글 키와 같을 수 있어 토글 보다 먼저 처리.
                bool resetFired = false;
                if (h.resetSizeKey != Key.None && kb[h.resetSizeKey].wasPressedThisFrame)
                {
                    if (!h.resetSizeRequiresShift || shift)
                    {
                        h.ResetSize();
                        resetFired = true;
                    }
                }

                // Toggle 키 — reset 가 같은 키로 발화된 경우 (Shift 동반), 토글 스킵.
                if (h.toggleKey != Key.None && kb[h.toggleKey].wasPressedThisFrame)
                {
                    bool sameKeyConsumed = resetFired && h.resetSizeKey == h.toggleKey;
                    // Reset 요구 modifier 가 Shift 인데 Shift 가 눌렸다면 토글 의도 X.
                    bool shiftBlocksToggle =
                        h.resetSizeRequiresShift && h.resetSizeKey == h.toggleKey && shift;
                    if (!sameKeyConsumed && !shiftBlocksToggle)
                        h.HandleToggleKey();
                }
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

    class ResizeGripHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public DraggableHud target;
        public void OnBeginDrag(PointerEventData e) { if (target != null) target.OnResizeBegin(e); }
        public void OnDrag(PointerEventData e) { if (target != null) target.OnResizeDrag(e); }
    }
}
