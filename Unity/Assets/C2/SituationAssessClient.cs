using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

namespace DroneSim.C2
{
    /// <summary>
    /// /assess 호출 클라이언트.
    /// FireSim.OnFiresChanged + SimClock.OnTimeChanged 구독 → debounce 후 POST.
    /// 결과는 AssessmentUpdated 이벤트로 BriefingPanel 등에 push.
    /// LLM 출력은 표시 전용 — 이 컴포넌트가 임의 가공하지 않는다.
    /// </summary>
    [DisallowMultipleComponent]
    public class SituationAssessClient : MonoBehaviour
    {
        [Header("Refs (비우면 자동)")]
        public FireSim fireSim;
        public SimClock clock;
        public BuildingInfoService infoService;   // serverUrl 재사용.
        public BuildingInfoProbe probe;           // WorldToGps 재사용.

        [Header("HTTP")]
        [Tooltip("비우면 BuildingInfoService.serverUrl 사용.")]
        public string serverUrlOverride = "";
        public float radiusM = 200f;
        public int topN = 8;
        [Tooltip("HTTP 전체 타임아웃. LLM 이 hang 하더라도 빠르게 fallback briefing 받을 수 있도록 20s 권장.")]
        public float requestTimeoutSec = 20f;

        [Header("트리거")]
        [Tooltip("SimClock 등 비-화재 변경에 적용되는 디바운스 (화재는 즉시).")]
        public float debounceSec = 0.6f;
        [Tooltip("수동 즉시 요청 키 (개발용).")]
        public Key manualKey = Key.F11;

        [Header("검출 화재 자동 평가")]
        [Tooltip("DetectionMarker.All 의 fire_region/smoke_region 도 fires 에 합쳐 평가.")]
        public bool includeDetectionFires = true;
        [Tooltip("실 검출 화재 모니터링 간격 (초). 새 마커 발견 시 즉시 RequestNow.")]
        public float detectionPollSec = 0.5f;

        // ── 응답 스키마 ──────────────────────────────────────────────
        [Serializable] public class Occupancy { public string level, rule_id, category, time_bucket; public int count_est; public float density; }
        [Serializable] public class Risk { public string level, reason; public int total_pop_est, high_density_count; }
        [Serializable] public class PriBuilding
        {
            public string key, title, use, floors, info_source;
            public float height_m, min_dist_m;
            public int nearest_fire_idx;
            public Occupancy occupancy;
        }
        [Serializable] public class Provenance
        {
            public List<string> building_keys;
            public List<string> detection_ids;
            public string sim_time, time_bucket, time_label_ko, briefing_source;
            public int fire_count, smoke_count;
            public float radius_m;
        }
        [Serializable] public class FireStation
        {
            public string name, parent, kind, road_address, jibun_address, district, vehicle_types_raw, ref_date;
            public List<string> vehicle_types;
            public int vehicle_total;
            public double lat, lng;
            public float distance_m;
        }
        [Serializable] public class HazardFlags
        {
            public bool highrise, industrial, educational, crowded, multi_fire, hot_zone;
        }
        [Serializable] public class VehicleNeed
        {
            public string type, reason;
            public int needed, available;
            public bool ok;
        }
        [Serializable] public class AssessResult
        {
            public Risk risk;
            public string spreading;
            public List<PriBuilding> priority_buildings;
            public List<FireStation> nearest_fire_stations;
            public HazardFlags hazard_flags;
            public List<VehicleNeed> vehicle_recommendation;
            public string briefing;
            public Provenance provenance;
            public bool failed;        // 클라이언트 전용 — HTTP 실패 시 true.
            public string errorText;
        }

        public event Action<AssessResult> AssessmentUpdated;
        public AssessResult Latest { get; private set; }

        Coroutine _pendingDebounce;
        Coroutine _inflight;
        bool _pendingRetry;          // inflight 중 RequestNow 호출되면 마크 → 응답 후 1회 재요청.
        float _nextDetectionPollAt;
        int _lastDetectionFireCount = -1;

        void Start()
        {
            if (fireSim == null) fireSim = FindObjectOfType<FireSim>();
            if (clock == null) clock = FindObjectOfType<SimClock>();
            if (infoService == null) infoService = FindObjectOfType<BuildingInfoService>();
            if (probe == null) probe = FindObjectOfType<BuildingInfoProbe>();
            if (fireSim != null) fireSim.OnFiresChanged += OnFiresChanged;
            if (clock != null) clock.OnTimeChanged += OnTimeChanged;
        }

        void OnDestroy()
        {
            if (fireSim != null) fireSim.OnFiresChanged -= OnFiresChanged;
            if (clock != null) clock.OnTimeChanged -= OnTimeChanged;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && manualKey != Key.None && kb[manualKey].wasPressedThisFrame)
                RequestNow();

            // 검출 화재(ProjectionUdpReceiver/Replay 가 spawn 한 fire_region/smoke_region) 모니터링.
            // 새 마커 발견 시 즉시 평가. FireSim 의 시뮬 화재와 합쳐서 fires 전달.
            if (includeDetectionFires && Time.time >= _nextDetectionPollAt)
            {
                _nextDetectionPollAt = Time.time + Mathf.Max(0.05f, detectionPollSec);
                int n = CountDetectionFires();
                if (_lastDetectionFireCount < 0) _lastDetectionFireCount = n;
                else if (n != _lastDetectionFireCount)
                {
                    _lastDetectionFireCount = n;
                    // 화재 0건이면 OnFiresChanged 가 reset 처리 — 여기선 시뮬+검출 합산이 0 일 때만 reset.
                    int simCount = fireSim != null && fireSim.Fires != null ? fireSim.Fires.Count : 0;
                    if (simCount + n == 0)
                    {
                        Latest = null; AssessmentUpdated?.Invoke(null);
                    }
                    else RequestNow();
                }
            }
        }

        static int CountDetectionFires()
        {
            int n = 0;
            var list = DetectionMarker.All;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null) continue;
                string cn = m.className ?? "";
                if (cn.Contains("fire") || cn.Contains("smoke")) n++;
            }
            return n;
        }

        // 화재 추가/제거는 즉시 평가 (지연 X). 빈 리스트면 panel 리셋.
        void OnFiresChanged(IReadOnlyList<FireSim.Fire> fires)
        {
            if (_pendingDebounce != null) { StopCoroutine(_pendingDebounce); _pendingDebounce = null; }
            if (fires == null || fires.Count == 0)
            {
                if (_inflight != null) StopCoroutine(_inflight);
                _inflight = null;
                Latest = null;
                AssessmentUpdated?.Invoke(null);
                return;
            }
            RequestNow();
        }

        void OnTimeChanged() => ScheduleRequest();

        public void ScheduleRequest()
        {
            if (_pendingDebounce != null) StopCoroutine(_pendingDebounce);
            _pendingDebounce = StartCoroutine(DebounceThenRequest());
        }

        IEnumerator DebounceThenRequest()
        {
            yield return new WaitForSeconds(debounceSec);
            _pendingDebounce = null;
            RequestNow();
        }

        public void RequestNow()
        {
            // 이전 요청이 아직 진행 중이면 그것을 중단하지 말고 끝나길 기다림.
            // (LLM hang 시 중복 호출 폭주 + 응답 못 받는 현상 방지.)
            // 응답 후 자동으로 1회 재요청.
            if (_inflight != null) { _pendingRetry = true; return; }
            _pendingRetry = false;
            _inflight = StartCoroutine(SendAssess());
        }

        IEnumerator SendAssess()
        {
            // 시뮬 + 검출 합산 0건이면 호출 스킵.
            int simN = fireSim != null && fireSim.Fires != null ? fireSim.Fires.Count : 0;
            int detN = includeDetectionFires ? CountDetectionFires() : 0;
            if (simN + detN == 0)
            {
                _inflight = null; yield break;
            }
            string url = ResolveServerUrl() + "/assess";
            string body = BuildBody();
            if (body == null) { _inflight = null; yield break; }

            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = Mathf.CeilToInt(requestTimeoutSec);
                yield return req.SendWebRequest();

                AssessResult res = null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Assess] /assess HTTP 실패: {req.error}");
                    res = new AssessResult { failed = true, errorText = req.error };
                }
                else
                {
                    try { res = JsonUtility.FromJson<AssessResult>(req.downloadHandler.text); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Assess] 응답 파싱 실패: {ex.Message}");
                        res = new AssessResult { failed = true, errorText = ex.Message };
                    }
                }
                Latest = res;
                AssessmentUpdated?.Invoke(res);
                if (!res.failed && res.provenance != null)
                    Debug.Log($"[Assess] risk={res.risk?.level} spread={res.spreading} " +
                              $"src={res.provenance.briefing_source} bucket={res.provenance.time_bucket} " +
                              $"buildings={res.provenance.building_keys?.Count}");
            }
            _inflight = null;
            // inflight 중에 들어온 변경 요청이 있었으면 1회 재평가.
            if (_pendingRetry)
            {
                _pendingRetry = false;
                RequestNow();
            }
        }

        string ResolveServerUrl()
        {
            if (!string.IsNullOrEmpty(serverUrlOverride)) return serverUrlOverride;
            if (infoService != null && !string.IsNullOrEmpty(infoService.serverUrl)) return infoService.serverUrl;
            return "http://127.0.0.1:8077";
        }

        string BuildBody()
        {
            var sb = new StringBuilder();
            var ids = new List<string>();
            sb.Append("{");
            sb.Append("\"fires\":[");
            bool first = true;

            // 1) 시뮬 화재 (FireSim).
            if (fireSim != null && fireSim.Fires != null)
            {
                foreach (var f in fireSim.Fires)
                {
                    if (!first) sb.Append(',');
                    sb.Append("{\"lat\":").Append(Inv(f.lat))
                      .Append(",\"lng\":").Append(Inv(f.lng))
                      .Append(",\"cls\":\"").Append(f.className).Append("\"}");
                    ids.Add(f.id);
                    first = false;
                }
            }

            // 2) 검출 화재 (DetectionMarker.All 중 fire/smoke). probe.WorldToGps 로 GPS 변환.
            if (includeDetectionFires && probe != null)
            {
                var list = DetectionMarker.All;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m == null) continue;
                    string cn = m.className ?? "";
                    if (!(cn.Contains("fire") || cn.Contains("smoke"))) continue;
                    // FireSim 마커도 DetectionMarker 로 등록돼 있어 중복 — id prefix 로 구분.
                    // FireSim 의 marker 는 GO 이름이 "SimFire_..." 이므로 제외.
                    if (m.gameObject != null && m.gameObject.name.StartsWith("SimFire_")) continue;
                    Vector3 w = m.transform.position;
                    probe.WorldToGps(w, out double lat, out double lng);
                    if (!first) sb.Append(',');
                    sb.Append("{\"lat\":").Append(Inv(lat))
                      .Append(",\"lng\":").Append(Inv(lng))
                      .Append(",\"cls\":\"").Append(cn).Append("\"}");
                    ids.Add($"det_{m.GetInstanceID()}");
                    first = false;
                }
            }

            sb.Append("],");
            string iso = clock != null ? clock.NowIsoKst()
                       : DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-ddTHH:mm:ss") + "+09:00";
            sb.Append("\"sim_time_iso\":\"").Append(iso).Append("\",");
            sb.Append("\"radius_m\":").Append(Inv(radiusM)).Append(',');
            sb.Append("\"top_n\":").Append(topN).Append(',');
            sb.Append("\"detection_ids\":[");
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(ids[i]).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Inv(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
