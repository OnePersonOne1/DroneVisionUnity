using System.Numerics;
using DroneSim.Flight.Core;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    public class Rk4IntegratorTests
    {
        static DroneParams ZeroDragParams() => new DroneParams { LinearDrag = Vector3.Zero };

        [Test]
        public void FreeFall_OneSecond_MatchesAnalytic()
        {
            // 추력 0 · 무항력 자유낙하: z(t) = -½·g·t², v_z(t) = -g·t.
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            float dt = 0.0025f;
            float t = 0f;
            while (t < 1.0f)
            {
                s = Rk4Integrator.Step(s, 0f, Vector3.Zero, p, dt);
                t += dt;
            }
            float zExpected = -0.5f * p.Gravity * t * t;
            float vzExpected = -p.Gravity * t;
            Assert.AreEqual(zExpected, s.P.Z, 1e-3f);
            Assert.AreEqual(vzExpected, s.V.Z, 1e-3f);
            Assert.AreEqual(0f, s.P.X, 1e-5f);
            Assert.AreEqual(0f, s.P.Y, 1e-5f);
        }

        [Test]
        public void HoverThrust_StaysAtRest()
        {
            // 정확한 호버 추력 → 1초 후 위치/속도 변화 무시 가능.
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(new Vector3(0f, 0f, 100f));
            float T = p.Mass * p.Gravity;
            float dt = 0.0025f;
            for (int i = 0; i < 400; i++)
                s = Rk4Integrator.Step(s, T, Vector3.Zero, p, dt);
            Assert.AreEqual(100f, s.P.Z, 1e-3f);
            Assert.AreEqual(0f, s.V.Z, 1e-3f);
        }

        [Test]
        public void ConstantUpwardAcc_OneSecond()
        {
            // 2·m·g 추력 → 알짜 +g 가속 → v_z(1) = g, z(1) = g/2.
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            float T = 2f * p.Mass * p.Gravity;
            float dt = 0.0025f;
            float t = 0f;
            while (t < 1.0f)
            {
                s = Rk4Integrator.Step(s, T, Vector3.Zero, p, dt);
                t += dt;
            }
            Assert.AreEqual(0.5f * p.Gravity * t * t, s.P.Z, 1e-3f);
            Assert.AreEqual(p.Gravity * t, s.V.Z, 1e-3f);
        }

        [Test]
        public void Quaternion_StaysUnit_AfterManySteps()
        {
            // 비대칭 각속도로 회전하면서 누적 정규화 오차 확인.
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            s.Omega = new Vector3(0.3f, 0.7f, -0.5f);
            float dt = 0.0025f;
            for (int i = 0; i < 4000; i++) // 10s
                s = Rk4Integrator.Step(s, 0f, Vector3.Zero, p, dt);
            float norm = (float)System.Math.Sqrt(
                s.Q.X * s.Q.X + s.Q.Y * s.Q.Y + s.Q.Z * s.Q.Z + s.Q.W * s.Q.W);
            Assert.AreEqual(1f, norm, 1e-4f);
        }

        [Test]
        public void NoNaN_AfterLongHover()
        {
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            float T = p.Mass * p.Gravity;
            float dt = 0.0025f;
            for (int i = 0; i < 4000; i++)
                s = Rk4Integrator.Step(s, T, new Vector3(0.001f, -0.001f, 0.0005f), p, dt);
            Assert.IsFalse(float.IsNaN(s.P.X) || float.IsNaN(s.P.Y) || float.IsNaN(s.P.Z));
            Assert.IsFalse(float.IsNaN(s.V.X) || float.IsNaN(s.V.Y) || float.IsNaN(s.V.Z));
            Assert.IsFalse(float.IsNaN(s.Q.W));
            Assert.IsFalse(float.IsNaN(s.Omega.X));
        }
    }
}
