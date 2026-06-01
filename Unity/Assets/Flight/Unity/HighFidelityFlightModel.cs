using UnityEngine;
using DroneSim.Flight.Core;
using DroneSim.Flight.Core.Controllers;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// 6-DOF EOM + RK4 코어를 호출하는 얇은 MonoBehaviour 어댑터.
    ///
    /// - PhysX 가 동역학을 못 만지도록 Rigidbody(있으면) isKinematic=true 강제.
    /// - 고정 dt 누산기로 코어 Rk4Integrator.Step 호출(렌더 frame rate 무관).
    /// - 입력은 <see cref="SetDirectControl"/> 의 (T, τ_body) — Phase 2 검증, Phase 3 제어기 출력.
    /// - 코어의 ENU 상태(P, Q) 를 어댑터 경계(<see cref="FrameConversion"/>) 한 곳에서만 Unity 로 변환.
    /// </summary>
    [DisallowMultipleComponent]
    public class HighFidelityFlightModel : MonoBehaviour, IFlightModel
    {
        [Header("Config")]
        public FlightConfig config;

        [Header("Phase 2 검증용 — 직접 입력(체크 해제 시 외부 SetDirectControl 만 적용)")]
        [Tooltip("매 스텝 T = m·g + bias, τ = 0 자동 주입. ON 이면 호버, bias 0 미만이면 하강.")]
        public bool debugApplyHoverThrust = false;
        [Tooltip("호버 추력에 더해질 보정(N). +면 상승, -면 하강.")]
        public float debugThrustBias = 0f;

        [Header("Phase 3 검증용 — Attitude 제어 (목표 자세 → 토크)")]
        [Tooltip("ON 이면 q_des 목표로 attitude+rate cascaded PID 가 매 스텝 τ 를 계산. " +
                 "OFF 이면 외부 SetDirectControl 또는 debugApplyHoverThrust 만 적용.")]
        public bool debugApplyAttitudeControl = false;
        [Tooltip("목표 자세(deg) — body→ENU Euler(roll=x, pitch=y, yaw=z). " +
                 "roll 10°만 줘서 step 응답 확인 등.")]
        public Vector3 debugDesiredAttitudeDeg = Vector3.zero;
        [Tooltip("Attitude 제어 시 호버 추력 자동 주입(공중에서 시험할 때).")]
        public bool debugAttitudeWithHover = true;

        [Header("Phase 4 — Position 제어 / waypoint 추종")]
        [Tooltip("ON 이면 매 스텝 DroneAgent.Waypoints 의 현재 목표로 position→attitude→rate " +
                 "cascade 가 동작. 다른 debug 토글보다 우선.")]
        public bool debugApplyNavigation = false;
        [Tooltip("Yaw 자유 파라미터(deg, ENU heading). 위치 PID 출력 자세에 곱해진다.")]
        public float debugYawDesiredDeg = 0f;

        [Header("지면 충돌(EOM 외부 단순 강제 — PhysX 가 동역학에 간섭 못하게 한다)")]
        [Tooltip("매 Tick 끝에 지면 raycast 로 위치 clamp. PhysX 충돌은 안 쓰고 명시적 제약만.")]
        public bool enforceGround = true;
        [Tooltip("지면으로 인정할 레이어 마스크. 비워두면 모든 레이어.")]
        public LayerMask groundMask = ~0;
        [Tooltip("지면 위로 띄울 여유(real meter). 스케일 자동 적용. 디버그 mesh 크기 고려해 2 m 권장.")]
        public float groundOffsetMeters = 2f;
        [Tooltip("지면 검사 raycast 최대 거리(Unity 단위, 스케일 무관). 씬 전체를 포괄하도록 크게.")]
        public float raycastReachUnity = 1e7f;
        [Tooltip("ApplyGroundConstraint 진단 로그(초당 1회). 콜라이더 부재 시 원인 추적용.")]
        public bool logGroundDiagnostics = true;

        float _lastGroundLogTime = -999f;

        [Header("Debug (read-only)")]
        public Vector3 debugPositionEnu;
        public Vector3 debugVelocityEnu;
        public Vector3 debugOmegaBody;
        public float debugThrust;
        public Vector3 debugTorqueBody;

        DroneParams _params;
        State6Dof _state;
        readonly RatePid _rate = new RatePid();
        readonly AttitudeController _att = new AttitudeController();
        readonly PositionController _pos = new PositionController();
        DroneAgent _agent;       // 같은 GameObject 의 DroneAgent, lazy resolve.
        float _thrust;
        N.Vector3 _torque;
        float _accum;
        Rigidbody _rb;
        bool _active = true;
        float _scale = 1f;        // unityUnitsPerMeter (1 real m → N Unity units)
        Vector3 _originUnity;     // ENU 원점(=spawn 시 Unity 위치). _state.P 는 이 점 기준 ENU 오프셋(m).

        public Vector3 PositionUnity => _originUnity + FrameConversion.EnuToUnity(_state.P) * _scale;
        public Quaternion RotationUnity => FrameConversion.EnuToUnity(_state.Q);
        public N.Vector3 PositionEnu => _state.P;
        public N.Vector3 VelocityEnu => _state.V;
        public N.Vector3 OmegaBody => _state.Omega;
        public float UnityUnitsPerMeter => _scale;
        public Vector3 OriginUnity => _originUnity;

        /// <summary>임의의 Unity world 위치 → 이 드론의 ENU 좌표(spawn 점 기준 m).
        /// C2 UI 가 화면 클릭점을 명령 API 에 넘기기 직전에 호출.</summary>
        public N.Vector3 UnityWorldToEnu(in Vector3 worldUnity)
        {
            Vector3 disp = (worldUnity - _originUnity) / Mathf.Max(_scale, 1e-6f);
            return FrameConversion.UnityToEnu(disp);
        }

        /// <summary>이 드론의 ENU 좌표 → Unity world 위치. 시각화(미니맵 마커 등) 용.</summary>
        public Vector3 EnuToUnityWorld(in N.Vector3 posEnu)
        {
            return _originUnity + FrameConversion.EnuToUnity(posEnu) * _scale;
        }

        public bool IsActive { get => _active; set => _active = value; }

        void Awake()
        {
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<FlightConfig>();
                Debug.Log("[HF] config 비어있어 기본값으로 생성. Inspector 에서 ScriptableObject 연결 권장.");
            }
            _params = config.ToCore();
            _scale = config.unityUnitsPerMeter > 0f
                ? config.unityUnitsPerMeter
                : FrameConversion.ResolveUnityUnitsPerMeter();
            if (_scale <= 0f)
            {
                Debug.LogWarning($"[HF] unityUnitsPerMeter 미해결. fallback 1 — 시각 위치/raycast 오차 가능.");
                _scale = 1f;
            }
            _originUnity = transform.position;
            // _state.P = ENU 원점에서의 오프셋(m). spawn 점이 원점 → (0,0,0).
            _state = State6Dof.AtRest(N.Vector3.Zero);
            _state.Q = FrameConversion.UnityToEnu(transform.rotation);
            Debug.Log($"[HF] unityUnitsPerMeter={_scale}, originUnity={_originUnity}");
            _rb = GetComponent<Rigidbody>();
            if (_rb != null)
            {
                _rb.isKinematic = true;            // PhysX 가 적분하지 못하게
                _rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        public void SetDirectControl(float thrustNewton, in N.Vector3 torqueBody)
        {
            _thrust = thrustNewton;
            _torque = torqueBody;
        }

        public void Teleport(in N.Vector3 positionEnu, in N.Quaternion attitudeEnu)
        {
            _state.P = positionEnu;
            _state.V = N.Vector3.Zero;
            _state.Q = attitudeEnu;
            _state.Omega = N.Vector3.Zero;
            ApplyToTransform();
        }

        public void Step(float dt) => Tick(dt);

        void FixedUpdate()
        {
            if (!_active) return;
            Tick(Time.fixedDeltaTime);
        }

        /// <summary>고정 dt 누산기. FixedUpdate dt 보다 더 작은 step 으로 여러 번 적분.</summary>
        void Tick(float frameDt)
        {
            if (debugApplyHoverThrust)
            {
                _thrust = _params.Mass * _params.Gravity + debugThrustBias;
                _torque = N.Vector3.Zero;
            }
            if (debugApplyAttitudeControl)
            {
                if (debugAttitudeWithHover)
                    _thrust = _params.Mass * _params.Gravity + debugThrustBias;
                // 목표 자세: ENU 기준 Euler (roll, pitch, yaw).
                Vector3 deg = debugDesiredAttitudeDeg;
                float rx = deg.x * Mathf.Deg2Rad, ry = deg.y * Mathf.Deg2Rad, rz = deg.z * Mathf.Deg2Rad;
                // RHC Euler 합성: yaw(z) · pitch(y) · roll(x).
                N.Quaternion qDes = N.Quaternion.CreateFromYawPitchRoll(rz, ry, rx);
                // Cascaded: q_des → ω_des → τ.
                N.Vector3 omegaDes = _att.Step(qDes, _state.Q);
                _torque = _rate.Step(omegaDes, _state.Omega, frameDt);
            }
            if (debugApplyNavigation)
            {
                if (_agent == null) _agent = GetComponent<DroneAgent>();
                _pos.YawDesired = debugYawDesiredDeg * Mathf.Deg2Rad;
                // 1. 큐 도달 판정 + 다음 wp.
                if (_agent != null) _agent.Waypoints.Tick(_state.P, _state.V);
                N.Vector3 target = _agent != null
                    ? _agent.Waypoints.TargetOrHold(_state.P)
                    : _state.P;
                // 2. Position controller → T, q_des.
                _pos.Step(_state.P, _state.V, target, _params, frameDt, out float T, out N.Quaternion qDes);
                // 3. Attitude → ω_des.
                N.Vector3 omegaDes = _att.Step(qDes, _state.Q);
                // 4. Rate → τ.
                _torque = _rate.Step(omegaDes, _state.Omega, frameDt);
                _thrust = T;
            }
            _accum += frameDt;
            float step = Mathf.Max(1e-5f, config.fixedStep);
            // 보호: 한 프레임에 적분이 폭주하지 않도록 상한.
            int maxIter = Mathf.CeilToInt(frameDt / step) + 4;
            int iter = 0;
            while (_accum >= step && iter < maxIter)
            {
                _state = Rk4Integrator.Step(_state, _thrust, _torque, _params, step);
                _accum -= step;
                iter++;
            }
            ApplyGroundConstraint();
            ApplyToTransform();
            // 디버그 미러
            debugPositionEnu = new Vector3(_state.P.X, _state.P.Y, _state.P.Z);
            debugVelocityEnu = new Vector3(_state.V.X, _state.V.Y, _state.V.Z);
            debugOmegaBody = new Vector3(_state.Omega.X, _state.Omega.Y, _state.Omega.Z);
            debugThrust = _thrust;
            debugTorqueBody = new Vector3(_torque.X, _torque.Y, _torque.Z);
        }

        void ApplyToTransform()
        {
            Vector3 displacementUnity = FrameConversion.EnuToUnity(_state.P) * _scale;
            transform.SetPositionAndRotation(
                _originUnity + displacementUnity,
                FrameConversion.EnuToUnity(_state.Q));
        }

        /// <summary>RK4 적분 결과를 PhysX 충돌 대신 raycast 로 지면에 clamp.
        /// posUnity = origin + EnuToUnity(_state.P) * scale (스케일 적용된 Unity 좌표).
        /// 드론 위치에서 ⬇/⬆ 양방향 raycast 로 위치 판정:
        ///   - ⬇ hit: 지면 위 — 통과.
        ///   - ⬇ no hit + ⬆ hit: 지면 *아래로* 떨어짐 → hit 점 위로 clamp + V.Z(하강) 0.
        ///   - 양쪽 no hit: 맵 외곽 → 통과.</summary>
        void ApplyGroundConstraint()
        {
            if (!enforceGround) return;
            Vector3 posUnity = _originUnity + FrameConversion.EnuToUnity(_state.P) * _scale;
            float reachUnity = raycastReachUnity;
            float offsetUnity = groundOffsetMeters * _scale;

            // 1. ⬇ raycast: 드론이 표면 위에 있으면 hit (정상 front-face).
            bool downHit = Physics.Raycast(posUnity, Vector3.down, out RaycastHit dHit, reachUnity, groundMask, QueryTriggerInteraction.Ignore);
            // 2. ⬆ raycast: 드론이 표면 *아래* 로 떨어졌으면 표면 back-face 를 봐야 한다.
            //    Terrain 같은 단면(single-sided) mesh 는 기본 backface culling 으로 안 보임 →
            //    일시적으로 Physics.queriesHitBackfaces 켰다가 복원.
            bool prevBackface = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool upHit = Physics.Raycast(posUnity, Vector3.up, out RaycastHit uHit, reachUnity, groundMask, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = prevBackface;

            // 2. 콜라이더 기반 clamp: ⬇ no hit + ⬆ hit → 지면 아래로 떨어진 상태.
            if (!downHit && upHit)
            {
                float groundUnityY = uHit.point.y;
                Vector3 clampedUnity = new Vector3(posUnity.x, groundUnityY + offsetUnity, posUnity.z);
                Vector3 dispU = (clampedUnity - _originUnity) / Mathf.Max(_scale, 1e-6f);
                _state.P = FrameConversion.UnityToEnu(dispU);
                if (_state.V.Z < 0f)
                    _state.V = new N.Vector3(_state.V.X, _state.V.Y, 0f);
            }

            // 3. 진단 로그 (10초당 1회). 콜라이더 누락 시 양쪽 NO HIT 으로 떨어진다.
            if (logGroundDiagnostics && Time.time - _lastGroundLogTime > 10f)
            {
                _lastGroundLogTime = Time.time;
                string dStr = downHit ? $"⬇ hit y={dHit.point.y:F1} ({dHit.collider.name})" : "⬇ NO HIT";
                string uStr = upHit ? $"⬆ hit y={uHit.point.y:F1} ({uHit.collider.name})" : "⬆ NO HIT";
                Debug.Log($"[HF.Ground] posUnityY={posUnity.y:F1} reach={reachUnity:F0} | {dStr} | {uStr} | stateZ={_state.P.Z:F2}m");
            }
        }
    }
}
