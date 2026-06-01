using System.Numerics;

namespace DroneSim.Flight.Core.Controllers
{
    /// <summary>
    /// Mid-loop **attitude controller** (목표 자세 quaternion → 목표 body 각속도 ω_des).
    ///
    /// 입력: q_des (body→inertial, 목표 자세), q_curr (body→inertial, 현재 자세).
    /// 자세 오차 quaternion: q_err = q_des ⊗ q_curr⁻¹  (작은 회전: ‖q_err.vec‖ ≈ θ/2).
    ///   - q_err.W < 0 이면 부호 뒤집어 short-path 회전 보장.
    ///   - axis-angle 등가: angle ≈ 2·atan2(‖vec‖, w), axis = vec / ‖vec‖.
    /// ω_des(body) = 2·Kp·sign(W)·q_err.vec   (proportional only — inner rate PID 가 dynamics 처리).
    /// 또는 axis-angle 정확 표기: ω_des = Kp · angle · axis (q_curr⁻¹ 좌표계 = body frame).
    ///
    /// 본 구현은 axis-angle 명시 형태 — angle 계산이 더 직관적.
    /// </summary>
    public sealed class AttitudeController
    {
        /// <summary>각 축 독립 비례 게인 (1/s). 큰 값 → 빠른 자세 정렬, 너무 크면 inner rate 와 진동.</summary>
        public Vector3 Kp = new Vector3(6f, 6f, 3f);

        /// <summary>목표 body 각속도 크기 한계(rad/s). 큰 자세 오차에서 폭주 방지.</summary>
        public Vector3 MaxRate = new Vector3(6f, 6f, 4f);

        /// <summary>
        /// q_des, q_curr → ω_des (body, rad/s).
        /// </summary>
        public Vector3 Step(in Quaternion qDesired, in Quaternion qCurrent)
        {
            // q_err = q_des ⊗ q_curr⁻¹ (body→body 작은 회전).
            Quaternion qErr = Quaternion.Multiply(qDesired, Quaternion.Conjugate(qCurrent));
            // q_err.W 음수면 short-path 회전 위해 부호 뒤집기 (-q 는 같은 회전).
            if (qErr.W < 0f)
                qErr = new Quaternion(-qErr.X, -qErr.Y, -qErr.Z, -qErr.W);

            // axis-angle: angle = 2·atan2(‖vec‖, w), axis = vec / ‖vec‖.
            Vector3 vec = new Vector3(qErr.X, qErr.Y, qErr.Z);
            float vecLen = vec.Length();
            float angle = 2f * (float)System.Math.Atan2(vecLen, qErr.W);
            Vector3 axis = vecLen > 1e-6f ? vec / vecLen : Vector3.UnitZ;

            // q_err 은 inertial 표현이지만 q_des·q_curr⁻¹ = q_curr·(body 오차)·q_curr⁻¹ 같은 꼴이라
            // *body* frame 으로 회전을 표현. axis 는 그대로 body frame 회전축.
            Vector3 omegaDes = new Vector3(
                Kp.X * angle * axis.X,
                Kp.Y * angle * axis.Y,
                Kp.Z * angle * axis.Z);

            // Saturate.
            return new Vector3(
                Clamp(omegaDes.X, -MaxRate.X, MaxRate.X),
                Clamp(omegaDes.Y, -MaxRate.Y, MaxRate.Y),
                Clamp(omegaDes.Z, -MaxRate.Z, MaxRate.Z));
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
