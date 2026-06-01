using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// 기체·모터·항력 물리 파라미터(SI 단위). UnityEngine 무의존.
    /// 디폴트 값은 ~500g 급 quad (DJI Tello~F450 중간) 기준 예시 — Unity 어댑터에서
    /// ScriptableObject 로 덮어쓴다.
    /// </summary>
    public sealed class DroneParams
    {
        // ── 기체 ─────────────────────────────────────────────────────────────
        /// <summary>질량 m [kg].</summary>
        public float Mass = 1.0f;

        /// <summary>관성텐서 J 의 대각성분 (Jxx, Jyy, Jzz) [kg·m²].
        /// 비대각은 0 가정 (대칭 X-config).</summary>
        public Vector3 InertiaDiag = new Vector3(0.012f, 0.012f, 0.022f);

        /// <summary>모터 ↔ 무게중심 거리 [m]. X-config 의 대각선 절반.</summary>
        public float Arm = 0.18f;

        // ── 모터 (각 모터: f_i = Kf·Ω_i², τ_yaw_i = ±Km·Ω_i²) ─────────────
        /// <summary>추력 계수 [N·s²/rad²].</summary>
        public float Kf = 1.2e-5f;

        /// <summary>반토크 계수 [N·m·s²/rad²].</summary>
        public float Km = 2.0e-7f;

        /// <summary>모터 최대 각속도 Ω_max [rad/s]. 포화 한계.</summary>
        public float MaxRotorRadPerSec = 1500f;

        // ── 항력 ─────────────────────────────────────────────────────────────
        /// <summary>선형 항력 계수 대각 D [N·s/m]. F_drag = -D·v (inertial).
        /// 0 으로 두면 무항력. 안정 hover 디버그 시 작게(예: 0.05~0.2) 두면 도움.</summary>
        public Vector3 LinearDrag = new Vector3(0.10f, 0.10f, 0.20f);

        // ── 환경 ─────────────────────────────────────────────────────────────
        /// <summary>중력 가속도 [m/s²]. 표준값 9.81.</summary>
        public float Gravity = Frames.GravityMagnitude;

        /// <summary>전체 모터 최대 추력 합 [N]. = 4·Kf·Ω_max².</summary>
        public float MaxTotalThrust => 4f * Kf * MaxRotorRadPerSec * MaxRotorRadPerSec;

        /// <summary>호버 시 필요한 모터당 추력 [N].</summary>
        public float HoverThrustPerMotor => Mass * Gravity / 4f;

        /// <summary>호버 시 모터당 각속도 [rad/s]. = √(m·g / (4·Kf)).</summary>
        public float HoverRotorRadPerSec => (float)System.Math.Sqrt(HoverThrustPerMotor / Kf);
    }
}
