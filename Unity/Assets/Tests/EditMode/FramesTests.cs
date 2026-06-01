using System.Numerics;
using DroneSim.Flight.Core;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    public class FramesTests
    {
        const float Eps = 1e-5f;

        [Test]
        public void Gravity_PointsDownInEnu()
        {
            // ENU 에서 중력은 -Z. 크기는 9.81.
            Assert.AreEqual(0f, Frames.GravityEnu.X, Eps);
            Assert.AreEqual(0f, Frames.GravityEnu.Y, Eps);
            Assert.AreEqual(-Frames.GravityMagnitude, Frames.GravityEnu.Z, Eps);
            Assert.AreEqual(9.81f, Frames.GravityMagnitude, Eps);
        }

        [Test]
        public void EnuUnitVectors_AreOrthonormalRightHanded()
        {
            // East × North = Up (RHC 정의)
            Vector3 cross = Vector3.Cross(Frames.East, Frames.North);
            Assert.AreEqual(Frames.Up.X, cross.X, Eps);
            Assert.AreEqual(Frames.Up.Y, cross.Y, Eps);
            Assert.AreEqual(Frames.Up.Z, cross.Z, Eps);
        }

        [Test]
        public void EnuToUnity_IsYZSwap()
        {
            // (east=2, north=3, up=5) → unity(x=east=2, y=up=5, z=north=3)
            Vector3 enu = new Vector3(2f, 3f, 5f);
            Vector3 u = Frames.EnuToUnity(enu);
            Assert.AreEqual(2f, u.X, Eps);
            Assert.AreEqual(5f, u.Y, Eps);
            Assert.AreEqual(3f, u.Z, Eps);
        }

        [Test]
        public void EnuToUnity_RoundTrip()
        {
            Vector3 enu = new Vector3(1.7f, -2.3f, 4.1f);
            Vector3 back = Frames.UnityToEnu(Frames.EnuToUnity(enu));
            Assert.AreEqual(enu.X, back.X, Eps);
            Assert.AreEqual(enu.Y, back.Y, Eps);
            Assert.AreEqual(enu.Z, back.Z, Eps);
        }

        [Test]
        public void Yaw90AroundUpInEnu_RotatesEastToNorth()
        {
            // ENU 에서 +90° (반시계, +Up 축, RHC) 회전 → East 벡터가 North 로 간다.
            float rad = (float)System.Math.PI * 0.5f;
            Quaternion qYaw90 = Quaternion.CreateFromAxisAngle(Frames.Up, rad);
            Vector3 rotated = Vector3.Transform(Frames.East, qYaw90);
            Assert.AreEqual(Frames.North.X, rotated.X, 1e-4f);
            Assert.AreEqual(Frames.North.Y, rotated.Y, 1e-4f);
            Assert.AreEqual(Frames.North.Z, rotated.Z, 1e-4f);
        }
    }
}
