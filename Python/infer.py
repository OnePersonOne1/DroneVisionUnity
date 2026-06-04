#!/usr/bin/env python3
"""오프라인 추론 — 저장된 세션 폴더에 RF-DETR 일괄 추론.

    # 커스텀 ckpt (기본, 6-class smoke/fire/...):
    python3 infer.py                                  # output/ 최신 세션
    python3 infer.py --session ../output/20260515_044413
    python3 infer.py --session <경로> --force          # 기존 결과 덮어쓰기
    python3 infer.py --all                             # output/* 전체 세션 배치
    python3 infer.py --last 2                          # 최신 2 세션만

    # COCO pretrained (80-class) 비교 추론:
    python3 infer.py --coco --last 2                   # 최신 2 세션, detect_offline_coco/
    python3 infer.py --coco --all                      # 전 세션 COCO
    python3 infer.py --coco --session <경로>           # 단일 세션

결과:
  - 커스텀: <session>/detect_offline/detections_<session>.csv (+ jpg)
  - COCO  : <session>/detect_offline_coco/detections_<session>_coco.csv (+ jpg)

다음 단계: projection_pipeline.py --session <경로>  →  replay_offline.py / Unity ProjectionReplay
(projection_pipeline.py 는 detect_offline/ 만 읽음 — COCO 결과는 비교/시각화 용도)
"""
import argparse
import glob
import os

import IP_webcam as ipw


def _all_sessions():
    return sorted(
        d for d in glob.glob(os.path.join(ipw.OFFLINE_INPUT_BASE, "*"))
        if os.path.isdir(d) and os.path.isdir(os.path.join(d, "image"))
    )


def _latest_session():
    cands = _all_sessions()
    return cands[-1] if cands else None


def main():
    p = argparse.ArgumentParser(description="저장된 세션에 RF-DETR 오프라인 추론")
    p.add_argument("--session", default=None,
                   help="세션 폴더 경로 (기본: output/ 아래 가장 최근 세션)")
    p.add_argument("--all", action="store_true",
                   help="output/* 의 모든 세션을 배치 처리")
    p.add_argument("--last", type=int, default=None,
                   help="최신 N 개 세션만 처리 (예: --last 2). --all/--session 과 함께 쓸 수 없음")
    p.add_argument("--coco", action="store_true",
                   help="COCO pretrained weight 로 추론 → detect_offline_coco/ 에 저장")
    p.add_argument("--force", action="store_true",
                   help="이미 detect_offline 결과가 있어도 덮어씀")
    args = p.parse_args()

    out_suffix = "_coco" if args.coco else ""

    # --all 또는 --last 는 배치.
    if args.all or args.last:
        ipw.run_offline_inference(use_coco=args.coco, last=args.last, force=args.force)
        return

    session = args.session or _latest_session()
    if not session:
        raise SystemExit(f"[오류] image/ 가 있는 세션이 없습니다: {ipw.OFFLINE_INPUT_BASE}/")
    session = os.path.abspath(session)
    if not os.path.isdir(os.path.join(session, "image")):
        raise SystemExit(f"[오류] '{session}' 에 image/ 폴더가 없습니다.")

    from coco_classes import COCO_CLASSES
    classes = COCO_CLASSES if args.coco else ipw.CUSTOM_CLASSES
    label = "COCO pretrained" if args.coco else "커스텀 ckpt"

    print(f"[infer] 세션: {session}  ({label}, suffix='{out_suffix}')")
    model, box_ann, lbl_ann = ipw.load_rfdetr_model(use_coco=args.coco)
    n = ipw.infer_session(session, model, box_ann, lbl_ann,
                          force=args.force, classes=classes, out_suffix=out_suffix)
    print(f"[infer] 완료 — {n} 프레임 처리.")
    if not args.coco:
        print(f"[다음] python3 projection_pipeline.py --session {session}")


if __name__ == "__main__":
    main()
