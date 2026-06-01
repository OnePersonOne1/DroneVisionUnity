using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// 고정 timestep Runge-Kutta 4 적분기.
    ///
    /// 입력: 현재 상태 s, 제어 입력(T, τ_body), 파라미터 p, 스텝 dt.
    /// 출력: 다음 상태 s' = s + (k1 + 2k2 + 2k3 + k4)·dt/6.
    ///
    /// 입력 (T, τ) 는 RK4 의 4개 평가점에서 동일하다고 가정 (zero-order hold).
    /// 빠른 제어 루프와 빠른 dt(예: 400 Hz, dt=0.0025s) 환경에서 충분.
    /// </summary>
    public static class Rk4Integrator
    {
        /// <summary>
        /// 한 스텝 적분. 마지막에 quaternion 정규화 포함.
        /// </summary>
        public static State6Dof Step(
            in State6Dof s,
            float thrust,
            in Vector3 torque,
            DroneParams p,
            float dt)
        {
            var k1 = RigidBodyDynamics.Derivative(s, thrust, torque, p);

            var s2 = Advance(s, k1, dt * 0.5f);
            var k2 = RigidBodyDynamics.Derivative(s2, thrust, torque, p);

            var s3 = Advance(s, k2, dt * 0.5f);
            var k3 = RigidBodyDynamics.Derivative(s3, thrust, torque, p);

            var s4 = Advance(s, k3, dt);
            var k4 = RigidBodyDynamics.Derivative(s4, thrust, torque, p);

            float sixth = dt / 6f;
            float third = dt / 3f;

            State6Dof next;
            next.P = s.P + (k1.DP + k4.DP) * sixth + (k2.DP + k3.DP) * third;
            next.V = s.V + (k1.DV + k4.DV) * sixth + (k2.DV + k3.DV) * third;
            next.Omega = s.Omega + (k1.DOmega + k4.DOmega) * sixth + (k2.DOmega + k3.DOmega) * third;
            next.Q = new Quaternion(
                s.Q.X + (k1.DQ.X + k4.DQ.X) * sixth + (k2.DQ.X + k3.DQ.X) * third,
                s.Q.Y + (k1.DQ.Y + k4.DQ.Y) * sixth + (k2.DQ.Y + k3.DQ.Y) * third,
                s.Q.Z + (k1.DQ.Z + k4.DQ.Z) * sixth + (k2.DQ.Z + k3.DQ.Z) * third,
                s.Q.W + (k1.DQ.W + k4.DQ.W) * sixth + (k2.DQ.W + k3.DQ.W) * third);
            next.Q = Quaternion.Normalize(next.Q);

            return next;
        }

        /// <summary>s + d·dt (RK4 중간 평가점 계산용, quaternion 도 부분 적분 + 정규화).</summary>
        static State6Dof Advance(in State6Dof s, in State6Dof.Derivative d, float dt)
        {
            State6Dof o;
            o.P = s.P + d.DP * dt;
            o.V = s.V + d.DV * dt;
            o.Omega = s.Omega + d.DOmega * dt;
            o.Q = new Quaternion(
                s.Q.X + d.DQ.X * dt,
                s.Q.Y + d.DQ.Y * dt,
                s.Q.Z + d.DQ.Z * dt,
                s.Q.W + d.DQ.W * dt);
            o.Q = Quaternion.Normalize(o.Q);
            return o;
        }
    }
}
