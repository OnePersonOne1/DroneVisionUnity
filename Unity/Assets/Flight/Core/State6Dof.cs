using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// 6-DOF 강체 상태. 모든 양은 SI, 좌표계는 <see cref="Frames"/> 규약을 따른다.
    ///   - <see cref="P"/> 위치       (ENU, m)
    ///   - <see cref="V"/> 속도       (ENU, m/s)
    ///   - <see cref="Q"/> 자세       (body→ENU quaternion; Vector3.Transform 으로 회전)
    ///   - <see cref="Omega"/> 각속도 (body frame, rad/s)
    /// 적분 후 매 스텝 Q 는 <see cref="Normalize"/> 호출로 ‖q‖=1 유지.
    /// </summary>
    public struct State6Dof
    {
        public Vector3 P;
        public Vector3 V;
        public Quaternion Q;
        public Vector3 Omega;

        public static State6Dof AtRest(Vector3 position) => new State6Dof
        {
            P = position,
            V = Vector3.Zero,
            Q = Quaternion.Identity,
            Omega = Vector3.Zero,
        };

        /// <summary>quaternion 정규화. 적분 후 매 스텝 호출.</summary>
        public void Normalize()
        {
            Q = Quaternion.Normalize(Q);
        }

        /// <summary>State 의 시간 미분과 동일 멤버 구성을 갖는 derivative 컨테이너.
        /// 회전 운동학은 q̇ = ½ q ⊗ [0,ω] 이므로 dQ 도 quaternion 으로 다룬다.</summary>
        public struct Derivative
        {
            public Vector3 DP;
            public Vector3 DV;
            public Quaternion DQ;
            public Vector3 DOmega;
        }
    }
}
