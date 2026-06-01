"""Sanity-check 시각화: projection.csv의 ray들을 평지(z=0)에 투영해 2D 플롯.

Unity 없이 기하학이 합리적인지 확인하는 사전 점검용. 실제 fbx terrain은
Unity의 Physics.Raycast가 처리.
"""

import argparse
import csv
import glob
import json
import os
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np

PROJECT_ROOT = Path(__file__).resolve().parent.parent

CLASS_COLOR = {
    "human": "green", "fire_region": "red", "smoke_region": "gray",
    "vehicle": "cyan", "building": "gold", "lake": "blue",
}


def find_latest_session():
    output_root = str(PROJECT_ROOT / "output")
    sessions = sorted(d for d in glob.glob(os.path.join(output_root, "*")) if os.path.isdir(d))
    if not sessions:
        raise SystemExit(f"No sessions under {output_root}")
    return sessions[-1]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--session", default=None)
    p.add_argument("--cam-height", type=float, default=30.0,
                   help="origin과 cam GPS_alt가 같을 때 사용할 가상 카메라 고도 (m)")
    p.add_argument("--out", default=None)
    args = p.parse_args()

    session = args.session or find_latest_session()
    proj_dir = os.path.join(session, "projection")
    proj_csv = sorted(glob.glob(os.path.join(proj_dir, "projection_*.csv")))[0]
    meta_json = sorted(glob.glob(os.path.join(proj_dir, "meta_*.json")))[0]
    print(f"[viz] {proj_csv}")

    with open(meta_json) as f:
        meta = json.load(f)

    origins, dirs, classes, confs = [], [], [], []
    with open(proj_csv, encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            origins.append([float(row["ox"]), float(row["oy"]), float(row["oz"])])
            dirs.append([float(row["dx"]), float(row["dy"]), float(row["dz"])])
            classes.append(row["class_name"])
            confs.append(float(row["confidence"]))
    origins = np.array(origins)
    dirs = np.array(dirs)
    n = len(origins)
    if n == 0:
        raise SystemExit("No detections in projection CSV.")

    # Stationary capture (oy ≈ 0)이면 합리적 시각화를 위해 카메라를 위로 들어올림.
    if origins[:, 1].max() < 1.0:
        cam = origins.copy()
        cam[:, 1] += args.cam_height
        print(f"[viz] all oy ≈ 0 → lifted camera by +{args.cam_height}m for display")
    else:
        cam = origins

    # Ray-ground intersect at Y = 0
    t = np.where(np.abs(dirs[:, 1]) > 1e-9,
                 (0.0 - cam[:, 1]) / dirs[:, 1], -1.0)
    hits = cam + t[:, None] * dirs
    valid = t > 0

    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(14, 7))

    seen = set()
    for i in range(n):
        if not valid[i]:
            continue
        color = CLASS_COLOR.get(classes[i], "magenta")
        ax1.plot([cam[i, 0], hits[i, 0]], [cam[i, 2], hits[i, 2]],
                 color=color, alpha=0.4, linewidth=0.8)
        label = classes[i] if classes[i] not in seen else None
        ax1.scatter(hits[i, 0], hits[i, 2], color=color, s=35,
                    edgecolor="black", linewidth=0.4, zorder=3, label=label)
        seen.add(classes[i])
        ax2.plot([cam[i, 0], hits[i, 0]], [cam[i, 1], hits[i, 1]],
                 color=color, alpha=0.4, linewidth=0.8)

    cam_mean_x = cam[:, 0].mean()
    cam_mean_y = cam[:, 1].mean()
    cam_mean_z = cam[:, 2].mean()
    ax1.scatter(cam_mean_x, cam_mean_z, marker="X", color="black", s=220,
                zorder=4, label="camera")
    ax2.scatter(cam_mean_x, cam_mean_y, marker="X", color="black", s=220,
                zorder=4, label="camera")
    ax2.axhline(y=0, color="brown", linestyle="--", alpha=0.6, label="ground")

    ax1.set_xlabel("X (East, m)")
    ax1.set_ylabel("Z (North, m)")
    ax1.set_title(f"Top-down  |  cam y={cam_mean_y:.1f}m  |  "
                  f"{int(valid.sum())}/{n} rays hit ground")
    ax1.legend(loc="best", fontsize=8)
    ax1.set_aspect("equal")
    ax1.grid(True, alpha=0.3)

    ax2.set_xlabel("X (East, m)")
    ax2.set_ylabel("Y (Up, m)")
    ax2.set_title("Side view")
    ax2.legend(loc="best", fontsize=8)
    ax2.grid(True, alpha=0.3)

    plt.suptitle(f"{os.path.basename(session)}  origin GPS "
                 f"({meta['origin_lat']:.5f}, {meta['origin_lng']:.5f}, "
                 f"{meta['origin_alt']:.1f}m)")
    plt.tight_layout()

    out = args.out or os.path.join(proj_dir, f"sanity_{os.path.basename(session)}.png")
    plt.savefig(out, dpi=120)
    print(f"[viz] saved {out}")


if __name__ == "__main__":
    main()
