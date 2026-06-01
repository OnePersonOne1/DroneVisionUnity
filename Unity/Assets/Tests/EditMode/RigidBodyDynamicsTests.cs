using System.Numerics;
using DroneSim.Flight.Core;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    public class RigidBodyDynamicsTests
    {
        const float Eps = 1e-4f;

        static DroneParams ZeroDragParams()
        {
            return new DroneParams
            {
                LinearDrag = Vector3.Zero,
            };
        }

        [Test]
        public void NoThrust_DerivativeOnlyGravity()
        {
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            var d = RigidBodyDynamics.Derivative(s, 0f, Vector3.Zero, p);
            Assert.AreEqual(0f, d.DV.X, Eps);
            Assert.AreEqual(0f, d.DV.Y, Eps);
            Assert.AreEqual(-p.Gravity, d.DV.Z, Eps);
            Assert.AreEqual(0f, d.DOmega.X, Eps);
            Assert.AreEqual(0f, d.DOmega.Y, Eps);
            Assert.AreEqual(0f, d.DOmega.Z, Eps);
        }

        [Test]
        public void HoverThrust_ExactlyCancelsGravity()
        {
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            float T = p.Mass * p.Gravity;
            var d = RigidBodyDynamics.Derivative(s, T, Vector3.Zero, p);
            // body Z 추력은 q=identity 에서 inertial Z 그대로 → 중력과 정확 상쇄.
            Assert.AreEqual(0f, d.DV.X, Eps);
            Assert.AreEqual(0f, d.DV.Y, Eps);
            Assert.AreEqual(0f, d.DV.Z, Eps);
        }

        [Test]
        public void DragOpposesVelocity()
        {
            var p = new DroneParams { LinearDrag = new Vector3(0.5f, 0.5f, 0.5f) };
            var s = State6Dof.AtRest(Vector3.Zero);
            s.V = new Vector3(2f, 0f, 0f);
            float T = p.Mass * p.Gravity; // 중력 상쇄
            var d = RigidBodyDynamics.Derivative(s, T, Vector3.Zero, p);
            // -D·v / m = -0.5·2 / 1 = -1.0 (x 방향)
            Assert.AreEqual(-1.0f, d.DV.X, Eps);
            Assert.AreEqual(0f, d.DV.Y, Eps);
            Assert.AreEqual(0f, d.DV.Z, Eps);
        }

        [Test]
        public void GyroscopicCoupling_ZeroForDiagonalOmegaAlongAxis()
        {
            // 단일 축 회전(ω = (ωx, 0, 0)) → ω × J·ω = 0 (J 가 대각이라면).
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            s.Omega = new Vector3(3f, 0f, 0f);
            var d = RigidBodyDynamics.Derivative(s, 0f, Vector3.Zero, p);
            Assert.AreEqual(0f, d.DOmega.X, Eps);
            Assert.AreEqual(0f, d.DOmega.Y, Eps);
            Assert.AreEqual(0f, d.DOmega.Z, Eps);
        }

        [Test]
        public void TorqueProducesAngularAcc()
        {
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            var tau = new Vector3(0.012f, 0f, 0f); // = Jxx · 1 rad/s² 예상
            var d = RigidBodyDynamics.Derivative(s, 0f, tau, p);
            Assert.AreEqual(1.0f, d.DOmega.X, 1e-3f);
        }

        [Test]
        public void QDot_QuaternionDerivativeFromOmega()
        {
            // ω = (0,0,π) → q̇ = ½ q ⊗ [0,ω] 에서 q=identity 이면 dq.Z = π/2.
            var p = ZeroDragParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            s.Omega = new Vector3(0f, 0f, (float)System.Math.PI);
            var d = RigidBodyDynamics.Derivative(s, 0f, Vector3.Zero, p);
            Assert.AreEqual(0f, d.DQ.W, 1e-4f);
            Assert.AreEqual(0f, d.DQ.X, 1e-4f);
            Assert.AreEqual(0f, d.DQ.Y, 1e-4f);
            Assert.AreEqual(0.5f * (float)System.Math.PI, d.DQ.Z, 1e-4f);
        }
    }
}
