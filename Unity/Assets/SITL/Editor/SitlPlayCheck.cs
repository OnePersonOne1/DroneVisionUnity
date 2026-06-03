using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using DroneSim.Flight.UnityAdapter;
using N = System.Numerics;

namespace DroneSim.SITL.EditorTools
{
    /// <summary>
    /// headless Play 통합테스트 (batchmode, -quit 없이 호출). SITL+브리지가 떠 있어야 한다.
    ///
    /// 검증: Play 진입 → drone_sitl_1 생성 + 텔레메트리 수신 → FlightCommands.SetWaypoint
    /// (RTS 명령 경로) 발행 → 브리지 auto-takeoff+goto → 드론 고도 상승/이동을 Unity 가
    /// 텔레메트리로 반영 → Land. 통과 시 Unity↔브리지↔PX4 양방향 루프 + sink 라우팅 동작 증명.
    ///
    /// 실행: Unity -batchmode -nographics -projectPath &lt;proj&gt;
    ///        -executeMethod DroneSim.SITL.EditorTools.SitlPlayCheck.Run -logFile -
    /// (-quit 금지: Play 를 update 콜백으로 운전하고 끝에서 EditorApplication.Exit.)
    /// </summary>
    public static class SitlPlayCheck
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";

        static double _t0, _phaseT;
        static int _phase;
        static MavlinkFlightModel _mav;
        static float _startRelAlt;
        static double _startLat, _startLon;
        static bool _finishing, _result;

        // EditorSettings 복원용.
        static bool _prevEnabled;
        static EnterPlayModeOptions _prevOptions;

        public static void Run()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Play 진입 시 도메인/씬 리로드를 꺼야 static 상태 + update 구독이 유지된다.
            _prevEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _prevOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions =
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            _t0 = EditorApplication.timeSinceStartup;
            _phase = 0;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
            Debug.Log("[PlayCheck] Play 진입 요청 (도메인 리로드 off)");
        }

        static double Now => EditorApplication.timeSinceStartup;

        static void Tick()
        {
            if (Now - _t0 > 90) { Finish(false, "absolute timeout 90s"); return; }
            if (!EditorApplication.isPlaying) return;

            switch (_phase)
            {
                case 0: // 스폰 + 텔레메트리 대기
                    var a = DroneRegistry.Get("drone_sitl_1");
                    if (a != null && a.Active is MavlinkFlightModel m && m.hasTelemetry)
                    {
                        _mav = m;
                        _startRelAlt = (float)m.curRelAlt;
                        _startLat = m.curLat; _startLon = m.curLon;
                        Debug.Log($"[PlayCheck] 텔레 OK: mode={a.CurrentMode} posUnity={m.PositionUnity} " +
                                  $"lla=({m.curLat:F6},{m.curLon:F6}) rel_alt={m.curRelAlt:F1} armed={m.armed}");
                        // 스폰 위치가 인천 앵커 부근인지 (lat/lon 근접) 확인.
                        if (System.Math.Abs(m.curLat - 37.384312) > 0.01 ||
                            System.Math.Abs(m.curLon - 126.655307) > 0.01)
                            Debug.LogWarning($"[PlayCheck] 경고: 스폰 LLA 가 인천 앵커와 멀다 ({m.curLat:F4},{m.curLon:F4})");
                        _phase = 1; _phaseT = Now;
                    }
                    else if (Now - _t0 > 25)
                        Finish(false, "drone_sitl_1 텔레메트리 미수신 (브리지/SITL 확인)");
                    break;

                case 1: // RTS 명령 경로: SetWaypoint → sink → 브리지 goto (auto-takeoff 유발)
                    {
                        var p = _mav.PositionEnu;
                        // 현 위치에서 동/북 40 m, 고도 10 m 목표.
                        FlightCommands.SetWaypoint("drone_sitl_1", new N.Vector3(p.X + 40f, p.Y + 40f, 10f));
                        Debug.Log("[PlayCheck] FlightCommands.SetWaypoint 발행 (sink→브리지 goto, auto-takeoff 기대)");
                        _phase = 2; _phaseT = Now;
                    }
                    break;

                case 2: // 고도 상승 + 이동 반영 대기
                    float dRel = (float)_mav.curRelAlt - _startRelAlt;
                    double dLat = System.Math.Abs(_mav.curLat - _startLat);
                    double dLon = System.Math.Abs(_mav.curLon - _startLon);
                    if (dRel > 2f && (dLat > 5e-5 || dLon > 5e-5))
                    {
                        Debug.Log($"[PlayCheck] 반영 확인: rel_alt {_startRelAlt:F1}→{_mav.curRelAlt:F1} " +
                                  $"lla=({_mav.curLat:F6},{_mav.curLon:F6}) posUnity={_mav.PositionUnity}");
                        // 자세/속도 텔레메트리도 흐르는지 확인 (기계제어 검증).
                        Debug.Log($"[PlayCheck] 자세/속도: roll={_mav.rollDeg:F1}° pitch={_mav.pitchDeg:F1}° " +
                                  $"yaw={_mav.yawDeg:F1}° vN={_mav.velN:F2} vE={_mav.velE:F2} vD={_mav.velD:F2} " +
                                  $"groundSpeed={_mav.groundSpeed:F2} m/s  rotUnity={_mav.RotationUnity.eulerAngles}");
                        _mav.Land();
                        Finish(true, null);
                    }
                    else if (Now - _phaseT > 45)
                        Finish(false, $"이동/상승 미반영 (dRel={dRel:F1} dLat={dLat:F6} dLon={dLon:F6})");
                    break;
            }
        }

        static void Finish(bool ok, string fail)
        {
            if (_finishing) return;
            _finishing = true; _result = ok;
            if (ok) Debug.Log("[PlayCheck] PASS: 스폰+텔레메트리+SetWaypoint(sink)+takeoff/goto 반영 — Unity↔브리지↔PX4 루프 정상");
            else Debug.LogError($"[PlayCheck] FAIL: {fail}");

            EditorApplication.update -= Tick;
            // EditorSettings 원복.
            EditorSettings.enterPlayModeOptionsEnabled = _prevEnabled;
            EditorSettings.enterPlayModeOptions = _prevOptions;

            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            EditorApplication.update += ExitTick;
        }

        static void ExitTick()
        {
            if (EditorApplication.isPlaying) return;   // 정지 완료 대기
            EditorApplication.update -= ExitTick;
            EditorApplication.Exit(_result ? 0 : 1);
        }
    }
}
