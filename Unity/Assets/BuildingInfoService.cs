using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

/// [Phase 2/3] BuildingInfoProbe.GpsResolved → 백엔드(info_server) 호출 → 건물 정보.
///
/// 백엔드: Python/info_service (OSM 역지오코딩 + GIS건물통합정보 + Kakao + Ollama LLM + 캐시).
///
/// - B(on-demand)/N(dwell) 으로 조준한 건물을 **선택 목록**에 누적(검색 순서대로, 팔레트 색).
///   각 선택은 ARInfoPanel(색상 카드) + BuildingFootprintHighlighter(건물 색 오버레이)가
///   SelectionsChanged 로 함께 받아 1:1 색 매칭 표시.
/// - X 키로 전체 선택 해제. 최대 maxSelections 개(초과 시 가장 오래된 것 제거).
/// - 드론 이동 시 /prefetch 로 주변 건물을 서버측에서 미리 생성·캐싱(비차단).
///
/// 좌표/반경은 GPS(미터) 기준이라 맵 Unity 스케일과 무관.
[DisallowMultipleComponent]
public class BuildingInfoService : MonoBehaviour
{
    [Header("백엔드")]
    public string serverUrl = "http://127.0.0.1:8077";
    [Tooltip("on-demand 조회 시 LLM 생성 요청(캐시 미스면 ~3s). 끄면 OSM/GIS 즉답만.")]
    public bool useLlm = true;
    public float requestTimeoutSec = 30f;

    [Header("Refs (비우면 자동 검색)")]
    public BuildingInfoProbe probe;
    public CubeGPSDisplay calibration;

    [Header("다중 선택")]
    [Tooltip("동시에 표시할 최대 건물 수(초과 시 오래된 것 제거).")]
    public int maxSelections = 6;
    [Tooltip("전체 선택 해제 키.")]
    public Key clearKey = Key.X;

    [Header("프리페치 (주변 건물 미리 로딩)")]
    public bool prefetchEnabled = true;
    public float prefetchMoveMeters = 80f;
    public float prefetchRadiusMeters = 200f;
    public float prefetchMinIntervalSec = 5f;
    public float prefetchTimeoutSec = 180f;

    [Serializable]
    public class Info
    {
        // 카드 기본 4필드 (기존 호환). detail 은 v2 스키마에서 빈 값.
        public string title, category, summary, detail;
        // v2 스키마 정형 필드 — info_server.build_card 의 결정론적 출력.
        public string address;
        public string floors;           // "지상 N층" or "지상 N층 / 지하 M층" or null
        public float height_m;          // 0 = 미상 (서버가 null 보내면 JsonUtility 가 0 으로 해석)
        public string use;
        public string approval_date;
        public string schema_version;
    }

    [Serializable]
    public class BuildingResult
    {
        public bool found;
        public string name, address, building_key, info_source;
        public Info info;
        public float[] footprint;   // 평면 색면용 삼각형(미사용 시 무해)
        public float[] box;         // OBB 4코너(lat,lng × 4) — 박스 베이스
        public int gis_floors;      // 지상층수 (박스 높이용)
        public float gis_height_m;  // 높이(m)
    }

    /// 선택된 건물 1건. color 는 카드/오버레이 공통(1:1 매칭). hitY 는 오버레이 배치 높이.
    public class Selection
    {
        public string key;
        public Color color;
        public BuildingResult info;
        public float hitY;
        public Vector3 world;   // 명중 월드 위치(미니맵 마커 등)
        public bool pinned;      // 핀 고정 — ClearSelections(X 키) 가 무시. 카드의 핀 버튼으로 토글.
    }

    static readonly Color[] PALETTE = {
        new Color(1.0f, 0.32f, 0.32f, 0.55f),   // red
        new Color(0.32f, 0.66f, 1.0f, 0.55f),   // blue
        new Color(0.40f, 0.92f, 0.45f, 0.55f),  // green
        new Color(1.0f, 0.84f, 0.25f, 0.55f),   // yellow
        new Color(0.95f, 0.50f, 0.95f, 0.55f),  // magenta
        new Color(0.45f, 0.95f, 0.95f, 0.55f),  // cyan
    };

    /// 요청 시작(좌표 도달) — 패널 로딩 표시용.
    public event Action<Vector3> InfoRequested;
    /// 선택 목록 변경 — ARInfoPanel/Highlighter 가 구독해 카드/오버레이 재구성.
    public event Action<List<Selection>> SelectionsChanged;

    readonly List<Selection> _selections = new List<Selection>();
    public IReadOnlyList<Selection> Selections => _selections;

    double _lastPfLat, _lastPfLng;
    bool _havePf, _prefetching;
    float _lastPfTime = -999f;

    void Start()
    {
        if (probe == null) probe = FindObjectOfType<BuildingInfoProbe>();
        if (calibration == null) calibration = FindObjectOfType<CubeGPSDisplay>();
        if (probe != null) probe.GpsResolved += OnGpsResolved;
        else Debug.LogWarning("[InfoService] BuildingInfoProbe 없음 — 좌표 입력을 받을 수 없음.");
    }

    void OnDestroy()
    {
        if (probe != null) probe.GpsResolved -= OnGpsResolved;
    }

    void OnGpsResolved(double lat, double lng, RaycastHit hit)
    {
        InfoRequested?.Invoke(hit.point);
        StartCoroutine(FetchBuildingInfo(lat, lng, hit.point));
    }

    IEnumerator FetchBuildingInfo(double lat, double lng, Vector3 hitWorld)
    {
        string body = "{\"lat\":" + Inv(lat) + ",\"lng\":" + Inv(lng) +
                      ",\"llm\":" + (useLlm ? "true" : "false") + "}";
        using (var req = MakePost("/building_info", body, requestTimeoutSec))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[InfoService] /building_info 실패: {req.error}");
                yield break;
            }
            BuildingResult res = null;
            try { res = JsonUtility.FromJson<BuildingResult>(req.downloadHandler.text); }
            catch (Exception ex) { Debug.LogWarning($"[InfoService] 응답 파싱 실패: {ex.Message}"); }
            if (res == null || !res.found)
            {
                Debug.Log("[InfoService] 건물 없음(좌표가 건물 위가 아님).");
                yield break;
            }
            string title = res.info != null ? res.info.title : res.name;
            int fpLen = res.footprint != null ? res.footprint.Length : 0;
            Debug.Log($"[InfoService] {title} ({res.info_source}) footprint floats={fpLen}");
            AddOrUpdate(res, hitWorld);
        }
    }

    void AddOrUpdate(BuildingResult res, Vector3 world)
    {
        string key = !string.IsNullOrEmpty(res.building_key) ? res.building_key : "pos/" + world.y;
        var existing = _selections.Find(s => s.key == key);
        if (existing != null)
        {
            existing.info = res; existing.hitY = world.y; existing.world = world;
        }
        else
        {
            _selections.Add(new Selection { key = key, info = res, hitY = world.y, world = world, color = PickColor() });
            // 초과 시 가장 오래된 비핀 항목부터 제거. 모두 핀이면 그대로 둠 (사용자 의도 우선).
            int cap = Mathf.Max(1, maxSelections);
            while (_selections.Count > cap)
            {
                int oldestUnpinned = _selections.FindIndex(s => !s.pinned);
                if (oldestUnpinned < 0) break;
                _selections.RemoveAt(oldestUnpinned);
            }
        }
        SelectionsChanged?.Invoke(_selections);
    }

    Color PickColor()
    {
        var used = new HashSet<Color>();
        foreach (var s in _selections) used.Add(s.color);
        foreach (var c in PALETTE)
            if (!used.Contains(c)) return c;
        return PALETTE[_selections.Count % PALETTE.Length];
    }

    /// X 키 — 핀된 카드는 보존, 나머지만 제거.
    public void ClearSelections()
    {
        int removed = _selections.RemoveAll(s => !s.pinned);
        if (removed > 0) SelectionsChanged?.Invoke(_selections);
    }

    /// 카드의 핀 버튼 → 호출. 토글.
    public void TogglePin(string key)
    {
        var sel = _selections.Find(s => s.key == key);
        if (sel == null) return;
        sel.pinned = !sel.pinned;
        SelectionsChanged?.Invoke(_selections);
    }

    /// 카드의 × 버튼 → 호출. 단일 선택 제거 (핀 무관).
    public void RemoveSelection(string key)
    {
        int idx = _selections.FindIndex(s => s.key == key);
        if (idx < 0) return;
        _selections.RemoveAt(idx);
        SelectionsChanged?.Invoke(_selections);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[clearKey].wasPressedThisFrame)
            ClearSelections();
        TickPrefetch();
    }

    void TickPrefetch()
    {
        if (!prefetchEnabled || _prefetching || probe == null || calibration == null) return;
        if (Time.time - _lastPfTime < prefetchMinIntervalSec) return;
        Transform cube = calibration.cubeObject;
        if (cube == null) return;
        probe.WorldToGps(cube.position, out double lat, out double lng);
        if (_havePf && MetersBetween(_lastPfLat, _lastPfLng, lat, lng) < prefetchMoveMeters) return;
        _lastPfLat = lat; _lastPfLng = lng; _havePf = true; _lastPfTime = Time.time;
        StartCoroutine(Prefetch(lat, lng));
    }

    IEnumerator Prefetch(double lat, double lng)
    {
        _prefetching = true;
        string body = "{\"center_lat\":" + Inv(lat) + ",\"center_lng\":" + Inv(lng) +
                      ",\"radius_m\":" + Inv(prefetchRadiusMeters) + "}";
        using (var req = MakePost("/prefetch", body, prefetchTimeoutSec))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
                Debug.Log($"[InfoService] prefetch: {req.downloadHandler.text}");
            else
                Debug.Log($"[InfoService] prefetch 미완(서버는 계속 캐싱): {req.error}");
        }
        _prefetching = false;
    }

    UnityWebRequest MakePost(string path, string json, float timeoutSec)
    {
        var req = new UnityWebRequest(serverUrl + path, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = Mathf.CeilToInt(timeoutSec);
        return req;
    }

    static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string Inv(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    static double MetersBetween(double lat1, double lng1, double lat2, double lng2)
    {
        double dLat = (lat2 - lat1) * 111320.0;
        double dLng = (lng2 - lng1) * 111320.0 *
                      System.Math.Cos((lat1 + lat2) * 0.5 * System.Math.PI / 180.0);
        return System.Math.Sqrt(dLat * dLat + dLng * dLng);
    }
}
