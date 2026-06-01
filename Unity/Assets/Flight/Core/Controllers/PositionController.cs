using System;
using System.Numerics;

namespace DroneSim.Flight.Core.Controllers
{
    /// <summary>
    /// Outer-loop **position controller**. ENU 위치 오차 → 목표 추력 + 목표 자세.
    ///
    /// 흐름:
    ///   e_p = w − p            (위치 오차, ENU m)
    ///   a_des = Kp·e_p + Ki·∫e_p − Kd·v   (가속도 명령, ENU m/s²)
    ///   F_req = m·(a_des − g_vec) = m·(a_des.x, a_des.y, a_des.z + g)
    ///   T_des = ‖F_req‖
    ///   thrust 방향 = F_unit = F_req / T_des
    ///   q_des = qTilt · qYaw 로 합성:
    ///     qTilt = body z(0,0,1) → F_unit 으로 회전 (axis = z × F_unit, angle = acos(F_unit.z))
    ///     qYaw  = +Z 축 yawDesired 회전 (수평면 yaw 자유 파라미터)
    /// 적분 anti-windup, 가속도/추력 포화 포함.
    /// </summary>
    public sealed class PositionController
    {
        // ── 게인 (축별 대각: x=East, y=North, z=Up) ─────────────────────────
        public Vector3 Kp = new Vector3(2.0f, 2.0f, 4.0f);
        public Vector3 Ki = new Vector3(0.05f, 0.05f, 0.10f);
        public Vector3 Kd = new Vector3(2.0f, 2.0f, 3.0f);

        public Vector3 IntegralLimit = new Vector3(1f, 1f, 2f);

        // ── 포화 ────────────────────────────────────────────────────────────
        public float MaxAccelHorizontal = 5f;   // m/s²
        public float MaxAccelVertical = 8f;
        public float MaxThrustFactor = 2.0f;    // 최대 T = factor · m·g

        // ── Yaw (자유 파라미터, rad — +Z 축 기준 ENU heading) ──────────────
        public float YawDesired = 0f;

        Vector3 _integral;

        public void Reset() { _integral = Vector3.Zero; }

        public void Step(in Vector3 pCurrent, in Vector3 vCurrent, in Vector3 wTarget,
            DroneParams p, float dt,
            out float thrustDesired, out Quaternion attitudeDesired)
        {
            // 1. 위치 PID.
            Vector3 e = wTarget - pCurrent;
            _integral += e * dt;
            _integral = new Vector3(
                Clamp(_integral.X, -IntegralLimit.X, IntegralLimit.X),
                Clamp(_integral.Y, -IntegralLimit.Y, IntegralLimit.Y),
                Clamp(_integral.Z, -IntegralLimit.Z, IntegralLimit.Z));
            Vector3 aDes = new Vector3(
                Kp.X * e.X + Ki.X * _integral.X - Kd.X * vCurrent.X,
                Kp.Y * e.Y + Ki.Y * _integral.Y - Kd.Y * vCurrent.Y,
                Kp.Z * e.Z + Ki.Z * _integral.Z - Kd.Z * vCurrent.Z);

            // 가속도 포화 (수평 norm 따로, 수직 따로).
            float horizMag = (float)Math.Sqrt(aDes.X * aDes.X + aDes.Y * aDes.Y);
            if (horizMag > MaxAccelHorizontal)
            {
                float k = MaxAccelHorizontal / horizMag;
                aDes.X *= k; aDes.Y *= k;
            }
            aDes.Z = Clamp(aDes.Z, -MaxAccelVertical, MaxAccelVertical);

            // 2. 필요 힘 (ENU): F = m·(a_des - g_vec) = m·(ax, ay, az + g).
            Vector3 fEnu = new Vector3(
                p.Mass * aDes.X,
                p.Mass * aDes.Y,
                p.Mass * (aDes.Z + p.Gravity));

            // 3. 추력 크기 (포화).
            float maxT = p.Mass * p.Gravity * MaxThrustFactor;
            float T = fEnu.Length();
            if (T > maxT) { fEnu *= maxT / T; T = maxT; }

            // 4. 목표 자세: body z → fUnit, yaw 자유.
            Vector3 fUnit = T > 1e-6f ? fEnu / T : new Vector3(0, 0, 1);
            Quaternion qTilt;
            float fz = Clamp(fUnit.Z, -1f, 1f);
            if (fz > 1f - 1e-6f)
            {
                qTilt = Quaternion.Identity;
            }
            else if (fz < -1f + 1e-6f)
            {
                // 180° flip — 비정상 (드론이 거꾸로 가속). 안전상 임의 가로축 회전.
                qTilt = Quaternion.CreateFromAxisAngle(new Vector3(1, 0, 0), (float)Math.PI);
            }
            else
            {
                Vector3 axis = Vector3.Cross(new Vector3(0, 0, 1), fUnit);
                axis = Vector3.Normalize(axis);
                float angle = (float)Math.Acos(fz);
                qTilt = Quaternion.CreateFromAxisAngle(axis, angle);
            }
            Quaternion qYaw = Quaternion.CreateFromAxisAngle(new Vector3(0, 0, 1), YawDesired);
            // 합성: 먼저 yaw(body 축 회전), 그 다음 tilt(world 축으로).
            //   q_des = qTilt · qYaw → body z 가 fUnit 으로 가고, body forward 는 yaw 방향에서 tilt 한 결과.
            attitudeDesired = Quaternion.Multiply(qTilt, qYaw);
            thrustDesired = T;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
