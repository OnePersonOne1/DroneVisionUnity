using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using UnityEngine;
using DroneSim.Flight.UnityAdapter;   // IFlightModel, IFlightCommandSink, FrameConversion
using N = System.Numerics;

namespace DroneSim.SITL
{
    /// <summary>
    /// PX4 SITL 드론의 Unity 측 표현 — **읽기 전용 물리**. 자체 적분/제어 없이 브리지
    /// (px4_bridge.py)가 보내는 텔레메트리(LLA/자세/속도)로 position/rotation 만 갱신하고,
    /// C2 명령(SetWaypoint 등)이 들어오면 브리지로 송신한다. 실제 비행은 PX4 가 소유.
    ///
    /// 어셈블리: Assets/SITL/ 은 asmdef 없음 → Assembly-CSharp. FlightAdapter(IFlightModel)
    /// 와 GPS 코드(CubeGPSDisplay/GPSEncoder) 둘 다 참조 가능(FlightAdapter 는 GPS 못 봄).
    ///
    /// 좌표 정합: LLA→Unity world 변환을 ProjectionUdpReceiver 와 **동일 규약**으로 미러링
    ///   수평: world.xz = anchor.xz + GPSToUCS(lat,lon) * horizontalScaleFactor
    ///   수직: world.y  = refBuilding.baseY + rel_alt * (meshHeight / refHeightMeters)
    /// → 같은 GPS 의 검출 마커와 정확히 같은 위치에 SITL 드론이 놓인다.
    ///
    /// 명령은 역변환: ENU(드론 기준) → world → LLA(WGS84) + AMSL → 브리지 goto/mission.
    /// </summary>
    [DisallowMultipleComponent]
    public class MavlinkFlightModel : MonoBehaviour, IFlightModel, IFlightCommandSink
    {
        [Header("Network (브리지와 합의된 포트)")]
        public int telemetryPort = 9871;     // 브리지 → Unity (수신 bind)
        public string bridgeHost = "127.0.0.1";
        public int commandPort = 9872;       // Unity → 브리지 (송신)

        [Header("Stale 처리")]
        [Tooltip("이 시간(초) 동안 텔레메트리 없으면 stale 로 표시. 브리지/Gazebo 종료 감지.")]
        public float staleTimeoutSec = 3f;

        [Header("Debug (read-only)")]
        public bool hasTelemetry;
        public bool isStale;
        public bool armed;
        public bool inAir;
        public string flightMode = "UNKNOWN";
        public double curLat, curLon, curAltAmsl, curRelAlt;

        // ── 텔레메트리 wire 포맷 (px4_bridge.py 와 1:1) ──
        [System.Serializable]
        class TelemetryMsg
        {
            public string type;
            public double t, lat, lon, alt_amsl, rel_alt;
            public double roll, pitch, yaw, vn, ve, vd;
            public bool armed, in_air;
            public string flight_mode;
        }

        // ── IFlightModel 상태 ──
        Vector3 _world;
        Quaternion _rot = Quaternion.identity;
        N.Vector3 _enu, _vel;
        bool _active = true;

        // ── 캘리브레이션 (CubeGPSDisplay 미러) ──
        CubeGPSDisplay _calib;
        double _anchorLat = 37.384312, _anchorLon = 126.655307;
        double _horizScale = 1.0;        // horizontalScaleFactor (Unity units / meter, 수평)
        double _vertScale = 1.0;         // unitsPerMeter (수직, 건물 기준)
        Vector3 _basePos;                // anchorObject.position
        float _baseY;                    // refBuilding bounds.min.y (고도 원점)
        bool _calibReady;

        double _homeAmsl;                // alt_amsl - rel_alt (홈 AMSL, 매 텔레 갱신)

        // ── 명령 경로(route) — QueueWaypoint 누적용 ──
        readonly List<N.Vector3> _route = new List<N.Vector3>();

        // ── 네트워킹 ──
        UdpClient _rx;
        Thread _rxThread;
        volatile bool _running;
        readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        UdpClient _tx;
        IPEndPoint _txEndpoint;
        float _lastRxTime = -999f;

        // ── IFlightModel ──
        public Vector3 PositionUnity => _world;
        public Quaternion RotationUnity => _rot;
        public N.Vector3 PositionEnu => _enu;
        public N.Vector3 VelocityEnu => _vel;
        public float UnityUnitsPerMeter => (float)_vertScale;
        public bool IsActive { get => _active; set => _active = value; }

        public bool IsStale => isStale;

        public N.Vector3 UnityWorldToEnu(in Vector3 worldUnity)
        {
            double hs = _horizScale > 1e-9 ? _horizScale : 1.0;
            double vs = _vertScale > 1e-9 ? _vertScale : 1.0;
            float e = (float)((worldUnity.x - _basePos.x) / hs);
            float n = (float)((worldUnity.z - _basePos.z) / hs);
            float u = (float)((worldUnity.y - _baseY) / vs);
            return new N.Vector3(e, n, u);   // ENU: X=East, Y=North, Z=Up
        }

        public Vector3 EnuToUnityWorld(in N.Vector3 posEnu)
        {
            float x = _basePos.x + (float)(posEnu.X * _horizScale);
            float z = _basePos.z + (float)(posEnu.Y * _horizScale);
            float y = _baseY + (float)(posEnu.Z * _vertScale);
            return new Vector3(x, y, z);
        }

        // 외부 텔레메트리로만 갱신 — 자체 적분/제어 없음.
        public void Step(float dt) { }
        public void SetDirectControl(float thrustNewton, in N.Vector3 torqueBody) { }
        public void Teleport(in N.Vector3 positionEnu, in N.Quaternion attitudeEnu)
            => Debug.LogWarning("[Mavlink] Teleport 무시 — SITL 드론은 브리지/PX4 가 위치를 소유.");

        void Awake()
        {
            _tx = new UdpClient();
            try { _rx = new UdpClient(telemetryPort); }
            catch (System.Exception e)
            {
                Debug.LogError($"[Mavlink] 텔레 포트 {telemetryPort} bind 실패: {e.Message}");
                enabled = false; return;
            }
            _txEndpoint = new IPEndPoint(IPAddress.Parse(bridgeHost), commandPort);
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true };
            _rxThread.Start();
            Debug.Log($"[Mavlink] telem listen udp:{telemetryPort}, cmd→{bridgeHost}:{commandPort}");
        }

        void OnDestroy()
        {
            _running = false;
            try { _rx?.Close(); } catch { }
            try { _rxThread?.Join(200); } catch { }
            try { _tx?.Close(); } catch { }
        }

        void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try { _queue.Enqueue(Encoding.UTF8.GetString(_rx.Receive(ref any))); }
                catch (SocketException) { if (_running) Debug.LogWarning("[Mavlink] socket error"); break; }
                catch (System.ObjectDisposedException) { break; }
            }
        }

        void Update()
        {
            if (!_calibReady) ResolveCalibration();

            // 큐 비우고 최신 프레임만 적용 (백로그 방지).
            string json = null;
            while (_queue.TryDequeue(out string j)) json = j;
            if (json != null) ApplyTelemetry(json);

            // stale 판정.
            bool wasStale = isStale;
            isStale = hasTelemetry && (Time.time - _lastRxTime > staleTimeoutSec);
            if (isStale && !wasStale)
                Debug.LogWarning($"[Mavlink] STALE — {staleTimeoutSec}s 텔레메트리 없음 (브리지/Gazebo 종료?). 마커 정지.");
            if (!isStale && wasStale)
                Debug.Log("[Mavlink] 텔레메트리 복구.");
        }

        void ApplyTelemetry(string json)
        {
            TelemetryMsg m;
            try { m = JsonUtility.FromJson<TelemetryMsg>(json); }
            catch { return; }
            if (m == null || m.type != "telemetry") return;

            _lastRxTime = Time.time;
            hasTelemetry = true;
            armed = m.armed; inAir = m.in_air; flightMode = m.flight_mode;
            curLat = m.lat; curLon = m.lon; curAltAmsl = m.alt_amsl; curRelAlt = m.rel_alt;
            _homeAmsl = m.alt_amsl - m.rel_alt;

            if (!_calibReady) return;   // 캘리브 전엔 위치 미갱신.

            // LLA → Unity world (ProjectionUdpReceiver 미러). 수직은 rel_alt(지면/홈 기준 m).
            GPSEncoder.SetLocalOrigin(new Vector2((float)_anchorLat, (float)_anchorLon));
            Vector3 ucs = GPSEncoder.GPSToUCS((float)m.lat, (float)m.lon);   // (xEast, 0, zNorth) meters
            float wx = _basePos.x + ucs.x * (float)_horizScale;
            float wz = _basePos.z + ucs.z * (float)_horizScale;
            float wy = _baseY + (float)(m.rel_alt * _vertScale);
            _world = new Vector3(wx, wy, wz);

            _enu = new N.Vector3(ucs.x, ucs.z, (float)m.rel_alt);   // E, N, U
            _vel = new N.Vector3((float)m.ve, (float)m.vn, (float)(-m.vd));   // NED→ENU: E=ve, N=vn, U=-vd

            // 자세: euler(NED deg) → Unity. yaw(Y, CW from North) 일치. pitch/roll 부호는
            // 통합 테스트에서 시각 확인 후 조정 가능(위치엔 무관, 마커 표시용).
            _rot = Quaternion.Euler(-(float)m.pitch, (float)m.yaw, -(float)m.roll);

            if (_active && !isStale)
                transform.SetPositionAndRotation(_world, _rot);
        }

        bool ResolveCalibration()
        {
            if (_calib == null) _calib = FindObjectOfType<CubeGPSDisplay>();
            if (_calib == null) return false;

            _anchorLat = _calib.anchorLatitude;
            _anchorLon = _calib.anchorLongitude;
            _horizScale = _calib.horizontalScaleFactor > 1e-9 ? _calib.horizontalScaleFactor : 1.0;
            _basePos = _calib.anchorObject != null ? _calib.anchorObject.position : Vector3.zero;

            // 수직 스케일 + 고도 원점 — 기준 건물 bounds (CubeGPSDisplay 와 동일 계산).
            var refB = _calib.altitudeReferenceBuilding;
            var rend = refB != null ? refB.GetComponent<Renderer>() : null;
            if (rend != null && _calib.referenceBuildingHeightMeters > 0)
            {
                _baseY = rend.bounds.min.y;
                float h = rend.bounds.max.y - rend.bounds.min.y;
                if (h > 1e-4f)
                {
                    _vertScale = h / _calib.referenceBuildingHeightMeters;
                    _calibReady = true;
                    Debug.Log($"[Mavlink] calib ready: anchor=({_anchorLat:F6},{_anchorLon:F6}) " +
                              $"horizScale={_horizScale} vertScale={_vertScale:F3} baseY={_baseY:F1}");
                    return true;
                }
            }
            // 기준 건물 없으면: 수직도 anchor 기준 1:1 fallback (정확도↓, 경고).
            _baseY = _basePos.y;
            _vertScale = _horizScale;
            _calibReady = true;
            Debug.LogWarning("[Mavlink] altitudeReferenceBuilding 없음 — 수직 fallback(vertScale=horizScale). " +
                             "고도 표시 부정확 가능.");
            return true;
        }

        // ── IFlightCommandSink — C2 명령을 브리지로 송신 ──────────────────────
        public void SetWaypoint(in N.Vector3 wEnu)
        {
            _route.Clear();
            _route.Add(wEnu);
            SendGoto(wEnu);
        }

        public void QueueWaypoint(in N.Vector3 wEnu)
        {
            _route.Add(wEnu);
            if (_route.Count == 1) SendGoto(wEnu);
            else SendMission(_route);
        }

        public void SetPath(IList<N.Vector3> pathEnu)
        {
            _route.Clear();
            if (pathEnu != null) _route.AddRange(pathEnu);
            if (_route.Count == 0) return;
            if (_route.Count == 1) SendGoto(_route[0]);
            else SendMission(_route);
        }

        public void StopAndHover()
        {
            _route.Clear();
            SendJson("{\"type\":\"hold\"}");
        }

        // ── 직접 제어 (SitlControlInput 키 입력 → 브리지). FlightCommands 와 무관(시그니처 불변). ──
        public void Arm() => SendJson("{\"type\":\"arm\"}");
        public void Land() { _route.Clear(); SendJson("{\"type\":\"land\"}"); }
        public void ReturnToLaunch() { _route.Clear(); SendJson("{\"type\":\"rtl\"}"); }
        public void Takeoff(float altMeters)
        {
            var ci = CultureInfo.InvariantCulture;
            SendJson($"{{\"type\":\"takeoff\",\"alt\":{altMeters.ToString("F2", ci)}}}");
        }

        // ── ENU → LLA 변환 + 브리지 송신 ──
        void SendGoto(in N.Vector3 enu)
        {
            if (!_calibReady || !hasTelemetry)
            {
                Debug.LogWarning("[Mavlink] goto 보류 — 캘리브/텔레메트리 미수신.");
                return;
            }
            EnuToLla(enu, out double lat, out double lon, out double altAmsl);
            var ci = CultureInfo.InvariantCulture;
            SendJson($"{{\"type\":\"goto\",\"lat\":{lat.ToString("F8", ci)}," +
                     $"\"lon\":{lon.ToString("F8", ci)},\"alt\":{altAmsl.ToString("F2", ci)}}}");
        }

        void SendMission(List<N.Vector3> route)
        {
            if (!_calibReady || !hasTelemetry)
            {
                Debug.LogWarning("[Mavlink] mission 보류 — 캘리브/텔레메트리 미수신.");
                return;
            }
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"type\":\"mission\",\"items\":[");
            for (int i = 0; i < route.Count; i++)
            {
                EnuToLla(route[i], out double lat, out double lon, out double altAmsl);
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"lat\":{lat.ToString("F8", ci)},\"lon\":{lon.ToString("F8", ci)}," +
                          $"\"alt\":{altAmsl.ToString("F2", ci)}}}");
            }
            sb.Append("]}");
            SendJson(sb.ToString());
        }

        /// <summary>ENU(드론/홈 기준 m) → WGS84 LLA + AMSL 고도.</summary>
        void EnuToLla(in N.Vector3 enu, out double lat, out double lon, out double altAmsl)
        {
            // 수평: ENU(E,N) meters → anchor 기준 GPS.
            GPSEncoder.SetLocalOrigin(new Vector2((float)_anchorLat, (float)_anchorLon));
            Vector2 gps = GPSEncoder.USCToGPS(new Vector3(enu.X, 0f, enu.Y));  // x=East→lon, z=North→lat
            lat = gps.x; lon = gps.y;
            // 수직: U(홈/지면 기준 m) → AMSL.
            altAmsl = _homeAmsl + enu.Z;
        }

        void SendJson(string json)
        {
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                _tx.Send(b, b.Length, _txEndpoint);
                Debug.Log($"[Mavlink] cmd→ {json}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[Mavlink] cmd 송신 오류: {e.Message}"); }
        }
    }
}
