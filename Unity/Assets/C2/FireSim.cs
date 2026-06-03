using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace DroneSim.C2
{
    /// <summary>
    /// 모의 화재(fire/smoke) 주입 + 레지스트리. /assess 엔드포인트 호출자가 이 목록을 읽는다.
    ///
    /// 입력:
    ///   - 키 (인스펙터 노출):
    ///       fireKey  (기본 `;`) — FP 카메라 중앙 raycast 점에 fire_region 마커.
    ///       smokeKey (기본 `'`) — 동일 위치에 smoke_region.
    ///       clearKey (기본 Delete) — 모든 모의 화재 제거.
    ///   - 옵션: enableClickInjection ON 이면 Strategic / 미니맵 위에서 Ctrl+RMB 도 주입(현재 비활성).
    ///
    /// 마커: ProjectionUdpReceiver 의 SpawnMarker 와 동일한 외관 — capsule + DetectionMarker.
    /// DetectionClasses.fire_region / smoke_region 의 색 사용.
    /// 결과 GameObject 는 DetectionMarker 로 등록되어 미니맵·범례에 자동 노출됨.
    ///
    /// 좌표 변환: BuildingInfoProbe.WorldToGps 재사용 (캘리브레이션 일원화).
    /// </summary>
    [DisallowMultipleComponent]
    public class FireSim : MonoBehaviour
    {
        [Serializable]
        public class Fire
        {
            public string id;             // "sim_fire_N" — provenance detection_id 로 사용.
            public string className;      // "fire_region" | "smoke_region"
            public double lat, lng;       // GPS (Kakao/GIS 와 일치하는 변환).
            public Vector3 world;         // Unity 월드 좌표.
            public GameObject marker;     // capsule + DetectionMarker.
            public float spawnTime;       // Time.time
        }

        [Header("Refs (비우면 자동)")]
        public BuildingInfoProbe probe;   // WorldToGps / GpsToWorld 재사용.
        public Camera fpCamera;           // null 이면 FirstPersonView.fpCamera 자동.

        [Header("입력 키")]
        [Tooltip("FP 카메라 중앙 raycast 점에 fire 주입.")]
        public Key fireKey = Key.Semicolon;       // ; — 충돌 없음
        [Tooltip("동일 위치에 smoke 주입.")]
        public Key smokeKey = Key.Quote;          // ' — 충돌 없음
        [Tooltip("모든 모의 화재 제거.")]
        public Key clearKey = Key.Delete;

        [Header("Raycast")]
        public LayerMask groundMask = ~0;
        public float maxRayDistance = 2000000f;

        [Header("마커 외관 (ProjectionUdpReceiver 와 동일 패턴)")]
        [Tooltip("prefab 슬롯이 있으면 사용, 없으면 capsule fallback.")]
        public GameObject markerPrefab;
        [Tooltip("폭 s × 높이 ~10s m (맵 ×100000 스케일 가정).")]
        public float markerScale = 3f;

        [Header("클래스별 lifetime (초, 0 또는 음수 = 영구)")]
        [Tooltip("fire 마커 자동 소멸 시간. 기본 0 = 영구 — 화재는 사용자가 명시 제거할 때까지 유지.")]
        public float fireMarkerLifetime = 0f;
        [Tooltip("smoke 마커 자동 소멸 시간. 0 = 영구. 짧게 두려면 양수 (예: 60).")]
        public float smokeMarkerLifetime = 0f;
        [Tooltip("그 외 클래스 (DetectionClasses 의 fire/smoke 외) 기본값. 0 = 영구.")]
        public float defaultMarkerLifetime = 0f;

        [Header("Click 주입 (실험적)")]
        [Tooltip("Strategic / 미니맵에서 Ctrl+RMB 로 fire 주입. 현 버전 비활성 — 기존 명령과 충돌 회피.")]
        public bool enableClickInjection = false;

        readonly List<Fire> _fires = new List<Fire>();
        int _seq;

        public IReadOnlyList<Fire> Fires => _fires;
        public event Action<IReadOnlyList<Fire>> OnFiresChanged;

        void Start()
        {
            if (probe == null) probe = FindObjectOfType<BuildingInfoProbe>();
            if (fpCamera == null)
            {
                var fpv = FindObjectOfType<FirstPersonView>();
                if (fpv != null) fpCamera = fpv.fpCamera;
            }
            if (probe == null) Debug.LogWarning("[FireSim] BuildingInfoProbe 없음 — 좌표 변환 불가.");
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (overUI) return;   // HUD 클릭/타이핑 중에는 키 무시.

            if (kb[fireKey].wasPressedThisFrame)  SpawnAtCrosshair("fire_region");
            if (kb[smokeKey].wasPressedThisFrame) SpawnAtCrosshair("smoke_region");
            if (kb[clearKey].wasPressedThisFrame) ClearAll();
        }

        /// <summary>FP 카메라 중앙 픽셀 ray → 지면/건물 hit 위치에 마커.</summary>
        public Fire SpawnAtCrosshair(string className)
        {
            if (fpCamera == null)
            {
                var fpv = FindObjectOfType<FirstPersonView>();
                if (fpv != null) fpCamera = fpv.fpCamera;
            }
            if (fpCamera == null) { Debug.LogWarning("[FireSim] FP 카메라 없음 — fire spawn 불가."); return null; }
            float w = fpCamera.pixelWidth  > 0 ? fpCamera.pixelWidth  : Screen.width;
            float h = fpCamera.pixelHeight > 0 ? fpCamera.pixelHeight : Screen.height;
            Ray r = fpCamera.ScreenPointToRay(new Vector3(w * 0.5f, h * 0.5f, 0f));
            if (!Physics.Raycast(r, out RaycastHit hit, maxRayDistance, groundMask))
            {
                Debug.Log("[FireSim] 중앙 ray miss — spawn 취소.");
                return null;
            }
            return SpawnAt(hit.point, className);
        }

        /// <summary>월드 좌표 직접 지정 spawn. (외부 호출용 — 미니맵 클릭 등에서 사용 가능.)</summary>
        public Fire SpawnAt(Vector3 world, string className)
        {
            if (probe == null)
            {
                Debug.LogWarning("[FireSim] BuildingInfoProbe 없음 — GPS 변환 불가.");
                return null;
            }
            probe.WorldToGps(world, out double lat, out double lng);

            var go = BuildMarkerGO(world, className);
            var f = new Fire
            {
                id = $"sim_fire_{++_seq}",
                className = className,
                lat = lat, lng = lng,
                world = world,
                marker = go,
                spawnTime = Time.time,
            };
            _fires.Add(f);
            Debug.Log($"[FireSim] +{f.id} ({className}) @ ({lat:F6},{lng:F6})  world={world}");
            OnFiresChanged?.Invoke(_fires);
            return f;
        }

        public void Remove(string id)
        {
            int idx = _fires.FindIndex(f => f.id == id);
            if (idx < 0) return;
            var f = _fires[idx];
            if (f.marker != null) Destroy(f.marker);
            _fires.RemoveAt(idx);
            Debug.Log($"[FireSim] -{id}");
            OnFiresChanged?.Invoke(_fires);
        }

        public void ClearAll()
        {
            if (_fires.Count == 0) return;
            foreach (var f in _fires) if (f.marker != null) Destroy(f.marker);
            _fires.Clear();
            Debug.Log("[FireSim] cleared all");
            OnFiresChanged?.Invoke(_fires);
        }

        GameObject BuildMarkerGO(Vector3 pos, string className)
        {
            Color color = DetectionClasses.ColorFor(className);
            GameObject go;
            if (markerPrefab != null)
            {
                go = Instantiate(markerPrefab, pos, Quaternion.identity);
                TintRecursive(go, color);
            }
            else
            {
                go = new GameObject($"SimFire_{className}");
                go.transform.position = pos;
                var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                vis.name = "Visual";
                vis.transform.SetParent(go.transform, false);
                float s = markerScale;
                vis.transform.localScale = new Vector3(s, s * 5f, s);
                vis.transform.localPosition = new Vector3(0f, vis.transform.localScale.y, 0f);
                var col = vis.GetComponent<Collider>(); if (col != null) Destroy(col);
                var rend = vis.GetComponent<Renderer>(); if (rend != null) TintRenderer(rend, color);
            }
            go.AddComponent<DetectionMarker>().Init(className, color);
            float life = LifetimeFor(className);
            if (life > 0f) Destroy(go, life);
            return go;
        }

        /// <summary>클래스명 → 적용할 자동 소멸 시간(초). 0 이면 영구.</summary>
        float LifetimeFor(string className)
        {
            if (string.IsNullOrEmpty(className)) return defaultMarkerLifetime;
            if (className.Contains("fire"))  return fireMarkerLifetime;
            if (className.Contains("smoke")) return smokeMarkerLifetime;
            return defaultMarkerLifetime;
        }

        static void TintRecursive(GameObject go, Color c)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>()) TintRenderer(r, c);
        }

        static void TintRenderer(Renderer r, Color c)
        {
            if (r == null) return;
            var mat = r.material;   // 인스턴스 사본 — 다른 마커 색에 영향 X.
            if (mat.HasProperty("_Color")) mat.color = c;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", c * 2f);
            }
        }
    }
}
