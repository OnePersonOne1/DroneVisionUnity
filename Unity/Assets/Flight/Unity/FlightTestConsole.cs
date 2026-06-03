using UnityEngine;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// Phase 4 검증용 인게임 콘솔. Play 중 화면 좌상단 IMGUI 패널에 좌표 입력 + 버튼.
    /// ContextMenu 로도 동일 명령 호출 가능 (인스펙터 우클릭).
    /// FlightCommands 단일 진입점만 호출.
    /// </summary>
    [DisallowMultipleComponent]
    public class FlightTestConsole : MonoBehaviour
    {
        [Header("대상")]
        public string droneId = "drone_sim_obj_1";

        [Header("입력 좌표 (ENU m, spawn 점 기준)")]
        public Vector3 target = new Vector3(10f, 0f, 5f);

        [Header("사각형 경로 (Rectangle preset)")]
        public Vector3 rectSize = new Vector3(10f, 10f, 5f);   // E, N, Up

        [Header("GUI")]
        public bool showOnGui = true;
        public Rect windowRect = new Rect(10, 10, 320, 230);

        void OnGUI()
        {
            if (!showOnGui) return;
            windowRect = GUI.Window(GetInstanceID(), windowRect, DrawWindow, "Flight Console");
        }

        void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label($"Drone ID: {droneId}");

            GUILayout.BeginHorizontal();
            GUILayout.Label("East:", GUILayout.Width(50));
            target.x = ParseFloat(GUILayout.TextField(target.x.ToString("F2"), GUILayout.Width(70)), target.x);
            GUILayout.Label("North:", GUILayout.Width(50));
            target.y = ParseFloat(GUILayout.TextField(target.y.ToString("F2"), GUILayout.Width(70)), target.y);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Up:", GUILayout.Width(50));
            target.z = ParseFloat(GUILayout.TextField(target.z.ToString("F2"), GUILayout.Width(70)), target.z);
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            if (GUILayout.Button("Set Waypoint  (clear queue + 단일)")) CmdSetWaypoint();
            if (GUILayout.Button("Queue Waypoint  (큐 뒤에 추가)")) CmdQueueWaypoint();
            if (GUILayout.Button($"Rectangle Path  ({rectSize.x}×{rectSize.y} @ z={rectSize.z})")) CmdRectanglePath();
            if (GUILayout.Button("Stop & Hover")) CmdStopAndHover();

            // 현재 큐 상태.
            var agent = DroneRegistry.Get(droneId);
            if (agent != null)
            {
                GUILayout.Space(5);
                GUILayout.Label($"queue pending: {agent.Waypoints.Pending}");
                if (agent.Waypoints.Current.HasValue)
                {
                    var c = agent.Waypoints.Current.Value;
                    GUILayout.Label($"current → ({c.X:F1}, {c.Y:F1}, {c.Z:F1})");
                }
            }
            else
            {
                GUILayout.Label("drone not found");
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        static float ParseFloat(string s, float fallback) => float.TryParse(s, out float v) ? v : fallback;

        // ── 명령 (ContextMenu 로도 인스펙터 우클릭 직접 호출 가능) ──

        [ContextMenu("Set Waypoint")]
        public void CmdSetWaypoint()
        {
            bool ok = FlightCommands.SetWaypoint(droneId, new N.Vector3(target.x, target.y, target.z));
            Debug.Log($"[FlightConsole] SetWaypoint({target}) → {ok}");
        }

        [ContextMenu("Queue Waypoint")]
        public void CmdQueueWaypoint()
        {
            bool ok = FlightCommands.QueueWaypoint(droneId, new N.Vector3(target.x, target.y, target.z));
            Debug.Log($"[FlightConsole] QueueWaypoint({target}) → {ok}");
        }

        [ContextMenu("Rectangle Path")]
        public void CmdRectanglePath()
        {
            var path = new System.Collections.Generic.List<N.Vector3> {
                new N.Vector3(rectSize.x, 0f,         rectSize.z),
                new N.Vector3(rectSize.x, rectSize.y, rectSize.z),
                new N.Vector3(0f,         rectSize.y, rectSize.z),
                new N.Vector3(0f,         0f,         rectSize.z),
            };
            bool ok = FlightCommands.SetPath(droneId, path);
            Debug.Log($"[FlightConsole] SetPath rect {rectSize} → {ok}");
        }

        [ContextMenu("Stop & Hover")]
        public void CmdStopAndHover()
        {
            bool ok = FlightCommands.StopAndHover(droneId);
            Debug.Log($"[FlightConsole] StopAndHover → {ok}");
        }
    }
}
