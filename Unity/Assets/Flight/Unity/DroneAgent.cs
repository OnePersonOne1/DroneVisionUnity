using System.Collections.Generic;
using UnityEngine;
using DroneSim.Flight.Core;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// 단일 드론 인스턴스의 게이트웨이. ID + 활성 IFlightModel + waypoint 큐 + 명령 진입점.
    /// UI(미니맵·전략뷰) 는 <see cref="FlightCommands"/> 를 통해서만 호출.
    /// </summary>
    [DisallowMultipleComponent]
    public class DroneAgent : MonoBehaviour
    {
        [Header("Identity")]
        public string agentId = "drone_sim_obj_0";

        [Header("Models (한 게임오브젝트에 둘 다 부착, 활성 모델만 IsActive=true)")]
        public HighFidelityFlightModel highFidelity;
        public ArcadeFlightModel arcade;

        public enum Mode { HighFidelity, Arcade }
        [SerializeField] Mode _mode = Mode.HighFidelity;

        /// <summary>ENU(SI m) 단위 waypoint 큐. 명령 API 만 수정.</summary>
        public readonly WaypointQueue Waypoints = new WaypointQueue();

        public IFlightModel Active => _mode == Mode.HighFidelity ? (IFlightModel)highFidelity : arcade;
        public Mode CurrentMode => _mode;

        void Awake()
        {
            if (highFidelity == null) highFidelity = GetComponent<HighFidelityFlightModel>();
            if (arcade == null) arcade = GetComponent<ArcadeFlightModel>();
            ApplyMode();
            DroneRegistry.Register(this);
        }

        void OnDestroy()
        {
            DroneRegistry.Unregister(this);
        }

        public void SetMode(Mode m)
        {
            if (_mode == m) return;
            // 모드 전환 시 현 상태 유지 위해 새 모델로 복사.
            var prev = Active;
            _mode = m;
            ApplyMode();
            if (prev != null && Active != null)
            {
                var pEnu = prev.PositionEnu;
                var qEnu = FrameConversion.UnityToEnu(prev.RotationUnity);
                Active.Teleport(pEnu, qEnu);
            }
        }

        public void ToggleMode() => SetMode(_mode == Mode.HighFidelity ? Mode.Arcade : Mode.HighFidelity);

        void ApplyMode()
        {
            if (highFidelity != null) highFidelity.IsActive = _mode == Mode.HighFidelity;
            if (arcade != null) arcade.IsActive = _mode == Mode.Arcade;
        }

        // ── 명령 인터페이스 (FlightCommands 가 호출, 외부에서 직접 호출 비권장) ─

        /// <summary>큐 클리어 + 단일 목표 지정.</summary>
        public void SetWaypoint(in N.Vector3 wEnu) => Waypoints.SetSingle(wEnu);

        /// <summary>현재 경로 뒤에 추가 (Shift+우클릭 등).</summary>
        public void QueueWaypoint(in N.Vector3 wEnu) => Waypoints.Append(wEnu);

        /// <summary>경로 일괄 지정 (드래그 결과).</summary>
        public void SetPath(IList<N.Vector3> path) => Waypoints.SetPath(path);

        /// <summary>큐 비우고 현재 위치에서 hover.</summary>
        public void StopAndHover()
        {
            if (Active == null) { Waypoints.Clear(); return; }
            Waypoints.StopAndHold(Active.PositionEnu);
        }
    }
}
