#!/usr/bin/env python3
"""PX4 SITL position step response — 포트폴리오 산출물 자동 생성.

자동 흐름:
  1) MAVSDK 로 PX4 연결.
  2) Arm → takeoff (hover-alt).
  3) Offboard 모드 진입, 호버 setpoint 안정.
  4) Position setpoint **step** (axis × amplitude).
  5) Telemetry 수집 (position + velocity, NED).
  6) Land + offboard stop.
  7) CSV 저장, 메트릭 계산, matplotlib 그래프 출력.

산출 메트릭 (제어공학 표준):
  - **Overshoot (%)** — peak vs target.
  - **Rise time** (10% → 90% 도달 시간).
  - **Settling time (±5%)** — 정상상태 진입 시각.
  - **Steady-state error** — 마지막 잔류 오차.

usage:
  # QGC 닫혀 있을 때 (14540 자유):
  python3 step_response.py --axis x --amplitude 10
  # QGC 떠 있을 때 (14580 으로 우회 — PX4 instance 1 의 alt listen):
  python3 step_response.py --axis z --amplitude 5 --conn udpout://127.0.0.1:14580
"""
from __future__ import annotations
import argparse
import asyncio
import csv
import time
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from mavsdk import System
from mavsdk.offboard import OffboardError, PositionNedYaw

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "step_responses"
OUT.mkdir(exist_ok=True)


async def main(axis: str, amplitude: float, hover_alt: float,
               duration: float, conn: str) -> None:
    drone = System()
    print(f"[step] connecting {conn} …")
    await drone.connect(system_address=conn)
    async for s in drone.core.connection_state():
        if s.is_connected:
            print("[step] connected"); break

    # arm + takeoff
    print(f"[step] arming + takeoff → {hover_alt:.1f} m AGL")
    await drone.action.set_takeoff_altitude(hover_alt)
    await drone.action.arm()
    await drone.action.takeoff()
    await asyncio.sleep(10)         # 호버 안정 대기.

    # offboard prime
    sp_hover = PositionNedYaw(0.0, 0.0, -hover_alt, 0.0)
    await drone.offboard.set_position_ned(sp_hover)
    try:
        await drone.offboard.start()
    except OffboardError as e:
        print(f"[step] offboard 실패: {e._result.result}")
        await drone.action.land()
        return
    await asyncio.sleep(2)

    # 텔레메트리 수집 (position + velocity NED, 동시 stream).
    records: list[dict] = []
    t0 = time.time()
    stop = asyncio.Event()

    async def collect_pv() -> None:
        async for pv in drone.telemetry.position_velocity_ned():
            if stop.is_set(): break
            records.append({
                't': time.time() - t0,
                'n': pv.position.north_m,
                'e': pv.position.east_m,
                'd': pv.position.down_m,
                'vn': pv.velocity.north_m_s,
                've': pv.velocity.east_m_s,
                'vd': pv.velocity.down_m_s,
            })

    pv_task = asyncio.create_task(collect_pv())
    await asyncio.sleep(1.0)          # baseline 1초.

    # step 입력.
    sp_step = {
        'x': PositionNedYaw( amplitude, 0.0, -hover_alt, 0.0),
        'y': PositionNedYaw(0.0,  amplitude, -hover_alt, 0.0),
        'z': PositionNedYaw(0.0, 0.0, -(hover_alt + amplitude), 0.0),
    }[axis]
    t_step = time.time() - t0
    print(f"[step] STEP axis={axis} amplitude=+{amplitude:g} m @ t={t_step:.2f}s")
    await drone.offboard.set_position_ned(sp_step)

    await asyncio.sleep(duration)

    # 호버 복귀 + 정리.
    await drone.offboard.set_position_ned(sp_hover)
    await asyncio.sleep(3)
    stop.set()
    await pv_task

    try:    await drone.offboard.stop()
    except OffboardError as e: print(f"[step] offboard stop 실패: {e._result.result}")
    await drone.action.land()
    print("[step] landing …")
    await asyncio.sleep(8)

    # ────────── 분석 + 저장 ──────────
    ts = time.strftime("%Y%m%d_%H%M%S")
    label = f"step_{axis}_{amplitude:g}m_{ts}"
    base = OUT / label

    with open(f"{base}.csv", 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=records[0].keys())
        w.writeheader(); w.writerows(records)
    print(f"[step] CSV  → {base}.csv  ({len(records)} samples)")

    t = np.array([r['t'] for r in records])
    n = np.array([r['n'] for r in records])
    e = np.array([r['e'] for r in records])
    d = np.array([r['d'] for r in records])
    series = {'x': n, 'y': e, 'z': -d}[axis]

    # baseline = step 직전 평균.
    pre_idx = np.searchsorted(t, t_step) - 1
    pre_idx = max(0, pre_idx)
    baseline = float(np.mean(series[max(0, pre_idx - 5):pre_idx + 1]))

    sig = series - baseline
    target = amplitude
    metrics = compute_metrics(t - t_step, sig, target)
    print("[step] metrics:")
    for k, v in metrics.items():
        print(f"        {k:18s} = {v}")

    plot(base, t, n, e, d, axis, amplitude, t_step, baseline, metrics)
    print(f"[step] plot → {base}.png")


def compute_metrics(t_rel: np.ndarray, sig: np.ndarray, target: float) -> dict:
    """제어공학 표준 step response 메트릭."""
    mask = t_rel >= 0
    tr, sr = t_rel[mask], sig[mask]
    if len(sr) == 0 or target == 0:
        return dict(overshoot_pct=float('nan'), rise_time=float('nan'),
                    settling_time=float('nan'), peak=float('nan'),
                    ss_error=float('nan'))
    peak = float(sr.max())
    overshoot_pct = max(0.0, (peak - target) / target * 100.0)

    # rise 10 → 90%
    def first_cross(level: float) -> float:
        idx = np.where(sr >= level)[0]
        return float(tr[idx[0]]) if len(idx) else float('nan')
    t10, t90 = first_cross(0.1 * target), first_cross(0.9 * target)
    rise = t90 - t10 if not (np.isnan(t10) or np.isnan(t90)) else float('nan')

    # settling ±5% — 마지막 진입 후 유지.
    band = 0.05 * abs(target)
    in_band = np.abs(sr - target) <= band
    settling = float('nan')
    for i in range(len(tr)):
        if in_band[i] and in_band[i:].all():
            settling = float(tr[i]); break

    ss_error = float(abs(sr[-1] - target))
    return dict(overshoot_pct=round(overshoot_pct, 2),
                rise_time=round(rise, 3),
                settling_time=round(settling, 3),
                peak=round(peak, 3),
                ss_error=round(ss_error, 3))


def plot(base: Path, t: np.ndarray, n: np.ndarray, e: np.ndarray, d: np.ndarray,
         axis: str, amplitude: float, t_step: float, baseline: float, m: dict) -> None:
    try: plt.style.use('seaborn-v0_8-darkgrid')
    except Exception: plt.style.use('ggplot')
    fig, (ax_main, ax_sub) = plt.subplots(2, 1, figsize=(10, 7), sharex=True,
                                          gridspec_kw={'height_ratios': [3, 1.2]})

    series = {'x': n, 'y': e, 'z': -d}[axis]
    ylabel = {'x': 'North (m)', 'y': 'East (m)', 'z': 'Altitude AGL (m)'}[axis]
    target_line = baseline + amplitude

    ax_main.plot(t, series, linewidth=1.7, color='#1f77b4', label='measured')
    ax_main.axvline(t_step, linestyle='--', color='red', alpha=0.5, label='step time')
    ax_main.axhline(target_line, linestyle=':', color='#2ca02c', alpha=0.7,
                    label=f'target = {target_line:.2f}')
    ax_main.axhline(baseline, linestyle=':', color='gray', alpha=0.4,
                    label=f'baseline = {baseline:.2f}')
    # settling band
    band = 0.05 * abs(amplitude)
    ax_main.fill_between(t, target_line - band, target_line + band,
                         color='green', alpha=0.07, label='±5 % band')
    ax_main.set_ylabel(ylabel)
    ax_main.set_title(f"PX4 SITL position step response — axis={axis}, "
                      f"amplitude={amplitude:g} m")
    ax_main.legend(loc='lower right', fontsize=9)

    txt = (
        f"overshoot      = {m['overshoot_pct']} %\n"
        f"rise (10→90)   = {m['rise_time']} s\n"
        f"settling (±5%) = {m['settling_time']} s\n"
        f"steady-state e = {m['ss_error']} m"
    )
    ax_main.text(0.02, 0.97, txt, transform=ax_main.transAxes,
                 verticalalignment='top', fontsize=10, family='monospace',
                 bbox=dict(boxstyle='round,pad=0.45',
                          facecolor='white', alpha=0.88))

    # 부 축 — 다른 두 축의 cross-coupling.
    others = {'x': [(e, 'East'),  (-d, 'Alt')],
              'y': [(n, 'North'), (-d, 'Alt')],
              'z': [(n, 'North'), (e,  'East')]}[axis]
    for sig, lbl in others:
        ax_sub.plot(t, sig - sig[0], label=lbl, linewidth=1.2)
    ax_sub.axhline(0, color='gray', alpha=0.4)
    ax_sub.set_xlabel('time (s)')
    ax_sub.set_ylabel('cross-coupling (m)')
    ax_sub.legend(loc='upper right', fontsize=9)

    plt.tight_layout()
    plt.savefig(f"{base}.png", dpi=140, bbox_inches='tight')
    plt.close()


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--axis", choices=['x', 'y', 'z'], default='x',
                   help="step 축. x=North(전진), y=East, z=Up(고도)")
    p.add_argument("--amplitude", type=float, default=10.0, help="step 진폭 (m)")
    p.add_argument("--hover-alt", type=float, default=5.0, help="호버 고도 (m AGL)")
    p.add_argument("--duration", type=float, default=10.0, help="응답 측정 시간 (s)")
    p.add_argument("--conn", default="udp://0.0.0.0:14540",
                   help="MAVSDK connection. QGC 떠 있으면 'udpout://127.0.0.1:14580'")
    a = p.parse_args()
    asyncio.run(main(a.axis, a.amplitude, a.hover_alt, a.duration, a.conn))
