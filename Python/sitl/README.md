# PX4 SITL ↔ Unity 지휘 시스템 (task/px4-sitl-bridge)

Gazebo + PX4 SITL 을 "실제 드론(모사체)"으로 띄워 Unity 자체 6-DOF 시뮬과 **독립** 동작시키고,
Unity 의 기존 RTS식 C2 명령(SetWaypoint / Path / StopAndHover)으로 이 SITL 드론을 조종한다.
비행 제어는 **PX4 펌웨어**가 소유 — Unity 는 명령 송신 + 텔레메트리 시각화만 한다.

```
[Gazebo 물리/센서] ─uORB─ [PX4 SITL] ─MAVLink udp:14540─ [px4_bridge.py / MAVSDK]
     ─UDP JSON─ [Unity MavlinkFlightModel : IFlightModel]
```

## 버전 고정

| 구성요소 | 버전 |
|---|---|
| PX4-Autopilot | **v1.16.0** (`make px4_sitl gz_x500`) |
| Gazebo | **Harmonic (gz)** — Ubuntu 22.04 기준, `ubuntu.sh --no-nuttx` 가 자동 설치 |
| MAVSDK-Python | **3.x** (`pip install "mavsdk>=3,<4"`) |
| MAVLink 외부 API 포트 | **udp:14540** (MAVSDK/브리지), GCS **udp:14550** |
| 홈/원점 | 인천 앵커 **37.384312 / 126.655307** (`PX4_HOME_LAT/LON/ALT`) |

## 포트

| 포트 | 방향 | 용도 |
|---|---|---|
| udp 14540 | PX4 ↔ 브리지 | MAVLink 외부 API |
| udp 14550 | PX4 → GCS | QGroundControl 등 (선택) |
| udp **9871** | 브리지 → Unity | 텔레메트리 push (LLA/자세/속도/armed/mode) |
| udp **9872** | Unity → 브리지 | 명령 (goto/mission/hold/arm/takeoff/land) |

> 9870 은 기존 검출 파이프라인(ProjectionUdpReceiver) 전용이라 겹치지 않게 9871/9872 사용.

## 시스템 요구치

- Ubuntu 22.04+ (Gazebo Harmonic 은 20.04 이하 불가).
- 디스크: 최초 빌드 시 PX4 소스+의존성 수 GB, 이미지 ~6–8 GB.
- RAM: SITL+Gazebo 헤드리스 ~2–4 GB. GUI 면 더.
- 헤드리스(`HEADLESS=1`) 권장 — GPU/OpenGL 불필요, 자원 절약. GUI 는 X11 필요.

## 사용법

```bash
# 1) 이미지 빌드 (최초 20~40분, 수 GB 다운로드)
./Python/sitl/run_sitl.sh build

# 2) SITL 기동 (헤드리스)
./Python/sitl/run_sitl.sh
#    GUI 로 보려면:
./Python/sitl/run_sitl.sh gui

# 3) 레이어 1 검증 — 브리지 없이 단독 arm/takeoff/land
#    (다른 터미널에서, 호스트에 mavsdk 설치 후)
pip install "mavsdk>=3,<4"
./Python/sitl/run_sitl.sh smoke
#    기대 출력: "[smoke] PASS: arm/takeoff/land 정상."
```

## 좌표 정합 (Unity 맵 위에 SITL 드론 표시)

PX4 SITL 홈을 인천 앵커로 고정했으므로, 텔레메트리 LLA → Unity world 변환은
검출 파이프라인과 **동일한** GPS→world 변환(`ProjectionUdpReceiver.GpsToWorld`,
`CubeGPSDisplay` 미러)을 재사용한다. → SITL 드론이 인천 맵 좌표계 위에 정확히 배치된다.

## gz 원점 주의 (인천 정합)

gz Harmonic 에서는 `PX4_HOME_LAT/LON` 이 world 의 `<spherical_coordinates>` 를 **override 하지
못한다**(경험적 확인 — 문서와 다름). 그래서 원점을 인천으로 박은 커스텀 world `worlds/incheon.sdf`
를 Dockerfile 에서 PX4 worlds 디렉터리로 COPY 하고, `PX4_GZ_WORLD=incheon` 으로 선택한다.
→ 텔레메트리 home = (37.384312, 126.655307) 로 CubeGPSDisplay.anchor 와 일치.

## 검증 상태 (2026-06-03)

- [x] **레이어 1 — SITL 환경**: 이미지 빌드(px4 v1.16 + gz Harmonic 8.12) → 단독 기동 →
      `sitl_smoketest.py` **PASS** (arm/takeoff/land). 빌드 전용 타깃은 `make px4_sitl_default`
      (`make px4_sitl gz_x500` 은 빌드 후 시뮬을 곧장 띄워 Docker 빌드 부적합).
- [x] **레이어 2 — 브리지** `px4_bridge.py`: `bridge_test.py` **PASS** (Unity 없이 명령 송신 →
      goto 이동 → 텔레메트리 수신). home 인천 정렬 확인. MAVSDK 3.x 연결 = `udpin://0.0.0.0:14540`.
- [ ] **레이어 3 — Unity**: 스크립트 작성 완료, 에디터 부착·플레이 검증 대기 (아래 가이드).

## 레이어 3 — Unity 부착 가이드 (씬 직접 편집 금지 → 인스펙터 수동)

스크립트(모두 작성됨):
- `Unity/Assets/SITL/MavlinkFlightModel.cs` — IFlightModel + IFlightCommandSink (텔레→읽기전용 위치, 명령→브리지).
- `Unity/Assets/SITL/SitlDroneSpawner.cs` — SITL 드론 런타임 생성 (`drone_sitl_N`, External 모드).
- `Unity/Assets/SITL/SitlControlInput.cs` — arm(U)/takeoff(K)/land(L)/RTL(G) 키.
- FlightAdapter/C2 측: IFlightModel 확장 + DroneAgent.External + 명령/표시 라우팅(`agent.Active`).

부착 절차 (Play 정지 상태에서):
1. 빈 GameObject 생성 → `SitlDroneSpawner` 추가. (포트 기본 9871/9872, bridgeHost 127.0.0.1)
2. 같은(또는 다른) GameObject 에 `SitlControlInput` 추가.
3. 씬에 `CubeGPSDisplay`(anchorObject/altitudeReferenceBuilding 지정됨)가 있어야 좌표 정합됨 — 기존 씬엔 이미 있음.
4. Play 시작 → SITL 드론(주황) 1대가 인천 맵 위 생성, 텔레메트리로 위치 갱신.

검증 순서:
```bash
./Python/sitl/run_sitl.sh         # 터미널1: SITL
docker compose -f docker-compose.sitl.yml up px4_bridge   # 터미널2: 브리지
# Unity Play → 미니맵/전략뷰에서 SITL 드론 우클릭 SetWaypoint → Gazebo 이동 → Unity 반영
# U=arm, K=takeoff, L=land, G=RTL. 시뮬 드론(흰색)과 동시 표시·각각 제어.
# 브리지/SITL 종료 → 수 초 뒤 SITL 마커 stale (앱 유지).
```

## 제약

- Unity 와 Gazebo 는 별도 프로세스 — 한쪽이 죽어도 다른 쪽 무영향.
- `network_mode: host` (Linux) — 컨테이너 간 MAVLink 타깃 라우팅 문제 회피.
- 씬(SampleScene.unity) 직접 편집 금지. Unity 신규 컴포넌트는 스크립트만, 인스펙터 부착은 수동.
