using UnityEngine;

namespace DroneSim.C2
{
    /// <summary>드론 위치 → 지면 raycast → AGL (Above Ground Level) 미터 계산.
    /// 맵 메시에 MeshCollider 가 부착돼 있어야 한다(전략 view 도 같은 조건).
    /// 실패 시 false + agl=0.</summary>
    public static class GroundAgl
    {
        public static bool TryGetAgl(Vector3 dronePosUnity, float scale, LayerMask mask, out float aglMeters)
        {
            // 1) 드론 위치에서 아래로 raycast — 지면 위면 직접 hit.
            if (Physics.Raycast(dronePosUnity, Vector3.down, out RaycastHit hit, 1e7f, mask, QueryTriggerInteraction.Ignore))
            {
                aglMeters = (dronePosUnity.y - hit.point.y) / Mathf.Max(scale, 1e-6f);
                return true;
            }
            // 2) Back-face 일 경우 — terrain single-sided 대응.
            bool prev = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool ok = Physics.Raycast(dronePosUnity, Vector3.down, out hit, 1e7f, mask, QueryTriggerInteraction.Ignore);
            if (!ok) ok = Physics.Raycast(dronePosUnity, Vector3.up, out hit, 1e7f, mask, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = prev;
            if (ok)
            {
                aglMeters = (dronePosUnity.y - hit.point.y) / Mathf.Max(scale, 1e-6f);
                return true;
            }
            aglMeters = 0f;
            return false;
        }
    }
}
