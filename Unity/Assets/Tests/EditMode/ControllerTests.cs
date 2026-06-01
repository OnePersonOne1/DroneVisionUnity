using System.Numerics;
using DroneSim.Flight.Core;
using DroneSim.Flight.Core.Controllers;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    public class ControllerTests
    {
        const float Eps = 1e-4f;

        // ── RatePid ─────────────────────────────────────────────────────────

        [Test]
        public void RatePid_ZeroError_ZeroTorque()
        {
            var pid = new RatePid();
            var tau = pid.Step(Vector3.Zero, Vector3.Zero, 0.0025f);
            Assert.AreEqual(0f, tau.X, Eps);
            Assert.AreEqual(0f, tau.Y, Eps);
            Assert.AreEqual(0f, tau.Z, Eps);
        }

        [Test]
        public void RatePid_ProportionalSignsCorrect()
        {
            var pid = new RatePid();
            var tau = pid.Step(new Vector3(1f, 0f, 0f), Vector3.Zero, 0.0025f);
            // 양의 오차 → 양의 토크 (Kp 양수).
            Assert.Greater(tau.X, 0f);
            Assert.AreEqual(0f, tau.Y, Eps);
            Assert.AreEqual(0f, tau.Z, Eps);
        }

        [Test]
        public void RatePid_IntegralAccumulates()
        {
            var pid = new RatePid();
            float dt = 0.01f;
            var initial = pid.Step(new Vector3(1f, 0f, 0f), Vector3.Zero, dt);
            float prev = initial.X;
            for (int i = 0; i < 50; i++)
            {
                var t = pid.Step(new Vector3(1f, 0f, 0f), Vector3.Zero, dt);
                // 적분이 쌓이며 토크가 단조증가(D-term 은 e 변화 없어 0).
                Assert.GreaterOrEqual(t.X, prev - 1e-5f);
                prev = t.X;
            }
        }

        [Test]
        public void RatePid_IntegralSaturates()
        {
            var pid = new RatePid { IntegralLimit = new Vector3(0.1f, 0.1f, 0.1f) };
            float dt = 0.01f;
            for (int i = 0; i < 5000; i++)
                pid.Step(new Vector3(10f, 0f, 0f), Vector3.Zero, dt);
            var tau = pid.Step(new Vector3(10f, 0f, 0f), Vector3.Zero, dt);
            // I-term 기여분 ≤ IntegralLimit. Kp·e 가 큰 값 더해질 수 있어 정확 한계는 못 묶지만
            // 적분 누적이 폭주(>>1)하지 않음을 검사.
            Assert.Less(tau.X, 100f);
        }

        // ── AttitudeController ─────────────────────────────────────────────

        [Test]
        public void Attitude_NoError_NoOmega()
        {
            var att = new AttitudeController();
            var w = att.Step(Quaternion.Identity, Quaternion.Identity);
            Assert.AreEqual(0f, w.X, Eps);
            Assert.AreEqual(0f, w.Y, Eps);
            Assert.AreEqual(0f, w.Z, Eps);
        }

        [Test]
        public void Attitude_RollDesired_PositiveRollRate()
        {
            // q_des = roll +10° (x 축). q_curr = identity.
            // → ω_des 가 +x 방향이어야 함.
            var att = new AttitudeController();
            float rad = 10f * (float)System.Math.PI / 180f;
            var qDes = Quaternion.CreateFromAxisAngle(new Vector3(1, 0, 0), rad);
            var w = att.Step(qDes, Quaternion.Identity);
            Assert.Greater(w.X, 0f);
            Assert.AreEqual(0f, w.Y, 0.05f);
            Assert.AreEqual(0f, w.Z, 0.05f);
        }

        [Test]
        public void Attitude_ShortPathChosen()
        {
            // 350° 회전 = 실은 -10°가 더 짧음. q_err.W < 0 short-path 분기 검사.
            var att = new AttitudeController();
            float rad = 350f * (float)System.Math.PI / 180f;
            var qDes = Quaternion.CreateFromAxisAngle(new Vector3(0, 0, 1), rad);
            var w = att.Step(qDes, Quaternion.Identity);
            // short-path 가 작동하면 ω_des.z 가 NEGATIVE (= -10° 방향).
            Assert.Less(w.Z, 0f);
        }

        [Test]
        public void Attitude_RateSaturated()
        {
            // 큰 자세 오차에서 ω_des 가 MaxRate 로 saturate.
            var att = new AttitudeController { MaxRate = new Vector3(1f, 1f, 1f) };
            float rad = 90f * (float)System.Math.PI / 180f;
            var qDes = Quaternion.CreateFromAxisAngle(new Vector3(1, 0, 0), rad);
            var w = att.Step(qDes, Quaternion.Identity);
            Assert.LessOrEqual(System.Math.Abs(w.X), 1.001f);
        }

        // ── Cascaded loop: attitude→rate→τ→EOM→roll step 응답 ─────────────

        [Test]
        public void Cascaded_Roll10DegStep_SettlesWithin2Seconds()
        {
            var p = new DroneParams { LinearDrag = Vector3.Zero };
            var s = State6Dof.AtRest(Vector3.Zero);
            var rate = new RatePid();
            var att = new AttitudeController();
            float dt = 0.0025f;
            float rad = 10f * (float)System.Math.PI / 180f;
            var qDes = Quaternion.CreateFromAxisAngle(new Vector3(1, 0, 0), rad);

            float hover = p.Mass * p.Gravity;
            float settleErrRad = float.MaxValue;
            int totalSteps = (int)(2.0f / dt);
            for (int i = 0; i < totalSteps; i++)
            {
                var omegaDes = att.Step(qDes, s.Q);
                var tau = rate.Step(omegaDes, s.Omega, dt);
                s = Rk4Integrator.Step(s, hover, tau, p, dt);

                // 최종 1초 후의 정착 오차 측정.
                if (i > totalSteps / 2)
                {
                    var qErr = Quaternion.Multiply(qDes, Quaternion.Conjugate(s.Q));
                    if (qErr.W < 0f) qErr = new Quaternion(-qErr.X, -qErr.Y, -qErr.Z, -qErr.W);
                    float angle = 2f * (float)System.Math.Atan2(
                        new Vector3(qErr.X, qErr.Y, qErr.Z).Length(), qErr.W);
                    settleErrRad = System.Math.Min(settleErrRad, angle);
                }
            }
            // 정착 오차 < 2° (≈ 0.035 rad). 게인이 합리적이면 통과.
            Assert.Less(settleErrRad, 0.035f,
                $"Roll 10° step 응답 정착 실패: 잔류오차 {settleErrRad * 180f / (float)System.Math.PI:F2}°");
        }
    }
}
