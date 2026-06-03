using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using N = System.Numerics;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>
    /// 런타임에 십자 막대 드론 GameObject 를 생성. cube(카메라용) 와 무관하게 독립.
    ///
    /// 외관: body FLU 기준
    ///   - 두 직사각형 arm 이 X 형태 (대각선) — m0(전우) ↔ m2(후좌), m1(후우) ↔ m3(전좌)
    ///   - 4개 작은 구가 모터 위치(arm 끝)에 부착, 전우(m0) 만 빨강(방향 표시)
    /// 부착 컴포넌트: HighFidelityFlightModel + ArcadeFlightModel + DroneAgent.
    /// 초기 모드 = HighFidelity, 호버 추력은 외부에서 SetDirectControl 로 입력.
    /// 시작 위치 기본: cube(카메라용) 의 forward × 5m 앞. cube 가 없으면 (0, 5, 0).
    /// 추가 spawn 키: 백쿼트(`). 첫 spawn 은 Start 에서 자동.
    /// </summary>
    [DisallowMultipleComponent]
    public class DroneSpawner : MonoBehaviour
    {
        [Header("Spawn 기준")]
        [Tooltip("이 Transform 의 forward × distance 만큼 떨어진 곳에 spawn. 비우면 자동으로 cube 검색.")]
        public Transform spawnAnchor;
        public float distanceFromAnchor = 5f;

        [Header("Config")]
        public FlightConfig config;

        [Header("Spawn 키 (디버그)")]
        public Key spawnAdditionalKey = Key.Backquote;
        [Tooltip("Play 시작 시 1대 자동 spawn.")]
        public bool spawnOnStart = true;

        [Header("Spawn 직후 동작 — 모든 spawn 드론에 적용")]
        public SpawnMode initialMode = SpawnMode.HoverInPlace;
        [Tooltip("AttitudeHold 시 목표 자세(roll, pitch, yaw deg).")]
        public Vector3 initialAttitudeDeg = Vector3.zero;
        [Tooltip("Hover/AttitudeHold 시 호버 추력에 더할 보정(N). +면 상승, -면 하강.")]
        public float initialThrustBias = 0f;

        public enum SpawnMode
        {
            FreeFall,        // 추력 0, 자유낙하 (지면 raycast 가 받쳐줌)
            HoverInPlace,    // T = m·g (그 자리 정지) — 기본
            AttitudeHold,    // T = m·g, attitude PID 가 initialAttitudeDeg 유지
            Navigate,        // Phase 4: position→attitude→rate cascade, 빈 큐면 spawn 점 hold
        }

        [Header("외관")]
        [Tooltip("디버그 mesh 배수(real-size = 1). 10 = 실제 10배, ×100000 맵에서 잘 보임. " +
                 "FlightConfig.visualScale 가 있으면 그 값을 우선.")]
        public float visualScale = 10f;
        public Color frontMotorColor = Color.red;
        public Color otherMotorColor = Color.white;
        public Color armColor = new Color(0.3f, 0.3f, 0.3f);

        IEnumerator Start()
        {
            if (spawnAnchor == null) spawnAnchor = AutoFindAnchor();
            if (!spawnOnStart) yield break;
            // CubeGPSDisplay.unityUnitsPerMeter 는 첫 Update 가 돌아야 계산됨 — 한 프레임 대기.
            // FlightConfig 에 수동값이 있으면 대기 불필요.
            if (config == null || config.unityUnitsPerMeter <= 0f)
            {
                int maxFrames = 120;
                for (int i = 0; i < maxFrames; i++)
                {
                    yield return null;
                    if (FrameConversion.ResolveUnityUnitsPerMeter() > 1e-3f) break;
                }
            }
            Spawn();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[spawnAdditionalKey].wasPressedThisFrame)
                Spawn();
        }

        Transform AutoFindAnchor()
        {
            // 이름이 "Cube" 인 GameObject — 카메라용 cube. (FlightAdapter 는 Assembly-CSharp
            // 미참조라 CubeGPSDisplay 타입을 못 본다. 이름 검색으로 우회.)
            var go = GameObject.Find("Cube");
            if (go != null) return go.transform;
            return null;
        }

        public DroneAgent Spawn()
        {
            // ENU 5m 이동 = Unity 5 * unityUnitsPerMeter 유닛.
            float uupm = config != null && config.unityUnitsPerMeter > 0f
                ? config.unityUnitsPerMeter
                : FrameConversion.ResolveUnityUnitsPerMeter();
            if (uupm <= 0f)
            {
                Debug.LogWarning("[DroneSpawner] unityUnitsPerMeter 미해결 (CubeGPSDisplay 못 찾음 또는 미계산). " +
                                 "fallback 1 — FlightConfig.unityUnitsPerMeter 에 수동값 권장.");
                uupm = 1f;
            }
            Vector3 pos = spawnAnchor != null
                ? spawnAnchor.position + spawnAnchor.forward * (distanceFromAnchor * uupm)
                : new Vector3(0f, 5f * uupm, 0f);

            // GO 이름은 임시로 "Drone" — AddComponent<DroneAgent>() 의 Awake → Register 가
            // 빈 agentId 를 보고 NextDefaultId 로 채워주면 그 ID 로 GO 이름 갱신.
            var root = new GameObject("Drone");
            root.transform.position = pos;

            float vs = (config != null ? config.visualScale : visualScale) * uupm;
            BuildVisual(root.transform, vs);

            var hf = root.AddComponent<HighFidelityFlightModel>();
            hf.config = config;
            var arc = root.AddComponent<ArcadeFlightModel>();
            arc.gravity = config != null ? config.gravity : 9.81f;
            // DroneAgent.agentId 디폴트 = "" → Awake → Register → NextDefaultId 1회 호출로 id 부여.
            // 옛 방식(pre-NextDefaultId → Unregister/re-Register) 은 NextDefaultId 가 두 번씩 불려
            // 0, 1, 3, 5, 7 처럼 짝수 시리얼이 건너뛰는 버그가 있었음.
            var agent = root.AddComponent<DroneAgent>();
            agent.highFidelity = hf;
            agent.arcade = arc;
            root.name = agent.agentId;

            ApplyInitialMode(hf);

            Debug.Log($"[DroneSpawner] '{agent.agentId}' spawn @ {pos} mode={initialMode}");
            return agent;
        }

        void ApplyInitialMode(HighFidelityFlightModel hf)
        {
            switch (initialMode)
            {
                case SpawnMode.FreeFall:
                    hf.debugApplyHoverThrust = false;
                    hf.debugApplyAttitudeControl = false;
                    break;
                case SpawnMode.HoverInPlace:
                    hf.debugApplyHoverThrust = true;
                    hf.debugApplyAttitudeControl = false;
                    hf.debugThrustBias = initialThrustBias;
                    break;
                case SpawnMode.AttitudeHold:
                    hf.debugApplyHoverThrust = false;
                    hf.debugApplyAttitudeControl = true;
                    hf.debugAttitudeWithHover = true;
                    hf.debugDesiredAttitudeDeg = initialAttitudeDeg;
                    hf.debugThrustBias = initialThrustBias;
                    break;
                case SpawnMode.Navigate:
                    hf.debugApplyHoverThrust = false;
                    hf.debugApplyAttitudeControl = false;
                    hf.debugApplyNavigation = true;
                    hf.debugYawDesiredDeg = initialAttitudeDeg.z;
                    hf.debugThrustBias = initialThrustBias;
                    break;
            }
        }

        void BuildVisual(Transform root, float effectiveScale)
        {
            // 외관은 Unity 표준 표시 축에 직접 배치(수평 XZ 평면).
            // body FLU(x=forward, y=left, z=up) ↔ Unity 표시 (X=right=East, Y=up, Z=forward=North):
            //   body (x_b, y_b, z_b) → mesh local (x_b, z_b, y_b)  // y↔z swap
            // 따라서 모터 (x_b=±a, y_b=±a, z_b=0) 은 mesh local (±a, 0, ±a) — Y=0 수평면.
            float armLen = 0.36f * effectiveScale;       // 모터간 대각 길이 (Unity 유닛)
            float armThick = 0.04f * effectiveScale;
            float motorR = 0.05f * effectiveScale;
            float a = (armLen * 0.5f) / 1.41421356f;     // 모터 축 좌표 = 대각/√2

            // 모터 (수평 XZ 평면, Y=0).
            //   m0 FR: body (+,-, 0) = mesh (+a, 0, -a)   ← 빨강(전방 우측 표시)
            //   m1 RR: body (-,-, 0) = mesh (-a, 0, -a)
            //   m2 RL: body (-,+, 0) = mesh (-a, 0, +a)
            //   m3 FL: body (+,+, 0) = mesh (+a, 0, +a)
            CreateMotor(root, "m0_FR", new Vector3(+a, 0f, -a), motorR, frontMotorColor);
            CreateMotor(root, "m1_RR", new Vector3(-a, 0f, -a), motorR, otherMotorColor);
            CreateMotor(root, "m2_RL", new Vector3(-a, 0f, +a), motorR, otherMotorColor);
            CreateMotor(root, "m3_FL", new Vector3(+a, 0f, +a), motorR, otherMotorColor);

            // 두 arm: XZ 평면 내 ±45° (Unity +Y 축 회전).
            //   ArmA: m0(+a,0,-a) ↔ m2(-a,0,+a) — +45°
            //   ArmB: m1(-a,0,-a) ↔ m3(+a,0,+a) — -45°
            CreateArm(root, "ArmA", new Vector3(armLen, armThick, armThick),
                Quaternion.Euler(0f, 45f, 0f));
            CreateArm(root, "ArmB", new Vector3(armLen, armThick, armThick),
                Quaternion.Euler(0f, -45f, 0f));
        }

        void CreateArm(Transform parent, string name, Vector3 size, Quaternion localRot)
        {
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            DestroyImmediate(arm.GetComponent<Collider>());
            arm.name = name;
            arm.transform.SetParent(parent, false);
            arm.transform.localRotation = localRot;
            arm.transform.localScale = size;
            SetUnlitColor(arm, armColor);
        }

        void CreateMotor(Transform parent, string name, Vector3 localPos, float radius, Color color)
        {
            var motor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyImmediate(motor.GetComponent<Collider>());
            motor.name = name;
            motor.transform.SetParent(parent, false);
            motor.transform.localPosition = localPos;
            motor.transform.localScale = Vector3.one * (radius * 2f);
            SetUnlitColor(motor, color);
        }

        static void SetUnlitColor(GameObject go, Color c)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (sh == null) return;
            var mat = new Material(sh) { color = c };
            mr.sharedMaterial = mat;
        }

    }
}
