#!/usr/bin/env python3
"""PID gain sweep — 한 파라미터를 여러 값으로 변경하면서 step response 자동 측정.

각 값마다:
  1) MAVSDK param.set_param_float(name, value).
  2) step_response.main() 호출 → CSV.
  3) 메트릭 계산.
종료 후:
  - 통합 overlay 그래프 (모든 값의 응답을 같은 axes 에).
  - 메트릭 표 (CSV).
포트폴리오: "MC_ROLLRATE_P 를 0.10 / 0.15 / 0.20 / 0.25 로 변경 시 overshoot 과 settling time
어떻게 변하는지 — 한눈에 보이는 비교".

usage:
  python3 pid_sweep.py --param MC_ROLLRATE_P --values 0.10,0.15,0.20,0.25
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
OUT = ROOT / "pid_sweep"
OUT.mkdir(exist_ok=True)


async def run_one(drone: System, axis: str, amplitude: float, hover_alt: float,
                  duration: float) -> tuple[np.ndarray, np.ndarray, float, float]:
    """단일 step 측정 → (t, series, t_step, baseline)."""
    sp_hover = PositionNedYaw(0.0, 0.0, -hover_alt, 0.0)
    await drone.offboard.set_position_ned(sp_hover)
    try: await drone.offboard.start()
    except OffboardError: pass
    await asyncio.sleep(2)

    records: list[dict] = []
    t0 = time.time()
    stop = asyncio.Event()

    async def collect():
        async for pv in drone.telemetry.position_velocity_ned():
            if stop.is_set(): break
            records.append({'t': time.time() - t0,
                            'n': pv.position.north_m,
                            'e': pv.position.east_m,
                            'd': pv.position.down_m})

    task = asyncio.create_task(collect())
    await asyncio.sleep(1)
    sp_step = {
        'x': PositionNedYaw( amplitude, 0.0, -hover_alt, 0.0),
        'y': PositionNedYaw(0.0,  amplitude, -hover_alt, 0.0),
        'z': PositionNedYaw(0.0, 0.0, -(hover_alt + amplitude), 0.0),
    }[axis]
    t_step = time.time() - t0
    await drone.offboard.set_position_ned(sp_step)
    await asyncio.sleep(duration)
    await drone.offboard.set_position_ned(sp_hover)
    await asyncio.sleep(3)
    stop.set(); await task

    t = np.array([r['t'] for r in records])
    n = np.array([r['n'] for r in records])
    e = np.array([r['e'] for r in records])
    d = np.array([r['d'] for r in records])
    series = {'x': n, 'y': e, 'z': -d}[axis]
    pre_idx = max(0, np.searchsorted(t, t_step) - 1)
    baseline = float(np.mean(series[max(0, pre_idx - 5):pre_idx + 1]))
    return t, series - baseline, t_step, baseline


def compute_metrics(t_rel: np.ndarray, sig: np.ndarray, target: float) -> dict:
    mask = t_rel >= 0
    tr, sr = t_rel[mask], sig[mask]
    if len(sr) == 0 or target == 0:
        return dict(overshoot_pct=float('nan'), rise=float('nan'),
                    settling=float('nan'), ss_error=float('nan'))
    peak = sr.max()
    overshoot_pct = max(0.0, (peak - target) / target * 100.0)
    def first_cross(level):
        idx = np.where(sr >= level)[0]
        return tr[idx[0]] if len(idx) else float('nan')
    t10, t90 = first_cross(0.1 * target), first_cross(0.9 * target)
    rise = t90 - t10 if not (np.isnan(t10) or np.isnan(t90)) else float('nan')
    band = 0.05 * abs(target)
    in_band = np.abs(sr - target) <= band
    settling = float('nan')
    for i in range(len(tr)):
        if in_band[i] and in_band[i:].all():
            settling = float(tr[i]); break
    return dict(overshoot_pct=round(overshoot_pct, 2),
                rise=round(rise, 3),
                settling=round(settling, 3),
                ss_error=round(abs(sr[-1] - target), 3))


async def main(param: str, values: list[float], axis: str, amplitude: float,
               hover_alt: float, duration: float, conn: str) -> None:
    drone = System()
    print(f"[sweep] connecting {conn} …")
    await drone.connect(system_address=conn)
    async for s in drone.core.connection_state():
        if s.is_connected:
            print("[sweep] connected"); break

    await drone.action.set_takeoff_altitude(hover_alt)
    await drone.action.arm()
    await drone.action.takeoff()
    await asyncio.sleep(10)

    # 원래 값 저장 → 종료 시 복귀.
    orig = (await drone.param.get_param_float(param))
    print(f"[sweep] {param} default = {orig}")

    results = []
    for v in values:
        print(f"\n[sweep] === {param} = {v} ===")
        await drone.param.set_param_float(param, float(v))
        await asyncio.sleep(2)
        t, sig, t_step, baseline = await run_one(drone, axis, amplitude, hover_alt, duration)
        m = compute_metrics(t - t_step, sig, amplitude)
        results.append((v, t - t_step, sig, m))
        print(f"[sweep] {param}={v}  overshoot={m['overshoot_pct']}% "
              f"rise={m['rise']}s settling={m['settling']}s ss_err={m['ss_error']}m")

    # 원래 값 복귀.
    await drone.param.set_param_float(param, orig)
    await drone.offboard.stop()
    await drone.action.land()

    # ────────── 비교 그래프 + 메트릭 표 ──────────
    ts = time.strftime("%Y%m%d_%H%M%S")
    label = f"sweep_{param}_{axis}_{ts}"
    base = OUT / label

    try: plt.style.use('seaborn-v0_8-darkgrid')
    except Exception: plt.style.use('ggplot')
    fig, ax = plt.subplots(figsize=(10, 6))
    cmap = plt.cm.viridis(np.linspace(0.15, 0.85, len(results)))

    for (v, tr, sig, _m), color in zip(results, cmap):
        ax.plot(tr, sig, linewidth=1.8, color=color, label=f"{param}={v}")
    ax.axhline(amplitude, linestyle=':', color='black', alpha=0.6,
               label=f'target = {amplitude}m')
    band = 0.05 * abs(amplitude)
    ax.fill_between([-1, max(r[1].max() for r in results)],
                    amplitude - band, amplitude + band, color='green', alpha=0.07)
    ax.set_xlabel('time since step (s)')
    ax.set_ylabel(f'response on axis {axis} (m, baseline-shifted)')
    ax.set_title(f"PID gain sweep — {param} effect on position step response\n"
                 f"(axis {axis}, amplitude {amplitude} m)")
    ax.legend(loc='lower right', fontsize=9)
    ax.set_xlim(-0.5, None)
    plt.tight_layout()
    plt.savefig(f"{base}.png", dpi=140, bbox_inches='tight')
    plt.close()
    print(f"\n[sweep] overlay plot → {base}.png")

    # 메트릭 CSV.
    with open(f"{base}_metrics.csv", 'w', newline='') as f:
        w = csv.writer(f)
        w.writerow([param, 'overshoot_pct', 'rise_time(10-90)', 'settling_time(±5%)', 'ss_error'])
        for v, _t, _s, m in results:
            w.writerow([v, m['overshoot_pct'], m['rise'], m['settling'], m['ss_error']])
    print(f"[sweep] metrics → {base}_metrics.csv")

    # 메트릭 표 출력.
    print("\n[sweep] summary:")
    print(f"  {param:>18s}  overshoot   rise   settling   ss_err")
    for v, _t, _s, m in results:
        print(f"  {v:>18g}  {m['overshoot_pct']:>7.1f}%  {str(m['rise']):>6s}s  "
              f"{str(m['settling']):>8s}s  {m['ss_error']:>7.3f}m")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--param", required=True, help="예: MC_ROLLRATE_P / MPC_XY_P / MPC_Z_P")
    p.add_argument("--values", required=True,
                   help="콤마 분리 값. 예: 0.10,0.15,0.20,0.25")
    p.add_argument("--axis", choices=['x', 'y', 'z'], default='x')
    p.add_argument("--amplitude", type=float, default=10.0)
    p.add_argument("--hover-alt", type=float, default=5.0)
    p.add_argument("--duration", type=float, default=10.0)
    p.add_argument("--conn", default="udp://0.0.0.0:14540")
    a = p.parse_args()
    vals = [float(x) for x in a.values.split(',')]
    asyncio.run(main(a.param, vals, a.axis, a.amplitude, a.hover_alt, a.duration, a.conn))
