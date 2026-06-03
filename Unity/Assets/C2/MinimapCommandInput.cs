using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;
using N = System.Numerics;

namespace DroneSim.C2
{
    /// <summary>
    /// 미니맵 마우스 입력 + 시각화 (Display 3 와 동일한 컨벤션).
    ///
    /// 시각화:
    ///   - 모든 등록 드론 = 원(점). 선택군은 노랑, 비선택은 시안 (`StrategicCommandInput` 의 선택과 동기).
    ///   - 각 드론의 현 wp = 녹색 원, 큐 wp = 작은 녹색 점.
    ///   - LMB 드래그 = 박스 셀렉트(직사각형). Ctrl+LMB 드래그 = Path 샘플(점선).
    ///
    /// 입력 (Display 3 와 동일):
    ///   - LMB(드론 점): 단일 선택. Shift+LMB = 선택군에 추가/제거.
    ///   - LMB 빈 공간 드래그: 박스 셀렉트 (드래그 임계 이상). 단순 클릭(임계 이하) = 선택 해제.
    ///   - Ctrl+LMB 드래그: 마우스 궤적 일정 간격 샘플링 → SetPath(선택군 전체).
    ///   - RMB(빈 곳): SetWaypoint(선택군 전체, 클릭점 ENU, 각 드론 고도 유지)
    ///   - Shift+RMB: QueueWaypoint
    ///
    /// 좌표 변환은 **부모 panel pivot 에 무관**하게 동작 — `RectTransformUtility` 의 local 값과
    /// pivot, rect.size 만으로 normalized(0..1) 좌표를 산출.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Minimap))]
    public class MinimapCommandInput : MonoBehaviour
    {
        [Header("Refs (비우면 자동)")]
        public StrategicView.StrategicCommandInput selectionSrc;

        [Header("색/크기 (Display 3 마커와 동일 컨벤션)")]
        public Color droneColor = new Color(0.3f, 0.9f, 1f, 1f);
        public Color selectedColor = new Color(1f, 0.9f, 0.2f, 1f);
        public Color targetColor = new Color(0.4f, 1f, 0.4f, 0.95f);
        public Color queueColor = new Color(0.4f, 1f, 0.4f, 0.55f);
        public float droneRadius = 6f;
        public float targetRadius = 7f;
        public float queueRadius = 4f;

        [Header("선택 hit-test 반경 (UI 픽셀)")]
        [Tooltip("LMB 클릭 시 이 픽셀 안의 드론 점이면 그 드론 선택. droneRadius 보다 크게 잡아 손가락 굵기 보정.")]
        public float selectHitRadiusPixels = 14f;

        [Header("드래그 임계")]
        [Tooltip("이 이하의 드래그는 단순 클릭으로 간주 — 빈 공간 클릭으로 선택 해제 안 함.")]
        public float dragThresholdPixels = 6f;

        [Header("Path 드래그 (Ctrl+LMB)")]
        public float pathSampleIntervalSec = 0.06f;

        [Header("박스/Path 시각화")]
        public Color boxColor = new Color(0.4f, 1f, 0.4f, 0.15f);
        public Color pathDotColor = new Color(0.4f, 1f, 0.4f, 0.9f);
        public float pathDotRadius = 4f;

        Minimap _minimap;
        RectTransform _panelRt;
        RectTransform _bodyRt;          // DraggableHud 가 만든 본문(헤더 제외) — 클릭 영역.
        RectTransform _gripRt;          // DraggableHud 의 resize 그립 — 클릭 시 제외.
        readonly Dictionary<string, UIShape> _droneDots = new Dictionary<string, UIShape>();
        readonly Dictionary<string, UIShape> _targetDots = new Dictionary<string, UIShape>();
        readonly Dictionary<string, List<UIShape>> _queueDots = new Dictionary<string, List<UIShape>>();
        readonly List<string> _toRemove = new List<string>();

        // ─── 드래그 상태 ───────────────────────────────────────────────
        enum DragMode { None, Box, Path }
        DragMode _drag = DragMode.None;
        Vector2 _dragStartScreen, _dragCurrentScreen;
        Vector2 _dragStartCentered;            // panel local centered (자식 anchoredPosition 좌표계)
        Vector2 _dragCurrentCentered;
        readonly List<Vector3> _pathWorldPoints = new List<Vector3>();
        float _lastPathSampleTime;
        UIShape _boxShape;
        readonly List<UIShape> _pathDots = new List<UIShape>();

        void Awake()
        {
            _minimap = GetComponent<Minimap>();
            if (selectionSrc == null) selectionSrc = FindObjectOfType<StrategicView.StrategicCommandInput>();
        }

        void Update()
        {
            if (!ResolveRefs()) return;
            HandleMouse();
            UpdateVisuals();
        }

        bool ResolveRefs()
        {
            if (_panelRt == null)
            {
                var t = transform.Find("Minimap Canvas/MinimapPanel");
                if (t != null) _panelRt = t as RectTransform;
            }
            if (_bodyRt == null && _panelRt != null)
            {
                var tb = _panelRt.Find("Body");   // DraggableHud 가 만든 body 컨테이너.
                if (tb != null) _bodyRt = tb as RectTransform;
            }
            if (_gripRt == null)
            {
                var hud = GetComponent<DraggableHud>();
                if (hud != null) _gripRt = hud.ResizeGripRt;
            }
            return _minimap != null && _minimap.drone != null && _panelRt != null;
        }

        /// <summary>클릭이 body 영역(헤더 제외) 안에 있는지. body 가 없으면 panel 전체.</summary>
        RectTransform InteractionRect() => _bodyRt != null ? _bodyRt : _panelRt;

        /// <summary>resize 그립 위에서 클릭한 경우 — 입력 무시.</summary>
        bool OverGrip(Vector2 screen)
            => _gripRt != null && _gripRt.gameObject.activeInHierarchy &&
               RectTransformUtility.RectangleContainsScreenPoint(_gripRt, screen, null);

        /// <summary>화면 좌표 → 패널 중앙 기준 centered local (자식 dot anchoredPosition 과 같은 좌표계).</summary>
        bool ScreenToCentered(Vector2 screen, out Vector2 centered)
        {
            centered = default;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_panelRt, screen, null, out Vector2 local))
                return false;
            Vector2 size = _panelRt.rect.size;
            if (size.x <= 0f || size.y <= 0f) return false;
            Vector2 pivot = _panelRt.pivot;
            centered = local + new Vector2(size.x * (pivot.x - 0.5f), size.y * (pivot.y - 0.5f));
            return true;
        }

        /// <summary>centered local → 월드 (origin=drone 위치, Y 는 drone Y 유지).</summary>
        bool CenteredToWorld(Vector2 centered, out Vector3 worldXZ)
        {
            worldXZ = default;
            Vector2 size = _panelRt.rect.size;
            if (size.x <= 0f || size.y <= 0f) return false;
            float dx = centered.x / (size.x * 0.5f) * _minimap.viewRadius;
            float dz = centered.y / (size.y * 0.5f) * _minimap.viewRadius;
            Vector3 origin = _minimap.drone.position;
            worldXZ = new Vector3(origin.x + dx, origin.y, origin.z + dz);
            return true;
        }

        // 월드 offset(dx, dz) → 자식 anchoredPosition (자식 anchor=(0.5,0.5) 이라면 부모 중앙 기준).
        Vector2 WorldOffsetToUi(float dx, float dz)
        {
            Vector2 size = _panelRt.rect.size;
            float hx = size.x * 0.5f, hy = size.y * 0.5f;
            return new Vector2(
                Mathf.Clamp(dx / _minimap.viewRadius * hx, -hx, hx),
                Mathf.Clamp(dz / _minimap.viewRadius * hy, -hy, hy));
        }

        // ─── 통합 마우스 처리 ──────────────────────────────────────────
        void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var kb = Keyboard.current;
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            bool ctrl = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);

            Vector2 screen = mouse.position.ReadValue();
            bool lmbDown = mouse.leftButton.wasPressedThisFrame;
            bool lmbHeld = mouse.leftButton.isPressed;
            bool lmbUp = mouse.leftButton.wasReleasedThisFrame;
            bool rmbDown = mouse.rightButton.wasPressedThisFrame;

            // RMB → SetWaypoint / Queue (드래그 영향 X, 즉시).
            if (rmbDown) HandleRmb(screen, shift);

            // LMB Down: body 안 + grip 위 아님일 때만.
            if (lmbDown && !OverGrip(screen) &&
                RectTransformUtility.RectangleContainsScreenPoint(InteractionRect(), screen, null))
            {
                HandleLmbDown(screen, shift, ctrl);
            }

            if (lmbHeld && _drag != DragMode.None) HandleDragUpdate(screen);
            if (lmbUp && _drag != DragMode.None) HandleLmbUp();
        }

        // ─── LMB Press ─────────────────────────────────────────────────
        void HandleLmbDown(Vector2 screen, bool shift, bool ctrl)
        {
            if (!ScreenToCentered(screen, out Vector2 centered)) return;

            // 1) 드론 점 hit-test 우선.
            string hitId = HitDroneIdAt(centered);
            if (hitId != null)
            {
                if (selectionSrc == null) return;
                if (shift)
                {
                    if (selectionSrc.selectedDroneIds.Contains(hitId)) selectionSrc.selectedDroneIds.Remove(hitId);
                    else selectionSrc.selectedDroneIds.Add(hitId);
                }
                else
                {
                    selectionSrc.selectedDroneIds.Clear();
                    selectionSrc.selectedDroneIds.Add(hitId);
                }
                Debug.Log($"[MinimapCmd] select {hitId} (shift={shift}) → {selectionSrc.selectedDroneIds.Count} selected");
                _drag = DragMode.None;
                return;
            }

            // 2) 빈 공간 → 드래그 시작 (Ctrl 면 Path, 아니면 Box).
            _dragStartScreen = _dragCurrentScreen = screen;
            _dragStartCentered = _dragCurrentCentered = centered;
            if (ctrl)
            {
                _drag = DragMode.Path;
                _pathWorldPoints.Clear();
                if (CenteredToWorld(centered, out Vector3 w0))
                {
                    _pathWorldPoints.Add(w0);
                    _lastPathSampleTime = Time.time;
                }
            }
            else
            {
                _drag = DragMode.Box;
            }
        }

        void HandleDragUpdate(Vector2 screen)
        {
            _dragCurrentScreen = screen;
            if (!ScreenToCentered(screen, out Vector2 centered)) return;
            _dragCurrentCentered = centered;

            if (_drag == DragMode.Box) UpdateBoxVisual();
            else if (_drag == DragMode.Path)
            {
                if (Time.time - _lastPathSampleTime > pathSampleIntervalSec)
                {
                    if (CenteredToWorld(centered, out Vector3 w))
                    {
                        _pathWorldPoints.Add(w);
                        _lastPathSampleTime = Time.time;
                    }
                }
                UpdatePathVisual();
            }
        }

        void HandleLmbUp()
        {
            if (_drag == DragMode.Box) FinalizeBoxSelect();
            else if (_drag == DragMode.Path) FinalizePath();
            ClearDragVisuals();
            _drag = DragMode.None;
        }

        // ─── Box select 최종 ─────────────────────────────────────────
        void FinalizeBoxSelect()
        {
            if ((_dragCurrentScreen - _dragStartScreen).sqrMagnitude <
                dragThresholdPixels * dragThresholdPixels)
            {
                // 단순 클릭 — 빈 공간 클릭 = 선택 해제.
                if (selectionSrc != null && selectionSrc.selectedDroneIds.Count > 0)
                {
                    selectionSrc.selectedDroneIds.Clear();
                    Debug.Log("[MinimapCmd] 선택 해제 → ALL");
                }
                return;
            }
            if (selectionSrc == null) return;
            float minX = Mathf.Min(_dragStartCentered.x, _dragCurrentCentered.x);
            float maxX = Mathf.Max(_dragStartCentered.x, _dragCurrentCentered.x);
            float minY = Mathf.Min(_dragStartCentered.y, _dragCurrentCentered.y);
            float maxY = Mathf.Max(_dragStartCentered.y, _dragCurrentCentered.y);
            selectionSrc.selectedDroneIds.Clear();
            foreach (var kv in _droneDots)
            {
                if (kv.Value == null || !kv.Value.gameObject.activeSelf) continue;
                Vector2 p = ((RectTransform)kv.Value.transform).anchoredPosition;
                if (p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY)
                    selectionSrc.selectedDroneIds.Add(kv.Key);
            }
            Debug.Log($"[MinimapCmd] box select count={selectionSrc.selectedDroneIds.Count}");
        }

        // ─── Path 최종 → SetPath ────────────────────────────────────
        void FinalizePath()
        {
            if (_pathWorldPoints.Count < 2) return;
            var enuPath = new List<N.Vector3>();
            int sent = 0;
            foreach (var agent in EffectiveAgents())
            {
                if (agent == null || agent.highFidelity == null) continue;
                var hf = agent.highFidelity;
                enuPath.Clear();
                var cur = hf.PositionEnu;
                foreach (var w in _pathWorldPoints)
                {
                    var e = hf.UnityWorldToEnu(w);
                    enuPath.Add(new N.Vector3(e.X, e.Y, cur.Z));   // 현 고도 유지
                }
                FlightCommands.SetPath(agent.agentId, enuPath);
                sent++;
            }
            Debug.Log($"[MinimapCmd] SetPath ({_pathWorldPoints.Count} pts) → {sent} drones");
        }

        string HitDroneIdAt(Vector2 centered)
        {
            float bestDist = float.MaxValue;
            string bestId = null;
            foreach (var kv in _droneDots)
            {
                if (kv.Value == null || !kv.Value.gameObject.activeSelf) continue;
                Vector2 p = ((RectTransform)kv.Value.transform).anchoredPosition;
                float d = Vector2.Distance(centered, p);
                if (d < selectHitRadiusPixels && d < bestDist) { bestDist = d; bestId = kv.Key; }
            }
            return bestId;
        }

        // ─── RMB 명령 라우팅 ───────────────────────────────────────────
        void HandleRmb(Vector2 screen, bool shift)
        {
            if (OverGrip(screen)) return;
            if (!RectTransformUtility.RectangleContainsScreenPoint(InteractionRect(), screen, null)) return;
            if (!ScreenToCentered(screen, out Vector2 centered)) return;
            if (!CenteredToWorld(centered, out Vector3 targetUnity)) return;

            int n = 0;
            foreach (var agent in EffectiveAgents())
            {
                if (agent == null || agent.highFidelity == null) continue;
                var hf = agent.highFidelity;
                var targetEnu = hf.UnityWorldToEnu(targetUnity);
                var cur = hf.PositionEnu;
                targetEnu = new N.Vector3(targetEnu.X, targetEnu.Y, cur.Z);   // 현 고도 유지
                if (shift) FlightCommands.QueueWaypoint(agent.agentId, targetEnu);
                else FlightCommands.SetWaypoint(agent.agentId, targetEnu);
                n++;
            }
            Debug.Log($"[MinimapCmd] {(shift ? "Queue" : "Set")}Waypoint → {n} drones");
        }

        // ─── 박스/Path 시각화 ───────────────────────────────────────
        void UpdateBoxVisual()
        {
            if (_boxShape == null) _boxShape = MakeBoxShape();
            float w = Mathf.Abs(_dragCurrentCentered.x - _dragStartCentered.x);
            float h = Mathf.Abs(_dragCurrentCentered.y - _dragStartCentered.y);
            float cx = (_dragStartCentered.x + _dragCurrentCentered.x) * 0.5f;
            float cy = (_dragStartCentered.y + _dragCurrentCentered.y) * 0.5f;
            BuildRect(_boxShape, w, h);
            var rt = (RectTransform)_boxShape.transform;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(cx, cy);
            _boxShape.gameObject.SetActive(true);
        }

        void UpdatePathVisual()
        {
            while (_pathDots.Count < _pathWorldPoints.Count) _pathDots.Add(MakePathDot());
            Vector3 origin = _minimap.drone.position;
            for (int i = 0; i < _pathWorldPoints.Count; i++)
            {
                Vector3 wp = _pathWorldPoints[i];
                ((RectTransform)_pathDots[i].transform).anchoredPosition =
                    WorldOffsetToUi(wp.x - origin.x, wp.z - origin.z);
                _pathDots[i].gameObject.SetActive(true);
            }
            for (int j = _pathWorldPoints.Count; j < _pathDots.Count; j++) _pathDots[j].gameObject.SetActive(false);
        }

        void ClearDragVisuals()
        {
            if (_boxShape != null) _boxShape.gameObject.SetActive(false);
            foreach (var d in _pathDots) if (d != null) d.gameObject.SetActive(false);
        }

        UIShape MakeBoxShape()
        {
            var go = new GameObject("MinimapBoxSelect");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_panelRt, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var s = go.AddComponent<UIShape>();
            s.color = boxColor;
            s.raycastTarget = false;
            return s;
        }

        UIShape MakePathDot()
        {
            var go = new GameObject($"MinimapPathDot_{_pathDots.Count}");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_panelRt, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(pathDotRadius * 2f, pathDotRadius * 2f);
            var s = go.AddComponent<UIShape>();
            s.color = pathDotColor;
            s.raycastTarget = false;
            BuildDisk(s, pathDotRadius, 12);
            return s;
        }

        static void BuildRect(UIShape s, float w, float h)
        {
            var verts = new Vector2[4] {
                new Vector2(-w*0.5f, -h*0.5f),
                new Vector2( w*0.5f, -h*0.5f),
                new Vector2( w*0.5f,  h*0.5f),
                new Vector2(-w*0.5f,  h*0.5f),
            };
            var tris = new int[6] { 0, 2, 1, 0, 3, 2 };
            s.SetMesh(verts, tris);
        }

        IEnumerable<DroneAgent> EffectiveAgents()
        {
            if (selectionSrc != null && selectionSrc.selectedDroneIds.Count > 0)
            {
                foreach (var id in selectionSrc.selectedDroneIds)
                {
                    var a = DroneRegistry.Get(id);
                    if (a != null) yield return a;
                }
            }
            else
            {
                foreach (var a in DroneRegistry.All) if (a != null) yield return a;
            }
        }

        // ─── 시각화 ────────────────────────────────────────────────────
        void UpdateVisuals()
        {
            Vector3 origin = _minimap.drone.position;
            var seen = new HashSet<string>();

            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null || agent.highFidelity == null) continue;
                seen.Add(agent.agentId);
                var hf = agent.highFidelity;

                bool isSel = selectionSrc != null && selectionSrc.IsSelected(agent.agentId);

                // 드론 점.
                if (!_droneDots.TryGetValue(agent.agentId, out var dot))
                {
                    dot = MakeDot($"DroneDot_{agent.agentId}", droneColor, droneRadius);
                    _droneDots[agent.agentId] = dot;
                }
                Vector3 dp = hf.PositionUnity;
                ((RectTransform)dot.transform).anchoredPosition =
                    WorldOffsetToUi(dp.x - origin.x, dp.z - origin.z);
                dot.color = isSel ? selectedColor : droneColor;
                dot.gameObject.SetActive(true);

                // 현재 타겟.
                if (!_targetDots.TryGetValue(agent.agentId, out var tdot))
                {
                    tdot = MakeDot($"TargetDot_{agent.agentId}", targetColor, targetRadius);
                    _targetDots[agent.agentId] = tdot;
                }
                if (agent.Waypoints.Current.HasValue)
                {
                    Vector3 wp = hf.EnuToUnityWorld(agent.Waypoints.Current.Value);
                    ((RectTransform)tdot.transform).anchoredPosition =
                        WorldOffsetToUi(wp.x - origin.x, wp.z - origin.z);
                    tdot.gameObject.SetActive(true);
                }
                else tdot.gameObject.SetActive(false);

                // 큐.
                if (!_queueDots.TryGetValue(agent.agentId, out var qList))
                {
                    qList = new List<UIShape>();
                    _queueDots[agent.agentId] = qList;
                }
                int i = 0;
                foreach (var w in agent.Waypoints.Upcoming)
                {
                    while (i >= qList.Count) qList.Add(MakeDot($"Q_{agent.agentId}_{qList.Count}", queueColor, queueRadius));
                    Vector3 wp = hf.EnuToUnityWorld(w);
                    ((RectTransform)qList[i].transform).anchoredPosition =
                        WorldOffsetToUi(wp.x - origin.x, wp.z - origin.z);
                    qList[i].gameObject.SetActive(true);
                    i++;
                }
                for (int j = i; j < qList.Count; j++) qList[j].gameObject.SetActive(false);
            }

            // 사라진 드론 정리.
            _toRemove.Clear();
            foreach (var kv in _droneDots) if (!seen.Contains(kv.Key)) _toRemove.Add(kv.Key);
            foreach (var id in _toRemove)
            {
                if (_droneDots.TryGetValue(id, out var d) && d != null) Destroy(d.gameObject);
                _droneDots.Remove(id);
                if (_targetDots.TryGetValue(id, out var t) && t != null) Destroy(t.gameObject);
                _targetDots.Remove(id);
                if (_queueDots.TryGetValue(id, out var ql))
                {
                    foreach (var q in ql) if (q != null) Destroy(q.gameObject);
                    _queueDots.Remove(id);
                }
            }
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
