using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DroneSim.Flight.UnityAdapter;
using N = System.Numerics;

namespace DroneSim.C2.StrategicView
{
    /// <summary>
    /// 전략 보기 마커 HUD — Display 3 의 Strategic Canvas 위에 그리기 + 번호 라벨.
    ///
    /// 표시:
    ///   - Me  : 'ME' 라벨.
    ///   - Drone : 드론 번호 (agentId 의 마지막 _N).
    ///   - Target waypoint : **같은 위치 그룹핑** → 라벨 = "all" (모든 드론 공유) 또는 "0,1,2" 콤마.
    ///   - Queue : 같은 그룹핑, 같은 라벨.
    /// </summary>
    [RequireComponent(typeof(StrategicViewBootstrap))]
    public class StrategicViewHud : MonoBehaviour
    {
        [Header("기준점")]
        public Transform meAnchor;

        [Header("색/크기 (픽셀)")]
        public Color meColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        public Color droneColor = new Color(0.2f, 0.9f, 1f, 1f);
        public Color selectedColor = new Color(1f, 0.9f, 0.2f, 1f);
        public Color targetColor = new Color(0.3f, 1f, 0.4f, 1f);
        public Color queueColor = new Color(0.3f, 1f, 0.4f, 0.55f);
        public Color labelColor = Color.white;
        public Color labelBgColor = new Color(0f, 0f, 0f, 0.75f);
        public float droneMarkerRadius = 10f;
        public float meMarkerRadius = 12f;
        public float targetMarkerRadius = 9f;
        public float queueMarkerRadius = 5f;
        public int labelFontSize = 18;
        public float labelBgPaddingX = 4f;
        public float labelBgPaddingY = 2f;

        [Header("그룹핑")]
        [Tooltip("같은 위치(픽셀)로 간주할 bin 크기. 작을수록 분리, 크면 묶임.")]
        public int groupingBinPx = 12;

        [Header("GPS 원점 / 검출")]
        public Color originColor = new Color(1f, 0.85f, 0.2f, 1f);
        public float originMarkerRadius = 18f;
        public bool showDetectionMarkers = true;
        public float detectionMarkerRadius = 11f;
        public bool showLegend = true;
        public Vector2 legendSize = new Vector2(340f, 320f);
        public Vector2 legendMargin = new Vector2(24f, 24f);

        [Header("디버그 HUD 박스")]
        public Vector2 debugHudSize = new Vector2(720f, 180f);

        [Header("경로 라인 (드론 → target → 큐)")]
        public bool showPathLines = true;
        public Color pathLineColor = new Color(0.3f, 1f, 0.4f, 0.55f);
        public float pathLineThickness = 2.5f;

        [Header("디버그 HUD")]
        public bool showDebugHud = true;
        public Vector2 debugHudMargin = new Vector2(20f, 20f);

        [Header("스케일 바 (HUD 박스 바로 위, 좌하단 기준)")]
        public bool showScaleBar = true;
        [Tooltip("HUD 박스 위로 띄울 간격 (픽셀).")]
        public float scaleBarGapAboveHud = 18f;
        public float scaleBarTargetPixels = 220f;
        public Color scaleBarColor = new Color(1f, 1f, 1f, 0.9f);

        [Header("레이아웃 튜너 (런타임 슬라이더)")]
        [Tooltip("F9 로 IMGUI 슬라이더 창 토글. 모든 박스/폰트 크기를 라이브 조정.")]
        public bool showLayoutTuner = false;
        public UnityEngine.InputSystem.Key tunerToggleKey = UnityEngine.InputSystem.Key.F9;
        public Rect tunerWindow = new Rect(20f, 20f, 380f, 540f);

        StrategicViewBootstrap _bootstrap;
        StrategicCommandInput _input;

        class Marker
        {
            public UIShape shape;
            public TextMeshProUGUI label;
            public RectTransform rt;
            public UIShape labelBg;
            public RectTransform labelBgRt;
            public RectTransform labelRt;
        }

        Marker _meDot;
        Marker _originMarker;
        ProjectionUdpReceiver _udp;
        ProjectionReplay _replay;
        GpsTeleport _teleport;
        readonly Dictionary<string, Marker> _droneDots = new Dictionary<string, Marker>();
        readonly Dictionary<DetectionMarker, Marker> _detMarkers = new Dictionary<DetectionMarker, Marker>();
        readonly List<DetectionMarker> _toRemove = new List<DetectionMarker>();
        // 타겟/큐는 같은 위치 그룹핑 — 풀로 관리(매 프레임 reassign).
        readonly List<Marker> _targetPool = new List<Marker>();
        readonly List<Marker> _queuePool = new List<Marker>();
        int _targetUsed, _queueUsed;

        RectTransform _legendRoot;
        TextMeshProUGUI[] _legendCountTexts;
        UIShape _legendBgShape;
        RectTransform _debugHudRoot;
        UIShape _debugHudBg;

        readonly Dictionary<string, UIShape> _pathLines = new Dictionary<string, UIShape>();
        readonly List<Vector2> _polyBuf = new List<Vector2>();

        TextMeshProUGUI _debugHudText;
        UIShape _scaleBarShape;
        TextMeshProUGUI _scaleBarText;
        float _scaleFromBootstrap = 1f;
        bool _scaleResolved;

        void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb[tunerToggleKey].wasPressedThisFrame)
                showLayoutTuner = !showLayoutTuner;
        }

        void OnGUI()
        {
            if (!showLayoutTuner) return;
            tunerWindow = GUI.Window(GetInstanceID() ^ 0x4754, tunerWindow, DrawTunerWindow, "HUD Layout — F9");
        }

        void DrawTunerWindow(int id)
        {
            GUILayout.Label("Font", EditorStyle());
            labelFontSize = (int)SliderRow("Label Font Size", labelFontSize, 8, 40);

            GUILayout.Space(8);
            GUILayout.Label("Debug HUD (좌하단)", EditorStyle());
            debugHudMargin.x = SliderRow("Margin X", debugHudMargin.x, 0f, 200f);
            debugHudMargin.y = SliderRow("Margin Y", debugHudMargin.y, 0f, 200f);
            debugHudSize.x = SliderRow("Width", debugHudSize.x, 200f, 1500f);
            debugHudSize.y = SliderRow("Height", debugHudSize.y, 60f, 400f);

            GUILayout.Space(8);
            GUILayout.Label("Legend (우상단)", EditorStyle());
            legendMargin.x = SliderRow("Margin X", legendMargin.x, 0f, 200f);
            legendMargin.y = SliderRow("Margin Y", legendMargin.y, 0f, 200f);
            legendSize.x = SliderRow("Width", legendSize.x, 150f, 500f);
            legendSize.y = SliderRow("Height", legendSize.y, 100f, 500f);

            GUILayout.Space(8);
            GUILayout.Label("Scale Bar", EditorStyle());
            scaleBarGapAboveHud = SliderRow("Gap Above HUD", scaleBarGapAboveHud, 0f, 100f);
            scaleBarTargetPixels = SliderRow("Target Px", scaleBarTargetPixels, 80f, 500f);

            GUILayout.Space(8);
            GUILayout.Label("배경 색 알파", EditorStyle());
            labelBgColor.a = SliderRow("BG Alpha", labelBgColor.a, 0f, 1f);

            GUILayout.Space(8);
            showDebugHud = GUILayout.Toggle(showDebugHud, " Show Debug HUD");
            showLegend = GUILayout.Toggle(showLegend, " Show Legend");
            showScaleBar = GUILayout.Toggle(showScaleBar, " Show Scale Bar");
            showPathLines = GUILayout.Toggle(showPathLines, " Show Path Lines");
            showDetectionMarkers = GUILayout.Toggle(showDetectionMarkers, " Show Detection Markers");

            GUI.DragWindow();
        }

        GUIStyle _hStyle;
        GUIStyle EditorStyle()
        {
            if (_hStyle == null)
            {
                _hStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            }
            return _hStyle;
        }

        float SliderRow(string label, float val, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {val:F1}", GUILayout.Width(160));
            val = GUILayout.HorizontalSlider(val, min, max);
            GUILayout.EndHorizontal();
            return val;
        }

        void Awake()
        {
            _bootstrap = GetComponent<StrategicViewBootstrap>();
            _input = GetComponent<StrategicCommandInput>();
            if (meAnchor == null)
            {
                var go = GameObject.Find("Cube");
                if (go != null) meAnchor = go.transform;
            }
            // GPS origin = **센서 계측 카메라 위치** (UDP cam_lat/cam_lng/cam_alt → 월드).
            //   우선순위: GpsTeleport override > UDP 최신 pose > Replay 최신 pose.
            _udp = FindObjectOfType<ProjectionUdpReceiver>();
            _replay = FindObjectOfType<ProjectionReplay>();
            _teleport = FindObjectOfType<GpsTeleport>();
            Debug.Log($"[StrategicHud] GPS pose source: udp={(_udp != null)} replay={(_replay != null)} teleport={(_teleport != null)}");
        }

        float _lastPoseLogTime = -999f;

        /// <summary>현재 센서 GPS 환산 월드 위치. 우선순위:
        ///   1. GpsTeleport.overrideEnabled (Y→T 워크플로우) → 그 좌표로 GpsToWorld.
        ///   2. UDP 최신 pose (라이브 수신).
        ///   3. Replay 최신 pose (CSV 재생).
        /// 셋 다 없으면 false (마커 숨김).</summary>
        bool TryGetSensorWorldPose(out Vector3 worldPos)
        {
            bool log = Time.time - _lastPoseLogTime > 5f;
            if (log)
            {
                string ts = _teleport == null ? "no-teleport" : (_teleport.overrideEnabled ? "override-ON" : "override-OFF");
                Debug.Log($"[StrategicHud] state: teleport={ts}, udp={(_udp != null)}, replay={(_replay != null)}");
            }
            if (_teleport != null && _teleport.overrideEnabled)
            {
                if (_udp != null || _replay != null)
                {
                    worldPos = _udp != null
                        ? _udp.GpsToWorld((float)_teleport.latitude, (float)_teleport.longitude, (float)_teleport.altitude)
                        : _replay.GpsToWorld((float)_teleport.latitude, (float)_teleport.longitude, (float)_teleport.altitude);
                    // altitudeReferenceBuilding 가 미설정이면 worldY 가 cube 위로 폭주(=카메라 뒤).
                    // 마커 시각화 안정성을 위해 Y 를 cube 레벨로 강제 (XZ 는 GPS 환산 그대로).
                    if (meAnchor != null) worldPos.y = meAnchor.position.y;
                    if (log) { _lastPoseLogTime = Time.time; Debug.Log($"[StrategicHud] GPS via teleport-override → {worldPos} (lat={_teleport.latitude},lng={_teleport.longitude}, alt clamped to cube.y)"); }
                    return true;
                }
                if (log) { _lastPoseLogTime = Time.time; Debug.LogWarning("[StrategicHud] override ON 인데 udp/replay 없어 GpsToWorld 호출 불가."); }
            }
            Vector3 fwd, up;
            if (_udp != null && _udp.TryGetLatestPose(out worldPos, out fwd, out up))
            {
                if (log) { _lastPoseLogTime = Time.time; Debug.Log($"[StrategicHud] GPS via UDP latest pose → {worldPos}"); }
                return true;
            }
            if (_replay != null && _replay.TryGetLatestPose(out worldPos, out fwd, out up))
            {
                if (log) { _lastPoseLogTime = Time.time; Debug.Log($"[StrategicHud] GPS via Replay latest pose → {worldPos}"); }
                return true;
            }
            if (log) { _lastPoseLogTime = Time.time; Debug.Log("[StrategicHud] GPS pose 미가용 (UDP/Replay 미수신, override OFF)."); }
            worldPos = default;
            return false;
        }

        void LateUpdate()
        {
            if (_bootstrap == null) return;
            var cam = _bootstrap.StrategicCamera;
            var canvasRt = _bootstrap.CanvasRect;
            if (cam == null || canvasRt == null) return;

            // ── Me ──
            if (meAnchor != null)
            {
                if (_meDot == null) _meDot = MakeMarker(canvasRt, "Me", meColor, meMarkerRadius, "ME");
                PlaceMarker(_meDot, meAnchor.position, cam);
            }

            // ── GPS 원점 (센서 계측 카메라 위치) ──
            if (_originMarker == null)
                _originMarker = MakeMarker(canvasRt, "GpsOrigin", originColor, originMarkerRadius, "GPS");
            if (TryGetSensorWorldPose(out Vector3 sensorPos))
                PlaceMarker(_originMarker, sensorPos, cam);
            else
                _originMarker.shape.gameObject.SetActive(false);

            // ── 검출 마커 ──
            if (showDetectionMarkers) RenderDetectionMarkers(canvasRt, cam);
            else foreach (var kv in _detMarkers) kv.Value.shape.gameObject.SetActive(false);

            // ── 범례 ──
            if (showLegend) RenderLegend(canvasRt);
            else if (_legendRoot != null) _legendRoot.gameObject.SetActive(false);

            // ── 경로 라인 ──
            if (showPathLines) RenderPathLines(canvasRt, cam);
            else foreach (var kv in _pathLines) kv.Value.gameObject.SetActive(false);

            // ── 디버그 HUD ──
            if (showDebugHud) RenderDebugHud(canvasRt);
            else if (_debugHudText != null) _debugHudText.gameObject.SetActive(false);

            // ── 스케일 바 ──
            if (showScaleBar) RenderScaleBar(canvasRt, cam);
            else if (_scaleBarShape != null) _scaleBarShape.gameObject.SetActive(false);

            // ── 드론 마커 (per-drone, label = 번호) ──
            var seen = new HashSet<string>();
            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null || agent.Active == null) continue;
                seen.Add(agent.agentId);
                bool isSel = _input != null && _input.IsSelected(agent.agentId);
                if (!_droneDots.TryGetValue(agent.agentId, out var m))
                {
                    m = MakeMarker(canvasRt, $"Drone_{agent.agentId}",
                        isSel ? selectedColor : droneColor, droneMarkerRadius, DroneNumber(agent.agentId));
                    _droneDots[agent.agentId] = m;
                }
                m.shape.color = isSel ? selectedColor : droneColor;
                float aglM = 0f;
                GroundAgl.TryGetAgl(agent.Active.PositionUnity, agent.Active.UnityUnitsPerMeter, ~0, out aglM);
                string newText = $"{DroneNumber(agent.agentId)}\nAGL: {aglM:F1} m";
                if (m.label.text != newText) { m.label.text = newText; ResizeLabelBg(m, newText); }
                PlaceMarker(m, agent.Active.PositionUnity, cam);
            }
            foreach (var kv in _droneDots) if (!seen.Contains(kv.Key)) kv.Value.shape.gameObject.SetActive(false);

            // ── 타겟/큐 그룹핑 ──
            RenderGroupedWaypoints(cam, canvasRt);
        }

        /// <summary>타겟·큐 위치를 픽셀 bin 단위로 그룹핑 → "all" 또는 "0,1,2" 라벨.</summary>
        void RenderGroupedWaypoints(Camera cam, RectTransform canvasRt)
        {
            int totalDrones = 0;
            foreach (var a in DroneRegistry.All)
                if (a != null && a.Active != null) totalDrones++;

            // (bin) → (worldPos, droneNumbers).
            var targetGroups = new Dictionary<Vector2Int, (Vector3 pos, List<string> ids)>();
            var queueGroups = new Dictionary<Vector2Int, (Vector3 pos, List<string> ids)>();

            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null || agent.Active == null) continue;
                string num = DroneNumber(agent.agentId);
                if (agent.Waypoints.Current.HasValue)
                {
                    Vector3 wp = agent.Active.EnuToUnityWorld(agent.Waypoints.Current.Value);
                    AddToGroup(targetGroups, wp, cam, num);
                }
                foreach (var w in agent.Waypoints.Upcoming)
                {
                    Vector3 wp = agent.Active.EnuToUnityWorld(w);
                    AddToGroup(queueGroups, wp, cam, num);
                }
            }

            _targetUsed = 0;
            foreach (var kv in targetGroups)
                RenderGroup(kv.Value.pos, kv.Value.ids, totalDrones, canvasRt, cam,
                            _targetPool, ref _targetUsed, targetColor, targetMarkerRadius, "Target");
            for (int i = _targetUsed; i < _targetPool.Count; i++) _targetPool[i].shape.gameObject.SetActive(false);

            _queueUsed = 0;
            foreach (var kv in queueGroups)
                RenderGroup(kv.Value.pos, kv.Value.ids, totalDrones, canvasRt, cam,
                            _queuePool, ref _queueUsed, queueColor, queueMarkerRadius, "Queue");
            for (int i = _queueUsed; i < _queuePool.Count; i++) _queuePool[i].shape.gameObject.SetActive(false);
        }

        void AddToGroup(Dictionary<Vector2Int, (Vector3, List<string>)> groups,
                        Vector3 worldPos, Camera cam, string droneNum)
        {
            Vector3 sp = cam.WorldToScreenPoint(worldPos);
            if (sp.z < 0f) return;
            int bin = Mathf.Max(1, groupingBinPx);
            var key = new Vector2Int(Mathf.RoundToInt(sp.x / bin), Mathf.RoundToInt(sp.y / bin));
            if (!groups.TryGetValue(key, out var entry))
            {
                entry = (worldPos, new List<string>());
                groups[key] = entry;
            }
            if (!entry.Item2.Contains(droneNum)) entry.Item2.Add(droneNum);
            // worldPos 는 첫 등록값 유지 — 그룹 중심 근사로 충분.
            groups[key] = entry;
        }

        void RenderGroup(Vector3 worldPos, List<string> ids, int totalDrones,
                         RectTransform canvasRt, Camera cam,
                         List<Marker> pool, ref int used,
                         Color color, float radius, string namePrefix)
        {
            if (used >= pool.Count) pool.Add(MakeMarker(canvasRt, $"{namePrefix}_{used}", color, radius, ""));
            var m = pool[used++];
            m.shape.color = color;
            ids.Sort();
            string newText = (ids.Count >= totalDrones && totalDrones > 0) ? "all" : string.Join(",", ids);
            if (m.label.text != newText) { m.label.text = newText; ResizeLabelBg(m, newText); }
            PlaceMarker(m, worldPos, cam);
        }

        void RenderDetectionMarkers(RectTransform canvasRt, Camera cam)
        {
            var live = DetectionMarker.All;
            var liveSet = new HashSet<DetectionMarker>();
            for (int i = 0; i < live.Count; i++)
            {
                var det = live[i];
                if (det == null) continue;
                liveSet.Add(det);
                string ko = DetectionClasses.KoreanFor(det.className);
                if (!_detMarkers.TryGetValue(det, out var m))
                {
                    m = MakeMarker(canvasRt, $"Det_{det.GetInstanceID()}", det.color, detectionMarkerRadius, ko);
                    _detMarkers[det] = m;
                }
                m.shape.color = det.color;
                if (m.label.text != ko) { m.label.text = ko; ResizeLabelBg(m, ko); }
                PlaceMarker(m, det.transform.position, cam);
            }
            // 사라진 검출 마커 정리.
            _toRemove.Clear();
            foreach (var kv in _detMarkers)
                if (!liveSet.Contains(kv.Key)) _toRemove.Add(kv.Key);
            foreach (var k in _toRemove)
            {
                if (_detMarkers.TryGetValue(k, out var m) && m.shape != null) Destroy(m.shape.gameObject);
                _detMarkers.Remove(k);
            }
        }

        void RenderLegend(RectTransform canvasRt)
        {
            if (_legendRoot == null) BuildLegend(canvasRt);
            _legendRoot.gameObject.SetActive(true);
            // 매 프레임 사이즈/색 갱신 (튜너로 라이브 변경 반영).
            _legendRoot.anchoredPosition = new Vector2(-legendMargin.x, -legendMargin.y);
            _legendRoot.sizeDelta = legendSize;
            if (_legendBgShape != null)
            {
                _legendBgShape.color = labelBgColor;
                BuildRect(_legendBgShape, legendSize.x, legendSize.y);
            }
            // 클래스별 카운트 갱신.
            var live = DetectionMarker.All;
            for (int i = 0; i < DetectionClasses.All.Length; i++)
            {
                if (_legendCountTexts == null || i >= _legendCountTexts.Length) break;
                int count = 0;
                var entry = DetectionClasses.All[i];
                for (int j = 0; j < live.Count; j++)
                    if (live[j] != null && live[j].className == entry.name) count++;
                _legendCountTexts[i].text = $"{entry.ko}: {count}";
                _legendCountTexts[i].fontSize = labelFontSize;
                _legendCountTexts[i].color = labelColor;
            }
        }

        void BuildLegend(RectTransform canvasRt)
        {
            var rootGo = new GameObject("StrategicLegend");
            var rootRt = rootGo.AddComponent<RectTransform>();
            rootRt.SetParent(canvasRt, false);
            rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(1f, 1f); // top-right
            rootRt.anchoredPosition = new Vector2(-legendMargin.x, -legendMargin.y);
            rootRt.sizeDelta = legendSize;
            _legendRoot = rootRt;

            // 배경
            var bgGo = new GameObject("Bg");
            var bgRt = bgGo.AddComponent<RectTransform>();
            bgRt.SetParent(rootGo.transform, false);
            bgRt.anchorMin = new Vector2(0f, 0f);
            bgRt.anchorMax = new Vector2(1f, 1f);
            bgRt.pivot = new Vector2(0.5f, 0.5f);
            bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
            _legendBgShape = bgGo.AddComponent<UIShape>();
            _legendBgShape.raycastTarget = false;

            // 타이틀
            var titleGo = new GameObject("Title");
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.SetParent(rootGo.transform, false);
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -6f);
            titleRt.sizeDelta = new Vector2(0f, 18f);
            var titleT = titleGo.AddComponent<TextMeshProUGUI>();
            titleT.text = "범례";
            titleT.alignment = TextAlignmentOptions.Center;
            titleT.color = labelColor;
            titleT.fontSize = labelFontSize + 2;
            titleT.fontStyle = FontStyles.Bold;
            titleT.raycastTarget = false;
            var f = KoreanFont.Get();
            if (f != null) titleT.font = f;

            int n = DetectionClasses.All.Length;
            _legendCountTexts = new TextMeshProUGUI[n];
            float rowH = 24f;
            float startY = -28f;
            for (int i = 0; i < n; i++)
            {
                var e = DetectionClasses.All[i];
                // Color swatch (disk).
                var swGo = new GameObject($"Swatch_{e.name}");
                var swRt = swGo.AddComponent<RectTransform>();
                swRt.SetParent(rootGo.transform, false);
                swRt.anchorMin = swRt.anchorMax = new Vector2(0f, 1f);
                swRt.pivot = new Vector2(0f, 0.5f);
                swRt.anchoredPosition = new Vector2(14f, startY - i * rowH);
                swRt.sizeDelta = new Vector2(16f, 16f);
                var sw = swGo.AddComponent<UIShape>();
                sw.color = e.color;
                sw.raycastTarget = false;
                BuildDisk(sw, 8f, 16);

                // Label.
                var lblGo = new GameObject($"Row_{e.name}");
                var lblRt = lblGo.AddComponent<RectTransform>();
                lblRt.SetParent(rootGo.transform, false);
                lblRt.anchorMin = lblRt.anchorMax = new Vector2(0f, 1f);
                lblRt.pivot = new Vector2(0f, 0.5f);
                lblRt.anchoredPosition = new Vector2(34f, startY - i * rowH);
                lblRt.sizeDelta = new Vector2(legendSize.x - 50f, 20f);
                var t = lblGo.AddComponent<TextMeshProUGUI>();
                t.text = $"{e.ko}: 0";
                t.alignment = TextAlignmentOptions.MidlineLeft;
                t.color = labelColor;
                t.fontSize = labelFontSize;
                t.fontStyle = FontStyles.Bold;
                t.raycastTarget = false;
                if (f != null) t.font = f;
                _legendCountTexts[i] = t;
            }
        }

        // ── 경로 라인 ─────────────────────────────────────────────────────
        void RenderPathLines(RectTransform canvasRt, Camera cam)
        {
            var seen = new HashSet<string>();
            foreach (var agent in DroneRegistry.All)
            {
                if (agent == null || agent.Active == null) continue;
                seen.Add(agent.agentId);
                _polyBuf.Clear();

                Vector3 dronSp = cam.WorldToScreenPoint(agent.Active.PositionUnity);
                if (dronSp.z > 0f) _polyBuf.Add(new Vector2(dronSp.x, dronSp.y));
                if (agent.Waypoints.Current.HasValue)
                {
                    Vector3 wp = agent.Active.EnuToUnityWorld(agent.Waypoints.Current.Value);
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    if (sp.z > 0f) _polyBuf.Add(new Vector2(sp.x, sp.y));
                }
                foreach (var w in agent.Waypoints.Upcoming)
                {
                    Vector3 wp = agent.Active.EnuToUnityWorld(w);
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    if (sp.z > 0f) _polyBuf.Add(new Vector2(sp.x, sp.y));
                }

                if (!_pathLines.TryGetValue(agent.agentId, out var line))
                {
                    line = MakeAbsoluteShape(canvasRt, $"Path_{agent.agentId}");
                    line.transform.SetAsFirstSibling();   // 마커 뒤에 그려지도록
                    _pathLines[agent.agentId] = line;
                }
                if (_polyBuf.Count < 2)
                {
                    line.gameObject.SetActive(false);
                    continue;
                }
                line.gameObject.SetActive(true);
                line.color = pathLineColor;
                BuildPolyline(line, _polyBuf, pathLineThickness);
            }
            foreach (var kv in _pathLines) if (!seen.Contains(kv.Key)) kv.Value.gameObject.SetActive(false);
        }

        UIShape MakeAbsoluteShape(RectTransform parentRt, string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parentRt, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(1f, 1f);
            var s = go.AddComponent<UIShape>();
            s.raycastTarget = false;
            return s;
        }

        static void BuildPolyline(UIShape s, List<Vector2> pts, float thickness)
        {
            int n = pts.Count;
            if (n < 2) { s.SetMesh(new Vector2[0], new int[0]); return; }
            var verts = new Vector2[n * 2];
            var tris = new int[(n - 1) * 6];
            float half = thickness * 0.5f;
            for (int i = 0; i < n; i++)
            {
                Vector2 dir = Vector2.zero;
                if (i > 0) dir += (pts[i] - pts[i - 1]).normalized;
                if (i < n - 1) dir += (pts[i + 1] - pts[i]).normalized;
                if (dir.sqrMagnitude < 1e-6f) dir = new Vector2(1, 0);
                dir.Normalize();
                Vector2 nrm = new Vector2(-dir.y, dir.x) * half;
                verts[i * 2] = pts[i] - nrm;
                verts[i * 2 + 1] = pts[i] + nrm;
            }
            for (int i = 0; i < n - 1; i++)
            {
                tris[i * 6 + 0] = i * 2;
                tris[i * 6 + 1] = i * 2 + 2;
                tris[i * 6 + 2] = i * 2 + 1;
                tris[i * 6 + 3] = i * 2 + 1;
                tris[i * 6 + 4] = i * 2 + 2;
                tris[i * 6 + 5] = i * 2 + 3;
            }
            s.SetMesh(verts, tris);
        }

        // ── 디버그 HUD ────────────────────────────────────────────────────
        void RenderDebugHud(RectTransform canvasRt)
        {
            if (_debugHudText == null)
            {
                var go = new GameObject("DebugHud");
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(canvasRt, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);   // bottom-left
                _debugHudRoot = rt;
                // bg
                var bgGo = new GameObject("Bg");
                var bgRt = bgGo.AddComponent<RectTransform>();
                bgRt.SetParent(go.transform, false);
                bgRt.anchorMin = new Vector2(0f, 0f);
                bgRt.anchorMax = new Vector2(1f, 1f);
                bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
                _debugHudBg = bgGo.AddComponent<UIShape>();
                _debugHudBg.raycastTarget = false;
                // text
                var textGo = new GameObject("Text");
                var trt = textGo.AddComponent<RectTransform>();
                trt.SetParent(go.transform, false);
                trt.anchorMin = new Vector2(0f, 0f);
                trt.anchorMax = new Vector2(1f, 1f);
                trt.offsetMin = new Vector2(8f, 4f);
                trt.offsetMax = new Vector2(-8f, -4f);
                _debugHudText = textGo.AddComponent<TextMeshProUGUI>();
                _debugHudText.alignment = TextAlignmentOptions.TopLeft;
                _debugHudText.color = labelColor;
                _debugHudText.raycastTarget = false;
                var f = KoreanFont.Get();
                if (f != null) _debugHudText.font = f;
            }
            _debugHudText.gameObject.SetActive(true);
            // 매 프레임 사이즈/색 갱신 (튜너로 라이브 변경 반영).
            _debugHudRoot.anchoredPosition = debugHudMargin;
            _debugHudRoot.sizeDelta = debugHudSize;
            _debugHudBg.color = labelBgColor;
            BuildRect(_debugHudBg, debugHudSize.x, debugHudSize.y);
            _debugHudText.fontSize = labelFontSize;
            _debugHudText.color = labelColor;

            int total = DroneRegistry.Count;
            string sel = _input == null || _input.selectedDroneIds.Count == 0
                ? "ALL"
                : string.Join(",", _input.selectedDroneIds);

            // 첫 선택 드론 상태(없으면 첫 등록 드론).
            DroneAgent show = _input != null && _input.selectedDroneId != null
                ? DroneRegistry.Get(_input.selectedDroneId) : null;
            if (show == null && DroneRegistry.Count > 0) show = DroneRegistry.All[0];

            string status;
            if (show != null && show.Active != null)
            {
                var hf = show.Active;
                var p = hf.PositionEnu;
                var v = hf.VelocityEnu;
                int pending = show.Waypoints.Pending;
                float vMag = (float)System.Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                float agl = 0f;
                GroundAgl.TryGetAgl(hf.PositionUnity, hf.UnityUnitsPerMeter, ~0, out agl);
                status = string.Format(
                    "<b>{0}</b>\n" +
                    "p = ({1,8:F1}, {2,8:F1}, {3,8:F1}) m\n" +
                    "|v| = {4,6:F2} m/s   AGL = {5,7:F1} m   wp 잔여 = {6,3}",
                    show.agentId, p.X, p.Y, p.Z, vMag, agl, pending);
            }
            else status = "(no drone)";

            string altLine = _input != null
                ? string.Format("   목표 AGL: {0,4:F0} m  (PgUp/PgDn/End, 1=10 2=30 3=50 4=100 5=200)", _input.commandAltitudeAGLMeters)
                : "";
            _debugHudText.text = $"드론 {total,2}대   선택: {sel}{altLine}\n{status}";
        }

        // ── 스케일 바 ────────────────────────────────────────────────────
        void RenderScaleBar(RectTransform canvasRt, Camera cam)
        {
            if (!_scaleResolved)
            {
                _scaleFromBootstrap = FrameConversion.ResolveUnityUnitsPerMeter();
                if (_scaleFromBootstrap <= 0f) _scaleFromBootstrap = 1f;
                _scaleResolved = true;
            }
            if (_scaleBarShape == null)
            {
                _scaleBarShape = MakeAbsoluteShape(canvasRt, "ScaleBar");
                var go2 = new GameObject("ScaleBarText");
                var rt = go2.AddComponent<RectTransform>();
                rt.SetParent(canvasRt, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
                rt.sizeDelta = new Vector2(220f, 18f);
                _scaleBarText = go2.AddComponent<TextMeshProUGUI>();
                _scaleBarText.alignment = TextAlignmentOptions.Center;
                _scaleBarText.color = scaleBarColor;
                _scaleBarText.fontSize = labelFontSize - 1;
                _scaleBarText.raycastTarget = false;
                var f = KoreanFont.Get();
                if (f != null) _scaleBarText.font = f;
            }
            _scaleBarShape.gameObject.SetActive(true);
            _scaleBarText.gameObject.SetActive(true);

            // 보이는 영역의 실제 미터 길이 = orthoSize * 2 / scale.
            // 1 px = (orthoSize * 2 / cam.pixelHeight) / scale 미터.
            float metersPerPixel = (cam.orthographicSize * 2f / Mathf.Max(1, cam.pixelHeight)) / _scaleFromBootstrap;
            float targetMeters = metersPerPixel * scaleBarTargetPixels;
            float niceMeters = NiceRound(targetMeters);
            float actualPx = niceMeters / metersPerPixel;

            // 위치: HUD 박스 바로 위 (debugHudMargin.y + debugHudSize.y + gap).
            float x0 = debugHudMargin.x;
            float y0 = debugHudMargin.y + debugHudSize.y + scaleBarGapAboveHud;
            var verts = new Vector2[4] {
                new Vector2(x0,             y0 - 2f),
                new Vector2(x0 + actualPx,  y0 - 2f),
                new Vector2(x0 + actualPx,  y0 + 2f),
                new Vector2(x0,             y0 + 2f),
            };
            var tris = new int[6] { 0, 2, 1, 0, 3, 2 };
            _scaleBarShape.SetMesh(verts, tris);
            _scaleBarShape.color = scaleBarColor;

            string lbl = niceMeters >= 1000f ? $"{niceMeters / 1000f:F1} km" : $"{niceMeters:F0} m";
            ((RectTransform)_scaleBarText.transform).anchoredPosition =
                new Vector2(x0 + actualPx * 0.5f - 110f, y0 + 6f);
            _scaleBarText.text = lbl;
        }

        static float NiceRound(float meters)
        {
            // 1, 2, 5 * 10^k 형태로 반올림.
            if (meters <= 0f) return 1f;
            float exp = Mathf.Floor(Mathf.Log10(meters));
            float pow = Mathf.Pow(10f, exp);
            float mantissa = meters / pow;
            float nice = mantissa < 1.5f ? 1f : mantissa < 3.5f ? 2f : mantissa < 7.5f ? 5f : 10f;
            return nice * pow;
        }

        static void PlaceMarker(Marker m, Vector3 worldPos, Camera cam)
        {
            Vector3 sp = cam.WorldToScreenPoint(worldPos);
            if (sp.z < 0f || sp.x < 0f || sp.x > cam.pixelWidth || sp.y < 0f || sp.y > cam.pixelHeight)
            {
                m.shape.gameObject.SetActive(false);
                return;
            }
            m.shape.gameObject.SetActive(true);
            m.rt.anchoredPosition = new Vector2(sp.x, sp.y);
        }

        Marker MakeMarker(RectTransform parentRt, string name, Color color, float radius, string labelText)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parentRt, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(radius * 2f, radius * 2f);
            var s = go.AddComponent<UIShape>();
            s.color = color;
            s.raycastTarget = false;
            BuildDisk(s, radius, 20);

            // 라벨 배경(검은 박스). pivot bottom-center, 마커 위쪽에 배치.
            Vector2 labelAnchor = new Vector2(0f, radius + 2f);
            var bgGo = new GameObject("LabelBg");
            var bgRt = bgGo.AddComponent<RectTransform>();
            bgRt.SetParent(go.transform, false);
            bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0.5f);
            bgRt.pivot = new Vector2(0.5f, 0f);
            bgRt.anchoredPosition = labelAnchor;
            bgRt.sizeDelta = new Vector2(24f, labelFontSize + labelBgPaddingY * 2f);
            var bg = bgGo.AddComponent<UIShape>();
            bg.color = labelBgColor;
            bg.raycastTarget = false;
            BuildRect(bg, bgRt.sizeDelta.x, bgRt.sizeDelta.y);

            // 라벨 텍스트 (sibling AFTER bg 라 위에 그려짐).
            var labelGo = new GameObject("Label");
            var lrt = labelGo.AddComponent<RectTransform>();
            lrt.SetParent(go.transform, false);
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.anchoredPosition = labelAnchor;
            lrt.sizeDelta = new Vector2(120f, labelFontSize + labelBgPaddingY * 2f);
            var t = labelGo.AddComponent<TextMeshProUGUI>();
            t.text = labelText;
            t.alignment = TextAlignmentOptions.Center;
            t.color = labelColor;
            t.fontSize = labelFontSize;
            t.fontStyle = FontStyles.Bold;
            t.raycastTarget = false;
            var f = KoreanFont.Get();
            if (f != null) t.font = f;

            var m = new Marker { shape = s, label = t, rt = rt, labelBg = bg, labelBgRt = bgRt, labelRt = lrt };
            ResizeLabelBg(m, labelText);
            return m;
        }

        /// <summary>label.text 의 preferred size 기준으로 bg + label rect 둘 다 갱신.</summary>
        void ResizeLabelBg(Marker m, string text)
        {
            if (m.labelBg == null || m.label == null) return;
            Vector2 pref = m.label.GetPreferredValues(text);
            float w = Mathf.Max(12f, pref.x + labelBgPaddingX * 2f);
            float h = Mathf.Max(labelFontSize + labelBgPaddingY * 2f, pref.y + labelBgPaddingY * 2f);
            m.labelBgRt.sizeDelta = new Vector2(w, h);
            if (m.labelRt != null) m.labelRt.sizeDelta = new Vector2(w, h);
            BuildRect(m.labelBg, w, h);
        }

        static string DroneNumber(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return "?";
            int idx = agentId.LastIndexOf('_');
            return idx >= 0 ? agentId.Substring(idx + 1) : agentId;
        }

        static void BuildRect(UIShape s, float w, float h)
        {
            var verts = new Vector2[4] {
                new Vector2(-w*0.5f, 0f),
                new Vector2( w*0.5f, 0f),
                new Vector2( w*0.5f, h),
                new Vector2(-w*0.5f, h),
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
    }
}
