using System.Collections.Generic;
using UnityEngine;
using TMPro;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2
{
    /// <summary>
    /// 등록된 모든 드론 머리 위에 월드 공간 TMP 라벨을 띄운다 (마인크래프트 nametag 스타일).
    ///   - 항상 카메라 쪽 향함(billboard, 수직 유지).
    ///   - 라벨: "{번호}\nalt: {Z:F1} m".
    ///   - 모든 디스플레이(1/2/3) 에서 동일하게 렌더 (월드 메시).
    /// </summary>
    [DisallowMultipleComponent]
    public class DroneNameTagManager : MonoBehaviour
    {
        [Header("위치")]
        [Tooltip("드론 머리 위 띄울 높이 (real m, unityUnitsPerMeter 자동 곱).")]
        public float heightOffsetMeters = 2f;

        [Header("폰트")]
        [Tooltip("폰트 크기 (real m). 0.5 ≈ 50cm 글자 — 카메라 거리에 맞게.")]
        public float fontSizeMeters = 1.5f;
        public Color textColor = Color.white;
        public Color bgColor = new Color(0f, 0f, 0f, 0.65f);

        [Header("Billboard")]
        [Tooltip("매 프레임 Camera.main 쪽으로 회전(수평 axis 유지).")]
        public bool billboard = true;

        class Tag
        {
            public GameObject root;
            public TextMeshPro tmp;
        }
        readonly Dictionary<string, Tag> _tags = new Dictionary<string, Tag>();
        readonly List<string> _toRemove = new List<string>();
        float _scale = 1f;
        bool _scaleResolved;

        void LateUpdate()
        {
            if (!_scaleResolved)
            {
                _scale = FrameConversion.ResolveUnityUnitsPerMeter();
                if (_scale > 1e-3f) _scaleResolved = true;
                else _scale = 1f;
            }
            float offset = heightOffsetMeters * _scale;
            float fontUnits = fontSizeMeters * _scale;
            Camera cam = Camera.main;

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
                // 텍스트 갱신 — AGL(지상고도) 표시.
                float agl;
                GroundAgl.TryGetAgl(agent.highFidelity.PositionUnity, agent.highFidelity.UnityUnitsPerMeter, ~0, out agl);
                tag.tmp.text = $"{DroneNumber(agent.agentId)}\nAGL: {agl:F1} m";
                tag.tmp.color = textColor;
                tag.tmp.fontSize = fontUnits;

                // 위치: 드론 머리 위.
                Vector3 dronePos = agent.highFidelity.PositionUnity;
                tag.root.transform.position = dronePos + Vector3.up * offset;

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
            // TMP 가 자식의 RectTransform 을 자동으로 만든다. root 는 일반 Transform.
            var trt = tmp.rectTransform;
            trt.sizeDelta = new Vector2(10f, 4f);
            trt.anchoredPosition = Vector2.zero;
            return new Tag { root = root, tmp = tmp };
        }

        static string DroneNumber(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            int idx = id.LastIndexOf('_');
            return idx >= 0 ? id.Substring(idx + 1) : id;
        }
    }
}
