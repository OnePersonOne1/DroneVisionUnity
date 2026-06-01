#!/usr/bin/env python3
"""촬영·저장 전용 — 폰 IP Webcam 에서 영상 + 센서 + GPS 만 저장.

RF-DETR 추론도, Unity UDP 송출도 하지 않는다(경량, 추론 의존성 import 안 함).
저장 위치: output/<타임스탬프>/  (image/, sensor/)

추론은 나중에 infer.py 로 그 세션 경로를 넘겨 따로 돌린다:
    python3 capture.py
    python3 infer.py --session ../output/<그 세션>
"""
import IP_webcam as ipw


def main():
    print(f"[{ipw.NOW}] capture 모드 — 영상+센서+GPS 저장만 (추론/UDP 없음)")
    print(f"[연결 확인] {ipw.SENSOR_URL} (timeout={ipw.CONNECTION_PROBE_TIMEOUT}s)")
    if not ipw.probe_ipwebcam():
        raise SystemExit("[오류] IP Webcam 응답 없음 — IP_webcam.py 의 IP_ADDRESS/PORT 확인 후 다시 실행")
    print(f"[연결] OK. 세션 폴더 → {ipw.BASE_DIR}")
    print("       (미리보기 창에서 q 키로 종료)")
    ipw.run_live_capture(enable_inference=False)


if __name__ == "__main__":
    main()
