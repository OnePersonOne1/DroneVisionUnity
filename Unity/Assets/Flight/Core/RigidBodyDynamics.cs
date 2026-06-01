using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// 6-DOF quadrotor 강체 운동방정식 (Newton-Euler).
    ///
    /// 병진(inertial, ENU):
    ///   ṗ = v
    ///   m·v̇ = m·g + R(q)·f_body − D·v
    /// 여기서 f_body = (0, 0, T) (body +z 추력, FLU).
    ///
    /// 회전(body frame):
    ///   J·ω̇ + ω × (J·ω) = τ        (Jω̇ = τ − ω×Jω)
    ///   q̇ = ½·q ⊗ [0, ω_body]      (운동학)
    ///
    /// 입력은 (T, τ_body) — 모터 믹서가 계산. 코어는 UnityEngine 무의존.
    /// </summary>
    public static class RigidBodyDynamics
    {
        /// <summary>
        /// 상태 s 에서의 시간 미분. RK4 의 한 평가점에서 호출.
        /// </summary>
        public static State6Dof.Derivative Derivative(
            in State6Dof s,
            float thrustBodyZ,        // T [N]
            in Vector3 torqueBody,    // τ_x, τ_y, τ_z [N·m]
            DroneParams p)
        {
            // 병진
            //   F_inertial = R(q)·(0,0,T) + m·g − D·v
            //   Vector3.Transform(v, q) = R(q)·v (System.Numerics 규약).
            Vector3 fBody = new Vector3(0f, 0f, thrustBodyZ);
            Vector3 fInertial = Vector3.Transform(fBody, s.Q);
            Vector3 dragInertial = s.V * p.LinearDrag;           // 성분곱 (대각 D)
            Vector3 gravity = new Vector3(0f, 0f, -p.Gravity);   // ENU, z down 음수
            Vector3 acc = gravity + (fInertial - dragInertial) * (1f / p.Mass);

            // 회전(body): J·ω̇ = τ − ω × (J·ω)
            Vector3 Jw = s.Omega * p.InertiaDiag;                // (Jxx·ωx, Jyy·ωy, Jzz·ωz)
            Vector3 gyro = Vector3.Cross(s.Omega, Jw);
            Vector3 angAcc = new Vector3(
                (torqueBody.X - gyro.X) / p.InertiaDiag.X,
                (torqueBody.Y - gyro.Y) / p.InertiaDiag.Y,
                (torqueBody.Z - gyro.Z) / p.InertiaDiag.Z);

            // 운동학: q̇ = ½·q ⊗ [0,ω_body]
            //   System.Numerics.Quaternion(X,Y,Z,W) — W 가 스칼라.
            Quaternion omegaPure = new Quaternion(s.Omega.X, s.Omega.Y, s.Omega.Z, 0f);
            // System.Numerics 의 q1*q2 는 "q2 다음 q1" 활성 회전 (Hamilton 규약과 동일)
            Quaternion dq = Quaternion.Multiply(s.Q, omegaPure);
            dq.X *= 0.5f; dq.Y *= 0.5f; dq.Z *= 0.5f; dq.W *= 0.5f;

            return new State6Dof.Derivative
            {
                DP = s.V,
                DV = acc,
                DQ = dq,
                DOmega = angAcc,
            };
        }
    }
}
