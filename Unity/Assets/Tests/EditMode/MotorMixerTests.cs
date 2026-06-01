using System;
using System.Numerics;
using DroneSim.Flight.Core;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    public class MotorMixerTests
    {
        const float Eps = 1e-3f;

        static DroneParams MakeParams() => new DroneParams();

        [Test]
        public void Allocate_PureHover_AllMotorsEqual()
        {
            var p = MakeParams();
            Span<float> f = stackalloc float[4];
            Span<float> o = stackalloc float[4];
            float T = p.Mass * p.Gravity; // 정확 호버
            MotorMixer.Allocate(T, 0f, 0f, 0f, p, f, o);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(T * 0.25f, f[i], Eps, $"motor {i}");
            // round-trip 으로 합/토크 확인
            MotorMixer.Combine(f, p, out float Tback, out Vector3 tauBack);
            Assert.AreEqual(T, Tback, Eps);
            Assert.AreEqual(0f, tauBack.X, Eps);
            Assert.AreEqual(0f, tauBack.Y, Eps);
            Assert.AreEqual(0f, tauBack.Z, Eps);
        }

        [Test]
        public void Allocate_Roundtrip_ArbitraryCommand()
        {
            var p = MakeParams();
            Span<float> f = stackalloc float[4];
            Span<float> o = stackalloc float[4];
            // hover 위에 작은 τ — 모든 fi > 0 보장.
            float T = p.Mass * p.Gravity;
            float tauX = 0.03f, tauY = -0.02f, tauZ = 0.01f;
            MotorMixer.Allocate(T, tauX, tauY, tauZ, p, f, o);
            for (int i = 0; i < 4; i++) Assert.GreaterOrEqual(f[i], 0f);

            MotorMixer.Combine(f, p, out float Tback, out Vector3 tauBack);
            Assert.AreEqual(T, Tback, Eps);
            Assert.AreEqual(tauX, tauBack.X, Eps);
            Assert.AreEqual(tauY, tauBack.Y, Eps);
            Assert.AreEqual(tauZ, tauBack.Z, Eps);
        }

        [Test]
        public void Allocate_NegativeForce_ClippedToZero()
        {
            var p = MakeParams();
            Span<float> f = stackalloc float[4];
            Span<float> o = stackalloc float[4];
            // 매우 큰 τx 로 일부 모터에 음수 추력 요청 (불가) → 0 클립.
            MotorMixer.Allocate(0f, 10f, 0f, 0f, p, f, o);
            for (int i = 0; i < 4; i++) Assert.GreaterOrEqual(f[i], 0f);
        }

        [Test]
        public void Allocate_MotorPositionsRollAndPitch_SignsConsistent()
        {
            // +τx (roll, 좌측 상승) 명령 → 좌측 모터(m2, m3) 가 우측(m0, m1) 보다 많은 추력.
            var p = MakeParams();
            Span<float> f = stackalloc float[4];
            Span<float> o = stackalloc float[4];
            float T = p.Mass * p.Gravity;
            MotorMixer.Allocate(T, +0.05f, 0f, 0f, p, f, o);
            Assert.Greater(f[2], f[0]);  // 좌후 > 우전
            Assert.Greater(f[3], f[1]);  // 좌전 > 우후

            // +τy (pitch, 기수 상승) → 후방(m1, m2) 가 전방(m0, m3) 보다 많은 추력.
            MotorMixer.Allocate(T, 0f, +0.05f, 0f, p, f, o);
            Assert.Greater(f[1], f[0]);  // 우후 > 우전
            Assert.Greater(f[2], f[3]);  // 좌후 > 좌전

            // +τz (yaw +Z) → CCW 쌍(m0, m2) 이 CW 쌍(m1, m3) 보다 많은 추력.
            MotorMixer.Allocate(T, 0f, 0f, +0.005f, p, f, o);
            Assert.Greater(f[0], f[1]);
            Assert.Greater(f[2], f[3]);
        }
    }
}
