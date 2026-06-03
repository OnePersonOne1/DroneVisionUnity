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
        // 비워두면 Awake 의 DroneRegistry.Register 가 NextDefaultId() 로 자동 부여 ("drone_sim_obj_1", 2, ...).
        // 디폴트를 비워야 매 Spawn 마다 Register 가 중복 충돌해서 _nextSerial 이 건너뛰는 현상이 안 생긴다.
        public string agentId = "";

        [Header("Models (한 게임오브젝트에 둘 다 부착, 활성 모델만 IsActive=true)")]
        public HighFidelityFlightModel highFidelity;
        public ArcadeFlightModel arcade;

        // External(SITL/Mavlink) 모델 — 런타임 부착(AttachExternalModel), 직렬화 안 함.
        // Assembly-CSharp 의 MavlinkFlightModel 이 IFlightModel 로 들어온다(타입 강참조 회피).
        IFlightModel _external;

        public enum Mode { HighFidelity, Arcade, External }
        [SerializeField] Mode _mode = Mode.HighFidelity;

        /// <summary>ENU(SI m) 단위 waypoint 큐. 명령 API 만 수정.</summary>
        public readonly WaypointQueue Waypoints = new WaypointQueue();

        public IFlightModel Active => _mode switch
        {
            Mode.HighFidelity => (IFlightModel)highFidelity,
            Mode.Arcade => arcade,
            Mode.External => _external,
            _ => null,
        };
        public Mode CurrentMode => _mode;

        /// <summary>외부 비행 모델(SITL 등)을 런타임 부착하고 External 모드로 전환.
        /// 전용 스포너(SitlDroneSpawner)가 호출. HF/Arcade 가 없는 GameObject 라도 동작.</summary>
        public void AttachExternalModel(IFlightModel model)
        {
            _external = model;
            _mode = Mode.External;
            ApplyMode();
        }

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
            // External(SITL) 은 토글 대상이 아니다 — 자체 텔레메트리로 구동되며 상태 복사 불가.
            if (_mode == Mode.External || m == Mode.External) return;
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
            if (_external != null) _external.IsActive = _mode == Mode.External;
        }

        // ── 명령 인터페이스 (FlightCommands 가 호출, 외부에서 직접 호출 비권장) ─
        //
        // 라우팅 규약: 항상 Waypoints 큐에 기록(HF 비행 + 미니맵/전략뷰 시각화의 단일 소스)하고,
        // Active 모델이 IFlightCommandSink 면 sink 로도 전달(SITL→브리지→PX4). HF/Arcade 는
        // sink 미구현이라 큐만 쓰는 기존 경로 그대로 — 시그니처/동작 불변.

        /// <summary>큐 클리어 + 단일 목표 지정.</summary>
        public void SetWaypoint(in N.Vector3 wEnu)
        {
            Waypoints.SetSingle(wEnu);
            (Active as IFlightCommandSink)?.SetWaypoint(wEnu);
        }

        /// <summary>현재 경로 뒤에 추가 (Shift+우클릭 등).</summary>
        public void QueueWaypoint(in N.Vector3 wEnu)
        {
            Waypoints.Append(wEnu);
            (Active as IFlightCommandSink)?.QueueWaypoint(wEnu);
        }

        /// <summary>경로 일괄 지정 (드래그 결과).</summary>
        public void SetPath(IList<N.Vector3> path)
        {
            Waypoints.SetPath(path);
            (Active as IFlightCommandSink)?.SetPath(path);
        }

        /// <summary>큐 비우고 현재 위치에서 hover.</summary>
        public void StopAndHover()
        {
            if (Active == null) { Waypoints.Clear(); return; }
            Waypoints.StopAndHold(Active.PositionEnu);
            (Active as IFlightCommandSink)?.StopAndHover();
        }
    }
}
