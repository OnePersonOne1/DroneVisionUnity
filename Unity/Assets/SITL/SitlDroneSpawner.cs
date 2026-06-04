using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.SITL
{
    /// <summary>
    /// PX4 SITL 드론을 런타임 생성 — DroneAgent + MavlinkFlightModel 부착 후 External 모드로 등록.
    /// 시뮬 드론(DroneSpawner)과 독립. SITL 드론은 별도 agentId("drone_sitl_N")로 등록되어
    /// 미니맵/전략뷰에서 시뮬 드론과 동시 표시·각각 제어된다.
    ///
    /// 위치는 브리지 텔레메트리가 즉시 덮어쓰므로 spawn 좌표는 placeholder(홈 부근).
    /// 씬 직접 편집 금지 규약 — 이 컴포넌트는 빈 GameObject 에 인스펙터로 부착해 쓴다.
    /// </summary>
    [DisallowMultipleComponent]
    public class SitlDroneSpawner : MonoBehaviour
    {
        [Header("브리지 포트 (MavlinkFlightModel 에 전달)")]
        public int telemetryPort = 9871;
        public string bridgeHost = "127.0.0.1";
        public int commandPort = 9872;

        [Header("생성")]
        [Tooltip("Play 시작 시 1대 자동 생성.")]
        public bool spawnOnStart = true;
        [Tooltip("추가 생성 키.")]
        public Key spawnKey = Key.F9;

        [Header("외관")]
        [Tooltip("디버그 mesh 배수(real-size=1). 큰 맵에서 잘 보이게.")]
        public float visualScale = 10f;
        public Color sitlColor = new Color(1f, 0.55f, 0.1f, 1f);   // 주황 — 시뮬 드론과 구분

        static int _serial = 1;

        void Start()
        {
            if (spawnOnStart) Spawn();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && spawnKey != Key.None && kb[spawnKey].wasPressedThisFrame) Spawn();
        }

        public DroneAgent Spawn()
        {
            float uupm = FrameConversion.ResolveUnityUnitsPerMeter();
            if (uupm <= 0f) uupm = 1f;

            // 비활성 상태로 만들어 DroneAgent.Awake(=Register) 전에 agentId 를 박는다.
            var root = new GameObject("DroneSITL");
            root.SetActive(false);

            // placeholder 위치 (cube/anchor 부근). 텔레메트리 도착 시 즉시 갱신됨.
            // MavlinkFlightModel 의 _basePos 결정 순서와 동일: cubeObject → anchorObject.
            // anchorObject(예: Point1) 가 카메라 시야 밖이면 cube 우선 사용해 시뮬 드론처럼 cube 앞에 spawn.
            var calib = FindObjectOfType<CubeGPSDisplay>();
            Transform baseT = null;
            if (calib != null) baseT = calib.cubeObject != null ? calib.cubeObject : calib.anchorObject;
            root.transform.position = baseT != null
                ? baseT.position + Vector3.up * (5f * uupm)
                : new Vector3(0f, 5f * uupm, 0f);

            BuildVisual(root.transform, visualScale * uupm);

            var agent = root.AddComponent<DroneAgent>();
            agent.agentId = NextSitlId();

            var mav = root.AddComponent<MavlinkFlightModel>();
            mav.telemetryPort = telemetryPort;
            mav.bridgeHost = bridgeHost;
            mav.commandPort = commandPort;

            root.SetActive(true);          // 여기서 Awake 들 실행 (Register + 소켓 bind).
            agent.AttachExternalModel(mav); // External 모드로 전환.
            root.name = agent.agentId;

            Debug.Log($"[SitlDroneSpawner] '{agent.agentId}' 생성 (telem:{telemetryPort} cmd→{bridgeHost}:{commandPort})");
            return agent;
        }

        static string NextSitlId()
        {
            while (true)
            {
                string id = $"drone_sitl_{_serial++}";
                if (DroneRegistry.Get(id) == null) return id;
            }
        }

        // 십자 막대 + 모터 구 (DroneSpawner 외관과 유사하되 주황색으로 구분).
        void BuildVisual(Transform root, float scale)
        {
            float armLen = 0.36f * scale, armThick = 0.04f * scale, motorR = 0.05f * scale;
            float a = (armLen * 0.5f) / 1.41421356f;
            CreateMotor(root, "m0_FR", new Vector3(+a, 0f, -a), motorR);
            CreateMotor(root, "m1_RR", new Vector3(-a, 0f, -a), motorR);
            CreateMotor(root, "m2_RL", new Vector3(-a, 0f, +a), motorR);
            CreateMotor(root, "m3_FL", new Vector3(+a, 0f, +a), motorR);
            CreateArm(root, "ArmA", new Vector3(armLen, armThick, armThick), Quaternion.Euler(0f, 45f, 0f));
            CreateArm(root, "ArmB", new Vector3(armLen, armThick, armThick), Quaternion.Euler(0f, -45f, 0f));
        }

        void CreateArm(Transform parent, string name, Vector3 size, Quaternion rot)
        {
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            DestroyImmediate(arm.GetComponent<Collider>());
            arm.name = name; arm.transform.SetParent(parent, false);
            arm.transform.localRotation = rot; arm.transform.localScale = size;
            SetColor(arm, sitlColor * 0.7f);
        }

        void CreateMotor(Transform parent, string name, Vector3 pos, float radius)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyImmediate(m.GetComponent<Collider>());
            m.name = name; m.transform.SetParent(parent, false);
            m.transform.localPosition = pos; m.transform.localScale = Vector3.one * (radius * 2f);
            SetColor(m, sitlColor);
        }

        static void SetColor(GameObject go, Color c)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (sh == null) return;
            mr.sharedMaterial = new Material(sh) { color = c };
        }
    }
}
