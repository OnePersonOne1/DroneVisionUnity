using UnityEngine;
using UnityEngine.InputSystem;

namespace DroneSim.C2
{
    /// <summary>
    /// 멀티 디스플레이 분담 설정.
    ///   Display 1 (idx 0): 1인칭(FP) + HUD/UI
    ///   Display 2 (idx 1): 3인칭 follow camera
    ///   Display 3 (idx 2): Strategic 전체 화면 맵 (별도 StrategicViewBootstrap 처리)
    ///
    /// 역할:
    ///   - 시작 시 추가 디스플레이 Display.Activate (idx ≥ 1).
    ///   - mainCamera / fpCamera 의 targetDisplay 강제 적용.
    ///   - FirstPersonView 의 V 토글이 둘 중 한 카메라를 끄지 못하게 매 LateUpdate 강제 enabled=true.
    ///     (FirstPersonView 자체는 수정 안 함 — 사후 보정 패턴.)
    ///
    /// FirstPersonView 의 라벨/크로스헤어 표시 자체는 V 토글 그대로(시각 효과만).
    /// </summary>
    [DisallowMultipleComponent]
    public class MultiDisplayCoordinator : MonoBehaviour
    {
        [Header("카메라 (비우면 자동 검색)")]
        public Camera mainCamera;
        public Camera fpCamera;
        public FirstPersonView firstPersonView;
        public CubeDroneController cubeController;

        [Header("디스플레이 인덱스 (0-indexed)")]
        public int fpDisplayIndex = 0;
        public int mainDisplayIndex = 1;
        [Tooltip("Strategic Camera 가 사용할 인덱스 — StrategicViewBootstrap 에도 같은 값.")]
        public int strategicDisplayIndex = 2;

        [Header("동작")]
        [Tooltip("시작 시 추가 디스플레이 Display.Activate 호출. Editor 에서 한 번 호출 후 되돌릴 수 없음에 주의.")]
        public bool activateExtraDisplays = true;
        [Tooltip("매 프레임 두 카메라 + CubeDroneController enabled=true 강제 — V 토글이 cube 조작을 끄지 못하게 안전망.")]
        public bool forceCamerasAlwaysOn = true;

        [Header("FP 카메라 1인칭 추종 (Cube)")]
        [Tooltip("매 LateUpdate 마다 fpCamera 위치/회전 = cubeTarget 의 그것. " +
                 "FirstPersonView 의 isActive 건드리지 않음 → CubeDroneController WASD 유지.")]
        public bool fpTrackCube = true;
        [Tooltip("FP 카메라 추종 대상 — 보통 Cube. 비우면 GameObject.Find(\"Cube\").")]
        public Transform fpFollowTarget;
        [Tooltip("Cube 의 local +Z(forward) 방향으로 띄울 거리 (Unity 단위, 씬 스케일 그대로). " +
                 "0 = cube 중심. 양수 = cube 앞쪽으로 약간.")]
        public float fpForwardOffset = 0f;
        [Tooltip("Cube 의 local +Y(up) 방향으로 띄울 거리.")]
        public float fpUpOffset = 0f;

        void Awake()
        {
            ResolveRefs();
            ApplyTargetDisplays();
            // 이전 세션에서 toggleKey 가 Key.None 으로 망가졌다면 V 로 복구
            // (Keyboard[Key.None] 이 ArgumentOutOfRangeException 던짐).
            if (firstPersonView != null && firstPersonView.toggleKey == Key.None)
                firstPersonView.toggleKey = Key.V;
        }

        System.Collections.IEnumerator Start()
        {
            if (activateExtraDisplays) ActivateExtraDisplays();
            yield return null;   // FirstPersonView.Start 가 fpCamera 생성하도록 1 프레임 대기.
            if (fpCamera == null && firstPersonView != null) fpCamera = firstPersonView.fpCamera;
            ApplyTargetDisplays();
            if (forceCamerasAlwaysOn) EnableBoth();
            if (fpFollowTarget == null)
            {
                var go = GameObject.Find("Cube");
                if (go != null) fpFollowTarget = go.transform;
            }
            Debug.Log($"[MultiDisplay] FP cam={(fpCamera!=null?"OK":"null")} target=Display {fpDisplayIndex+1}, " +
                      $"Main cam={(mainCamera!=null?"OK":"null")} target=Display {mainDisplayIndex+1}, " +
                      $"fpFollowTarget={(fpFollowTarget!=null?fpFollowTarget.name:"null")}");
        }

        void LateUpdate()
        {
            if (forceCamerasAlwaysOn) EnableBoth();
            // FP 카메라 1인칭 추종 — Cube 위치/회전과 동기 (FirstPersonView.isActive 건드리지 않음).
            if (fpTrackCube && fpCamera != null && fpFollowTarget != null)
            {
                var t = fpFollowTarget;
                fpCamera.transform.position = t.position + t.forward * fpForwardOffset + t.up * fpUpOffset;
                fpCamera.transform.rotation = t.rotation;
            }
        }

        void ResolveRefs()
        {
            if (firstPersonView == null) firstPersonView = FindObjectOfType<FirstPersonView>();
            if (fpCamera == null && firstPersonView != null) fpCamera = firstPersonView.fpCamera;
            if (mainCamera == null && firstPersonView != null) mainCamera = firstPersonView.followCamera;
            if (mainCamera == null)
            {
                var go = GameObject.Find("Main Camera");
                if (go != null) mainCamera = go.GetComponent<Camera>();
            }
            if (cubeController == null)
            {
                var t = fpFollowTarget;
                if (t == null)
                {
                    var go = GameObject.Find("Cube");
                    if (go != null) t = go.transform;
                }
                if (t != null) cubeController = t.GetComponent<CubeDroneController>();
            }
        }

        void ApplyTargetDisplays()
        {
            // FP 카메라는 FirstPersonView 가 런타임에 만들 수도 있어서 fpCamera 가 null 이면 Start 에서 다시 시도.
            if (fpCamera != null) fpCamera.targetDisplay = fpDisplayIndex;
            if (mainCamera != null) mainCamera.targetDisplay = mainDisplayIndex;
        }

        void ActivateExtraDisplays()
        {
            int top = Mathf.Max(mainDisplayIndex, strategicDisplayIndex);
            int avail = Display.displays.Length;
            for (int i = 1; i <= top; i++)
            {
                if (i >= avail) { Debug.LogWarning($"[MultiDisplay] Display {i} 없음 (avail={avail})."); continue; }
                try { Display.displays[i].Activate(); }
                catch (System.Exception e) { Debug.LogWarning($"[MultiDisplay] Display {i} Activate 실패: {e.Message}"); }
            }
            Debug.Log($"[MultiDisplay] 활성 디스플레이 수={Display.displays.Length}");
        }

        void EnableBoth()
        {
            // FirstPersonView 가 만든 fpCamera 는 늦게 생길 수 있음 → 재해결.
            if (fpCamera == null && firstPersonView != null && firstPersonView.fpCamera != null)
            {
                fpCamera = firstPersonView.fpCamera;
                fpCamera.targetDisplay = fpDisplayIndex;
            }
            if (fpCamera != null && !fpCamera.enabled) fpCamera.enabled = true;
            if (mainCamera != null && !mainCamera.enabled) mainCamera.enabled = true;
            // V 토글 시 FirstPersonView.ApplyDroneControllerState 가 cubeController.enabled=false 로 만들 수 있음 → 무효화.
            if (cubeController != null && !cubeController.enabled) cubeController.enabled = true;
        }
    }
}
