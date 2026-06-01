using UnityEngine;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// 비교용 단순 운동학 모드 (PhysX 없음, 동역학 없음). HighFidelity 와 1:1 비교/디버그용.
    /// 외부 입력은 (T, τ) 가 아니라 ENU 목표 가속도(직접 적분) — Phase 2 검증 단계에선
    /// SetDirectControl 의 thrust 만 사용해 v_z 누적 → 동작은 보이지만 *동역학적으로는 가짜*.
    /// CubeDroneController(카메라용 cube) 와 무관.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArcadeFlightModel : MonoBehaviour, IFlightModel
    {
        public float gravity = 9.81f;
        [Tooltip("HF 모델과 동기화 — 0 이면 자동.")]
        public float unityUnitsPerMeter = 0f;

        N.Vector3 _p, _v;
        N.Quaternion _q = N.Quaternion.Identity;
        float _thrust;
        N.Vector3 _torque;
        bool _active = true;
        float _scale = 1f;
        Vector3 _originUnity;

        public Vector3 PositionUnity => _originUnity + FrameConversion.EnuToUnity(_p) * _scale;
        public Quaternion RotationUnity => FrameConversion.EnuToUnity(_q);
        public N.Vector3 PositionEnu => _p;
        public N.Vector3 VelocityEnu => _v;
        public bool IsActive { get => _active; set => _active = value; }

        void Awake()
        {
            _scale = unityUnitsPerMeter > 0f ? unityUnitsPerMeter : FrameConversion.ResolveUnityUnitsPerMeter();
            if (_scale <= 0f) _scale = 1f;
            _originUnity = transform.position;
            _p = N.Vector3.Zero;
            _q = FrameConversion.UnityToEnu(transform.rotation);
            var rb = GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
        }

        public void SetDirectControl(float thrustNewton, in N.Vector3 torqueBody)
        {
            _thrust = thrustNewton;
            _torque = torqueBody;   // arcade 는 회전 적분 안 함, 보관만
        }

        public void Teleport(in N.Vector3 positionEnu, in N.Quaternion attitudeEnu)
        {
            _p = positionEnu;
            _q = attitudeEnu;
            _v = N.Vector3.Zero;
            ApplyTransform();
        }

        void FixedUpdate()
        {
            if (!_active) return;
            Step(Time.fixedDeltaTime);
        }

        public void Step(float dt)
        {
            // 가짜 1D 수직 운동: T 만 body z 가속도, 회전 자세 무시.
            float az = (_thrust - 1f * gravity) / 1f;   // m=1 가정. 동역학 부정확함은 의도된 것.
            _v.Z += az * dt;
            _p += _v * dt;
            ApplyTransform();
        }

        void ApplyTransform()
        {
            transform.SetPositionAndRotation(
                _originUnity + FrameConversion.EnuToUnity(_p) * _scale,
                FrameConversion.EnuToUnity(_q));
        }
    }
}
