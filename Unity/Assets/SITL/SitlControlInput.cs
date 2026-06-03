using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;
using DroneSim.C2.StrategicView;

namespace DroneSim.SITL
{
    /// <summary>
    /// SITL 드론 전용 직접 제어 키 (arm/takeoff/land/RTL). waypoint 류 RTS 명령은 기존
    /// 미니맵/전략뷰 경로(FlightCommands)가 그대로 처리하므로 여기선 다루지 않는다.
    ///
    /// 대상: StrategicCommandInput.selectedDroneIds 선택분 중 SITL 드론. 선택이 비어 있으면
    /// 등록된 모든 SITL 드론. (시뮬 드론은 영향 없음 — Active 가 MavlinkFlightModel 인 것만.)
    /// </summary>
    [DisallowMultipleComponent]
    public class SitlControlInput : MonoBehaviour
    {
        [Header("키 (조작법 기존 키와 충돌 회피)")]
        public Key armKey = Key.U;
        public Key takeoffKey = Key.K;
        public Key landKey = Key.L;
        public Key rtlKey = Key.G;

        [Header("Takeoff 고도 (real m, AGL)")]
        public float takeoffAltMeters = 5f;

        [Header("Refs (비우면 자동)")]
        public StrategicCommandInput selectionSrc;

        void Awake()
        {
            if (selectionSrc == null) selectionSrc = FindObjectOfType<StrategicCommandInput>();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb[armKey].wasPressedThisFrame) ForEachSitl(m => m.Arm(), "ARM");
            if (kb[takeoffKey].wasPressedThisFrame) ForEachSitl(m => m.Takeoff(takeoffAltMeters), $"TAKEOFF({takeoffAltMeters}m)");
            if (kb[landKey].wasPressedThisFrame) ForEachSitl(m => m.Land(), "LAND");
            if (kb[rtlKey].wasPressedThisFrame) ForEachSitl(m => m.ReturnToLaunch(), "RTL");
        }

        void ForEachSitl(System.Action<MavlinkFlightModel> action, string label)
        {
            int n = 0;
            foreach (var agent in EffectiveAgents())
            {
                if (agent != null && agent.Active is MavlinkFlightModel mav)
                {
                    action(mav); n++;
                }
            }
            if (n > 0) Debug.Log($"[SitlControl] {label} → {n} SITL drone(s)");
        }

        IEnumerable<DroneAgent> EffectiveAgents()
        {
            // 선택이 있으면 선택분, 없으면 전체 — 어느 쪽이든 SITL 만 ForEachSitl 에서 필터.
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
    }
}
