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
4. CSV 오프라인 재생이 필요하면 별도 GameObject 에 `ProjectionReplay` 를 추가하고 `Projection Csv Path` 에 `output/<session>/projection/*.csv` 절대 경로를 지정한다.
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
| RF-DETR 가중치 (~485 MB) | TODO: Google Drive 링크 | `Models/checkpoint_best_total.pth` |
| 데모 캡처 세션 zip (~145 MB) | TODO: Google Drive 링크 | 받아서 `output/` 에 풀기 (`output/20260529_122735/` 가 됨) |

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
| P | GPS HUD 토글 |

드론 시뮬레이션 (`drone_sim_obj_n`):

| 입력 | 동작 |
|---|---|
| `` ` `` | 드론 추가 spawn |
| F10 | 비행 모드 토글 (HighFidelity ↔ Arcade) |
| LMB 클릭 (드론 마커) | 단일 선택 (Shift = 추가) |
| LMB 드래그 (전략 뷰) | 박스 셀렉트 |
| LMB 단일 클릭 (전략 뷰) | 선택 해제 → 전체 대상 |
| Ctrl + LMB 드래그 | 마우스 궤적 → SetPath |
| RMB | 선택군 SetWaypoint (Shift = QueueWaypoint) |
| 화살표 / MMB 드래그 / 휠 | 전략 카메라 팬·줌 |
| Home | 전략 카메라 Cube 위로 재중심 |

전체 키 매핑은 [`조작법.md`](조작법.md) 참조.

## 멀티 디스플레이

| 디스플레이 | 내용 |
|---|---|
| Display 1 | 1인칭(FP) 카메라 + HUD/UI |
| Display 2 | 3인칭(Main) 카메라 |
| Display 3 | 전략 보기 (탑다운 직교, 드론·타겟·검출 마커·범례·스케일 바) |

`MultiDisplayCoordinator` 가 시작 시 추가 디스플레이를 활성화하고 매 프레임 카메라 enabled 를 강제한다. Editor 단일 모니터 환경에서는 Game view 의 디스플레이 탭으로 전환해 확인한다.

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

## 라이선스

(추가 예정)
