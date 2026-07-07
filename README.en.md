# DroneVisionUnity
Aerial RF-DETR object detection re-projected onto a GPS-anchored Unity 3D map of Incheon, with a custom 6-DOF drone simulator alongside PX4 SITL.

## Overview
A two-part pipeline. Python owns RF-DETR inference and monocular pixel→direction geolocation; Unity renders the Incheon 3D map, spawns class markers via raycast, hosts an RTS-style C2 UI, and runs a custom 6-DOF flight simulator alongside PX4 SITL. Live mode communicates over UDP JSON; offline mode replays a projection CSV.

## Architecture
```
[camera/phone] ─▶ RF-DETR + sensor fusion (Python/IP_webcam.py)
                       │
                       ▼  UDP JSON {frame_id, detections[u,v,cam_lat/lng/alt,dir]}
                 Unity (URP): GPS→world (CubeGPSDisplay) → Physics.Raycast → class markers
                       ▲
     (offline) projection CSV ── replay_offline.py ── UDP ┘
```

## Key Features
- RF-DETR aerial detection: 4-class custom (`fire_region` / `smoke_region` / `lake` / `human`) + COCO 80-class fallback via `--coco`.
- Monocular pixel→direction geolocation re-projected onto the Incheon mesh through GPS→world raycast.
- Custom 6-DOF Newton–Euler flight model with RK4 integrator and cascade PID (rate / attitude / position).
- RTS-style multi-drone C2: box-select, StarCraft-style control groups, altitude presets, waypoint queues.
- PX4 v1.16 SITL with Gazebo Harmonic, MAVSDK bridge, and QGroundControl (ports 14540 / 14550).
- LLM situational briefing: FastAPI backend + OSM / GIS / Kakao Local + Ollama `qwen2.5:14b` on port 8077.
- Step-response and PID-sweep tools emitting overshoot / rise / settling / ss_error as CSV + PNG.

## Tech Stack
| Layer | Stack |
|---|---|
| Perception | RF-DETR, supervision, OpenCV |
| Runtime | Python 3.11, numpy, pillow |
| Visualization | Unity 6000.4.0f1 (URP) |
| Flight sim | Custom 6-DOF (Newton–Euler + RK4), PX4 v1.16 SITL, Gazebo Harmonic, MAVSDK, QGroundControl |
| Backend | FastAPI, shapely, Kakao Local API, Ollama `qwen2.5:14b` |
| Transport | UDP JSON (bridge ↔ Unity on 9871 / 9872), offline CSV replay |
| Infra | Docker Compose (`hanmyeongil/ipwebcam_rfdetr`, `hanmyeongil/px4-sitl`) |

## Quick Start
Assets (not tracked in git — download and place):

| File | Download | Place at |
|---|---|---|
| RF-DETR weights (~485 MB) | https://drive.google.com/file/d/1hoCFljPKaiiLfD9MdyfQ1XfUVvRozeyT/view?usp=drive_link | `Models/checkpoint_best_total.pth` |
| Demo capture session (~145 MB) | https://drive.google.com/file/d/1sTiMGBD5R_6A1fty9yYbVERDCs7U_Hkx/view?usp=drive_link | unzip into `output/` |

Unity: open `Unity/` with Unity 6000.4.0f1 (URP) and load `Assets/Scenes/SampleScene.unity`.

```bash
cd Python
python3 capture.py                                # capture + save
python3 infer.py --session ../output/<session>    # offline RF-DETR
python3 projection_pipeline.py --session ../output/<session>
python3 IP_webcam.py                              # all-in-one live
docker compose -f docker-compose.sitl.yml up -d px4_sitl px4_bridge   # optional PX4 SITL
```

## Documentation
| Doc | Scope |
|---|---|
| [`tutorial.md`](tutorial.md) | End-to-end walkthrough |
| [`조작법.md`](조작법.md) | Full controls and key bindings |
| [`Unity/PIPELINE_MERGE_NOTES.md`](Unity/PIPELINE_MERGE_NOTES.md) | Coordinate conventions (ENU / FLU / SI) |

## Roadmap
- Multi-drone PX4 SITL (per-instance UDP ports + sysid → DroneAgent routing).
- RF-DETR training-set expansion for underrepresented `human` / `fire_region` classes.
- Phase 2/3 of `BuildingInfoProbe`: reverse geocoding + AR briefing panel.

## License
TBD
