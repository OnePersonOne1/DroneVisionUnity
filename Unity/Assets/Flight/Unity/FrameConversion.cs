using UnityEngine;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// ENU(core) ↔ Unity(world) 좌표 변환. **어댑터 경계 단일 진실 공급원**.
    ///
    /// 코어는 ENU(RHC, x=East, y=North, z=Up, gravity = -z) 만 다룬다.
    /// Unity world 는 (x=East, y=Up, z=North, LHC) 로 calib 되어 있다(이 프로젝트 규약).
    /// 두 frame 사이 변환은 단순 y↔z 축 교환이며, 그 자체로 handedness 가 반전된다.
    ///
    /// 벡터:
    ///   enu(e, n, u) → unity(x=e, y=u, z=n)
    ///   unity(x, y, z) → enu(e=x, n=z, u=y)
    ///
    /// Quaternion (body→inertial 활성회전 q):
    ///   q_enu = (W, X, Y, Z) (System.Numerics 표기)
    ///   q_unity = (W, X', Y', Z') 에서  X' = -X,  Y' = -Z,  Z' = -Y
    ///   유도: 새 회전축 = swap(axis), 새 각 = -θ (handedness 반전).
    ///   결과: q_unity = (X=-X_enu, Y=-Z_enu, Z=-Y_enu, W=W_enu) (UnityEngine.Quaternion 표기)
    ///   검증: yaw 90°(ENU +Z 축) → Unity 에서 +X 가 +Z 로 이동(East→North). FramesTests 참조.
    /// </summary>
    public static class FrameConversion
    {
        // ── 위치/속도/가속도 (벡터) ─────────────────────────────────────────
        public static Vector3 EnuToUnity(in N.Vector3 enu) => new Vector3(enu.X, enu.Z, enu.Y);
        public static N.Vector3 UnityToEnu(in Vector3 u) => new N.Vector3(u.x, u.z, u.y);

        // ── Quaternion (자세) ────────────────────────────────────────────────
        public static Quaternion EnuToUnity(in N.Quaternion qEnu)
            => new Quaternion(-qEnu.X, -qEnu.Z, -qEnu.Y, qEnu.W);

        public static N.Quaternion UnityToEnu(in Quaternion qU)
            => new N.Quaternion(-qU.x, -qU.z, -qU.y, qU.w);

        // ── 편의 (offset, 단위벡터 등) ──────────────────────────────────────
        public static N.Vector3 ToN(in Vector3 u) => new N.Vector3(u.x, u.y, u.z);
        public static Vector3 ToU(in N.Vector3 n) => new Vector3(n.X, n.Y, n.Z);

        /// <summary>씬에서 CubeGPSDisplay 의 스케일을 reflection 으로 조회.
        ///   우선순위: `unityUnitsPerMeter`(vertical, runtime 계산) → `horizontalScaleFactor`(수동).
        ///   둘 다 0/미계산이면 0 반환 (호출 측이 fallback 결정).
        /// FlightAdapter 가 Assembly-CSharp 미참조라 강한 타입 참조 불가 → 이름 기반.</summary>
        public static float ResolveUnityUnitsPerMeter()
        {
            var all = Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name != "CubeGPSDisplay") continue;
                // 1) vertical scale — altitudeReferenceBuilding 의 bounds 로부터 계산 (정확도 ↑, 단 첫 Update 후).
                float s = ReadDoubleOrFloat(t.GetField("unityUnitsPerMeter"), mb);
                if (s > 1e-3f) return s;
                // 2) horizontal scale — 인스펙터 수동 설정값 (vertical 미계산 시 fallback).
                s = ReadDoubleOrFloat(t.GetField("horizontalScaleFactor"), mb);
                if (s > 1e-3f) return s;
            }
            return 0f;
        }

        static float ReadDoubleOrFloat(System.Reflection.FieldInfo field, object obj)
        {
            if (field == null) return 0f;
            object v = field.GetValue(obj);
            if (v is double d) return (float)d;
            if (v is float f) return f;
            return 0f;
        }
    }
}
