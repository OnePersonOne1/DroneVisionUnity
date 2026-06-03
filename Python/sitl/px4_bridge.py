#!/usr/bin/env python3
"""PX4 SITL ↔ Unity 브리지 (MAVSDK).

역할: Unity 의 RTS식 C2 명령(JSON/UDP)을 MAVLink 액션으로 변환해 PX4 에 전달하고,
PX4 텔레메트리를 JSON/UDP 로 Unity 에 push. **비행 제어는 PX4 가 소유** — 브리지는 변환만.

좌표 규약: 브리지는 **LLA(WGS84) + AMSL 고도**(MAVLink 네이티브)만 다룬다. Unity 측
MavlinkFlightModel 이 world↔LLA 변환을 담당하므로 브리지엔 맵/스케일 개념이 없다.

연결:
  PX4 외부 API : udp://:14540  (MAVSDK)
  Unity → 브리지(명령) : udp 0.0.0.0:9872  (수신 bind)
  브리지 → Unity(텔레) : udp <UNITY_HOST>:9871  (송신, ~15 Hz)

명령 wire format (Unity → 브리지, 1 datagram = 1 명령):
  {"type":"goto","lat":..,"lon":..,"alt":.. ,"yaw":..?}        # alt=AMSL m, yaw=deg(NED) optional
  {"type":"mission","items":[{"lat":..,"lon":..,"alt":..}, ...]}   # path/queue
  {"type":"hold"}                                              # StopAndHover
  {"type":"arm"} {"type":"takeoff","alt":..?} {"type":"land"} {"type":"rtl"}

텔레메트리 wire format (브리지 → Unity, ~15 Hz):
  {"type":"telemetry","t":<unix>,"lat":..,"lon":..,"alt_amsl":..,"rel_alt":..,
   "roll":..,"pitch":..,"yaw":..,"vn":..,"ve":..,"vd":..,
   "armed":bool,"in_air":bool,"flight_mode":str}

실행:  python3 Python/sitl/px4_bridge.py
환경변수:  UNITY_HOST(기본 127.0.0.1), TELEM_PORT(9871), CMD_PORT(9872),
          MAVLINK_URL(udp://:14540), TELEM_HZ(15),
          AUTO_TAKEOFF(1: 첫 goto 시 지상이면 자동 arm+takeoff),
          TAKEOFF_ALT(5), MAX_ALT_AMSL(500), MIN_ALT_AMSL(-50)
"""
import asyncio
import json
import os
import socket
import time

from mavsdk import System
from mavsdk.action import ActionError
from mavsdk.mission import MissionItem, MissionPlan, MissionError

# ── 설정 ────────────────────────────────────────────────────────────────────
UNITY_HOST   = os.environ.get("UNITY_HOST", "127.0.0.1")
TELEM_PORT   = int(os.environ.get("TELEM_PORT", "9871"))
CMD_PORT     = int(os.environ.get("CMD_PORT", "9872"))
MAVLINK_URL  = os.environ.get("MAVLINK_URL", "udpin://0.0.0.0:14540")
TELEM_HZ     = float(os.environ.get("TELEM_HZ", "15"))
AUTO_TAKEOFF = os.environ.get("AUTO_TAKEOFF", "1") == "1"
TAKEOFF_ALT  = float(os.environ.get("TAKEOFF_ALT", "5"))
MAX_ALT_AMSL = float(os.environ.get("MAX_ALT_AMSL", "500"))   # 지오펜스(고도 상한)
MIN_ALT_AMSL = float(os.environ.get("MIN_ALT_AMSL", "-50"))   # 고도 하한


def log(msg: str) -> None:
    print(f"[bridge] {msg}", flush=True)


# ── 최신 텔레메트리 집계 (각 구독 태스크가 갱신, 펌프가 송신) ────────────────
class TelemetryState:
    def __init__(self):
        self.lat = self.lon = self.alt_amsl = self.rel_alt = 0.0
        self.roll = self.pitch = self.yaw = 0.0
        self.vn = self.ve = self.vd = 0.0
        self.armed = False
        self.in_air = False
        self.flight_mode = "UNKNOWN"
        self.have_position = False

    def to_json(self) -> bytes:
        return json.dumps({
            "type": "telemetry", "t": time.time(),
            "lat": self.lat, "lon": self.lon,
            "alt_amsl": self.alt_amsl, "rel_alt": self.rel_alt,
            "roll": self.roll, "pitch": self.pitch, "yaw": self.yaw,
            "vn": self.vn, "ve": self.ve, "vd": self.vd,
            "armed": self.armed, "in_air": self.in_air,
            "flight_mode": self.flight_mode,
        }).encode("utf-8")


# ── 명령 검증 (안전 디폴트: 의심스러우면 reject) ────────────────────────────
def valid_lla(lat, lon, alt) -> bool:
    try:
        lat = float(lat); lon = float(lon); alt = float(alt)
    except (TypeError, ValueError):
        return False
    if not (-90.0 <= lat <= 90.0): return False
    if not (-180.0 <= lon <= 180.0): return False
    if not (MIN_ALT_AMSL <= alt <= MAX_ALT_AMSL): return False
    return True


class Bridge:
    def __init__(self, drone: System, state: TelemetryState):
        self.drone = drone
        self.state = state
        self._busy = False   # mission/goto 동시 실행 직렬화용 가드.

    async def ensure_airborne(self) -> bool:
        """지상/disarm 상태면 arm + takeoff. goto/mission 전 호출."""
        if self.state.in_air:
            return True
        if not AUTO_TAKEOFF:
            log("거부: 드론이 공중에 없음 (AUTO_TAKEOFF=0). 먼저 takeoff 명령 필요.")
            return False
        try:
            if not self.state.armed:
                log("auto: arming")
                await self.drone.action.arm()
            await self.drone.action.set_takeoff_altitude(TAKEOFF_ALT)
            log(f"auto: takeoff {TAKEOFF_ALT} m")
            await self.drone.action.takeoff()
            # 이륙 고도 도달 대기 (간단: rel_alt 가 목표의 80% 넘을 때까지, 최대 20s).
            for _ in range(200):
                if self.state.rel_alt >= TAKEOFF_ALT * 0.8:
                    break
                await asyncio.sleep(0.1)
            return True
        except ActionError as e:
            log(f"auto-takeoff 실패: {e}")
            return False

    async def handle(self, cmd: dict) -> None:
        ctype = cmd.get("type")
        if ctype == "arm":
            await self._safe(self.drone.action.arm(), "arm")
        elif ctype == "takeoff":
            alt = float(cmd.get("alt", TAKEOFF_ALT))
            await self._safe(self.drone.action.set_takeoff_altitude(alt), "set_takeoff_alt")
            await self._safe(self.drone.action.arm(), "arm(takeoff)")
            await self._safe(self.drone.action.takeoff(), "takeoff")
        elif ctype == "land":
            await self._safe(self.drone.action.land(), "land")
        elif ctype == "rtl":
            await self._safe(self.drone.action.return_to_launch(), "rtl")
        elif ctype == "hold":
            await self._safe(self.drone.action.hold(), "hold")
        elif ctype == "goto":
            await self._goto(cmd)
        elif ctype == "mission":
            await self._mission(cmd)
        else:
            log(f"무시: 알 수 없는 명령 type={ctype!r}")

    async def _safe(self, coro, name: str) -> None:
        try:
            await coro
            log(f"ok: {name}")
        except (ActionError, MissionError) as e:
            log(f"실패: {name} → {e}")

    async def _goto(self, cmd: dict) -> None:
        lat, lon, alt = cmd.get("lat"), cmd.get("lon"), cmd.get("alt")
        if not valid_lla(lat, lon, alt):
            log(f"거부: goto 좌표 무효 (lat={lat}, lon={lon}, alt={alt}, "
                f"고도범위 [{MIN_ALT_AMSL},{MAX_ALT_AMSL}])")
            return
        yaw = cmd.get("yaw")
        yaw = float("nan") if yaw is None else float(yaw)   # nan = 현 헤딩 유지
        if not await self.ensure_airborne():
            return
        await self._safe(
            self.drone.action.goto_location(float(lat), float(lon), float(alt), yaw),
            f"goto({lat:.6f},{lon:.6f},{alt:.1f})")

    async def _mission(self, cmd: dict) -> None:
        items = cmd.get("items") or []
        pts = [it for it in items if valid_lla(it.get("lat"), it.get("lon"), it.get("alt"))]
        if len(pts) < len(items):
            log(f"경고: mission item {len(items) - len(pts)}개 무효 좌표로 제외")
        if not pts:
            log("거부: mission 유효 item 없음")
            return
        mitems = [
            MissionItem(
                float(p["lat"]), float(p["lon"]), float(p["alt"]),
                float(p.get("speed", 5.0)),
                True,                       # is_fly_through
                float("nan"), float("nan"),  # gimbal pitch/yaw
                MissionItem.CameraAction.NONE,
                float("nan"),               # loiter time
                float("nan"),               # camera photo interval
                float("nan"),               # acceptance radius
                float("nan"),               # yaw
                float("nan"),               # camera photo distance
                MissionItem.VehicleAction.NONE,
            )
            for p in pts
        ]
        try:
            await self.drone.mission.set_return_to_launch_after_mission(False)
            await self.drone.mission.upload_mission(MissionPlan(mitems))
            log(f"ok: mission upload {len(mitems)} items")
            if not await self.ensure_airborne():
                return
            await self.drone.mission.start_mission()
            log("ok: mission start")
        except (MissionError, ActionError) as e:
            log(f"실패: mission → {e}")


# ── 텔레메트리 구독 태스크들 ────────────────────────────────────────────────
async def sub_position(drone, st):
    async for p in drone.telemetry.position():
        st.lat = p.latitude_deg; st.lon = p.longitude_deg
        st.alt_amsl = p.absolute_altitude_m; st.rel_alt = p.relative_altitude_m
        st.have_position = True

async def sub_attitude(drone, st):
    async for a in drone.telemetry.attitude_euler():
        st.roll = a.roll_deg; st.pitch = a.pitch_deg; st.yaw = a.yaw_deg

async def sub_velocity(drone, st):
    async for v in drone.telemetry.velocity_ned():
        st.vn = v.north_m_s; st.ve = v.east_m_s; st.vd = v.down_m_s

async def sub_armed(drone, st):
    async for armed in drone.telemetry.armed():
        st.armed = armed

async def sub_in_air(drone, st):
    async for in_air in drone.telemetry.in_air():
        st.in_air = in_air

async def sub_flight_mode(drone, st):
    async for fm in drone.telemetry.flight_mode():
        st.flight_mode = str(fm)


async def telemetry_pump(st: TelemetryState, sock: socket.socket):
    """집계된 최신 상태를 고정 주기로 Unity 에 push."""
    period = 1.0 / max(1.0, TELEM_HZ)
    dst = (UNITY_HOST, TELEM_PORT)
    while True:
        if st.have_position:
            try:
                sock.sendto(st.to_json(), dst)
            except OSError as e:
                log(f"텔레 송신 오류: {e}")
        await asyncio.sleep(period)


# ── 명령 수신 (asyncio UDP) ─────────────────────────────────────────────────
class CommandProtocol(asyncio.DatagramProtocol):
    def __init__(self, bridge: Bridge, loop):
        self.bridge = bridge
        self.loop = loop

    def datagram_received(self, data, addr):
        try:
            cmd = json.loads(data.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            log(f"거부: 잘못된 JSON ({len(data)} bytes) from {addr}")
            return
        if not isinstance(cmd, dict):
            log("거부: JSON 객체 아님")
            return
        # 명령 처리는 비동기 — 수신 콜백을 막지 않도록 태스크로 던진다.
        self.loop.create_task(self.bridge.handle(cmd))


async def wait_connected(drone: System, timeout=30) -> bool:
    async def _wait():
        async for s in drone.core.connection_state():
            if s.is_connected:
                return True
    try:
        await asyncio.wait_for(_wait(), timeout=timeout)
        return True
    except asyncio.TimeoutError:
        return False


async def main():
    log(f"MAVLink={MAVLINK_URL}  Unity telem→{UNITY_HOST}:{TELEM_PORT}  cmd←:{CMD_PORT}")
    drone = System()
    await drone.connect(system_address=MAVLINK_URL)
    if not await wait_connected(drone):
        log("FAIL: PX4 연결 못 함 (SITL 기동/포트 확인). 종료.")
        return
    log("PX4 연결됨.")

    st = TelemetryState()
    bridge = Bridge(drone, st)

    tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    loop = asyncio.get_running_loop()
    # 명령 수신 UDP endpoint.
    await loop.create_datagram_endpoint(
        lambda: CommandProtocol(bridge, loop),
        local_addr=("0.0.0.0", CMD_PORT))
    log(f"명령 수신 대기 udp:{CMD_PORT}")

    # 텔레메트리 구독 + 펌프 동시 실행. 하나가 죽어도 전체가 같이 종료되도록 gather.
    await asyncio.gather(
        sub_position(drone, st),
        sub_attitude(drone, st),
        sub_velocity(drone, st),
        sub_armed(drone, st),
        sub_in_air(drone, st),
        sub_flight_mode(drone, st),
        telemetry_pump(st, tx),
    )


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
