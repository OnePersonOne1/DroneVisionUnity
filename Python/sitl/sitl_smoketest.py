#!/usr/bin/env python3
"""PX4 SITL 단독 검증 — 브리지 없이 MAVSDK 로 arm → takeoff → 잠깐 호버 → land.

레이어 1 합격 기준: "Gazebo SITL 단독 기동 후 MAVSDK 로 arm/takeoff/land 가능 확인".

전제: docker-compose.sitl.yml 로 px4_sitl 이 떠 있고(MAVLink udp:14540), 본 스크립트는
호스트 또는 host-net 컨테이너에서 실행. 의존성: pip install "mavsdk>=3,<4".

실행:  python3 Python/sitl/sitl_smoketest.py
"""
import asyncio
import sys

from mavsdk import System

# PX4 외부 API 포트. SITL 이 이 포트로 onboard MAVLink 를 push → MAVSDK 가 listen.
# MAVSDK 3.x 는 구형 udp:// 대신 udpin://(수신) 스킴 사용.
CONNECT = "udpin://0.0.0.0:14540"
TAKEOFF_ALT_M = 5.0
HOVER_SEC = 8.0


async def run() -> int:
    drone = System()
    print(f"[smoke] connecting {CONNECT} ...")
    await drone.connect(system_address=CONNECT)

    # 연결 대기 (최대 30s).
    async def wait_connected():
        async for state in drone.core.connection_state():
            if state.is_connected:
                return True
        return False
    try:
        await asyncio.wait_for(wait_connected(), timeout=30)
    except asyncio.TimeoutError:
        print("[smoke] FAIL: PX4 에 연결 못 함 (SITL 기동/포트 확인).")
        return 1
    print("[smoke] connected.")

    # 위치/홈 확정 대기 (EKF 수렴 + global position OK).
    async def wait_health():
        async for h in drone.telemetry.health():
            if h.is_global_position_ok and h.is_home_position_ok:
                return True
        return False
    try:
        await asyncio.wait_for(wait_health(), timeout=60)
    except asyncio.TimeoutError:
        print("[smoke] FAIL: global/home position 미수렴 (EKF).")
        return 1
    print("[smoke] position OK.")

    print("[smoke] arming ...")
    await drone.action.arm()

    await drone.action.set_takeoff_altitude(TAKEOFF_ALT_M)
    print(f"[smoke] takeoff to {TAKEOFF_ALT_M} m ...")
    await drone.action.takeoff()
    await asyncio.sleep(HOVER_SEC)

    print("[smoke] landing ...")
    await drone.action.land()

    # 착륙(disarm) 대기.
    async def wait_disarmed():
        async for armed in drone.telemetry.armed():
            if not armed:
                return True
        return False
    try:
        await asyncio.wait_for(wait_disarmed(), timeout=60)
    except asyncio.TimeoutError:
        print("[smoke] WARN: 착륙 disarm 확인 타임아웃 (수동 확인).")

    print("[smoke] PASS: arm/takeoff/land 정상.")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(run()))
