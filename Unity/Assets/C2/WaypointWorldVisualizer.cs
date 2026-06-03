using System.Collections.Generic;
using UnityEngine;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2
{
    /// <summary>
    /// 모든 디스플레이의 월드 공간에서 보이는 웨이포인트 시각화:
    ///   - 각 웨이포인트(현재 target + 큐)를 매우 긴 반투명 원기둥으로 표시(어느 고도에서도 가시).
    ///   - 드론 위치 → 현재 target → 큐 순으로 잇는 반투명 경로 라인.
    /// 색상은 선택 상태(노랑) 또는 기본(시안).
    /// </summary>
    [DisallowMultipleComponent]
    public class WaypointWorldVisualizer : MonoBehaviour
    {
        [Header("원기둥")]
        public bool showCylinders = true;
        [Tooltip("원기둥 반경 (real m).")]
        public float cylinderRadiusMeters = 1.5f;
        [Tooltip("원기둥 높이 (Unity 단위). 스케일 무관, 충분히 크게.")]
        public float cylinderHeightUnity = 1e6f;
        [Range(0f, 1f)] public float cylinderAlpha = 0.30f;

        [Header("지면 원반 (waypoint 위치 표시)")]
        public bool showGroundDiscs = true;
        [Tooltip("지면 원반 반경 (real m). 원기둥 반경보다 크게 잡아 멀리서도 보임.")]
        public float discRadiusMeters = 6f;
        [Tooltip("원반 두께 (Unity 단위). 너무 얇으면 z-fighting.")]
        public float discThicknessUnity = 0.5f;
        [Range(0f, 1f)] public float discAlpha = 0.55f;
        [Tooltip("지면 위로 띄울 높이 (real m). 0 이면 지면과 같은 높이 → z-fighting 가능.")]
        public float discLiftMeters = 0.2f;
        public LayerMask groundMask = ~0;
        [Tooltip("지면 raycast 최대 거리 (Unity 단위).")]
        public float groundRayDistanceUnity = 1e6f;
        [Tooltip("원반 색 (cylinder 색과 다르게).")]
        public Color discDefaultColor = new Color(1f, 0.4f, 0.2f, 1f);
        public Color discSelectedColor = new Color(1f, 0.85f, 0.1f, 1f);

        [Header("경로 라인")]
        public bool showPathLines = true;
        [Tooltip("라인 굵기 (real m).")]
        public float lineThicknessMeters = 0.6f;
        [Range(0f, 1f)] public float lineAlpha = 0.55f;

        [Header("색")]
        public Color defaultColor = new Color(0.3f, 0.9f, 1f, 1f);
        public Color selectedColor = new Color(1f, 0.9f, 0.2f, 1f);

        StrategicView.StrategicCommandInput _selection;
        float _scale = 1f;
        bool _scaleResolved;
        Shader _shader;

        class DroneVis
        {
            public List<MeshRenderer> cylinders = new List<MeshRenderer>();
            public List<MeshRenderer> discs = new List<MeshRenderer>();
            public LineRenderer line;
        }
        readonly Dictionary<string, DroneVis> _vis = new Dictionary<string, DroneVis>();
        readonly List<string> _toRemove = new List<string>();

        void Awake()
        {
            _selection = FindObjectOfType<StrategicView.StrategicCommandInput>();
            _shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (_shader == null) _shader = Shader.Find("Sprites/Default");
        }

        void LateUpdate()
        {
            if (!_scaleResolved)
            {
                _scale = FrameConversion.ResolveUnityUnitsPerMeter();
                if (_scale > 1e-3f) _scaleResolved = true;
                else _scale = 1f;
            }
            float radius = cylinderRadiusMeters * _scale;
            float discR = discRadiusMeters * _scale;
            float discLift = discLiftMeters * _scale;
            float lineW = lineThicknessMeters * _scale;

            var seen = new HashSet<string>();
            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null || agent.Active == null) continue;
                seen.Add(agent.agentId);

                if (!_vis.TryGetValue(agent.agentId, out var v))
                {
                    v = new DroneVis();
                    _vis[agent.agentId] = v;
                }

                // 웨이포인트 좌표 수집(월드).
                var wps = new List<Vector3>();
                if (agent.Waypoints.Current.HasValue)
                    wps.Add(agent.Active.EnuToUnityWorld(agent.Waypoints.Current.Value));
                foreach (var w in agent.Waypoints.Upcoming)
                    wps.Add(agent.Active.EnuToUnityWorld(w));

                bool isSel = _selection != null && _selection.IsSelected(agent.agentId);
                Color c = isSel ? selectedColor : defaultColor;
                Color cylC = new Color(c.r, c.g, c.b, cylinderAlpha);
                Color lineC = new Color(c.r, c.g, c.b, lineAlpha);
                Color dc = isSel ? discSelectedColor : discDefaultColor;
                Color discC = new Color(dc.r, dc.g, dc.b, discAlpha);

                // 원기둥.
                if (showCylinders)
                {
                    while (v.cylinders.Count < wps.Count) v.cylinders.Add(CreateCylinder());
                    for (int i = 0; i < wps.Count; i++)
                    {
                        var mr = v.cylinders[i];
                        mr.gameObject.SetActive(true);
                        // 위치: wp.xz, Y 는 wp.y 중심 (높이 절반씩 양쪽).
                        mr.transform.position = new Vector3(wps[i].x, wps[i].y, wps[i].z);
                        // Unity 기본 cylinder 는 2 단위 높이라 Y 스케일=h/2.
                        mr.transform.localScale = new Vector3(radius * 2f, cylinderHeightUnity * 0.5f, radius * 2f);
                        mr.sharedMaterial.color = cylC;
                    }
                    for (int i = wps.Count; i < v.cylinders.Count; i++) v.cylinders[i].gameObject.SetActive(false);
                }
                else foreach (var m in v.cylinders) if (m != null) m.gameObject.SetActive(false);

                // 지면 원반 (waypoint XZ → 지면 raycast → 그 지점에 평평한 원기둥).
                if (showGroundDiscs)
                {
                    while (v.discs.Count < wps.Count) v.discs.Add(CreateDisc());
                    for (int i = 0; i < wps.Count; i++)
                    {
                        var mr = v.discs[i];
                        // 충분히 위에서 아래로 raycast.
                        Vector3 from = new Vector3(wps[i].x, wps[i].y + groundRayDistanceUnity * 0.5f, wps[i].z);
                        Vector3 groundPos;
                        if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, groundRayDistanceUnity, groundMask))
                            groundPos = hit.point + Vector3.up * discLift;
                        else
                            groundPos = new Vector3(wps[i].x, discLift, wps[i].z);   // fallback
                        mr.gameObject.SetActive(true);
                        mr.transform.position = groundPos;
                        mr.transform.localScale = new Vector3(discR * 2f, discThicknessUnity * 0.5f, discR * 2f);
                        mr.sharedMaterial.color = discC;
                    }
                    for (int i = wps.Count; i < v.discs.Count; i++) v.discs[i].gameObject.SetActive(false);
                }
                else foreach (var m in v.discs) if (m != null) m.gameObject.SetActive(false);

                // 경로 라인.
                if (showPathLines)
                {
                    if (v.line == null) v.line = CreateLine(agent.agentId);
                    if (wps.Count > 0)
                    {
                        v.line.gameObject.SetActive(true);
                        v.line.positionCount = wps.Count + 1;
                        v.line.SetPosition(0, agent.Active.PositionUnity);
                        for (int i = 0; i < wps.Count; i++) v.line.SetPosition(i + 1, wps[i]);
                        v.line.startWidth = v.line.endWidth = lineW;
                        v.line.startColor = v.line.endColor = lineC;
                    }
                    else v.line.gameObject.SetActive(false);
                }
                else if (v.line != null) v.line.gameObject.SetActive(false);
            }

            // 사라진 드론 정리.
            _toRemove.Clear();
            foreach (var kv in _vis) if (!seen.Contains(kv.Key)) _toRemove.Add(kv.Key);
            foreach (var id in _toRemove)
            {
                var v = _vis[id];
                foreach (var m in v.cylinders) if (m != null) Destroy(m.gameObject);
                foreach (var m in v.discs) if (m != null) Destroy(m.gameObject);
                if (v.line != null) Destroy(v.line.gameObject);
                _vis.Remove(id);
            }
        }

        MeshRenderer CreateCylinder()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, true);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = MakeTransparentMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        MeshRenderer CreateDisc()
        {
            // 평평한 원기둥(Y 매우 작음) = 지면 원반. 같은 메쉬·머티리얼 구성.
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "WpGroundDisc";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, true);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = MakeTransparentMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        LineRenderer CreateLine(string id)
        {
            var go = new GameObject($"PathLine_{id}");
            go.transform.SetParent(transform, true);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = MakeTransparentMaterial();
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        Material MakeTransparentMaterial()
        {
            var mat = new Material(_shader);
            mat.color = Color.white;
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
    }
}
