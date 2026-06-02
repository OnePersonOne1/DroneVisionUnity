using System.Collections.Generic;
using UnityEngine;
using TMPro;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2
{
    /// <summary>
    /// 등록된 모든 드론 머리 위에 월드 공간 TMP 라벨을 띄운다 (마인크래프트 nametag 스타일).
    ///   - 항상 카메라 쪽 향함(billboard, 수직 유지).
    ///   - 라벨: "{번호}\nAGL: {m} m".
    ///   - 텍스트 뒤 반투명 검은 박스로 가독성 보강.
    ///   - TMP rectTransform pivot = (0.5, 0) (bottom-center) → root.position 이 라벨 바닥
    ///     기준점이 되어 heightOffsetMeters 가 "드론 머리 위 N m" 로 명확히 동작.
    ///   - 모든 디스플레이(1/2/3) 에서 동일하게 렌더 (월드 메시).
    /// </summary>
    [DisallowMultipleComponent]
    public class DroneNameTagManager : MonoBehaviour
    {
        [Header("위치")]
        [Tooltip("드론 머리 위 띄울 높이 (real m, unityUnitsPerMeter 자동 곱). 라벨 '바닥' 이 이 높이.")]
        public float heightOffsetMeters = 3f;

        [Header("폰트 크기 모드")]
        [Tooltip("ON: constant screen-space — 카메라 거리·줌 무관 화면 픽셀 크기 일정 (MMO/RTS 표준). " +
                 "OFF: 월드 m 고정 (가까이서 보면 크고 멀면 작아짐).")]
        public bool useScreenSpaceSize = true;
        [Tooltip("constant-screen 모드의 목표 픽셀 높이 — 거리 무관 이 픽셀 크기로 보임.")]
        public float screenPixelHeight = 40f;
        [Tooltip("OFF 모드에서 사용할 월드 폰트 크기 (real m).")]
        public float fontSizeMeters = 1.5f;
        public Color textColor = Color.white;

        [Header("배경 박스")]
        [Tooltip("텍스트 뒤 반투명 박스 표시(가독성). 끄면 박스 없음.")]
        public bool showBackground = true;
        public Color bgColor = new Color(0f, 0f, 0f, 0.65f);
        [Tooltip("박스 가로 배율(텍스트 폭 대비). 1.0=텍스트 폭과 동일, 1.3=30% 더 넓게.")]
        public float bgScaleX = 1.20f;
        [Tooltip("박스 세로 배율(텍스트 높이 대비).")]
        public float bgScaleY = 1.20f;
        [Tooltip("박스 추가 위치 보정 (real m). x=오른쪽, y=위쪽. " +
                 "텍스트 중앙(텍스트 바닥 + 높이/2) 기준 local offset.")]
        public Vector2 bgOffsetMeters = Vector2.zero;
        [Tooltip("절대 크기 모드 — 0보다 크면 텍스트 크기 무시하고 이 값(real m) 으로 고정. " +
                 "예: (8, 4) 라면 가로 8m, 세로 4m. 0 이면 텍스트 자동 맞춤(bgScaleX/Y 사용).")]
        public Vector2 bgFixedSizeMeters = Vector2.zero;

        [Header("Billboard")]
        [Tooltip("매 프레임 Camera.main 쪽으로 회전(수평 axis 유지).")]
        public bool billboard = true;

        [Header("폰트 크기 안전 클램프 (real m)")]
        [Tooltip("constant-screen 모드의 폰트 월드 단위 하한 — 너무 작아 사라지지 않게.")]
        public float minWorldFontMeters = 0.2f;
        [Tooltip("월드 단위 상한 — 너무 커지지 않게.")]
        public float maxWorldFontMeters = 500f;

        class Tag
        {
            public GameObject root;
            public TextMeshPro tmp;
            public Transform bg;          // 텍스트 뒤 quad
            public MeshRenderer bgRend;
            public Material bgMat;
        }
        readonly Dictionary<string, Tag> _tags = new Dictionary<string, Tag>();
        readonly List<string> _toRemove = new List<string>();
        float _scale = 1f;
        bool _scaleResolved;
        Shader _bgShader;

        void Awake()
        {
            _bgShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (_bgShader == null) _bgShader = Shader.Find("Sprites/Default");
        }

        void LateUpdate()
        {
            if (!_scaleResolved)
            {
                _scale = FrameConversion.ResolveUnityUnitsPerMeter();
                if (_scale > 1e-3f) _scaleResolved = true;
                else _scale = 1f;
            }
            float offset = heightOffsetMeters * _scale;
            Camera cam = Camera.main;
            float minUnits = minWorldFontMeters * _scale;
            float maxUnits = maxWorldFontMeters * _scale;

            var seen = new HashSet<string>();
            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null || agent.highFidelity == null) continue;
                seen.Add(agent.agentId);
                if (!_tags.TryGetValue(agent.agentId, out var tag))
                {
                    tag = BuildTag(agent);
                    _tags[agent.agentId] = tag;
                }
                // 텍스트 갱신 — AGL(지상고도).
                float agl;
                GroundAgl.TryGetAgl(agent.highFidelity.PositionUnity, agent.highFidelity.UnityUnitsPerMeter, ~0, out agl);
                tag.tmp.text = $"{DroneNumber(agent.agentId)}\nAGL: {agl:F1} m";
                tag.tmp.color = textColor;

                // 위치: 드론 머리 위 (root = 라벨 바닥 기준점).
                Vector3 dronePos = agent.highFidelity.PositionUnity;
                tag.root.transform.position = dronePos + Vector3.up * offset;

                // 폰트 크기 — constant screen-space (게임 표준) 또는 월드 고정.
                //   screen-space: 1 픽셀의 월드 높이 = 2·d·tan(fov/2) / pixelHeight,
                //                 폰트 = pixelHeight 목표값 × 1 픽셀 월드 높이 → 화면 픽셀 크기 일정.
                //   ortho 카메라: 1 픽셀 월드 높이 = 2·orthoSize / pixelHeight.
                float fontUnits;
                if (useScreenSpaceSize && cam != null)
                {
                    float pxH = Mathf.Max(cam.pixelHeight, 1);
                    float worldPerPixel;
                    if (cam.orthographic)
                    {
                        worldPerPixel = (2f * cam.orthographicSize) / pxH;
                    }
                    else
                    {
                        float distUnity = Vector3.Distance(dronePos, cam.transform.position);
                        float halfFovRad = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
                        worldPerPixel = (2f * distUnity * Mathf.Tan(halfFovRad)) / pxH;
                    }
                    fontUnits = Mathf.Clamp(worldPerPixel * screenPixelHeight, minUnits, maxUnits);
                }
                else
                {
                    fontUnits = Mathf.Clamp(fontSizeMeters * _scale, minUnits, maxUnits);
                }
                tag.tmp.fontSize = fontUnits;

                // 배경 박스 위치/크기 — 인스펙터 노브로 조절.
                if (tag.bg != null)
                {
                    if (showBackground)
                    {
                        tag.bg.gameObject.SetActive(true);

                        // 가로/세로 크기: 절대 모드(>0) 우선, 아니면 텍스트 크기 × scale.
                        // 텍스트 크기는 GetPreferredValues 가 TMP 내부 캐시·타이밍 때문에 거리 보정된
                        // 새 fontSize 를 즉시 못 따라잡는 일이 있어, fontUnits 와 글자 수에서 직접 산출.
                        // → 거리에 따라 fontUnits 가 커지면 박스도 같이 커짐.
                        float w, h;
                        if (bgFixedSizeMeters.x > 0f && bgFixedSizeMeters.y > 0f)
                        {
                            w = bgFixedSizeMeters.x * _scale;
                            h = bgFixedSizeMeters.y * _scale;
                        }
                        else
                        {
                            EstimateTextSize(tag.tmp.text, fontUnits, out float textW, out float textH);
                            w = textW * bgScaleX;
                            h = textH * bgScaleY;
                        }

                        // 위치: 텍스트 pivot=(0.5,0). 텍스트 중앙 = local(0, h_text/2).
                        // 박스 중심을 동일 지점에 두고 사용자 offset 추가.
                        // 텍스트 높이 추정 — 박스 높이 h 와 거의 같다고 보고 그것으로 정렬.
                        float baseY = h * 0.5f;
                        float offX = bgOffsetMeters.x * _scale;
                        float offY = bgOffsetMeters.y * _scale;
                        tag.bg.localPosition = new Vector3(offX, baseY + offY, 0.02f);
                        tag.bg.localScale = new Vector3(w, h, 1f);
                        if (tag.bgMat != null) tag.bgMat.color = bgColor;
                    }
                    else
                    {
                        tag.bg.gameObject.SetActive(false);
                    }
                }

                // Billboard: Camera.main 쪽 수평 회전 (Y 축은 위 유지).
                if (billboard && cam != null)
                {
                    Vector3 toCam = cam.transform.position - tag.root.transform.position;
                    toCam.y = 0f;
                    if (toCam.sqrMagnitude > 1e-4f)
                        tag.root.transform.rotation = Quaternion.LookRotation(-toCam, Vector3.up);
                }
            }

            // 사라진 드론 정리.
            _toRemove.Clear();
            foreach (var kv in _tags) if (!seen.Contains(kv.Key)) _toRemove.Add(kv.Key);
            foreach (var id in _toRemove)
            {
                if (_tags.TryGetValue(id, out var t) && t.root != null) Destroy(t.root);
                _tags.Remove(id);
            }
        }

        Tag BuildTag(DroneAgent agent)
        {
            var root = new GameObject($"NameTag_{agent.agentId}");
            root.transform.SetParent(transform, true);

            // 배경 quad — 텍스트 뒤에 위치.
            var bgGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bgGo.name = "BG";
            bgGo.transform.SetParent(root.transform, false);
            var col = bgGo.GetComponent<Collider>(); if (col != null) Destroy(col);
            var bgRend = bgGo.GetComponent<MeshRenderer>();
            var bgMat = MakeBgMaterial();
            bgRend.sharedMaterial = bgMat;
            bgRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bgRend.receiveShadows = false;

            // 텍스트.
            var tmpGo = new GameObject("Text");
            tmpGo.transform.SetParent(root.transform, false);
            var tmp = tmpGo.AddComponent<TextMeshPro>();
            tmp.text = "?";
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = textColor;
            tmp.fontSize = 1f;
            tmp.enableWordWrapping = false;
            var f = KoreanFont.Get();
            if (f != null) tmp.font = f;
            // pivot = (0.5, 0) → root.position 이 텍스트 바닥 중앙.
            var trt = tmp.rectTransform;
            trt.pivot = new Vector2(0.5f, 0f);
            trt.sizeDelta = new Vector2(10f, 4f);
            trt.anchoredPosition = Vector2.zero;
            // TMP 가 +Z 쪽으로 살짝 있어 bg(z=0.02) 보다 카메라에 가깝게.
            tmpGo.transform.localPosition = new Vector3(0f, 0f, 0f);

            return new Tag { root = root, tmp = tmp, bg = bgGo.transform, bgRend = bgRend, bgMat = bgMat };
        }

        Material MakeBgMaterial()
        {
            var mat = new Material(_bgShader);
            mat.color = bgColor;
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_SURFACE_TYPE_OPAQUE");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_ZWrite", 0);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            return mat;
        }

        static string DroneNumber(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            int idx = id.LastIndexOf('_');
            return idx >= 0 ? id.Substring(idx + 1) : id;
        }

        /// <summary>fontUnits + 문자 수에서 텍스트 박스를 분석적으로 추정.
        /// fontUnits 가 constant-screen-space 결과이므로 박스도 같은 화면 픽셀 비율로 따라옴.</summary>
        const float CHAR_WIDTH_FACTOR = 0.55f;   // 평균 글리프 폭 ÷ fontSize. 한글 섞이면 더 크지만 bgScaleX 로 보정.
        const float LINE_HEIGHT_FACTOR = 1.20f;
        static void EstimateTextSize(string text, float fontUnits, out float w, out float h)
        {
            int maxChars = 1, lines = 1, curr = 0;
            if (!string.IsNullOrEmpty(text))
            {
                foreach (char ch in text)
                {
                    if (ch == '\n')
                    {
                        if (curr > maxChars) maxChars = curr;
                        curr = 0; lines++;
                    }
                    else curr++;
                }
                if (curr > maxChars) maxChars = curr;
            }
            w = fontUnits * maxChars * CHAR_WIDTH_FACTOR;
            h = fontUnits * lines * LINE_HEIGHT_FACTOR;
        }
    }
}
