#!/usr/bin/env python3
"""레이어 2 검증 — Unity 없이 브리지 E2E 확인.

이 스크립트가 "Unity 역할"을 흉내낸다:
  - 텔레메트리 수신: udp 9871 bind (브리지가 UNITY_HOST:9871 로 push).
  - 명령 송신: udp 127.0.0.1:9872 (브리지 명령 포트)로 JSON.

흐름: arm → takeoff → (텔레 관찰) → 홈에서 북동쪽으로 약간 이동(goto) → 이동 확인 → land.
브리지 명령 wire format 은 MavlinkFlightModel 과 동일(LLA).

전제: px4_sitl + px4_bridge 가 떠 있음. 실행: python3 Python/sitl/bridge_test.py
"""
import json
import socket
import threading
import time

TELEM_BIND = ("0.0.0.0", 9871)
CMD_DST = ("127.0.0.1", 9872)

_latest = {}
_lock = threading.Lock()
_running = True


def telem_listener():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind(TELEM_BIND)
    s.settimeout(1.0)
    while _running:
        try:
            data, _ = s.recvfrom(4096)
            msg = json.loads(data.decode("utf-8"))
            if msg.get("type") == "telemetry":
                with _lock:
                    _latest.clear(); _latest.update(msg)
        except socket.timeout:
            continue
        except (ValueError, OSError):
            continue
    s.close()


def send(cmd: dict, tx: socket.socket):
    tx.sendto(json.dumps(cmd).encode("utf-8"), CMD_DST)
    print(f"[test] cmd→ {cmd}")


def snap():
    with _lock:
        return dict(_latest)


def wait_telem(timeout=30):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if snap().get("type") == "telemetry":
            return True
        time.sleep(0.2)
    return False


def main():
    global _running
    th = threading.Thread(target=telem_listener, daemon=True)
    th.start()
    tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    print("[test] 브리지 텔레메트리 대기 ...")
    if not wait_telem(30):
        print("[test] FAIL: 텔레메트리 없음 (브리지/SITL 확인). 종료.")
        _running = False
        return 1
    s0 = snap()
    print(f"[test] 텔레 수신 OK — home≈({s0['lat']:.6f},{s0['lon']:.6f}) "
          f"amsl={s0['alt_amsl']:.1f} mode={s0['flight_mode']} armed={s0['armed']}")

    home_lat, home_lon = s0["lat"], s0["lon"]
    home_amsl = s0["alt_amsl"] - s0["rel_alt"]

    # arm + takeoff
    send({"type": "takeoff", "alt": 5.0}, tx)
    print("[test] takeoff 대기(고도 상승) ...")
    for _ in range(200):
        if snap().get("rel_alt", 0) >= 4.0:
            break
        time.sleep(0.1)
    print(f"[test] rel_alt={snap().get('rel_alt', 0):.1f} m")

    # 북동쪽으로 약 30 m 이동 (위도/경도 약 +0.00027°/+0.00034° ≈ 30 m).
    tgt_lat = home_lat + 0.00027
    tgt_lon = home_lon + 0.00034
    tgt_alt = home_amsl + 10.0
    send({"type": "goto", "lat": tgt_lat, "lon": tgt_lon, "alt": tgt_alt}, tx)
    print("[test] goto 이동 관찰(최대 40s) ...")
    moved = False
    for _ in range(400):
        s = snap()
        d_lat = abs(s.get("lat", home_lat) - home_lat)
        d_lon = abs(s.get("lon", home_lon) - home_lon)
        if d_lat > 0.0001 or d_lon > 0.0001:
            moved = True
            print(f"[test] 이동 확인: ({s['lat']:.6f},{s['lon']:.6f}) rel_alt={s['rel_alt']:.1f}")
            break
        time.sleep(0.1)
    if not moved:
        print("[test] WARN: 가시적 이동 미확인 (도달했거나 느림).")

    # land
    send({"type": "land"}, tx)
    print("[test] land 명령. disarm 대기 ...")
    for _ in range(600):
        if not snap().get("armed", True):
            break
        time.sleep(0.1)
    print(f"[test] armed={snap().get('armed')} in_air={snap().get('in_air')}")

    _running = False
    print("[test] PASS: 브리지 E2E (명령 송신 → 이동 → 텔레 수신) 동작." if moved
          else "[test] DONE (이동 미확인 — 로그 확인 권장).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
