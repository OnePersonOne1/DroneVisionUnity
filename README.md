# DroneVisionUnity

드론·카메라 영상을 RF-DETR 로 객체 검출하고, 검출 결과를 인천 3D 지도 위 GPS 좌표에 투영해 시각화하는 파이프라인. Python 이 추론과 좌표 계산을 담당하고 Unity 가 지도 렌더링과 인터랙션을 담당한다. Unity 측에는 PhysX 가 아닌 자체 6-DOF 비행 모델로 동작하는 드론 시뮬레이션도 포함된다.

좌표 변환 규약 상세는 [`Unity/PIPELINE_MERGE_NOTES.md`](Unity/PIPELINE_MERGE_NOTES.md), 키 매핑은 [`조작법.md`](조작법.md) 참고.

## 구성 요소

| 모듈 | 설명 |
|---|---|
| `Unity/` | Unity 6000.4.0f1 (URP). 인천 3D 메시·GPS 보정·시각화·드론 시뮬레이션 |
| `Python/` | RF-DETR 추론, 픽셀→방향 벡터 계산, UDP 송신, CSV 재생 |
| `Python/info_service/` | OSM 역지오코딩 + GIS 건물 정보 + Kakao + Ollama LLM 백엔드 |
| `Models/` | RF-DETR 가중치 (Git LFS 또는 외부 스토리지) |
| `output/<session>/` | 캡처 세션 데이터 (image / sensor / detect / projection) |

## 데이터 흐름

```
[카메라/폰]                              ┌────────────────────────────┐
   영상 + GPS/IMU                         │  Unity (URP)               │
        │                                 │  ProjectionUdpReceiver     │
        ▼                                 │   ↓ GpsTeleport 오버라이드 │
  Python/IP_webcam.py ── UDP JSON ───────▶│   ↓ GPS → 월드 변환        │
   (RF-DETR + 센서 융합)                  │   ↓ Physics.Raycast        │
        │                                 │   ↓ 클래스별 마커 spawn    │
        ▼ (오프라인)                      │  ProjectionReplay (CSV)    │
  projection_pipeline.py                  └────────────────────────────┘
        ▼
  output/<session>/projection/*.csv ───▶ replay_offline.py ── UDP ──┘
```

UDP/CSV 페이로드: `class_name, class_id, confidence, u, v, cam_lat, cam_lng, cam_alt, direction[3]`. 방향 벡터는 카메라 자세 기반이라 좌표계와 독립적이다.

## 요구사항

- Unity 6000.4.0f1 (URP)
- Python 3.11
  - `rfdetr`, `supervision`, `opencv-python`, `numpy`, `pillow`
  - `info_service/` 사용 시: `fastapi`, `uvicorn`, `shapely`, `requests`
- RF-DETR 가중치 `Models/checkpoint_best_total.pth` (약 485 MB, 별도 다운로드 — 아래 "자산 다운로드" 참고)
- (라이브 모드 시) IP Webcam 등 영상·센서 송신 소스
- (LLM 기능 사용 시) 로컬 Ollama 서버 + 모델

## Unity 설정

1. Unity Hub 에서 `Unity/` 폴더를 6000.4.0f1 로 연다. 첫 실행 시 `Library/` 가 자동 생성된다.
2. `Assets/Scenes/SampleScene.unity` 를 연다. 드론·카메라·맵·HUD 가 모두 구성돼 있다.
3. 추론 마커 표시가 필요하면 빈 GameObject 에 `ProjectionUdpReceiver` 컴포넌트를 추가한다. 씬에 `CubeGPSDisplay` 가 있으면 보정 값(앵커 위경도, 스케일, 기준 건물)이 자동 동기화된다.
4. CSV 오프라인 재생이 필요하면 별도 GameObject 에 `ProjectionReplay` 를 추가하고 `Projection Csv Path` 에 CSV 파일 **절대 경로**를 지정한다. Unity 의 작업 디렉토리는 프로젝트 루트가 아니므로 상대 경로는 동작하지 않는다. 예: `/home/user/DroneVisionUnity/output/20260529_122735/projection/projection_20260529_122735.csv` (Windows: `C:\Users\...\projection_*.csv`). 파일을 찾지 못하면 콘솔에 `[ProjectionReplay] CSV not found: '...'` 가 뜬다.
5. GPS 순간이동(텔레포트) 기능은 `GpsTeleport` 컴포넌트로 사용한다. `Y` 키로 Cube 현재 GPS 복사, `T` 키로 오버라이드 토글.

### 드론 시뮬레이션 활성화

자체 6-DOF 드론(`drone_sim_obj_n`)을 사용하려면:

1. `Assets ▸ Create ▸ Drone ▸ FlightConfig` 로 `FlightConfig` ScriptableObject 를 만든다.
2. 빈 GameObject 에 다음 컴포넌트를 부착한다.
   - `DroneSpawner` (Config 슬롯에 위 SO, `Initial Mode = Navigate` 권장)
   - `FlightModeSwitcher`
   - `MultiDisplayCoordinator`
3. 또 다른 GameObject 에 `StrategicViewBootstrap` + `StrategicCameraController` + `StrategicCommandInput` + `StrategicViewHud` 를 부착한다.
4. Main Camera 의 `Target Display = Display 2`, FP Camera 의 `Target Display = Display 1` 로 설정한다 (`MultiDisplayCoordinator` 가 자동 적용).
5. `finally.fbx` 자식 메시에 Mesh Collider 가 부착돼 있는지 확인한다. 없으면 Project 창의 fbx 를 Inspector ▸ Model ▸ Add Colliders 옵션 활성화 후 다시 드래그한다.

Play 시 Cube 앞 5 m 에 십자 막대 드론이 spawn 된다. 추가 spawn 은 백쿼트(`` ` ``), 모드 토글은 F10.

## 자산 다운로드

GitHub 저장소에는 100 MB 이상의 RF-DETR 가중치 및 캡처 세션이 포함되지 않는다. 아래 링크에서 받아 명시된 경로에 둔다.

| 파일 | 다운로드 | 저장 위치 |
|---|---|---|
| RF-DETR 가중치 (~485 MB) | https://drive.google.com/file/d/1hoCFljPKaiiLfD9MdyfQ1XfUVvRozeyT/view?usp=drive_link | `Models/checkpoint_best_total.pth` |
| 데모 캡처 세션 zip (~145 MB) | https://drive.google.com/file/d/1sTiMGBD5R_6A1fty9yYbVERDCs7U_Hkx/view?usp=drive_link | 받아서 `output/` 에 풀기 (`output/20260529_122735/` 가 됨) |

## Python 실행

```bash
cd Python
```

촬영·추론 분리 (권장):

```bash
python3 capture.py                                      # 촬영·저장만
python3 infer.py --session ../output/<session>          # 오프라인 추론
python3 infer.py --all                                   # output/* 전체 일괄
python3 projection_pipeline.py --session ../output/<session>
python3 replay_offline.py     --session ../output/<session> --rate 5 --loop
```

올인원 라이브:

```bash
python3 IP_webcam.py    # 폰 연결 시 라이브, 미연결 시 자동 오프라인 모드
```

세 스크립트는 같은 함수(`run_live_capture`, `infer_session`, `load_rfdetr_model`)를 공유한다. `IP_webcam.py` 상단의 `IP_ADDRESS`, `PORT`, `UDP_*` 상수를 환경에 맞게 수정한다.

### PX4 SITL + Gazebo + QGroundControl (`docker-compose.sitl.yml`)

Gazebo Harmonic + PX4 v1.16 SITL 을 별 컨테이너로 띄워 "실 드론(모사체)" 로 사용. Unity 의 자체 6-DOF 시뮬과 독립 동작, 기존 RTS 식 C2 명령(`SetWaypoint`/`SetPath`/`StopAndHover`)으로 SITL 드론 조종.

```bash
# 첫 빌드 (~30분, 이후 hub pull 권장)
docker compose -f docker-compose.sitl.yml build px4_sitl
# 또는 push 된 이미지 사용
docker compose -f docker-compose.sitl.yml pull

# 기동 (SITL + 브리지)
docker compose -f docker-compose.sitl.yml up -d px4_sitl px4_bridge

# 단독 검증 (선택)
./Python/sitl/run_sitl.sh smoke
python3 Python/sitl/bridge_test.py
```

포트:
- `14540` MAVSDK / px4_bridge — PX4 외부 API.
- `14550` GCS — QGroundControl 자동 발견 (Dockerfile patch 로 `udp_gcs_port_local` 변경).
- `9871` / `9872` 브리지 ↔ Unity.

QGroundControl AppImage 호스트 실행 → 좌상단 `Q` → Application Settings → Comm Links → Add (UDP, Listening Port 14540, Target `127.0.0.1:14540`) → Connect. Fly View 가 default. 자세한 작업 흐름은 `조작법.md` 의 SITL 섹션 참고.

#### 기계제어 분석 도구 (포트폴리오용)

```bash
# step response 자동 측정 (CSV + PNG + 메트릭)
python3 Python/sitl/step_response.py --axis x --amplitude 10
# QGC 동시 사용 시:
python3 Python/sitl/step_response.py --axis x --amplitude 10 --conn udpout://127.0.0.1:14580

# 게인 sweep 비교
python3 Python/sitl/pid_sweep.py --param MC_ROLLRATE_P \
    --values 0.10,0.15,0.20,0.25 --axis x
```

산출:
- `Python/sitl/step_responses/step_<axis>_<amp>m_<ts>.{csv,png}` — overshoot/rise(10→90)/settling(±5%)/ss_error 메트릭 자동 계산.
- `Python/sitl/pid_sweep/sweep_<param>_<ts>.png` + `_metrics.csv` — 4 게인 overlay + 표.

### 건물 정보·상황 판단 백엔드 (`info_server.py`)

Unity 의 건물 카드(B 키) + 화재 브리핑(`;`/`'` 키, F12 패널) 의 백엔드. 로컬 OSM·GIS·119안전센터 CSV 를 로드하고, 선택적으로 Kakao Local API 와 Ollama LLM 을 호출한다.

```bash
cd Python/info_service
# 의존성 1회: pip install -r requirements.txt
# Kakao 키는 env var 로만 (코드/파일에 하드코딩 금지)
KAKAO_REST_API_KEY=<발급키> python3 info_server.py   # localhost:8077
```

키 없이도 동작하지만 startup 경고 후 OSM/GIS 만으로 enrich → '이름 미상 건물' 비중이 늘어난다. `docker compose up` 으로 컨테이너 환경이면 `.env` (gitignored) 에서 `KAKAO_REST_API_KEY=...` 한 줄 두는 게 표준 (자세한 건 `.env.example` 참고).

선택적 LLM (브리핑 합성 전용): `ollama serve` 가 떠 있고 모델(`qwen2.5:14b` 기본)이 로드돼 있으면 브리핑이 자연스러운 한국어로 합성된다. 안 떠 있어도 동일한 정형 fact 로부터 template 브리핑이 즉시 반환된다. `INFO_LLM_TIMEOUT` 으로 응답 대기 시간 조정 (기본 15s).

엔드포인트:
- `POST /building_info` `{lat,lng}` → 정형 8필드 카드.
- `POST /prefetch` `{center_lat,center_lng,radius_m}` → 반경 건물 카드 백그라운드 캐싱.
- `POST /assess` `{fires:[{lat,lng,cls}], sim_time_iso, radius_m}` → 룰 기반 risk/spreading + 룰 기반 occupancy + top-5 119 안전센터 + 룰 기반 권장 차량 + LLM 또는 template briefing.
- `GET /health` → 데이터·LLM·Kakao 가용성 상태.

### 실시간 동기화 모드 (선택)

기본 파이프라인은 **검출 0개 프레임을 송신하지 않는다**(안전·기존 호환). 캡처 타임라인의 공백까지 Unity 가 그대로 받아야 하면 다음을 사용한다.

- **오프라인 재생**: `python3 replay_offline.py --realtime`
  - sensor CSV 의 모든 캡처 `frame_id` 를 원본 `sys_time` 간격으로 송신.
  - 검출 0개 프레임은 `detections=[]` + 카메라 fwd/up(IMU quat 추정) 만 담아 송신.
- **라이브** (`IP_webcam.py`): 파일 상단 `UDP_REALTIME_MODE = True` (기본). `False` 로 두면 기존처럼 검출 있을 때만 송신.

## 조작 키 요약

Cube (지도 시점 / GPS HUD):

| 키 | 동작 |
|---|---|
| W A S D / ↑↓ / ←→ / R F | 수평 이동 · 상승하강 · Yaw · Pitch |
| LMB 드래그 | 마우스 룩 (yaw + pitch) |
| V | 1인칭(FP) 토글 |
| T / Y | GPS 텔레포트 오버라이드 / 현재 GPS 복사 |
| I / O / C | 추론 표시 / 디버그 광선 / 디버그 마커 정리 |
| B / N / H / X | 건물 정보 조회 / dwell 모드 / 하이라이트 / 선택 해제 |
| `\` | 건물 정보 조회 (Display 4 sensor camera 기준) |
| P | GPS HUD 토글 |

상황 판단 — 모의 화재 + LLM 브리핑 (`/assess`):

| 키 | 동작 |
|---|---|
| `;` | FP 중앙 raycast 점에 **fire 마커** 주입 (`fire_region`, 기본 영구) |
| `'` | 동일 위치에 **smoke 마커** 주입 (`smoke_region`) |
| Del | 모든 모의 화재 일괄 제거 |
| Z | **SimClock 패널** 토글 — 시간대 프리셋(08/12/18/02) + 수동 시:분 |
| F12 / Shift+F12 | **Briefing 패널** 토글 / 크기 리셋 |
| F11 | 수동 즉시 /assess 호출 (개발용) |
| `[` | FP(Display 1) ↔ Sensor(Display 4) 카메라 교환 — 3 모니터 환경용 |

모의 화재 한 건이라도 추가되면 즉시 /assess 호출 → BriefingPanel 에 risk · 시간대 · top-5 119안전센터 · 룰 기반 권장 차량 · 한국어 브리핑 표시. RF-DETR 검출의 `fire_region`/`smoke_region` 마커도 자동 합산 (FireSim 마커와 동일하게 평가됨).

드론 시뮬레이션 (`drone_sim_obj_n`, n = 1, 2, 3, …):

| 입력 | 동작 |
|---|---|
| `` ` `` | 드론 추가 spawn |
| F10 | 비행 모드 토글 (HighFidelity ↔ Arcade) |
| LMB 클릭 (드론 마커) | 단일 선택 (Shift = 추가/제거) |
| LMB 드래그 (전략 뷰 또는 미니맵) | 박스 셀렉트 |
| LMB 단일 클릭 (전략 뷰 또는 미니맵) | 선택 해제 → 전체 대상 |
| Ctrl + LMB 드래그 (전략 뷰 또는 미니맵) | 마우스 궤적 → SetPath |
| RMB | 선택군 SetWaypoint (Shift = QueueWaypoint) |
| **ESC** | 선택 해제 (전역, 어느 디스플레이에서나) |
| Space | 전체 드론 정지 (큐 비움 + hover) |
| 화살표 / MMB 드래그 / 휠 | 전략 카메라 팬·줌 |
| Home / Backspace | 전략 카메라 Cube 위로 재중심 |
| M / Shift+M | 미니맵 토글 / 기본 크기로 리셋 |

PX4 SITL 드론 (`drone_sitl_N`, 주황 — `docker-compose.sitl.yml` 떠 있을 때):

| 입력 | 동작 |
|---|---|
| RMB / Ctrl+드래그 / Shift+RMB | 기존 SetWaypoint / SetPath / Queue 와 동일 (자동 라우팅) |
| Numpad7 / Numpad8 / Numpad9 / Numpad6 | ARM / TAKEOFF (5m) / LAND / RTL (`SitlControlInput`) |
| F9 | SITL 드론 추가 spawn (현재 1대 전제) |

전체 키 매핑은 [`조작법.md`](조작법.md) 참조.

## 멀티 디스플레이

| 디스플레이 | 내용 |
|---|---|
| Display 1 | 1인칭(FP) 카메라 + HUD/UI |
| Display 2 | 3인칭(Main) 카메라 |
| Display 3 | 전략 보기 (탑다운 직교, 드론·타겟·검출 마커·범례·스케일 바·AGL 라벨) |
| Display 4 | 센서 시점 뷰 (`SensorViewMode`, 전용 카메라) — `U` 토글, `F8` FIXED/FREE, `F7` roll 180° 보정 |

`MultiDisplayCoordinator` 가 Display 1/2/3 을 활성화하고 매 프레임 카메라 enabled / targetDisplay 를 강제한다. `SensorViewMode` 는 Display 4 카메라를 자체 생성·활성화한다. Editor 단일 모니터 환경에서는 Game view 의 디스플레이 탭으로 전환해 확인한다.

## 추가 시각화 (월드 공간)

- **드론 nametag** (`DroneNameTagManager`): 각 드론 머리 위 TextMeshPro 3D 라벨 `{N}\nAGL: {m} m` (N = 1, 2, 3, …), 카메라 빌보드. 모든 디스플레이 공통.
- **웨이포인트 시각화** (`WaypointWorldVisualizer`): 현재 타겟·큐 wp 에 **반투명 무한 높이 원기둥** + 드론→타겟→큐 경로 라인. 원기둥은 선택 노랑·비선택 시안. 추가로 **지면 raycast 한 지점에 큰 반투명 디스크** (`showGroundDiscs`, 선택 노랑·비선택 주황) — RTS 스타일 도착 마커. URP 투명 머티리얼 자동 생성.
- **선택 드론 강조** (`SelectedDroneHighlighter`): 명시 선택된 드론 (`selectedDroneIds` 에 들어있는 ID) 마다 **불투명 초록 wireframe 큐브** 를 두름. 1인칭/3인칭/전략 모든 카메라에서 가시. 선택 0개(전체 명령 모드) 일 때는 표시 X. 씬에 없으면 자동 부트스트랩(`RuntimeInitializeOnLoadMethod`).
- **AGL(지상고도)** (`GroundAgl`): 드론 위치에서 아래 방향 `Physics.Raycast`(terrain single-side 시 backface 폴백)로 지면까지 거리 / `unityUnitsPerMeter`. 라벨·전략 뷰 모두 동일 소스.

## 명령 고도 프리셋 (전략 뷰)

`StrategicCommandInput.commandAltitudeAGLMeters` 로 `SetWaypoint/SetPath` 의 wp 고도를 클릭한 지면 위 AGL 미터로 띄울 수 있다. `0` 이면 드론 현재 고도 유지.

- `PgUp` / `PgDn` : ±5 m (`PgDn` 은 0 하한)
- `End` : 0 (= 지면 그대로)
- `F1`–`F5` : 10 / 30 / 50 / 100 / 200 m 프리셋 (숫자 `1`–`9` 는 RTS 컨트롤 그룹용으로 비웠음)
- `Ctrl + 1`–`9` (또는 `Alt + 1`–`9`) 그룹 저장, `1`–`9` 단독 그룹 호출 (StarCraft 부대지정 방식). Editor 에서 Ctrl+digit 가 윈도우 단축키로 가로채면 Alt 모디파이어 사용.
- `Space` : 전체 드론 정지 (큐 비움 + 현 자리 hover, RTS 의 Stop=S 대응).
- `ESC` : 선택 해제 → 전체 드론 대상 모드 복귀 (어느 디스플레이에서나).

## HUD 런타임 튜너

`F9` 로 `StrategicViewHud` 의 IMGUI 슬라이더 창을 열어 debug HUD/범례/스케일 바의 크기·여백·폰트·알파 를 플레이 중 직접 조절한다. 변경값은 즉시 반영되며 매 프레임 백그라운드 박스가 다시 그려진다.

## 드론 시뮬레이션 어셈블리 구조

```
Unity/Assets/Flight/
├── Core/      (FlightCore.asmdef, noEngineReferences=true)
│   ├── Frames.cs                    ENU 규약, g=(0,0,-9.81)
│   ├── State6Dof.cs                 p, v, q, ω
│   ├── DroneParams.cs               m, J, k_f, k_m, arm, drag
│   ├── MotorMixer.cs                X-config 모터 믹싱
│   ├── RigidBodyDynamics.cs         Newton-Euler EOM
│   ├── Rk4Integrator.cs             고정 dt RK4
│   ├── WaypointQueue.cs             sphere-of-acceptance + bearing test
│   └── Controllers/
│       ├── RatePid.cs               inner-loop
│       ├── AttitudeController.cs    mid-loop
│       └── PositionController.cs    outer-loop
└── Unity/     (FlightAdapter.asmdef)
    ├── FrameConversion.cs           ENU↔Unity 단일 변환
    ├── IFlightModel.cs
    ├── HighFidelityFlightModel.cs   EOM 어댑터, FixedUpdate
    ├── ArcadeFlightModel.cs         비교용 단순 모드
    ├── DroneAgent.cs                ID + 두 모델 + waypoint queue
    ├── DroneRegistry.cs             ID 발급 + 전역 컬렉션
    ├── DroneSpawner.cs              런타임 시각화 생성
    ├── FlightCommands.cs            명령 진입점 (Set/Queue/Path/Stop)
    ├── FlightModeSwitcher.cs        모드 토글
    ├── FlightTestConsole.cs         IMGUI 콘솔
    └── FlightConfig.cs              ScriptableObject

Unity/Assets/C2/
├── MultiDisplayCoordinator.cs       Display 1/2/3 분담
└── StrategicView/
    ├── StrategicViewBootstrap.cs    Display 3 카메라 + Canvas
    ├── StrategicCameraController.cs 팬/줌/재중심
    ├── StrategicCommandInput.cs     입력 라우팅 + 박스 셀렉트
    └── StrategicViewHud.cs          마커·라벨·범례·디버그 HUD

Unity/Assets/Tests/EditMode/         (NUnit)
```

좌표계는 ENU(East-North-Up) + RHC, body frame 은 FLU, 단위는 SI. PX4·Gazebo·ROS2 와 호환되는 규약을 유지한다. 명령 API 는 `FlightCommands` 한 곳에서 정의되며, UI 와 외부 스크립트는 이 정적 API 만 호출한다.

## 좌표계 규약

- Unity world: `X = East`, `Y = Up`, `Z = North`.
- GPS↔world 변환은 `CubeGPSDisplay` 가 단일 진실 공급원이다. 앵커 위경도, `horizontalScaleFactor`, `altitudeReferenceBuilding`, `referenceBuildingHeightMeters` 가 직렬화 필드로 보존된다.
- 맵 메시는 앵커를 기준으로 사전 회전되어 있으므로 GPS↔world 변환은 회전을 적용하지 않는다.
- 드론 시뮬레이션 코어는 SI 미터를 사용하며, Unity world 와의 스케일 변환은 어댑터 경계 한 곳(`FrameConversion`)에서만 수행한다.

## 씬 / 빌드

- 메인 씬: `Assets/Scenes/SampleScene.unity`. Build Settings 에 등록되어 있다.
- Play 모드 변경은 휘발성이다. Inspector 보정 값을 영구 반영하려면 Play 정지 상태에서 수정 후 씬을 저장한다.
- `TextMesh Pro/Examples & Extras/` 의 샘플 씬은 파이프라인과 무관하다.

## 알려진 제약

- RF-DETR 가중치는 Git LFS 또는 외부 스토리지를 통해 배포한다 (단일 파일 약 485 MB).
- `output/`, `Unity/Library/`, `Logs/`, `Temp/`, 백업 zip 등은 `.gitignore` 로 추적에서 제외된다.
- 드론 시뮬레이션의 PID 게인은 기본값에서 오버슈트가 관찰될 수 있다. `PositionController` 의 `Kd` 를 상황에 맞게 조정한다.
- 고도·스케일 관련 마커 위치가 어긋나면 `CubeGPSDisplay.altitudeReferenceBuilding` 슬롯에 메시 + Renderer 가 부착된 기준 건물이 지정돼 있는지 먼저 확인한다.

## 향후 목표

- **PX4 SITL 다중 드론**: 현재 1대 전제 (`MavlinkFlightModel` 이 udp:9871/9872 단독 bind, `px4_bridge.py` 가 단일 MAVSDK 연결). 다중 활성화하려면 (a) PX4 SITL 인스턴스를 별 포트(14541, 14542, …) 로 분리, (b) `px4_bridge.py` 를 N MAVSDK 다중화하고 텔레메트리에 `system_id` 부착, (c) Unity 측 9871 single-socket 을 sysid → DroneAgent 라우터로 재설계 필요. 시뮬 드론 (`drone_sim_obj_N`) 은 이미 다중 지원이라 시뮬 N + SITL 1 운영으로 우회 가능 — 추가 작업은 후속 task.
- **인천 anchor / horizontalScaleFactor stale 문서 정정**: 본 README/CLAUDE.md 의 일부 절은 옛 anchor(`37.384312`, scale `1`) 를 기준으로 작성됐으나, 실제 씬의 `CubeGPSDisplay` 는 `lat 37.384049721493724 / lng 126.65615666326806 / horizontalScaleFactor 9.715` 로 정정됨 (`worlds/incheon.sdf` 도 그에 맞춰져 있음). 문서 정합 정리는 후속.
- **음성·LLM 명령 입력**: 현재 LLM 은 `/assess` 표시 전용. 음성/자연어로 SetWaypoint 같은 명령 트리거는 미지원 — 안전 우선 (LLM 출력이 제어 경로에 직결되지 않음).
- **빌드 자동화**: 현재 검증은 Editor Play 또는 `SitlPlayCheck.Run` 의 xvfb 헤드리스. CI 통합 (GitHub Actions 등) 미설정.

## 라이선스

(추가 예정)
