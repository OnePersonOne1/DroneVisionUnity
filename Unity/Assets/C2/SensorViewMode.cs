using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2
{
    /// <summary>
    /// 센서 시점 모드 — **Display 4 전용** 카메라가 센서의 현재 GPS 위치에 고정되고
    /// 자세는 FIXED/FREE 선택. Display 1 의 FP 카메라는 건드리지 않는다.
    ///
    /// 위치: UDP/Replay 의 cam_lat/cam_lng/cam_alt 환산 월드 좌표.
    /// 자세:
    ///   - FIXED : 센서 reported fwd/up → LookRotation. roll 180° 옵션으로 거꾸로 보정.
    ///   - FREE  : 위치는 센서 고정, 자세는 **LMB 드래그** 마우스 룩(다른 시점과 동일).
    ///            roll 보정은 FREE 에서 적용하지 않음(자유 자세는 항상 upright).
    /// 키:
    ///   - U  : 모드 on/off (display 4 카메라 enabled 토글)
    ///   - F8 : 자세 FIXED ↔ FREE
    ///   - F7 : roll 180° 뒤집기 토글 (FIXED 에서 카메라 거꾸로 찍힐 때)
    /// </summary>
    [DisallowMultipleComponent]
    public class SensorViewMode : MonoBehaviour
    {
        [Header("디스플레이 (0-indexed)")]
        [Tooltip("Display 4 는 인덱스 3.")]
        public int sensorDisplayIndex = 3;
        public bool activateDisplayOnStart = true;

        [Header("토글 키")]
        public Key toggleKey = Key.U;
        public Key attitudeKey = Key.F8;
        public Key flipKey = Key.F7;

        [Header("상태")]
        public bool isActive = false;
        public bool freeAttitude = false;
        [Tooltip("센서가 거꾸로 찍는 경우 자체 카메라 +Z 축 기준 180° 롤 보정.")]
        public bool flipRoll180 = true;

        [Header("카메라 옵션")]
        public float fieldOfView = 60f;
        public float nearClip = 0.1f;
        public float farClip = 1e7f;
        public Color backgroundColor = new Color(0.05f, 0.07f, 0.10f, 1f);
        public LayerMask renderLayers = ~0;

        [Header("Free 자세 마우스 룩 (LMB 드래그)")]
        public float mouseSensitivity = 0.2f;
        public float minPitch = -85f;
        public float maxPitch = 85f;

        [Header("Refs (비우면 자동)")]
        public ProjectionUdpReceiver udp;
        public ProjectionReplay replay;

        [Header("화면 라벨")]
        public bool showOnGuiBadge = true;

        Camera _sensorCam;
        float _yaw, _pitch;
        bool _displayActivated;

        void Awake()
        {
            if (udp == null) udp = FindObjectOfType<ProjectionUdpReceiver>();
            if (replay == null) replay = FindObjectOfType<ProjectionReplay>();
            BuildCamera();
        }

        void Start()
        {
            if (activateDisplayOnStart) ActivateDisplay();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb[toggleKey].wasPressedThisFrame)
            {
                isActive = !isActive;
                if (isActive && !_displayActivated) ActivateDisplay();
                if (_sensorCam != null) _sensorCam.enabled = isActive;
                if (isActive) ResetFreeAttitudeFromCurrent();
                Debug.Log($"[SensorViewMode] {(isActive ? "ON" : "OFF")} (display {sensorDisplayIndex + 1})");
            }
            if (isActive && kb[attitudeKey].wasPressedThisFrame)
            {
                freeAttitude = !freeAttitude;
                if (freeAttitude) ResetFreeAttitudeFromCurrent();
                Debug.Log($"[SensorViewMode] attitude → {(freeAttitude ? "FREE" : "FIXED")}");
            }
            if (isActive && kb[flipKey].wasPressedThisFrame)
            {
                flipRoll180 = !flipRoll180;
                Debug.Log($"[SensorViewMode] roll-180 = {flipRoll180}");
            }
        }

        void LateUpdate()
        {
            if (!isActive || _sensorCam == null) return;

            // Position: 항상 센서 GPS.
            Vector3 pos = _sensorCam.transform.position;
            Vector3 fwd = _sensorCam.transform.forward;
            Vector3 up = _sensorCam.transform.up;
            bool have = false;
            if (udp != null && udp.TryGetLatestPose(out var p1, out var f1, out var u1))
            { pos = p1; fwd = f1; up = u1; have = true; }
            else if (replay != null && replay.TryGetLatestPose(out var p2, out var f2, out var u2))
            { pos = p2; fwd = f2; up = u2; have = true; }

            _sensorCam.transform.position = pos;

            if (freeAttitude)
            {
                // FREE: LMB 드래그 마우스 룩 (다른 시점과 동일). RMB 는 strategic 명령용으로 비워둠.
                // FREE 는 항상 upright — flipRoll180 적용 안 함.
                var mouse = Mouse.current;
                if (mouse != null && mouse.leftButton.isPressed)
                {
                    Vector2 d = mouse.delta.ReadValue();
                    _yaw += d.x * mouseSensitivity;
                    _pitch -= d.y * mouseSensitivity;
                    _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
                }
                _sensorCam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }
            else if (have)
            {
                Quaternion rot = Quaternion.LookRotation(fwd, up);
                if (flipRoll180) rot = rot * Quaternion.Euler(0f, 0f, 180f);
                _sensorCam.transform.rotation = rot;
            }
        }

        void BuildCamera()
        {
            var go = new GameObject("Sensor View Camera");
            go.transform.SetParent(transform, false);
            _sensorCam = go.AddComponent<Camera>();
            _sensorCam.targetDisplay = sensorDisplayIndex;
            _sensorCam.fieldOfView = fieldOfView;
            _sensorCam.nearClipPlane = nearClip;
            _sensorCam.farClipPlane = farClip;
            _sensorCam.clearFlags = CameraClearFlags.SolidColor;
            _sensorCam.backgroundColor = backgroundColor;
            _sensorCam.cullingMask = renderLayers;
            _sensorCam.enabled = isActive;
        }

        void ActivateDisplay()
        {
            if (Display.displays.Length <= sensorDisplayIndex)
            {
                Debug.LogWarning($"[SensorViewMode] Display {sensorDisplayIndex + 1} 없음 (avail={Display.displays.Length}). Editor 단일 모니터면 Game view 탭에서 'Display {sensorDisplayIndex + 1}' 선택해 확인.");
                return;
            }
            try
            {
                Display.displays[sensorDisplayIndex].Activate();
                _displayActivated = true;
                Debug.Log($"[SensorViewMode] Display {sensorDisplayIndex + 1} activated");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SensorViewMode] Display Activate 실패: {e.Message}");
            }
        }

        void ResetFreeAttitudeFromCurrent()
        {
            // FIXED 의 roll-180 flip 에 의해 eulerAngles 가 뒤집힌 값을 갖기 때문에
            // forward 벡터에서 직접 yaw/pitch 를 추출 → FREE 는 항상 upright 로 시작.
            if (_sensorCam == null) return;
            Vector3 f = _sensorCam.transform.forward;
            _yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _pitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        }

        void OnGUI()
        {
            if (!showOnGuiBadge || !isActive) return;
            float w = 380f, h = 28f;
            var r = new Rect((Screen.width - w) * 0.5f, 10f, w, h);
            var prev = GUI.color;
            GUI.color = freeAttitude ? new Color(1f, 0.85f, 0.3f) : new Color(0.3f, 1f, 0.8f);
            GUI.Box(r, $"SENSOR VIEW (Disp {sensorDisplayIndex + 1}) — {(freeAttitude ? "FREE" : "FIXED")} att, flip={flipRoll180}");
            GUI.color = prev;
        }
    }
}
