using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;
using N = System.Numerics;

namespace DroneSim.C2.StrategicView
{
    /// <summary>
    /// 모든 디스플레이 마우스 입력 → FlightCommands (다중 선택 + 박스 셀렉트 + Ctrl+드래그 SetPath).
    ///
    /// LMB(드론 마커): 단일 선택. Shift+클릭 = 추가 선택.
    /// LMB 드래그(빈 공간, strategic 디스플레이): 박스 셀렉트(다중).
    /// Ctrl+LMB 드래그(strategic): 마우스 궤적 일정 간격 샘플링 → SetPath(선택군).
    /// RMB: 선택군 전원 SetWaypoint(클릭 위치). Shift+RMB → Queue.
    /// 1대 환경에서도 다중 선택 코드 경로 동일하게 동작.
    /// </summary>
    [RequireComponent(typeof(StrategicViewBootstrap))]
    public class StrategicCommandInput : MonoBehaviour
    {
        [Header("선택 드론(다중) — 비어있으면 모든 드론 대상")]
        public List<string> selectedDroneIds = new List<string>();

        [Header("Ground/Raycast")]
        public LayerMask groundMask = ~0;
        public float raycastDistance = 1e8f;

        [Header("선택 클릭 임계 (viewport)")]
        public float selectViewportRadius = 0.04f;
        [Tooltip("이 이하의 드래그는 단순 클릭으로 간주 — 빈 공간 클릭으로 선택 해제 안 함.")]
        public float dragThresholdPixels = 6f;

        [Header("경로 샘플링")]
        public float pathSampleIntervalSec = 0.06f;

        [Header("고도 제어 (RMB 시 명령 고도)")]
        [Tooltip("RMB SetWaypoint 시 wp 고도 = 지면 + 이 값(real m, AGL). 0 = 드론 현재 고도 유지.")]
        public float commandAltitudeAGLMeters = 0f;
        public float altitudeStepMeters = 5f;
        public Key altitudeUpKey = Key.PageUp;
        public Key altitudeDownKey = Key.PageDown;
        public Key altitudeResetKey = Key.End;
        [Tooltip("숫자 키 1~5: AGL 프리셋 (각각 10/30/50/100/200 m).")]
        public bool enableNumericPresets = true;
        public LayerMask groundMaskForAGL = ~0;

        [Header("전체 정지")]
        [Tooltip("모든 드론의 웨이포인트 큐 비우고 현 자리 hover. RTS 의 Stop(S) 대응.")]
        public Key stopAllKey = Key.Space;

        [Header("시각화")]
        public Color boxColor = new Color(0.4f, 1f, 0.4f, 0.15f);
        public Color pathDotColor = new Color(0.4f, 1f, 0.4f, 0.9f);
        public float pathDotRadius = 4f;

        [Header("Refs")]
        public MultiDisplayCoordinator multiDisplay;

        StrategicViewBootstrap _bootstrap;

        enum DragMode { None, Box, Path }
        DragMode _drag = DragMode.None;
        Vector2 _dragStart, _dragCurrent;
        int _dragStartDisplay = -1;
        readonly List<Vector3> _pathPoints = new List<Vector3>();
        float _lastPathSampleTime;

        UIShape _boxShape;
        readonly List<UIShape> _pathDots = new List<UIShape>();

        public string selectedDroneId => selectedDroneIds.Count > 0 ? selectedDroneIds[0] : null;
        public bool IsSelected(string id) => selectedDroneIds.Count == 0 || selectedDroneIds.Contains(id);
        /// <summary>현재 활성 대상 드론들 — 선택이 비어있으면 등록된 모든 드론.</summary>
        IEnumerable<DroneAgent> EffectiveAgents()
        {
            if (selectedDroneIds.Count == 0)
            {
                foreach (var a in DroneRegistry.All) if (a != null) yield return a;
            }
            else
            {
                foreach (var id in selectedDroneIds)
                {
                    var a = DroneRegistry.Get(id);
                    if (a != null) yield return a;
                }
            }
        }

        void Awake()
        {
            _bootstrap = GetComponent<StrategicViewBootstrap>();
            if (multiDisplay == null) multiDisplay = FindObjectOfType<MultiDisplayCoordinator>();
            // 레거시 직렬화 보정: 옛 기본값 ["drone_sim_obj_0"] 이 씬에 남아 있으면 비워서 "전체 대상" 모드로.
            if (selectedDroneIds.Count == 1 && selectedDroneIds[0] == "drone_sim_obj_0")
            {
                selectedDroneIds.Clear();
                Debug.Log("[Cmd] legacy single selection 정리 → target = ALL drones");
            }
        }

        void Start()
        {
            Debug.Log($"[Cmd] 시작 선택 = " +
                      (selectedDroneIds.Count == 0
                       ? $"ALL ({DroneRegistry.Count} 대)"
                       : $"[{string.Join(",", selectedDroneIds)}]"));
        }

        void Update()
        {
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            if (mouse == null) return;
            Vector2 screen = mouse.position.ReadValue();
            bool lmbDown = mouse.leftButton.wasPressedThisFrame;
            bool lmbUp = mouse.leftButton.wasReleasedThisFrame;
            bool lmbHeld = mouse.leftButton.isPressed;
            bool rmb = mouse.rightButton.wasPressedThisFrame;
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            bool ctrl = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);

            // 전체 정지 키 — 모든 드론 큐 비우고 hover (선택 무관).
            if (kb != null && stopAllKey != Key.None && kb[stopAllKey].wasPressedThisFrame)
            {
                int n = 0;
                foreach (var a in DroneRegistry.All)
                {
                    if (a == null) continue;
                    if (FlightCommands.StopAndHover(a.agentId)) n++;
                }
                Debug.Log($"[Cmd] STOP ALL → {n} drones hover");
            }

            // 고도 키.
            if (kb != null)
            {
                if (kb[altitudeUpKey].wasPressedThisFrame) commandAltitudeAGLMeters += altitudeStepMeters;
                if (kb[altitudeDownKey].wasPressedThisFrame) commandAltitudeAGLMeters = Mathf.Max(0f, commandAltitudeAGLMeters - altitudeStepMeters);
                if (kb[altitudeResetKey].wasPressedThisFrame) commandAltitudeAGLMeters = 0f;
                if (enableNumericPresets)
                {
                    if (kb.digit1Key.wasPressedThisFrame) commandAltitudeAGLMeters = 10f;
                    if (kb.digit2Key.wasPressedThisFrame) commandAltitudeAGLMeters = 30f;
                    if (kb.digit3Key.wasPressedThisFrame) commandAltitudeAGLMeters = 50f;
                    if (kb.digit4Key.wasPressedThisFrame) commandAltitudeAGLMeters = 100f;
                    if (kb.digit5Key.wasPressedThisFrame) commandAltitudeAGLMeters = 200f;
                    if (kb.digit0Key.wasPressedThisFrame) commandAltitudeAGLMeters = 0f;
                }
            }

            if (rmb) { HandleRmb(screen, shift); return; }

            if (lmbDown) HandleLmbDown(screen, shift, ctrl);
            if (lmbHeld && _drag != DragMode.None) HandleDragUpdate(screen);
            if (lmbUp) HandleLmbUp();
        }

        // ── LMB Press ─────────────────────────────────────────────────────
        void HandleLmbDown(Vector2 screen, bool shift, bool ctrl)
        {
            int displayIdx = GetMouseDisplayIndex(screen);
            Camera cam = PickCameraForDisplay(displayIdx);
            if (cam == null) return;
            Vector2 vp = ViewportFor(cam, screen);

            DroneAgent hit = HitDroneAt(cam, vp);
            if (hit != null)
            {
                if (shift) AddToSelection(hit.agentId);
                else SelectSingle(hit.agentId);
                _drag = DragMode.None;
                return;
            }

            // 빈 공간 클릭: strategic 디스플레이에서만 box/path 드래그 가능.
            if (cam == _bootstrap.StrategicCamera)
            {
                _dragStart = screen;
                _dragCurrent = screen;
                _dragStartDisplay = displayIdx;
                if (ctrl)
                {
                    _drag = DragMode.Path;
                    _pathPoints.Clear();
                    if (RaycastFromViewport(cam, vp, out Vector3 hitW))
                    {
                        _pathPoints.Add(hitW);
                        _lastPathSampleTime = Time.time;
                    }
                }
                else
                {
                    _drag = DragMode.Box;
                }
            }
        }

        // ── 드래그 진행 ──────────────────────────────────────────────────
        void HandleDragUpdate(Vector2 screen)
        {
            _dragCurrent = screen;
            if (_drag == DragMode.Box) UpdateBoxVisual();
            else if (_drag == DragMode.Path)
            {
                if (Time.time - _lastPathSampleTime > pathSampleIntervalSec)
                {
                    Camera cam = PickCameraForDisplay(_dragStartDisplay);
                    if (cam != null)
                    {
                        Vector2 vp = ViewportFor(cam, screen);
                        if (InViewport(vp) && RaycastFromViewport(cam, vp, out Vector3 hit))
                        {
                            _pathPoints.Add(hit);
                            _lastPathSampleTime = Time.time;
                        }
                    }
                }
                UpdatePathVisual();
            }
        }

        // ── LMB Release ───────────────────────────────────────────────────
        void HandleLmbUp()
        {
            if (_drag == DragMode.Box) FinalizeBoxSelect();
            else if (_drag == DragMode.Path) FinalizePath();
            ClearDragVisuals();
            _drag = DragMode.None;
        }

        // ── RMB → SetWaypoint to all selected ────────────────────────────
        void HandleRmb(Vector2 screen, bool shift)
        {
            int displayIdx = GetMouseDisplayIndex(screen);
            Camera cam = PickCameraForDisplay(displayIdx);
            if (cam == null) { Debug.Log("[Cmd] RMB display 미확인"); return; }
            Vector2 vp = ViewportFor(cam, screen);
            if (!InViewport(vp)) return;
            if (!RaycastFromViewport(cam, vp, out Vector3 worldHit))
            {
                Debug.Log($"[Cmd] RMB raycast fail display={displayIdx}");
                return;
            }
            int sent = 0;
            foreach (var agent in EffectiveAgents())
            {
                if (agent.highFidelity == null) continue;
                var hf = agent.highFidelity;
                Vector3 wpWorld = worldHit;
                if (commandAltitudeAGLMeters > 0f)
                {
                    float scale = hf.UnityUnitsPerMeter;
                    wpWorld.y = worldHit.y + commandAltitudeAGLMeters * scale;
                }
                var enu = hf.UnityWorldToEnu(wpWorld);
                if (commandAltitudeAGLMeters <= 0f)
                    enu = new N.Vector3(enu.X, enu.Y, hf.PositionEnu.Z);   // 현 고도 유지
                if (shift) FlightCommands.QueueWaypoint(agent.agentId, enu);
                else FlightCommands.SetWaypoint(agent.agentId, enu);
                sent++;
            }
            Debug.Log($"[Cmd] {(shift ? "Queue" : "Set")}Waypoint(disp{displayIdx + 1}) → {sent} drones, AGL={commandAltitudeAGLMeters:F0}m");
        }

        // ── Box Select 최종 ──────────────────────────────────────────────
        void FinalizeBoxSelect()
        {
            if ((_dragCurrent - _dragStart).sqrMagnitude < dragThresholdPixels * dragThresholdPixels)
            {
                // 빈 공간 단순 클릭 → 선택 해제 (= 전체 드론 대상으로 복귀).
                if (selectedDroneIds.Count > 0)
                {
                    selectedDroneIds.Clear();
                    Debug.Log("[Cmd] selection cleared (target = all drones)");
                }
                return;
            }
            Camera cam = PickCameraForDisplay(_dragStartDisplay);
            if (cam == null) return;
            Vector2 vp1 = ViewportFor(cam, _dragStart);
            Vector2 vp2 = ViewportFor(cam, _dragCurrent);
            float minX = Mathf.Min(vp1.x, vp2.x), maxX = Mathf.Max(vp1.x, vp2.x);
            float minY = Mathf.Min(vp1.y, vp2.y), maxY = Mathf.Max(vp1.y, vp2.y);

            selectedDroneIds.Clear();
            foreach (var a in DroneRegistry.All)
            {
                if (a == null || a.highFidelity == null) continue;
                Vector3 v3 = cam.WorldToViewportPoint(a.highFidelity.PositionUnity);
                if (v3.z < 0f) continue;
                if (v3.x >= minX && v3.x <= maxX && v3.y >= minY && v3.y <= maxY)
                    selectedDroneIds.Add(a.agentId);
            }
            Debug.Log($"[Cmd] box select count={selectedDroneIds.Count}");
        }

        // ── Path 최종 → SetPath ─────────────────────────────────────────
        void FinalizePath()
        {
            if (_pathPoints.Count < 2) return;
            var enuPath = new List<N.Vector3>();
            int sent = 0;
            foreach (var agent in EffectiveAgents())
            {
                if (agent.highFidelity == null) continue;
                var hf = agent.highFidelity;
                enuPath.Clear();
                float scale = hf.UnityUnitsPerMeter;
                foreach (var w in _pathPoints)
                {
                    Vector3 wpWorld = w;
                    if (commandAltitudeAGLMeters > 0f) wpWorld.y = w.y + commandAltitudeAGLMeters * scale;
                    var enu = hf.UnityWorldToEnu(wpWorld);
                    if (commandAltitudeAGLMeters <= 0f)
                        enu = new N.Vector3(enu.X, enu.Y, hf.PositionEnu.Z);
                    enuPath.Add(enu);
                }
                FlightCommands.SetPath(agent.agentId, enuPath);
                sent++;
            }
            Debug.Log($"[Cmd] SetPath ({_pathPoints.Count} pts) → {sent} drones");
        }

        // ── 선택 ────────────────────────────────────────────────────────
        void SelectSingle(string id)
        {
            selectedDroneIds.Clear();
            selectedDroneIds.Add(id);
            Debug.Log($"[Cmd] selected: {id}");
        }
        void AddToSelection(string id)
        {
            if (!selectedDroneIds.Contains(id)) selectedDroneIds.Add(id);
            Debug.Log($"[Cmd] selection: {string.Join(",", selectedDroneIds)}");
        }

        DroneAgent HitDroneAt(Camera cam, Vector2 clickVp)
        {
            float bestSq = selectViewportRadius * selectViewportRadius;
            DroneAgent best = null;
            foreach (var a in DroneRegistry.All)
            {
                if (a == null || a.highFidelity == null) continue;
                Vector3 v3 = cam.WorldToViewportPoint(a.highFidelity.PositionUnity);
                if (v3.z < 0f) continue;
                float d = ((Vector2)v3 - clickVp).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = a; }
            }
            return best;
        }

        // ── 시각화 ──────────────────────────────────────────────────────
        void UpdateBoxVisual()
        {
            if (_boxShape == null) _boxShape = MakeBoxShape();
            Vector2 a = _dragStart, b = _dragCurrent;
            float w = Mathf.Abs(b.x - a.x), h = Mathf.Abs(b.y - a.y);
            float cx = (a.x + b.x) * 0.5f, cy = (a.y + b.y) * 0.5f;
            BuildRect(_boxShape, w, h);
            var rt = (RectTransform)_boxShape.transform;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(cx, cy);
            _boxShape.gameObject.SetActive(true);
        }

        void UpdatePathVisual()
        {
            Camera cam = PickCameraForDisplay(_dragStartDisplay);
            if (cam == null) return;
            while (_pathDots.Count < _pathPoints.Count) _pathDots.Add(MakePathDot());
            for (int i = 0; i < _pathPoints.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(_pathPoints[i]);
                var rt = (RectTransform)_pathDots[i].transform;
                rt.anchoredPosition = new Vector2(sp.x, sp.y);
                _pathDots[i].gameObject.SetActive(sp.z > 0f);
            }
            for (int j = _pathPoints.Count; j < _pathDots.Count; j++) _pathDots[j].gameObject.SetActive(false);
        }

        void ClearDragVisuals()
        {
            if (_boxShape != null) _boxShape.gameObject.SetActive(false);
            foreach (var d in _pathDots) d.gameObject.SetActive(false);
        }

        UIShape MakeBoxShape()
        {
            var go = new GameObject("StrategicBoxSelect");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_bootstrap.CanvasRect, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var s = go.AddComponent<UIShape>();
            s.color = boxColor;
            s.raycastTarget = false;
            return s;
        }
        UIShape MakePathDot()
        {
            var go = new GameObject("StrategicPathDot");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_bootstrap.CanvasRect, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(pathDotRadius * 2, pathDotRadius * 2);
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

        // ── 헬퍼 ────────────────────────────────────────────────────────
        Vector2 ViewportFor(Camera cam, Vector2 screen)
        {
            float w = cam.pixelWidth > 0 ? cam.pixelWidth : Screen.width;
            float h = cam.pixelHeight > 0 ? cam.pixelHeight : Screen.height;
            return new Vector2(screen.x / w, screen.y / h);
        }
        static bool InViewport(Vector2 vp)
            => vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;

        bool RaycastFromViewport(Camera cam, Vector2 vp, out Vector3 worldPoint)
        {
            Ray ray = cam.ViewportPointToRay(new Vector3(vp.x, vp.y, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, raycastDistance, groundMask, QueryTriggerInteraction.Ignore))
            {
                worldPoint = hit.point; return true;
            }
            bool prev = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool ok = Physics.Raycast(ray, out hit, raycastDistance, groundMask, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = prev;
            if (ok) { worldPoint = hit.point; return true; }
            worldPoint = default; return false;
        }

        int GetMouseDisplayIndex(Vector2 screen)
        {
#if UNITY_EDITOR
            var ew = UnityEditor.EditorWindow.mouseOverWindow;
            if (ew != null && ew.GetType().Name == "GameView")
            {
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;
                var prop = ew.GetType().GetProperty("targetDisplay", F);
                if (prop != null && prop.PropertyType == typeof(int)) return (int)prop.GetValue(ew);
                var field = ew.GetType().GetField("m_TargetDisplay", F);
                if (field != null && field.FieldType == typeof(int)) return (int)field.GetValue(ew);
            }
            return -1;
#else
            Vector3 rel = Display.RelativeMouseAt(new Vector3(screen.x, screen.y, 0f));
            return (int)rel.z;
#endif
        }

        Camera PickCameraForDisplay(int idx)
        {
            if (idx == _bootstrap.DisplayIndex) return _bootstrap.StrategicCamera;
            if (multiDisplay != null)
            {
                if (idx == multiDisplay.fpDisplayIndex && multiDisplay.fpCamera != null) return multiDisplay.fpCamera;
                if (idx == multiDisplay.mainDisplayIndex && multiDisplay.mainCamera != null) return multiDisplay.mainCamera;
            }
            return null;
        }
    }
}
