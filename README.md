# DroneVisionUnity

드론/카메라 영상 → RF-DETR 객체 검출 → **인천 3D 맵 위 GPS 좌표로 투영·표시**하는
파이프라인. Python(추론·투영)과 Unity(맵·시각화)로 구성된다.

> **현재 구조 (2026-05-16 병합 이후)**
> - `Unity/` = **팀원의 인천 프로젝트**(URP, Unity **6000.4.0f1**)가 정본.
>   GPS 정렬은 팀원이 담당·검증한 `GPSEncoder` 모델 + 인천 씬 보정값을 사용.
> - 구 HDRP 프로젝트는 `Unity_HDRP_old/` 에 보존(삭제 안 함).
>   `GeoAnchor.cs`(구 ECEF 모델)는 **더 이상 사용하지 않음**.
> - Python은 검출별로 **카메라 GPS(`cam_lat/cam_lng/cam_alt`)** 를 보내고,
>   Unity가 GPSEncoder 보정으로 월드 좌표로 변환한다.
> - 좌표/보정 상세: [`Unity/PIPELINE_MERGE_NOTES.md`](Unity/PIPELINE_MERGE_NOTES.md)
> - 조작 키: [`조작법.md`](조작법.md)

---

## 디렉터리 구조

```
DroneVisionUnity/
├── Unity/                              # Unity 프로젝트 루트 (= 인천 프로젝트, URP)
│   ├── Assets/
│   │   ├── Scenes/
│   │   │   └── SampleScene.unity       # ★ 메인 씬 (Build Settings 등록됨)
│   │   ├── CubeDroneController.cs      # 드론(Cube) 조작 (New Input System)
│   │   ├── DroneFollowCamera.cs        # 드론 추적 카메라
│   │   ├── CubeGPSDisplay.cs           # 드론 GPS HUD 표시
│   │   ├── UnityToGPSConverter.cs      # Unity→GPS 역변환(QGIS 출력용)
│   │   ├── GPSEncoder.cs               # ★ GPS↔미터 변환 엔진(정본)
│   │   ├── GPSAnchorPlacer.cs / GPSObjectPlacer.cs   # GPS 수동 배치 유틸
│   │   ├── ProjectionUdpReceiver.cs    # ★ 라이브 UDP 수신 + Raycast 마커 (+추론 on/off)
│   │   ├── ProjectionReplay.cs         # ★ projection CSV 직접 재생
│   │   ├── GpsTeleport.cs              # ★ GPS 순간이동(오버라이드) 토글
│   │   ├── finally.fbx                 # 인천 3D 맵 메시 (★ 2026-05-25 신 버전, 씬이 사용)
│   │   ├── Untitled.fbx                # 구 인천 맵 메시 (참조용으로 잔존)
│   │   ├── Settings/                   # URP 렌더 설정
│   │   └── TextMesh Pro/               # 패키지 샘플(무관, 무시)
│   ├── Packages/  ProjectSettings/  UserSettings/
│   ├── Unity.slnx                       # Unity가 생성한 솔루션 (인천.slnx는 정리 권장)
│   └── PIPELINE_MERGE_NOTES.md         # 좌표 변환 규약·수동 작업 상세
│
├── Python/
│   ├── geo_projection.py        # 픽셀→Unity축 방향벡터 계산 핵심(방향의 권위)
│   ├── capture.py               # ★ 촬영·저장 전용 (RF-DETR/UDP 없음, 경량)
│   ├── infer.py                 # ★ 저장 세션 경로 받아 RF-DETR 오프라인 추론
│   ├── IP_webcam.py             # 공용 로직 + 올인원 라이브(촬영+추론+UDP)
│   ├── projection_pipeline.py   # 오프라인 detect+sensor join → projection CSV
│   ├── udp_sender.py            # UDP JSON datagram 송신기
│   ├── replay_offline.py        # projection CSV → UDP 재생
│   └── visualize_projection.py  # matplotlib 사전 검증
│
├── Models/checkpoint_best_total.pth    # RF-DETR 가중치 (~485 MB)
├── output/<session>/                   # 캡처 세션(image/sensor/detect/projection)
├── Unity_HDRP_old/                     # 구 HDRP 프로젝트 백업(보존)
├── incheon_source_backup.zip           # 팀원 인천 프로젝트 원본 아카이브
├── docker-compose.yml
├── 조작법.md                           # 키/조작 레퍼런스
└── README.md                           # (this file)
```

---

## 데이터 흐름

```
[카메라/폰]                               ┌─────────────────────────────────────┐
   영상 + GPS/IMU                          │  Unity/  (인천 프로젝트, URP)        │
        │                                  │   ProjectionUdpReceiver             │
        ▼                                  │     │ (GpsTeleport ON이면 좌표 치환) │
  Python/IP_webcam.py  ── UDP JSON ───────►│     ▼ GPSEncoder: GPS→월드          │
   (RF-DETR + 센서융합)                    │     ▼ Physics.Raycast(Incheon mesh) │
        │                                  │   class별 색상 마커                 │
        ▼ (오프라인)                       │                                     │
  Python/projection_pipeline.py            │   ProjectionReplay (CSV 직접 재생)  │
        ▼                                  └─────────────────────────────────────┘
  output/<session>/projection/*.csv  ──►  Python/replay_offline.py ── UDP ──┘
```

검출별 UDP/CSV 필드: `class_name, class_id, confidence, u, v,
cam_lat, cam_lng, cam_alt, direction[3]`. 방향 벡터는 자세 기반이라 좌표계와 무관.

---

## 사전 준비

- **Unity 6000.4.0f1** (인천 프로젝트 버전). 6000.4.6f1로 열면 업그레이드
  프롬프트가 뜨며, 백업이 있으니 진행해도 되지만 가능하면 6000.4.0f1 권장.
- Python 3.11, `pip install` : `rfdetr`(또는 사용 중인 RF-DETR 패키지),
  `supervision`, `opencv-python`, `numpy`, `pillow`.
- `Models/checkpoint_best_total.pth` 존재 확인(약 485 MB).
- 라이브 모드면 폰 IP Webcam 등 영상/센서 소스.

---

## Unity 실행 및 설정 — 차근차근

### 1) 프로젝트 열기

1. **Unity Hub ▸ Add ▸ Add project from disk** → `DroneVisionUnity/Unity/` 선택.
2. 에디터 버전 **6000.4.0f1** 로 열기(없으면 Unity Hub에서 해당 버전 설치).
3. 첫 실행 시 `Library/` 가 자동 재생성되며 임포트에 수 분 소요(정상).

### 2) 메인 씬 열기

- **Project 창 ▸ `Assets/Scenes/SampleScene.unity` 더블클릭.**
- 이 씬이 유일한 작업 씬이며 Build Settings에도 등록돼 있다(아래 "씬 정리" 참고).
- 씬 구성(이미 만들어져 있음):
  | GameObject | 역할 |
  |---|---|
  | `Cube` | 드론 본체. `CubeDroneController`(조작) + `CubeGPSDisplay` 대상 |
  | `Camera` | `DroneFollowCamera` — Cube 추적 |
  | `Main Camera` | 기본 카메라 |
  | `GPS Manager` | `UnityToGPSConverter` (Unity→GPS 역변환) |
  | `GPS_Status_Panel` / `GPS_Status_Text` | 드론 GPS HUD(TextMeshPro) |
  | `GPS` | 인천 맵 메시(`finally.fbx`, 2026-05-25 신모델) 포함 노드 |
  | `Global Volume` | URP 포스트프로세싱 |
  | `Directional Light`, `EventSystem` | 조명 / UI 입력 |

### 3) 추론 파이프라인 GameObject 추가 (필수, 1회)

씬은 팀원 정본이라 스크립트만 추가돼 있다.

1. Hierarchy 우클릭 ▸ **Create Empty** → 이름 `ProjectionPipeline`.
2. Inspector ▸ **Add Component ▸ `Projection Udp Receiver`**.
3. **보정 연결은 보통 불필요.** 씬에 팀원 `CubeGPSDisplay` 가 있으면
   `Calibration` 슬롯이 자동 검색되어 **anchorObject · anchorLat/Lng ·
   horizontalScaleFactor · altitudeReferenceBuilding ·
   referenceBuildingHeightMeters 가 CubeGPSDisplay 에서 그대로 자동 동기화**된다
   (= 팀원 값과 항상 1:1 일치).
   - 씬에 `CubeGPSDisplay` 가 없을 때만 인스펙터 보정값/`Anchor Object` 수동 지정.
   - 콘솔에 `src=CubeGPSDisplay` 로그가 뜨면 자동 동기화 정상.
   - 콘솔에 `refBldg=...` 가 객체 이름으로 찍히면 고도 기준 건물도 정상 인식.
4. (선택) `Marker Prefab` 지정, `Ground Mask` 를 인천 맵 레이어로 한정,
   `Max Ray Distance` 가 맵 스케일에 충분한지 확인(기본 2,000,000).
5. `Inference Enabled` 체크(기본 ON). 런타임 토글 키는 `I`.

### 4) GPS 순간이동(텔레포트) 추가 (선택)

1. Hierarchy ▸ Create Empty → `GpsTeleport`.
2. **Add Component ▸ `Gps Teleport`**.
3. `Latitude` / `Longitude` / `Altitude` = 순간이동할 목표 좌표.
4. `Drone` 에 `Cube`(시점 오브젝트) 드래그 → 켜질 때 화면도 그 위치로 이동.
5. `Receiver` 는 비워두면 자동으로 `ProjectionUdpReceiver` 를 찾음.
6. `ProjectionUdpReceiver`/`ProjectionReplay` 의 `Teleport` 필드도 비워두면
   자동 연결됨(원하면 직접 드래그).
7. 사용: **`T`** 키 또는 `Override Enabled` 체크 → 수신 GPS 무시하고 지정
   좌표로 추론·표시.

### 5) 오프라인 CSV 재생용 (선택)

- 별도 Empty에 `Projection Replay` 추가 → `Projection Csv Path` 에
  `output/<session>/projection/projection_*.csv` **절대경로** 입력.
- `Anchor Object` 는 위와 동일하게 연결.

### 6) 실행

1. 에디터 상단 **Play(▶)**.
2. 아래 Python 송신기 실행 → 마커가 인천 맵 위에 색상별로 표시됨.
3. `Console` 에 `[UdpRx] listening on UDP 9870 ...` 로그 확인.

---

## Python 실행

```bash
cd DroneVisionUnity/Python
```

> 먼저 `IP_webcam.py` 상단 상수(`IP_ADDRESS`, `PORT`, `UDP_*`) 조정.

**촬영·추론 분리 (권장 워크플로):**
```bash
# 1) 촬영·저장만 (RF-DETR/UDP 없음, 경량) → output/<타임스탬프>/
python3 capture.py

# 2) 저장된 세션에 오프라인 추론 (경로 인자)
python3 infer.py --session ../output/<session>          # 기본: 최신 세션
python3 infer.py --all                                   # output/* 전체 배치
python3 infer.py --session <경로> --force                # 결과 덮어쓰기

# 3) detect+sensor 조인 → projection CSV → Unity 재생
python3 projection_pipeline.py --session ../output/<session>
python3 replay_offline.py     --session ../output/<session> --rate 5 --loop
# 또는 Unity ProjectionReplay 로 CSV 직접 재생
```

**올인원 라이브 (촬영+추론+UDP 동시, 기존 방식):**
```bash
python3 IP_webcam.py     # 폰 연결 시 라이브, 미연결 시 output/* 전체 오프라인 추론(자동 분기)
```

> 역할 분담: `capture.py`=촬영만 · `infer.py`=세션 경로 받아 추론 ·
> `IP_webcam.py`=공용 로직 + 올인원 라이브. 셋은 같은 함수를 재사용한다
> (`run_live_capture`, `infer_session`, `load_rfdetr_model`).

**사전 검증(matplotlib):**
```bash
python3 visualize_projection.py --session ../output/<session>
```

> Python 스크립트는 파일 위치 기준 `PROJECT_ROOT = ..` 를 사용하므로 어느
> CWD에서 실행해도 `Models/` 와 `output/` 을 올바르게 찾는다.
> Unity의 `ProjectionUdpReceiver` 가 고정 앵커를 쓰므로 더 이상 Python
> `--origin` 과 Unity 값을 맞출 필요가 없다(`meta_*.json` 의 origin은 로그용).

---

## 조작 키 (요약)

**Cube (카메라용)**

| 키 | 기능 |
|---|---|
| W A S D | 수평 이동 |
| ↑ / ↓ | 상승 / 하강 |
| ← / → | Yaw 좌/우 회전 |
| R / F | Pitch 기수 올림/내림 |
| **V** | 1인칭 ↔ 3인칭 시점 전환 |
| **T** | GPS 순간이동 on/off |
| **I** | 추론(마커 표시) on/off |

**Cube 마우스 룩**

| 입력 | 기능 |
|---|---|
| **LMB 드래그** | cube yaw + pitch (1인칭/3인칭 양쪽 공통) |

**드론 시뮬레이션 (`drone_sim_obj_n`, Cube 와 무관)**

| 키 / 입력 | 기능 |
|---|---|
| **`** (백쿼트) | 드론 1대 추가 spawn |
| **F10** | 비행 모드 토글 (HighFidelity ↔ Arcade) |
| **LMB 클릭** (드론 마커) | 단일 선택 (Shift+ = 추가) |
| **LMB 드래그** (Display 3 빈 공간) | 박스 셀렉트 |
| **LMB 단일 클릭** (Display 3 빈 공간) | 선택 해제 → 전체 대상 |
| **Ctrl + LMB 드래그** (Display 3) | 마우스 궤적 → `SetPath` |
| **RMB** | 선택군 `SetWaypoint` (어느 디스플레이든) |
| **Shift+RMB** | `QueueWaypoint` |
| **↑↓←→** (Display 3) | 전략 카메라 팬 |
| **MMB 드래그 / 휠** | 팬 / 줌 |
| **Home** | 전략 카메라 → Cube 위로 재중심 |

전체 설명은 [`조작법.md`](조작법.md) 참고. 입력은 **New Input System** 전용
(`activeInputHandler: 2`).

---

## 드론 시뮬레이션 (Flight + C2)

**카메라용 `Cube` 와 별개**의 독립 6-DOF 드론 시뮬레이션. PhysX arcade 가 아닌 자체
EOM(Newton-Euler) + RK4 적분 + cascaded 제어기로 비행. `drone_sim_obj_n`(n=0,1,…)
형식으로 런타임에 spawn. Display 1=1인칭, Display 2=3인칭, Display 3=전략 보기로 멀티 디스플레이 분담.

### 어셈블리 구조

```
Unity/Assets/Flight/
├── Core/      (FlightCore.asmdef, noEngineReferences=true)
│   ├── Frames.cs                    # ENU(E,N,U) 규약, g=(0,0,-9.81)
│   ├── State6Dof.cs                 # p, v, q, ω
│   ├── DroneParams.cs               # m, J, k_f, k_m, arm, drag
│   ├── MotorMixer.cs                # X-config [T,τ]↔[f₁..f₄]
│   ├── RigidBodyDynamics.cs         # m·v̇=mg+R(q)f-Dv, Jω̇=τ-ω×Jω, q̇=½q⊗[0,ω]
│   ├── Rk4Integrator.cs             # 고정 dt RK4
│   ├── WaypointQueue.cs             # sphere-of-acceptance + bearing test + pass-through
│   └── Controllers/
│       ├── RatePid.cs               # inner-loop (ω 오차 → τ)
│       ├── AttitudeController.cs    # mid-loop (q_des → ω_des)
│       └── PositionController.cs    # outer-loop (e_p → T, q_des)
└── Unity/     (FlightAdapter.asmdef, FlightCore + InputSystem 참조)
    ├── FrameConversion.cs           # ENU↔Unity y↔z swap + scale (단일 변환 지점)
    ├── IFlightModel.cs
    ├── HighFidelityFlightModel.cs   # EOM 어댑터, FixedUpdate 누산기, isKinematic 강제
    ├── ArcadeFlightModel.cs         # 비교용 단순 모드
    ├── DroneAgent.cs                # ID + 두 모델 + waypoint queue + 명령 메서드
    ├── DroneRegistry.cs             # drone_sim_obj_n 자동 ID 발급
    ├── DroneSpawner.cs              # 런타임 십자 막대 + SpawnMode(Navigate 등)
    ├── FlightCommands.cs            # **단일 명령 진입점** SetWaypoint/Queue/SetPath/Stop
    ├── FlightModeSwitcher.cs        # F10 = 모드 토글
    ├── FlightTestConsole.cs         # 인게임 GUI 콘솔 (좌표 입력, 사각형 경로 preset)
    └── FlightConfig.cs              # ScriptableObject (m, J, gains, scale, visualScale)

Unity/Assets/C2/                     # 명령·시각화 인터페이스 (Assembly-CSharp)
├── MultiDisplayCoordinator.cs       # Display 1/2/3 카메라·UI 분담 + FP 안전망
└── StrategicView/
    ├── StrategicViewBootstrap.cs    # Display 3 전체화면 카메라 + Canvas
    ├── StrategicCameraController.cs # 팬/줌/Home(재중심)
    ├── StrategicCommandInput.cs     # LMB 선택·박스, RMB 명령, Ctrl+드래그 SetPath
    └── StrategicViewHud.cs          # 드론·타겟·큐 마커 + 번호 라벨 + 그룹핑

Unity/Assets/Tests/EditMode/         (NUnit, FlightCore + FlightAdapter 참조)
└── *.cs                             # RK4·MotorMixer·Frames·FrameConversion·Controllers·WaypointQueue·MinimapInput
```

### 씬 설정 (1회)

1. **Assets ▸ Create ▸ Drone ▸ FlightConfig** — `FlightConfig` SO 생성. `unityUnitsPerMeter=0`(자동) / `visualScale=10` 기본.
2. Hierarchy ▸ Create Empty `[DroneSpawner]` → **Drone Spawner**.
   - `Config` 슬롯에 SO 드래그.
   - `Spawn Anchor` 비우면 자동으로 `Cube`.
   - `Initial Mode = Navigate` 권장.
3. `[FlightModeSwitcher]` → **Flight Mode Switcher**.
4. `[StrategicView]` → **Strategic View Bootstrap** + **Strategic Camera Controller** + **Strategic Command Input** + **Strategic View Hud**.
5. `[MultiDisplay]` → **Multi Display Coordinator** (Main Camera, FP/Main display index 등 기본 또는 자동).
6. (선택) `[FlightTestConsole]` → **Flight Test Console** — IMGUI 콘솔로 명령 직접 호출.
7. **카메라 targetDisplay**:
   - FP Camera → Display 1 (idx 0).
   - Main Camera → Display 2 (idx 1).
   - (Strategic Camera 는 Bootstrap 이 자동 생성·할당.)
8. **지면 충돌**: `finally.fbx` 자식 메시들에 `Mesh Collider` 부착 필수 (아래 "지면 메시 콜라이더 설정" 참고). `Physics.queriesHitBackfaces` 는 raycast 호출 시 임시 활성화하므로 single-sided terrain 도 처리됨.

### 검증

- Play → Cube 앞 5m 에 십자 막대 드론 spawn.
- ` 키로 `drone_sim_obj_1, 2, …` 추가.
- F10 — 전체 드론 Arcade ↔ HighFidelity 토글.
- 콘솔에 `[Cmd] 시작 선택 = ALL (N 대)` 로그 확인 — 빈 선택 = 전체 대상.
- Display 3 에서 RMB → 모든 드론 그 위치로 비행. 타겟 마커에 `"all"` 라벨.
- LMB 로 1번 드론만 선택 → RMB → 1번만 이동. 타겟 라벨 `"1"`.
- Ctrl+드래그 → 마우스 궤적 따라 SetPath.
- Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All — 단위테스트 PASS 기대.

### 지면 메시 콜라이더 설정

`HighFidelityFlightModel` 은 `Rigidbody.isKinematic=true` 라 PhysX 충돌 미사용 — 대신
명시적 `Physics.Raycast` 로 매 tick 마다 지면 위로 clamp.

**확인 방법**:
1. Hierarchy 의 `GPS` 또는 `finally` 노드 펼치기.
2. 자식 메시 GameObject 각각 클릭, Inspector 에 **Mesh Collider** 컴포넌트가 있는지 확인.
3. 없으면 → 컴포넌트 직접 부착(아래).

**설정 방법** (이미 씬에 있는 finally 가 콜라이더 없을 때):
1. Project ▸ `Assets/finally.fbx` 클릭 → Inspector 의 **Model 탭 ▸ Geometry 섹션 ▸
   Add Colliders ✓** (현재 메타 파일에서 `addColliders: 1` 확인됨 — 새로 드래그하면
   자동 부착).
2. 기존 씬 노드에 부착이 안 됐다면:
   - Hierarchy 에서 finally 루트 우클릭 → Delete.
   - Project 에서 `finally.fbx` 를 Hierarchy 로 드래그 → 모든 메시에 MeshCollider 자동.
   - 위치/회전이 원래 값으로 복원되도록 anchor(GPS) 좌표 재설정 필요할 수 있음.
3. 또는 일괄 부착 스크립트:
   - 씬의 finally 루트 선택 → 메뉴 **GameObject ▸ Children ▸ Add Mesh Collider**
     (Unity 6 에 메뉴 있음). 또는 Inspector 에서 자식들 다중 선택 → Add Component
     ▸ Mesh Collider.
4. `Mesh Collider` 컴포넌트 옵션: **Convex 는 OFF** (도시 메시는 비-convex), `Cooking
   Options` 기본값 유지.
5. 메시 read/write: `finally.fbx.meta` 의 `isReadable: 0` 그대로 OK
   (정적 콜라이더는 import 시 cook 된 데이터 사용). raycast 가 안 hit 하면 그때만
   Model 탭에서 **Read/Write Enabled ✓** 시도.

**Layer / Mask**:
- `HighFidelityFlightModel.Ground Mask` 기본 `~0` (모든 레이어). 도시 메시가 특정
  레이어에 있으면 마스크를 그 레이어로 한정해 다른 콜라이더(드론 자기 자신, 마커
  등) 와 충돌 회피.



### 사전에 만들어진 씬

| 씬 | 위치 | 용도 |
|---|---|---|
| **`SampleScene`** | `Assets/Scenes/SampleScene.unity` | **메인 작업 씬.** 드론·카메라·맵·HUD·GPS 보정이 모두 들어 있는 팀원 정본. Build Settings에 등록되어 빌드/시작 씬이 됨. |
| TextMesh Pro 예제들 | `Assets/TextMesh Pro/Examples & Extras/Scenes/*.unity` | 패키지 샘플 30여 개. **파이프라인과 무관** — 무시하거나 용량 정리 시 폴더째 삭제 가능. |

> 실질적으로 사용하는 씬은 **`SampleScene` 하나**다. 새 시나리오가 필요하면
> 이 씬을 복제해서 변형하는 것을 권장(아래 "다른 이름으로 저장").

### 씬 저장 방법

- **현재 씬 저장:** `Ctrl/Cmd + S` (메뉴: **File ▸ Save**).
  → 해당 `.unity` 파일(`Assets/Scenes/SampleScene.unity`)에 덮어쓰기 저장.
  파이프라인 GameObject·`Anchor Object` 연결·인스펙터 보정값이 이 파일에
  직렬화되어 보존된다. **설정 후 반드시 저장해야 영구 반영된다.**
- **다른 이름으로 저장(새 씬):** **File ▸ Save As…** → `Assets/Scenes/` 아래에
  새 이름으로 저장. 원본 `SampleScene` 을 보존한 채 실험할 때 사용.
- **새 빈 씬:** **File ▸ New Scene** → 템플릿 선택 → 저장 시 `Assets/Scenes/` 권장.
- **빌드/시작 씬 등록:** **File ▸ Build Profiles(또는 Build Settings)** ▸
  *Add Open Scenes* 또는 씬을 드래그. 목록에서 체크된 씬만 빌드에 포함되고,
  맨 위 씬이 시작 씬이다. 현재 `SampleScene` 이 등록되어 있음
  (`ProjectSettings/EditorBuildSettings.asset`).

### ⚠️ Play 모드와 저장 (중요)

- **Play(▶) 중의 변경은 저장되지 않는다.** 스폰된 마커, `GpsTeleport` 로
  이동한 위치, 런타임에 토글한 `Inference/Override` 값은 Play 정지 시 원복된다.
- 설정을 영구 반영하려면 **Play를 멈춘 상태에서** Inspector를 수정하고
  `Ctrl+S` 로 씬을 저장한다.
- GPS 보정값(앵커 위경도, `horizontalScaleFactor`, `altitudeReferenceBuilding`,
  `referenceBuildingHeightMeters`)은 `CubeGPSDisplay` 의 직렬화 필드이므로,
  값을 바꾸면 **씬 저장 시점에** 파일에 기록되고 수신부가 자동 추종한다.

---

## 백업 / 변경 이력

- `Unity_HDRP_old/` — 병합 전 HDRP 프로젝트(보존, 삭제 안 함).
- `incheon_source_backup.zip` — 팀원 인천 프로젝트 원본 아카이브.
- **2026-05-25 #2 업데이트**: 팀원이 인천 맵을 `Untitled.fbx` → **`finally.fbx`**
  (6.7MB, 약 2배) 로 교체. CubeGPSDisplay 인터페이스는 동일이라 우리 수신부 코드
  무수정으로 복원 가능. `Unity_backup/`에서 5개 스크립트 (ProjectionUdpReceiver/
  Replay/GpsTeleport/FirstPersonView/CubeGpsTeleport) 그대로 복원.
- 좌표계가 ECEF(`GeoAnchor`) → 팀원 `GPSEncoder` 모델로 바뀌었고 Python
  와이어 포맷이 `origin(ENU)` → `cam_lat/cam_lng/cam_alt` 로 변경됨.
- **CubeGPSDisplay 신모델(2026-05-25)**: 옛 `altitudeOffset` 상수 fudge 제거,
  대신 **기준 건물 메쉬 bounds**로 `unityUnitsPerMeter` 자동 도출
  (`altitudeReferenceBuilding` + `referenceBuildingHeightMeters=13.35`).
  수평 스케일도 `horizontalScaleFactor=1`(1단위=1m)로 단일화 — 이전의
  수평/고도 비대칭 문제 해소. 수신부는 **CubeGPSDisplay 의 정확한 역함수**로
  재작성됐고 (`Unity_backup/`에서 복원), 보정 상수는 CubeGPSDisplay 단일 소스
  자동 동기화. 자세한 규약은
  [`Unity/PIPELINE_MERGE_NOTES.md`](Unity/PIPELINE_MERGE_NOTES.md).

## 비고

- `Unity/Library/`, `Logs/`, `Temp/` 는 Unity가 자동 생성하므로 백업/공유에서 제외.
- RF-DETR 가중치(485 MB)는 git에 직접 올리기엔 큼 — LFS/외부 스토리지 권장.
- 고도/위치가 어색하면 `CubeGPSDisplay`의 `altitudeReferenceBuilding` 슬롯과
  `referenceBuildingHeightMeters` 값이 실제 건물과 맞는지 우선 점검. 또
  `anchorObject` 가 모델 내 앵커 GPS(37.384312/126.655307) 대응 지점에
  올바로 지정돼 있는지 확인. 값을 바꾸면 수신부가 자동 추종한다.
