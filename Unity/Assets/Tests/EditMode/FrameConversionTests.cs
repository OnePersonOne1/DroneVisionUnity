using UnityEngine;
using NUnit.Framework;
using DroneSim.Flight.UnityAdapter;
using N = System.Numerics;

namespace DroneSim.Flight.Tests
{
    public class FrameConversionTests
    {
        const float Eps = 1e-4f;

        [Test]
        public void Position_EnuToUnity_YZSwap()
        {
            var enu = new N.Vector3(2f, 3f, 5f);    // (east=2, north=3, up=5)
            var u = FrameConversion.EnuToUnity(enu);
            Assert.AreEqual(2f, u.x, Eps);   // East stays X
            Assert.AreEqual(5f, u.y, Eps);   // Up → Y
            Assert.AreEqual(3f, u.z, Eps);   // North → Z
        }

        [Test]
        public void Position_RoundTrip()
        {
            var enu = new N.Vector3(1.7f, -2.3f, 4.1f);
            var back = FrameConversion.UnityToEnu(FrameConversion.EnuToUnity(enu));
            Assert.AreEqual(enu.X, back.X, Eps);
            Assert.AreEqual(enu.Y, back.Y, Eps);
            Assert.AreEqual(enu.Z, back.Z, Eps);
        }

        [Test]
        public void Quaternion_Identity_RemainsIdentity()
        {
            var qU = FrameConversion.EnuToUnity(N.Quaternion.Identity);
            Assert.AreEqual(0f, qU.x, Eps);
            Assert.AreEqual(0f, qU.y, Eps);
            Assert.AreEqual(0f, qU.z, Eps);
            Assert.AreEqual(1f, qU.w, Eps);
        }

        [Test]
        public void Yaw90_EnuToUnity_RotatesUnityXToUnityZ()
        {
            // 핵심 단위테스트: ENU 에서 +Up 축 기준 +90° 회전 (East→North) 했을 때,
            // Unity 변환 후 적용하면 Unity +X 가 Unity +Z (=North) 로 가야 한다.
            float rad = Mathf.PI * 0.5f;
            var qEnu = N.Quaternion.CreateFromAxisAngle(new N.Vector3(0f, 0f, 1f), rad);

            var qUnity = FrameConversion.EnuToUnity(qEnu);
            Vector3 rotated = qUnity * new Vector3(1f, 0f, 0f);

            Assert.AreEqual(0f, rotated.x, 1e-3f);   // East 가 더는 East 가 아님
            Assert.AreEqual(0f, rotated.y, 1e-3f);
            Assert.AreEqual(1f, rotated.z, 1e-3f);   // Unity +Z = North
        }

        [Test]
        public void PitchNeg90_EnuToUnity_RotatesUnityXToUnityY()
        {
            // ENU(RHC) 에서 +North(Y) 축 기준 회전: +Y 방향 thumb, fingers curl +Z→+X.
            // → +90° 는 X→-Z(Down). 명료하게 East→Up 검증하려면 -90° 사용:
            //   -90° around +Y_enu: +X_enu(East) → +Z_enu(Up).
            // Unity 에선: +X(East) → +Y(Up).
            float rad = -Mathf.PI * 0.5f;
            var qEnu = N.Quaternion.CreateFromAxisAngle(new N.Vector3(0f, 1f, 0f), rad);

            var qUnity = FrameConversion.EnuToUnity(qEnu);
            Vector3 rotated = qUnity * new Vector3(1f, 0f, 0f);

            Assert.AreEqual(0f, rotated.x, 1e-3f);
            Assert.AreEqual(1f, rotated.y, 1e-3f);   // Up
            Assert.AreEqual(0f, rotated.z, 1e-3f);
        }

        [Test]
        public void Quaternion_RoundTrip()
        {
            var qEnu = N.Quaternion.CreateFromAxisAngle(
                N.Vector3.Normalize(new N.Vector3(0.4f, -0.7f, 0.2f)), 1.2f);
            var back = FrameConversion.UnityToEnu(FrameConversion.EnuToUnity(qEnu));
            Assert.AreEqual(qEnu.X, back.X, 1e-3f);
            Assert.AreEqual(qEnu.Y, back.Y, 1e-3f);
            Assert.AreEqual(qEnu.Z, back.Z, 1e-3f);
            Assert.AreEqual(qEnu.W, back.W, 1e-3f);
        }
    }
}
