using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;
using N = System.Numerics;

namespace DroneSim.C2
{
    /// <summary>
    /// 미니맵 마우스 입력 → FlightCommands. 기존 Minimap.cs 무수정 — 같은 GameObject 에 부착해
    /// public 필드(drone, viewRadius, panelSize)와 자식 RectTransform("Minimap Canvas/MinimapPanel")
    /// 만 읽는다.
    ///
    /// 입력:
    ///   - 우클릭(빈 곳): SetWaypoint(targetDroneId, 클릭점 ENU, 현 고도 유지)
    ///   - Shift+우클릭: QueueWaypoint
    ///
    /// 변환 흐름:
    ///   screen pixel → ScreenPointToLocalPointInRectangle → 패널 local
    ///     → pivot 보정 → 중앙 기준 centered 픽셀
    ///     → / (panelSize/2) * viewRadius = Unity world XZ offset (drone 기준)
    ///   target Unity world = minimap drone(=Cube) 위치 + offset, 고도 유지
    ///   target ENU = sim 드론의 HF.UnityWorldToEnu(target Unity world)
    ///   sim 드론 현 ENU.z 로 덮어써서 "수평 이동만" 보장
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Minimap))]
    public class MinimapCommandInput : MonoBehaviour
    {
        [Header("대상 드론 (DroneRegistry ID)")]
        public string targetDroneId = "drone_sim_obj_0";

        [Header("시각화")]
        public Color targetMarkerColor = new Color(0.4f, 1f, 0.4f, 0.95f);
        public float targetMarkerRadius = 7f;
        public Color queueDotColor = new Color(0.4f, 1f, 0.4f, 0.55f);
        public float queueDotRadius = 4f;

        Minimap _minimap;
        RectTransform _panelRt;
        UIShape _targetDot;
        readonly List<UIShape> _queueDots = new List<UIShape>();

        void Awake()
        {
            _minimap = GetComponent<Minimap>();
        }

        void Update()
        {
            if (!ResolveRefs()) return;
            HandleClick();
            UpdateVisuals();
        }

        bool ResolveRefs()
        {
            if (_panelRt == null)
            {
                var t = transform.Find("Minimap Canvas/MinimapPanel");
                if (t != null) _panelRt = t as RectTransform;
            }
            return _minimap != null && _minimap.drone != null && _panelRt != null;
        }

        void HandleClick()
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.wasPressedThisFrame) return;
            Vector2 screen = mouse.position.ReadValue();
            if (!RectTransformUtility.RectangleContainsScreenPoint(_panelRt, screen, null)) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_panelRt, screen, null, out Vector2 local);

            // pivot=(1,1) → local 범위 (-W,-H)..(0,0). center 로 옮기기 위해 +size/2.
            Vector2 size = _minimap.panelSize;
            Vector2 centered = local + size * 0.5f;     // -W/2..+W/2
            float dx = centered.x / (size.x * 0.5f) * _minimap.viewRadius;
            float dz = centered.y / (size.y * 0.5f) * _minimap.viewRadius;

            // 타겟 Unity world (수평 offset 만, 고도는 minimap drone 의 Y 유지).
            Vector3 origin = _minimap.drone.position;
            Vector3 targetUnity = new Vector3(origin.x + dx, origin.y, origin.z + dz);

            var agent = DroneRegistry.Get(targetDroneId);
            if (agent == null || agent.highFidelity == null)
            {
                Debug.LogWarning($"[MinimapCommandInput] 드론 '{targetDroneId}' 못 찾음.");
                return;
            }
            var hf = agent.highFidelity;
            var targetEnu = hf.UnityWorldToEnu(targetUnity);
            // 고도 유지: sim 드론 현 ENU.Z 사용 (수평 이동만 명령).
            var cur = hf.PositionEnu;
            targetEnu = new N.Vector3(targetEnu.X, targetEnu.Y, cur.Z);

            var kb = Keyboard.current;
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            if (shift) FlightCommands.QueueWaypoint(targetDroneId, targetEnu);
            else FlightCommands.SetWaypoint(targetDroneId, targetEnu);
            Debug.Log($"[MinimapCommandInput] {(shift ? "Queue" : "Set")}Waypoint → " +
                      $"ENU({targetEnu.X:F1}, {targetEnu.Y:F1}, {targetEnu.Z:F1})");
        }

        void UpdateVisuals()
        {
            var agent = DroneRegistry.Get(targetDroneId);
            if (agent == null || agent.highFidelity == null)
            {
                if (_targetDot != null) _targetDot.gameObject.SetActive(false);
                foreach (var d in _queueDots) d.gameObject.SetActive(false);
                return;
            }
            var hf = agent.highFidelity;
            var origin = _minimap.drone.position;

            // 현재 목표.
            if (_targetDot == null) _targetDot = MakeDot("MinimapTargetDot", targetMarkerColor, targetMarkerRadius);
            if (agent.Waypoints.Current.HasValue)
            {
                Vector3 wp = hf.EnuToUnityWorld(agent.Waypoints.Current.Value);
                ((RectTransform)_targetDot.transform).anchoredPosition =
                    WorldOffsetToUi(wp.x - origin.x, wp.z - origin.z);
                _targetDot.gameObject.SetActive(true);
            }
            else _targetDot.gameObject.SetActive(false);

            // 큐 잔여.
            int i = 0;
            foreach (var w in agent.Waypoints.Upcoming)
            {
                if (i >= _queueDots.Count)
                    _queueDots.Add(MakeDot($"MinimapQueueDot_{i}", queueDotColor, queueDotRadius));
                Vector3 wp = hf.EnuToUnityWorld(w);
                ((RectTransform)_queueDots[i].transform).anchoredPosition =
                    WorldOffsetToUi(wp.x - origin.x, wp.z - origin.z);
                _queueDots[i].gameObject.SetActive(true);
                i++;
            }
            for (int j = i; j < _queueDots.Count; j++) _queueDots[j].gameObject.SetActive(false);
        }

        Vector2 WorldOffsetToUi(float dx, float dz)
        {
            Vector2 size = _minimap.panelSize;
            float hx = size.x * 0.5f, hy = size.y * 0.5f;
            return new Vector2(
                Mathf.Clamp(dx / _minimap.viewRadius * hx, -hx, hx),
                Mathf.Clamp(dz / _minimap.viewRadius * hy, -hy, hy));
        }

        UIShape MakeDot(string name, Color color, float radius)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_panelRt, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(radius * 2f, radius * 2f);
            var s = go.AddComponent<UIShape>();
            s.color = color;
            s.raycastTarget = false;
            BuildDisk(s, radius, 24);
            return s;
        }

        static void BuildDisk(UIShape s, float radius, int seg)
        {
            var verts = new Vector2[seg + 1];
            var tris = new int[seg * 3];
            verts[0] = Vector2.zero;
            for (int i = 0; i < seg; i++)
            {
                float a = (i / (float)seg) * Mathf.PI * 2f;
                verts[i + 1] = new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius);
            }
            for (int i = 0; i < seg; i++)
            {
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = 1 + i;
                tris[i * 3 + 2] = 1 + ((i + 1) % seg);
            }
            s.SetMesh(verts, tris);
        }
    }
}
