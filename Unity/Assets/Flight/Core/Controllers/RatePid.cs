using System.Numerics;

namespace DroneSim.Flight.Core.Controllers
{
    /// <summary>
    /// Inner-loop **rate controller** (body frame angular velocity → body torque).
    ///
    /// 입력:  ω_des (body, rad/s), ω_curr (body, rad/s).
    /// 오차:  e = ω_des − ω_curr.
    /// 출력:  τ_body = Kp·e + Ki·∫e dt + Kd·d(e)/dt   (각 축 독립 PID).
    ///
    /// 각 축(roll/pitch/yaw) 별로 PID 게인을 따로 둔다. yaw 축은 일반적으로 다른 게인 필요.
    /// I-term anti-windup: 적분값을 |iLimit| 로 saturate.
    /// 적분/미분은 입력 dt 로 계산(외부에서 고정 timestep 주입).
    /// </summary>
    public sealed class RatePid
    {
        // ── 게인 (축별 대각: x=roll, y=pitch, z=yaw) ────────────────────────
        public Vector3 Kp = new Vector3(0.10f, 0.10f, 0.08f);
        public Vector3 Ki = new Vector3(0.02f, 0.02f, 0.02f);
        public Vector3 Kd = new Vector3(0.003f, 0.003f, 0.002f);

        /// <summary>I-term 포화 한계(N·m). |∫e dt · Ki| 가 이 이상 못 됨.</summary>
        public Vector3 IntegralLimit = new Vector3(0.5f, 0.5f, 0.5f);

        Vector3 _integral;
        Vector3 _prevError;
        bool _hasPrev;

        public void Reset()
        {
            _integral = Vector3.Zero;
            _prevError = Vector3.Zero;
            _hasPrev = false;
        }

        public Vector3 Step(in Vector3 omegaDesired, in Vector3 omegaCurrent, float dt)
        {
            if (dt <= 0f) return Vector3.Zero;
            Vector3 e = omegaDesired - omegaCurrent;

            // Integral (anti-windup 은 Ki·∫ 를 IntegralLimit 으로 clamp).
            _integral += e * dt;
            _integral = new Vector3(
                Clamp(_integral.X, -IntegralLimit.X / NonZero(Ki.X), IntegralLimit.X / NonZero(Ki.X)),
                Clamp(_integral.Y, -IntegralLimit.Y / NonZero(Ki.Y), IntegralLimit.Y / NonZero(Ki.Y)),
                Clamp(_integral.Z, -IntegralLimit.Z / NonZero(Ki.Z), IntegralLimit.Z / NonZero(Ki.Z)));

            // Derivative (첫 호출이면 0).
            Vector3 d = Vector3.Zero;
            if (_hasPrev) d = (e - _prevError) / dt;
            _prevError = e;
            _hasPrev = true;

            // Per-axis PID.
            return new Vector3(
                Kp.X * e.X + Ki.X * _integral.X + Kd.X * d.X,
                Kp.Y * e.Y + Ki.Y * _integral.Y + Kd.Y * d.Y,
                Kp.Z * e.Z + Ki.Z * _integral.Z + Kd.Z * d.Z);
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        static float NonZero(float v) => System.Math.Abs(v) < 1e-9f ? 1e-9f : v;
    }
}
