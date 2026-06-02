using UnityEngine;
using UnityEngine.InputSystem;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2.StrategicView
{
    /// <summary>
    /// 전략 카메라 팬/줌. 입력 라우팅: 마우스가 전략 뷰 영역 위에 있을 때만 작동.
    ///
    /// 키:
    ///   - 화살표(↑↓←→): 팬 (WASD 는 Cube 와 충돌 회피).
    ///   - 마우스 휠: 줌 (orthographicSize).
    ///   - 중간 마우스 드래그: 팬 (StarCraft-style 우클릭과 분리 — 우클릭은 명령용).
    /// </summary>
    [RequireComponent(typeof(StrategicViewBootstrap))]
    public class StrategicCameraController : MonoBehaviour
    {
        [Header("팬")]
        [Tooltip("화살표 키 팬 속도 — orthographicSize 와 비례 (m/s · sizeFactor).")]
        public float panKeySpeedFactor = 1.0f;
        [Tooltip("중간 마우스 드래그 팬 감도. 작을수록 둔감.")]
        public float panDragSensitivity = 1.0f;

        [Header("줌")]
        public float zoomFactor = 1.15f;
        [Tooltip("최소 ortho size(real m). 런타임에 unityUnitsPerMeter 곱.")]
        public float minOrthoSizeMeters = 5f;
        [Tooltip("최대 ortho size(real m).")]
        public float maxOrthoSizeMeters = 5000f;

        [Header("GPS 위치로 복귀")]
        [Tooltip("Strategic Camera 를 Cube(현재 GPS 시점) 위로 재중심.")]
        public Key recenterKey = Key.Home;
        [Tooltip("보조 키 — Home 이 노트북 Fn 레이어에 있을 때 대비. Key.None 이면 비활성.")]
        public Key recenterKey2 = Key.Backspace;

        StrategicViewBootstrap _bootstrap;
        Vector2 _lastMouseScreen;
        bool _draggingMmb;
        float _scale = 1f;

        void Awake() { _bootstrap = GetComponent<StrategicViewBootstrap>(); }

        float ResolveScale()
        {
            if (_scale > 1.001f) return _scale;
            float s = FrameConversion.ResolveUnityUnitsPerMeter();
            if (s > 1e-3f) _scale = s;
            return _scale;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;

            // ── GPS 복귀 (Home/보조키) — cam null 가드보다 먼저 체크해서 부트스트랩 지연에도 무시되지 않게. ──
            if (kb != null)
            {
                bool primary = recenterKey != Key.None && kb[recenterKey].wasPressedThisFrame;
                bool secondary = recenterKey2 != Key.None && kb[recenterKey2].wasPressedThisFrame;
                if (primary || secondary)
                {
                    if (_bootstrap != null)
                    {
                        _bootstrap.RecenterCamera();
                        Debug.Log($"[StrategicCam] recenter → Cube 위치 (key={(primary ? recenterKey : recenterKey2)})");
                    }
                    else
                    {
                        Debug.LogWarning("[StrategicCam] recenter 무시 — _bootstrap null");
                    }
                }
            }

            var cam = _bootstrap?.StrategicCamera;
            if (cam == null) return;

            // 카메라 팬/줌은 비-파괴적(시각 변화만) — overView 게이트 제거, 항상 허용.
            // Editor 단일 모니터에서 Display 3 탭 보면서 조작 가능. RMB SetWaypoint 는 별도 (StrategicCommandInput).

            // ── 화살표 팬 (마우스 위치 무관, kb 항상 가능 — 다른 키 그룹과 안 겹침) ──
            if (kb != null)
            {
                Vector2 pan = Vector2.zero;
                if (kb.upArrowKey.isPressed)    pan.y += 1f;
                if (kb.downArrowKey.isPressed)  pan.y -= 1f;
                if (kb.rightArrowKey.isPressed) pan.x += 1f;
                if (kb.leftArrowKey.isPressed)  pan.x -= 1f;
                // Cube 의 ←→ 는 yaw — 화살표는 Cube 와 strategic 둘 다 쓰지만 strategic 팬은
                // 시각적 변화만, 동시에 cube yaw 도 같이 일어남 → 사용자가 의도적으로 선택.
                if (pan.sqrMagnitude > 0f)
                {
                    float speed = panKeySpeedFactor * cam.orthographicSize * Time.unscaledDeltaTime;
                    Vector3 delta = new Vector3(pan.x * speed, 0f, pan.y * speed);
                    cam.transform.position += delta;
                }
            }

            // ── 중간 마우스 드래그 팬 (overView 게이트 없이 항상) ──
            if (mouse != null)
            {
                if (mouse.middleButton.wasPressedThisFrame)
                {
                    _draggingMmb = true;
                    _lastMouseScreen = mouse.position.ReadValue();
                }
                if (mouse.middleButton.wasReleasedThisFrame) _draggingMmb = false;
                if (_draggingMmb)
                {
                    Vector2 cur = mouse.position.ReadValue();
                    Vector2 d = cur - _lastMouseScreen;
                    _lastMouseScreen = cur;
                    float worldPerPx = cam.orthographicSize * 2f / Mathf.Max(1, Screen.height) * panDragSensitivity;
                    Vector3 worldDelta = new Vector3(-d.x * worldPerPx, 0f, -d.y * worldPerPx);
                    cam.transform.position += worldDelta;
                }

                // ── 휠 줌 (항상) ──
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    float s = ResolveScale();
                    float minU = minOrthoSizeMeters * s;
                    float maxU = maxOrthoSizeMeters * s;
                    if (scroll > 0f)
                        cam.orthographicSize = Mathf.Max(minU, cam.orthographicSize / zoomFactor);
                    else
                        cam.orthographicSize = Mathf.Min(maxU, cam.orthographicSize * zoomFactor);
                }
            }
        }
    }
}
