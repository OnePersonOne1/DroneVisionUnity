using UnityEngine;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>비행 모드 전환용 추상화. Arcade(단순 운동학) 와 HighFidelity(EOM+제어기) 가 구현.
    /// 한 DroneAgent 가 한 시점에 하나의 모델만 보유 — 같은 ID 의 드론을 두 모델로 동시 구동 금지.</summary>
    public interface IFlightModel
    {
        /// <summary>코어의 자체 진행 — 호출 측이 dt 를 결정(0 이면 모델이 자체 누산).</summary>
        void Step(float dt);

        /// <summary>월드 좌표 (Unity) — 시각화/카메라/HUD 가 읽는다. 단일 진실 공급원.</summary>
        Vector3 PositionUnity { get; }
        Quaternion RotationUnity { get; }

        /// <summary>코어 좌표 (ENU) — 제어기/명령 API 가 읽는다.</summary>
        N.Vector3 PositionEnu { get; }
        N.Vector3 VelocityEnu { get; }

        // ── 좌표 변환 (어댑터 경계) — C2 UI 가 화면 클릭점 ↔ ENU 변환에 쓴다 ──────
        // HF/Arcade 는 spawn 원점 기준, Mavlink(SITL) 은 GPS 홈 기준으로 구현.
        // UI 가 agent.Active(IFlightModel) 만 보고 동작하도록 인터페이스로 승격.

        /// <summary>1 real m → Unity 유닛 배율.</summary>
        float UnityUnitsPerMeter { get; }

        /// <summary>임의의 Unity world 위치 → 이 드론 기준 ENU(m).</summary>
        N.Vector3 UnityWorldToEnu(in Vector3 worldUnity);

        /// <summary>이 드론 기준 ENU(m) → Unity world 위치 (시각화용).</summary>
        Vector3 EnuToUnityWorld(in N.Vector3 posEnu);

        /// <summary>Phase 2 검증용 직접 입력 — Phase 3 부터 제어기가 호출, 외부 호출 비권장.</summary>
        void SetDirectControl(float thrustNewton, in N.Vector3 torqueBody);

        /// <summary>현재 상태를 깨끗한 초기값으로 (재배치 등).</summary>
        void Teleport(in N.Vector3 positionEnu, in N.Quaternion attitudeEnu);

        /// <summary>활성/비활성 토글 (모드 스위치 시).</summary>
        bool IsActive { get; set; }
    }
}
