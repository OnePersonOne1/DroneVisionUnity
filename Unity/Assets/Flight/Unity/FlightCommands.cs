using System.Collections.Generic;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// **단일 명령 진입점**. 미니맵·전략뷰·외부 스크립트는 오직 이 정적 API 만 호출.
    /// 좌표는 모두 **ENU(SI 미터)** — UI 측에서 화면→Unity world→ENU 변환을 끝낸 뒤 넘긴다.
    /// 모든 메서드는 droneId 로 <see cref="DroneRegistry"/> 에서 agent 찾고 위임.
    /// 못 찾으면 false 반환 (조용한 실패 — 호출자 책임으로 로그).
    /// </summary>
    public static class FlightCommands
    {
        /// <summary>큐 비우고 단일 목표 (좌클릭/우클릭).</summary>
        public static bool SetWaypoint(string droneId, in N.Vector3 wEnu)
        {
            var a = DroneRegistry.Get(droneId); if (a == null) return false;
            a.SetWaypoint(wEnu); return true;
        }

        /// <summary>현재 경로 뒤에 추가 (Shift+우클릭).</summary>
        public static bool QueueWaypoint(string droneId, in N.Vector3 wEnu)
        {
            var a = DroneRegistry.Get(droneId); if (a == null) return false;
            a.QueueWaypoint(wEnu); return true;
        }

        /// <summary>경로 일괄(Ctrl+드래그 결과).</summary>
        public static bool SetPath(string droneId, IList<N.Vector3> pathEnu)
        {
            var a = DroneRegistry.Get(droneId); if (a == null) return false;
            a.SetPath(pathEnu); return true;
        }

        /// <summary>큐 비우고 현재 자리에서 hover.</summary>
        public static bool StopAndHover(string droneId)
        {
            var a = DroneRegistry.Get(droneId); if (a == null) return false;
            a.StopAndHover(); return true;
        }
    }
}
