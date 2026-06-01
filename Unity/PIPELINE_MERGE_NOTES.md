# 파이프라인 병합 노트 (인천 GPS 정본 채택)

최종 갱신: 2026-05-25 (두 번째 팀원 업데이트: 맵 `finally.fbx` 도입 + 수신부 재복원)

> **2026-05-25 추가 업데이트**: 팀원이 새 인천 맵 `finally.fbx`(6.7MB, 구
> `Untitled.fbx` 의 약 2배)를 도입, 씬은 prefab `value: finally` 로 교체.
> `Untitled.fbx` 는 참조용으로 잔존. CubeGPSDisplay 인터페이스/씬 보정값
> (`hScale=1`, `refH=13.35`, anchor 37.384312/126.655307) **동일** → 우리
> 수신부 5개 스크립트(ProjectionUdpReceiver, ProjectionReplay, GpsTeleport,
> FirstPersonView, CubeGpsTeleport) **무수정 복원**으로 충분.
> 이 문서 자체도 `Unity_backup/` 에서 복원함.

## 무엇이 바뀌었나

- **`Unity/` = 팀원의 인천 프로젝트** (URP, Unity 6000.4.0f1). GPS 정렬은 팀원이
  담당·검증한 것을 **정본(source of truth)** 으로 사용.
- 기존 HDRP 프로젝트는 **`Unity_HDRP_old/`** 에 보존.
- 팀원 원본 아카이브는 **`incheon_source_backup.zip`**, 수신부 백업은
  **`Unity_backup/Assets/`** 에 보존.
- **`GeoAnchor.cs`(ECEF/ENU)는 사용 안 함.** 좌표 변환은 팀원 `GPSEncoder.cs`
  + `CubeGPSDisplay` 보정값으로 통일.
- **CubeGPSDisplay 신모델 (2026-05-25)**: 옛 `altitudeOffset` 상수 fudge ·
  `scaleFactor=10` 비대칭 제거. **기준 건물 메쉬 bounds 기반 자동 단위/m 도출**.

## 좌표 변환 = 팀원 CubeGPSDisplay 의 정확한 역함수

추가한 수신부(`ProjectionUdpReceiver`, `ProjectionReplay`)는 **별도 보정 로직을
두지 않는다.** 팀원 `CubeGPSDisplay` 의 변환을 그대로 뒤집을 뿐이다.

### CubeGPSDisplay 정변환 (Unity world → GPS)

수평:
```
rel       = cube.position - anchorObject.position
corrected = rel / horizontalScaleFactor          # 씬값 = 1  → 1 Unity 단위 = 1 m (수평)
gps       = GPSEncoder.USCToGPS(corrected)       # lat=lat0+z/mLat, lon=lon0+x/mLon
```
고도:
```
buildingBaseY      = altitudeReferenceBuilding.Renderer.bounds.min.y
buildingTopY       = altitudeReferenceBuilding.Renderer.bounds.max.y
unityHeight        = buildingTopY - buildingBaseY
unityUnitsPerMeter = unityHeight / referenceBuildingHeightMeters   # 씬값 = 13.35 m
realAlt            = (cube.position.y - buildingBaseY) / unityUnitsPerMeter
```

### 수신부 역변환 (GPS → Unity world)

```
GPSEncoder.SetLocalOrigin(anchorLat, anchorLng)
corrected = GPSEncoder.GPSToUCS(cam_lat, cam_lng)
rel       = corrected * horizontalScaleFactor
world.xz  = anchorObject.position.xz + rel.xz
world.y   = buildingBaseY + cam_alt * unityUnitsPerMeter
```
그 뒤 `Physics.Raycast(world, direction)` 로 인천 메시에 명중 → 마커 생성.

### 보정 상수는 CubeGPSDisplay 단일 소스에서 자동 동기화

`ProjectionUdpReceiver.calibration` / `ProjectionReplay.calibration` 슬롯이 씬의
`CubeGPSDisplay` 를 가리키면(비워두면 `FindObjectOfType` 자동 검색),
매 변환마다 다음을 **그대로 복사** — 팀원 값과 항상 1:1 일치:

| 동기화 항목 | 인천 씬값 (2026-05-25) |
|---|---|
| `anchorObject` | 팀원이 지정한 모델 내 앵커 Transform |
| `anchorLatitude` / `anchorLongitude` | 37.384312 / 126.655307 |
| `horizontalScaleFactor` | **1**  (1 Unity 단위 = 1 m, 수평) |
| `altitudeReferenceBuilding` | 팀원이 지정한 기준 건물 Transform |
| `referenceBuildingHeightMeters` | **13.35 m** |

> 팀원이 인스펙터 값을 바꾸면 수신부도 자동으로 따라간다. 별도 입력/옵션 없음.

### 이전 모델 대비 차이 요약

| 항목 | 구 모델 | 신 모델 (2026-05-25) |
|---|---|---|
| 수평 스케일 | `scaleFactor = 10` (이전 비대칭의 원인) | `horizontalScaleFactor = 1` |
| 고도 datum | `altitudeOffset = 2856` (HUD 표시용 상수) | 기준 건물 bounds 기반 자동 |
| 수직 스케일 | 미정의 (수평과 비대칭) | `unityHeight / 13.35` 자동 도출 |
| 결과 | 고도가 어긋남(이전 "음수 고도" 이슈) | 측량값 기반으로 일관 |

## 추가된 스크립트 (Unity/Assets/, 복원·재작성됨)

- `ProjectionUdpReceiver.cs` — 라이브 UDP(9870) 수신, 위 역변환 + Raycast 마커.
  추론 on/off 토글(`I` 키 / 인스펙터).
- `ProjectionReplay.cs` — projection CSV 직접 재생 (동일 역변환).
- `GpsTeleport.cs` — GPS 순간이동 오버라이드(`T` 키). 켜지면 수신/CSV GPS를
  무시하고 지정 좌표로 추론·표시, 옵션으로 시점 오브젝트도 텔레포트.

## ✋ 수동 Unity 작업

씬(`Assets/Scenes/SampleScene.unity`)은 팀원 정본이라 자동으로 안 건드림.
에디터에서 1회:

1. Unity Hub로 `Unity/` 프로젝트 열기(6000.4.0f1 권장).
2. 빈 GameObject `ProjectionPipeline` → `ProjectionUdpReceiver` 추가.
3. **보정 연결 보통 불필요** — 씬에 팀원 `CubeGPSDisplay` 가 있으면
   `Calibration` 슬롯이 자동 검색되어 anchorObject/anchor위경도/
   horizontalScaleFactor/altitudeReferenceBuilding/referenceBuildingHeightMeters
   가 그대로 동기화됨.
4. (선택) `Marker Prefab`, `Ground Mask`(인천 맵 레이어), `Max Ray Distance`.
5. (선택) `GpsTeleport` GameObject 추가 → 좌표 입력, `T` 토글.
6. 변경 후 Play 정지 상태에서 씬 저장(`Ctrl+S`).

## Python 와이어 포맷

검출별: `class_name, class_id, confidence, u, v,
cam_lat, cam_lng, cam_alt, direction[3]`. 방향 벡터는 자세 기반(좌표계 무관).
관련 파일: `udp_sender.py`, `IP_webcam.py`, `projection_pipeline.py`,
`replay_offline.py`, `capture.py`, `infer.py`. `geo_projection.py` 는 방향 계산의
권위로 유지. CSV 헤더: `... u v cam_lat cam_lng cam_alt dx dy dz`.

## 실행

촬영·추론 분리(권장):
```
python3 Python/capture.py                                    # 촬영·저장만
python3 Python/infer.py --session ../output/<세션>           # RF-DETR 오프라인 추론
python3 Python/projection_pipeline.py --session ../output/<세션>
python3 Python/replay_offline.py     --session ../output/<세션> --rate 5 --loop
# 또는 Unity ProjectionReplay 로 CSV 직접 재생
```
올인원 라이브: `python3 Python/IP_webcam.py`.

## 검증 체크리스트

- [ ] 에디터 컴파일 에러 0.
- [ ] `ProjectionUdpReceiver` 콘솔 로그에 `src=CubeGPSDisplay` 와
  `refBldg=<건물이름>` 표시(자동 동기화·기준 건물 인식 정상).
- [ ] 앵커 GPS 자체를 송출 → 마커가 `anchorObject` 위치에 찍히는지(수평 OK).
- [ ] 기준 건물 옥상 좌표/고도를 송출 → 마커가 건물 옥상 부근에 찍히는지(고도 OK).
- [ ] 어색하면 `CubeGPSDisplay` 의 `altitudeReferenceBuilding` 지정과
  `referenceBuildingHeightMeters` 가 실제 건물과 일치하는지 우선 점검.
