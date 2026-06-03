using System.Collections.Generic;
using UnityEngine;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2
{
    /// <summary>
    /// 현재 명시적으로 선택된 드론(StrategicCommandInput.selectedDroneIds 에 들어있는 ID 만)을
    /// 1인칭/3인칭/전략 어디서나 잘 보이는 초록 wireframe 박스로 강조.
    ///
    /// IsSelected() 가 아니라 selectedDroneIds.Contains() 사용 — 명시 선택만 하이라이트
    /// (선택 0개 = 전체 명령 모드일 때는 박스 X, RTS 표준).
    ///
    /// 박스 = 12개 얇은 큐브 (한 변마다 1개), opaque 머티리얼.
    /// 박스 크기는 드론 visual 의 Renderer.bounds 합집합 × sizePadding.
    /// 회전은 기본 world-axis (RTS 표준). followRotation=true 면 드론과 함께 회전.
    /// </summary>
    [DisallowMultipleComponent]
    public class SelectedDroneHighlighter : MonoBehaviour
    {
        [Header("색")]
        [Tooltip("선택 박스 색. 기본 = 불투명 초록.")]
        public Color color = new Color(0.15f, 1f, 0.25f, 1f);

        [Header("크기")]
        [Tooltip("드론 visual bounds 에 곱할 배수 (1.0 = 정확히 감쌈, 1.3 = 30% 여유).")]
        public float sizePadding = 1.3f;
        [Tooltip("edge 두께 = box 짧은 변 × 이 비율.")]
        [Range(0.005f, 0.2f)] public float edgeThicknessRatio = 0.05f;

        [Header("회전")]
        [Tooltip("드론 회전을 따라갈지. 끄면 world-axis 정렬 (RTS 표준).")]
        public bool followRotation = false;

        StrategicView.StrategicCommandInput _selection;
        Shader _shader;
        Material _mat;

        class Box
        {
            public GameObject root;
            public Transform[] edges = new Transform[12];
            public Bounds localBounds;
            public bool boundsResolved;
        }
        readonly Dictionary<string, Box> _boxes = new Dictionary<string, Box>();
        readonly List<string> _toRemove = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstance()
        {
            if (FindObjectOfType<SelectedDroneHighlighter>() != null) return;
            var go = new GameObject("SelectedDroneHighlighter");
            go.AddComponent<SelectedDroneHighlighter>();
        }

        void Awake()
        {
            _shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color")
                      ?? Shader.Find("Universal Render Pipeline/Unlit");
            _mat = new Material(_shader) { color = color };
        }

        void LateUpdate()
        {
            if (_selection == null) _selection = FindObjectOfType<StrategicView.StrategicCommandInput>();
            if (_selection == null) return;
            _mat.color = color;

            var explicitSel = _selection.selectedDroneIds;
            var seen = new HashSet<string>();
            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null) continue;
                seen.Add(agent.agentId);
                bool isSel = explicitSel != null && explicitSel.Contains(agent.agentId);

                if (!isSel)
                {
                    if (_boxes.TryGetValue(agent.agentId, out var bx) && bx.root != null)
                        bx.root.SetActive(false);
                    continue;
                }

                if (!_boxes.TryGetValue(agent.agentId, out var box))
                {
                    box = CreateBox(agent);
                    _boxes[agent.agentId] = box;
                }
                if (box.root == null) continue;
                box.root.SetActive(true);

                // bounds 미해결 시(첫 프레임에 mesh renderer 가 아직 enable 안 됐을 수 있음) 재시도.
                if (!box.boundsResolved)
                {
                    box.localBounds = ComputeLocalBounds(agent.transform, out bool ok);
                    box.boundsResolved = ok;
                }

                var t = agent.transform;
                box.root.transform.position = t.position;
                box.root.transform.rotation = followRotation ? t.rotation : Quaternion.identity;

                Vector3 size = box.localBounds.size * sizePadding;
                if (size.x < 1e-3f) size.x = 1e-3f;
                if (size.y < 1e-3f) size.y = 1e-3f;
                if (size.z < 1e-3f) size.z = 1e-3f;
                float edgeT = Mathf.Min(Mathf.Min(size.x, size.y), size.z) * edgeThicknessRatio;
                ApplyEdges(box, size, edgeT, box.localBounds.center);
            }

            _toRemove.Clear();
            foreach (var kv in _boxes) if (!seen.Contains(kv.Key)) _toRemove.Add(kv.Key);
            foreach (var id in _toRemove)
            {
                var b = _boxes[id];
                if (b.root != null) Destroy(b.root);
                _boxes.Remove(id);
            }
        }

        Box CreateBox(DroneAgent agent)
        {
            var box = new Box();
            box.root = new GameObject($"SelBox_{agent.agentId}");
            box.root.transform.SetParent(transform, true);
            for (int i = 0; i < 12; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(box.root.transform, false);
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                box.edges[i] = go.transform;
            }
            box.localBounds = ComputeLocalBounds(agent.transform, out bool ok);
            box.boundsResolved = ok;
            return box;
        }

        Bounds ComputeLocalBounds(Transform root, out bool ok)
        {
            ok = false;
            var renderers = root.GetComponentsInChildren<Renderer>(false);
            if (renderers == null || renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.one);

            // root 회전을 무시하고 root local 좌표로 union (world bounds 의 8 corner → 역회전).
            Quaternion invR = Quaternion.Inverse(root.rotation);
            Vector3 rootPos = root.position;
            bool init = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                Vector3 wmin = r.bounds.min;
                Vector3 wmax = r.bounds.max;
                if (wmax == wmin) continue;
                for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                {
                    Vector3 corner = new Vector3(
                        x == 0 ? wmin.x : wmax.x,
                        y == 0 ? wmin.y : wmax.y,
                        z == 0 ? wmin.z : wmax.z);
                    Vector3 local = invR * (corner - rootPos);
                    if (!init) { b = new Bounds(local, Vector3.zero); init = true; }
                    else b.Encapsulate(local);
                }
            }
            ok = init;
            return init ? b : new Bounds(Vector3.zero, Vector3.one);
        }

        void ApplyEdges(Box box, Vector3 size, float edgeT, Vector3 center)
        {
            float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
            int idx = 0;
            // X-축 평행 4 edges (y,z 부호 변화).
            for (int yi = -1; yi <= 1; yi += 2)
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    var e = box.edges[idx++];
                    e.localPosition = center + new Vector3(0f, hy * yi, hz * zi);
                    e.localRotation = Quaternion.identity;
                    e.localScale = new Vector3(size.x, edgeT, edgeT);
                }
            // Y-축 평행 4 edges.
            for (int xi = -1; xi <= 1; xi += 2)
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    var e = box.edges[idx++];
                    e.localPosition = center + new Vector3(hx * xi, 0f, hz * zi);
                    e.localRotation = Quaternion.identity;
                    e.localScale = new Vector3(edgeT, size.y, edgeT);
                }
            // Z-축 평행 4 edges.
            for (int xi = -1; xi <= 1; xi += 2)
                for (int yi = -1; yi <= 1; yi += 2)
                {
                    var e = box.edges[idx++];
                    e.localPosition = center + new Vector3(hx * xi, hy * yi, 0f);
                    e.localRotation = Quaternion.identity;
                    e.localScale = new Vector3(edgeT, edgeT, size.z);
                }
        }
    }
}
