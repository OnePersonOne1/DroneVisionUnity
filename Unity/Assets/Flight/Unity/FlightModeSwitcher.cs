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
            // 첫 번째 드론의 모드를 뒤집고 그것을 따라가도록 일괄 적용.
            var target = DroneRegistry.All[0].CurrentMode == DroneAgent.Mode.HighFidelity
                ? DroneAgent.Mode.Arcade
                : DroneAgent.Mode.HighFidelity;
            foreach (var a in DroneRegistry.All) a.SetMode(target);
            Debug.Log($"[FlightModeSwitcher] 전체 {n}대 → {target}");
        }
    }
}
