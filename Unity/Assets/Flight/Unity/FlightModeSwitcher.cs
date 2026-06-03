using UnityEngine;
using UnityEngine.InputSystem;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>전체 DroneAgent 의 비행 모드를 한 번에 전환(디버그). M 키로 토글.
    /// 단일 드론 환경에서 Arcade vs HighFidelity 동작 비교용.</summary>
    [DisallowMultipleComponent]
    public class FlightModeSwitcher : MonoBehaviour
    {
        // FirstPersonView.modeToggleKey 가 M 이므로 충돌 회피 — Function 키 사용.
        public Key toggleKey = Key.F10;

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb[toggleKey].wasPressedThisFrame) return;
            int n = DroneRegistry.Count;
            if (n == 0) return;
            // External(SITL) 은 토글 대상이 아니다 — 기준/적용 모두 시뮬 드론만.
            DroneAgent pivot = null;
            foreach (var a in DroneRegistry.All)
                if (a != null && a.CurrentMode != DroneAgent.Mode.External) { pivot = a; break; }
            if (pivot == null) return;
            var target = pivot.CurrentMode == DroneAgent.Mode.HighFidelity
                ? DroneAgent.Mode.Arcade
                : DroneAgent.Mode.HighFidelity;
            int applied = 0;
            foreach (var a in DroneRegistry.All)
            {
                if (a == null || a.CurrentMode == DroneAgent.Mode.External) continue;
                a.SetMode(target); applied++;
            }
            Debug.Log($"[FlightModeSwitcher] 시뮬 {applied}대 → {target} (SITL 제외)");
        }
    }
}
