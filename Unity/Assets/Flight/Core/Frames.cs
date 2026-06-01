using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// 좌표계 규약 (단일 진실 공급원).
    ///
    /// **Inertial frame (코어 내부 전 영역):** ENU, right-handed, SI 단위.
    ///   x = East, y = North, z = Up.
    ///   중력 g = (0, 0, -9.81) [m/s²].
    ///
    /// **Body frame:** FLU (Forward-Left-Up), right-handed.
    ///   x_b = forward (기수), y_b = left, z_b = up.
    ///   Quaternion q ∈ Quat 는 body→inertial 회전 (R(q)·v_body = v_inertial).
    ///
    /// **Unity world (어댑터 경계 밖):** X=East, Y=Up, Z=North (Y-up).
    ///   ENU↔Unity 변환은 y↔z 축 교환 (handedness 교환 포함):
    ///     enu_to_unity: (e, n, u) → (e, u, n)
    ///     unity_to_enu: (x, y, z) → (x, z, y)
    ///   변환은 어댑터 계층(`Flight/Unity/`)에서만 수행. 코어는 ENU 만 안다.
    /// </summary>
    public static class Frames
    {
        public const float GravityMagnitude = 9.81f;
        public static readonly Vector3 GravityEnu = new Vector3(0f, 0f, -GravityMagnitude);

        /// <summary>ENU 좌표 기준 단위벡터.</summary>
        public static readonly Vector3 East  = new Vector3(1f, 0f, 0f);
        public static readonly Vector3 North = new Vector3(0f, 1f, 0f);
        public static readonly Vector3 Up    = new Vector3(0f, 0f, 1f);

        /// <summary>ENU→Unity 축 치환(테스트/어댑터 참조용). 핸드니스가 바뀐다.</summary>
        public static Vector3 EnuToUnity(in Vector3 enu) => new Vector3(enu.X, enu.Z, enu.Y);

        /// <summary>Unity→ENU 역 치환.</summary>
        public static Vector3 UnityToEnu(in Vector3 u) => new Vector3(u.X, u.Z, u.Y);
    }
}
