#!/usr/bin/env bash
# PX4 SITL + Gazebo Harmonic 기동 엔트리포인트.
#
# 홈 위치는 인천 앵커(CubeGPSDisplay.anchorLatitude/Longitude)로 고정 — SITL 드론이
# 인천 맵 좌표계 위에 뜨도록. 값은 환경변수로 덮어쓸 수 있다.
set -euo pipefail

# ── 홈/원점 (인천 앵커, CubeGPSDisplay 기본값과 동일) ───────────────────────
# gz Harmonic 에서는 PX4_HOME_LAT/LON 이 world 의 spherical_coordinates 를 override 하지
# 못한다(경험적 확인). 따라서 원점은 커스텀 world(incheon.sdf)로 지정하고 그걸 선택한다.
export PX4_GZ_WORLD="${PX4_GZ_WORLD:-incheon}"
# (참고용 — gz 에선 미적용이지만 jMAVSim/Classic 호환 위해 남겨둠.)
export PX4_HOME_LAT="${PX4_HOME_LAT:-37.384312}"
export PX4_HOME_LON="${PX4_HOME_LON:-126.655307}"
export PX4_HOME_ALT="${PX4_HOME_ALT:-5.0}"

# ── 헤드리스 (GUI 없이; X11 안 띄울 때) ────────────────────────────────────
# HEADLESS=1 이면 Gazebo GUI 미기동 — 서버/원격에서 권장. GUI 보려면 HEADLESS=0 + X11.
export HEADLESS="${HEADLESS:-1}"

# 모델 타깃 (gz_x500 기본; 카메라 필요시 gz_x500_mono_cam).
PX4_TARGET="${PX4_TARGET:-gz_x500}"

echo "[sitl] PX4 v1.16 SITL + Gazebo Harmonic"
echo "[sitl] target=${PX4_TARGET} home=(${PX4_HOME_LAT}, ${PX4_HOME_LON}, ${PX4_HOME_ALT}m) HEADLESS=${HEADLESS}"
echo "[sitl] MAVLink external API on udp:14540 (MAVSDK/브리지 접속), GCS on udp:14550 (QGroundControl 자동 발견)"
echo "[sitl] (이미지 빌드 시 px4-rc.mavlink 의 udp_gcs_port_local 가 14550 으로 patch 됨)"

cd /opt/PX4-Autopilot
# DONT_RUN 미설정 → 빌드 산출물로 SITL 기동. 이미 빌드돼 있으면 즉시 실행.
exec make px4_sitl "${PX4_TARGET}"
