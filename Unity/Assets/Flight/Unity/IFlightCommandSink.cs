using System.Collections.Generic;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// **선택적** 명령 수신 인터페이스. IFlightModel 이 이걸 구현하면, 그 모델은 waypoint 큐를
    /// 스스로 소비하지 않고 외부(예: PX4 SITL 브리지)에 명령을 위임한다.
    ///
    /// 라우팅 규약 (<see cref="DroneAgent"/>):
    ///   - HF/Arcade: 이 인터페이스 미구현 → DroneAgent.Waypoints 큐로 비행 (기존 경로 그대로).
    ///   - Mavlink(SITL): 이 인터페이스 구현 → DroneAgent 가 큐에도 기록(시각화)하고 sink 로도 전달,
    ///     실제 비행은 sink(브리지→PX4)가 수행.
    ///
    /// 좌표는 모두 ENU(SI m, 드론 기준) — FlightCommands 와 동일. 시그니처는 FlightCommands 와 1:1.
    /// </summary>
    public interface IFlightCommandSink
    {
        void SetWaypoint(in N.Vector3 wEnu);
        void QueueWaypoint(in N.Vector3 wEnu);
        void SetPath(IList<N.Vector3> pathEnu);
        void StopAndHover();
    }
}
