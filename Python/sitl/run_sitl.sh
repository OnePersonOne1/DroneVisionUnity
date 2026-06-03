#!/usr/bin/env bash
# PX4 SITL(Gazebo Harmonic) 기동 호스트 헬퍼.
#
# 사용:
#   ./run_sitl.sh            # 헤드리스 기동 (서버/원격 권장)
#   ./run_sitl.sh gui        # Gazebo GUI 기동 (X11 필요)
#   ./run_sitl.sh build      # 이미지 빌드만 (최초 20~40분)
#   ./run_sitl.sh smoke      # 떠 있는 SITL 에 arm/takeoff/land 단독 검증
set -euo pipefail

# repo 루트 (이 스크립트는 Python/sitl/ 에 있음).
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE="docker compose -f ${ROOT}/docker-compose.sitl.yml"

cmd="${1:-up}"
case "$cmd" in
  build)
    exec ${COMPOSE} build px4_sitl
    ;;
  gui)
    # GUI 렌더를 위해 로컬 X 접근 허용 (종료 후 xhost -local: 권장).
    command -v xhost >/dev/null && xhost +local: || true
    exec env HEADLESS=0 ${COMPOSE} up px4_sitl
    ;;
  smoke)
    # 호스트에 mavsdk 있으면 직접, 없으면 컨테이너 안에서 실행.
    if python3 -c "import mavsdk" 2>/dev/null; then
      exec python3 "${ROOT}/Python/sitl/sitl_smoketest.py"
    else
      exec docker exec -it px4_sitl_gz python3 /workspace/Python/sitl/sitl_smoketest.py 2>/dev/null \
        || { echo "mavsdk 미설치 + 컨테이너 마운트 없음. 'pip install mavsdk>=3,<4' 후 재시도."; exit 1; }
    fi
    ;;
  up|"")
    exec ${COMPOSE} up px4_sitl
    ;;
  *)
    echo "usage: $0 [up|gui|build|smoke]"; exit 2
    ;;
esac
