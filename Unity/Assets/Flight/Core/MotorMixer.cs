using System;
using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// X-config quadrotor 모터 믹싱. body frame = FLU (x=forward, y=left, z=up).
    ///
    /// 모터 배치 (위에서 본 평면도, 각 모터는 동체 좌표 (rx, ry, 0) 에 위치):
    /// <code>
    ///         +x (forward)
    ///            ↑
    ///   m3 ◯-----+-----◯ m0
    ///      ↺      |    ↻
    ///      |    arm    |   a = arm/√2,  rx=±a, ry=±a
    ///      ↻      |    ↺
    ///   m2 ◯-----+-----◯ m1
    ///            ↓
    /// (위에서 봤을 때) ↺ = CCW, ↻ = CW. 대각선 짝(m0-m2, m1-m3)은 같은 방향,
    /// 인접 짝은 반대 방향 → hover 시 반토크 자연 상쇄.
    /// </code>
    /// 반토크 부호 규약 s_i (양수 = +z 반토크 기여, 음수 = -z): s0=+1(CW), s1=-1(CCW),
    /// s2=+1(CW), s3=-1(CCW).  *CW 로터는 motor 가 rotor 에 -z 토크 → frame 은 +z 반토크.*
    /// 모터별 추력 f_i = Kf·Ω_i² (body +z), 모터별 반토크 = s_i·Km·Ω_i² (body z 축).
    ///
    /// 총합:
    ///   T   = f0 + f1 + f2 + f3                                     (body +z 추력 합)
    ///   τ_x = a·(-f0 - f1 + f2 + f3)  (roll, 양수면 좌측 상승)
    ///   τ_y = a·(-f0 + f1 + f2 - f3)  (pitch, 양수면 기수 상승)
    ///   τ_z = (Km/Kf)·(f0 - f1 + f2 - f3) (yaw, 양수면 +z 회전)
    /// 여기서 a = arm/√2.
    ///
    /// 역믹싱 (제어기 출력 [T, τ] → 모터별 추력 → 각속도):
    ///   f0 = T/4 - τ_x/(4a) - τ_y/(4a) + τ_z·Kf/(4·Km)
    ///   f1 = T/4 - τ_x/(4a) + τ_y/(4a) - τ_z·Kf/(4·Km)
    ///   f2 = T/4 + τ_x/(4a) + τ_y/(4a) + τ_z·Kf/(4·Km)
    ///   f3 = T/4 + τ_x/(4a) - τ_y/(4a) - τ_z·Kf/(4·Km)
    /// 음수는 0 으로 clip (모터는 역추력 불가).
    /// </summary>
    public static class MotorMixer
    {
        public const int MotorCount = 4;

        /// <summary>
        /// [T, τx, τy, τz] (body frame, 4-벡터) → [f0..f3] (각 모터 추력, N).
        /// 음수 클립 후, 각 모터의 Ω_i = √(f_i/Kf) 를 동시에 반환(가능하면).
        /// 호출자 책임: thrust 가 모터 포화에 걸리는지 확인.
        /// </summary>
        public static void Allocate(
            float T, float tauX, float tauY, float tauZ,
            DroneParams p,
            Span<float> motorForces,
            Span<float> motorOmega)
        {
            if (motorForces.Length < MotorCount || motorOmega.Length < MotorCount)
                throw new ArgumentException("motorForces / motorOmega 길이는 4 이상.");

            float a = p.Arm / 1.41421356f; // arm/√2
            float c = p.Kf / p.Km;          // τz 계수 역수

            // (위 주석의 역믹싱 공식)
            float quarterT  = 0.25f * T;
            float quarterTx = 0.25f * tauX / a;
            float quarterTy = 0.25f * tauY / a;
            float quarterTz = 0.25f * tauZ * c;

            motorForces[0] = quarterT - quarterTx - quarterTy + quarterTz;
            motorForces[1] = quarterT - quarterTx + quarterTy - quarterTz;
            motorForces[2] = quarterT + quarterTx + quarterTy + quarterTz;
            motorForces[3] = quarterT + quarterTx - quarterTy - quarterTz;

            float omegaMax = p.MaxRotorRadPerSec;
            for (int i = 0; i < MotorCount; i++)
            {
                if (motorForces[i] < 0f) motorForces[i] = 0f; // 역추력 불가
                float omega = (float)Math.Sqrt(motorForces[i] / p.Kf);
                if (omega > omegaMax)
                {
                    omega = omegaMax;
                    motorForces[i] = p.Kf * omega * omega; // clip 후 force 재계산
                }
                motorOmega[i] = omega;
            }
        }

        /// <summary>
        /// 정믹싱: [f0..f3] → (T, τ_body). 회로 검증(allocate 결과를 다시 합성해 일치 확인) 용도.
        /// </summary>
        public static void Combine(
            ReadOnlySpan<float> motorForces,
            DroneParams p,
            out float T,
            out Vector3 tauBody)
        {
            float f0 = motorForces[0], f1 = motorForces[1], f2 = motorForces[2], f3 = motorForces[3];
            float a = p.Arm / 1.41421356f;
            float kmkf = p.Km / p.Kf;
            T = f0 + f1 + f2 + f3;
            tauBody = new Vector3(
                a    * (-f0 - f1 + f2 + f3),
                a    * (-f0 + f1 + f2 - f3),
                kmkf * ( f0 - f1 + f2 - f3));
        }
    }
}
