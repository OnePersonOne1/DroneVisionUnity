using UnityEngine;
using DroneSim.Flight.Core;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>드론 파라미터의 Inspector 표시·편집용 ScriptableObject 래퍼.
    /// 런타임에 <see cref="ToCore"/> 로 코어 <see cref="DroneParams"/> 인스턴스를 만든다.</summary>
    [CreateAssetMenu(menuName = "Drone/FlightConfig", fileName = "FlightConfig")]
    public class FlightConfig : ScriptableObject
    {
        [Header("기체 (SI)")]
        public float mass = 1.0f;
        public Vector3 inertiaDiag = new Vector3(0.012f, 0.012f, 0.022f);
        public float arm = 0.18f;

        [Header("모터")]
        public float kf = 1.2e-5f;
        public float km = 2.0e-7f;
        public float maxRotorRadPerSec = 1500f;

        [Header("항력 (선형, ENU 대각)")]
        public Vector3 linearDrag = new Vector3(0.10f, 0.10f, 0.20f);

        [Header("환경")]
        public float gravity = 9.81f;

        [Header("적분 / 루프")]
        [Tooltip("물리 코어 고정 스텝(s). 권장 0.0025 (=400Hz).")]
        public float fixedStep = 0.0025f;

        [Header("월드 스케일 (CubeGPSDisplay 와 동기화)")]
        [Tooltip("1 real meter → Unity 유닛 수. ×100000 맵이면 100000. " +
                 "0 이면 런타임에 CubeGPSDisplay.unityUnitsPerMeter 자동 검색.")]
        public float unityUnitsPerMeter = 0f;

        [Header("외관 (디버그)")]
        [Tooltip("드론 mesh 표시 배수(real-size = 1). 디버그용 10 = 실제 10배 크기.")]
        public float visualScale = 10f;

        public DroneParams ToCore()
        {
            return new DroneParams
            {
                Mass = mass,
                InertiaDiag = new N.Vector3(inertiaDiag.x, inertiaDiag.y, inertiaDiag.z),
                Arm = arm,
                Kf = kf,
                Km = km,
                MaxRotorRadPerSec = maxRotorRadPerSec,
                LinearDrag = new N.Vector3(linearDrag.x, linearDrag.y, linearDrag.z),
                Gravity = gravity,
            };
        }
    }
}
