#!/usr/bin/env python3
"""오프라인 추론 — 저장된 세션 폴더에 RF-DETR 일괄 추론.

    python3 infer.py                                  # output/ 최신 세션
    python3 infer.py --session ../output/20260515_044413
    python3 infer.py --session <경로> --force          # 기존 결과 덮어쓰기
    python3 infer.py --all                             # output/* 전체 세션 배치

결과: <session>/detect_offline/detections_<session>.csv (+ annotated jpg)
다음 단계: projection_pipeline.py --session <경로>  →  replay_offline.py / Unity ProjectionReplay
"""
import argparse
import glob
import os

import IP_webcam as ipw


def _latest_session():
    cands = sorted(
        d for d in glob.glob(os.path.join(ipw.OFFLINE_INPUT_BASE, "*"))
        if os.path.isdir(d) and os.path.isdir(os.path.join(d, "image"))
    )
    return cands[-1] if cands else None


def main():
    p = argparse.ArgumentParser(description="저장된 세션에 RF-DETR 오프라인 추론")
    p.add_argument("--session", default=None,
                   help="세션 폴더 경로 (기본: output/ 아래 가장 최근 세션)")
    p.add_argument("--all", action="store_true",
                   help="output/* 의 모든 세션을 배치 처리")
    p.add_argument("--force", action="store_true",
                   help="이미 detect_offline 결과가 있어도 덮어씀")
    args = p.parse_args()

    if args.all:
        ipw.run_offline_inference()
        return

    session = args.session or _latest_session()
    if not session:
        raise SystemExit(f"[오류] image/ 가 있는 세션이 없습니다: {ipw.OFFLINE_INPUT_BASE}/")
    session = os.path.abspath(session)
    if not os.path.isdir(os.path.join(session, "image")):
        raise SystemExit(f"[오류] '{session}' 에 image/ 폴더가 없습니다.")

    print(f"[infer] 세션: {session}")
    model, box_ann, lbl_ann = ipw.load_rfdetr_model()
    n = ipw.infer_session(session, model, box_ann, lbl_ann, force=args.force)
    print(f"[infer] 완료 — {n} 프레임 처리.")
    print(f"[다음] python3 projection_pipeline.py --session {session}")


if __name__ == "__main__":
    main()
