import cv2
import requests
import time
import csv
import json
import os
import re
import glob
import threading
import queue
import numpy as np
from datetime import datetime, timezone, timedelta
from pathlib import Path
from PIL import Image

PROJECT_ROOT = Path(__file__).resolve().parent.parent
# 무거운 추론 의존성(supervision, rfdetr_plus)은 load_rfdetr_model() 안에서 지연 import.
# → capture.py 가 IP_webcam 을 import 해도 RF-DETR 스택을 끌어오지 않아 경량 유지.

from geo_projection import (
    R_phone_cam_landscape_left,
    intrinsics_from_fov,
    unity_ray_from_pixel,
    unity_camera_axes_from_quat,
)
from udp_sender import UdpSender

# ================= 설정 (Configuration) =================
IP_ADDRESS = "100.95.102.89" # 스마트폰 IP로 변경
PORT = "8080"
VIDEO_URL = f"http://{IP_ADDRESS}:{PORT}/video"
SENSOR_URL = f"http://{IP_ADDRESS}:{PORT}/sensors.json"
GPS_URL    = f"http://{IP_ADDRESS}:{PORT}/gps.json"

# RF-DETR 추론 설정
MODEL_WEIGHTS = str(PROJECT_ROOT / "Models" / "checkpoint_best_total.pth")
INFERENCE_THRESHOLD = 0.5
CUSTOM_CLASSES = {
    0: "smoke_region",
    1: "lake",
    2: "fire_region",
    3: "human",
    4: "building",
    5: "vehicle",
}

# 세션 타임스탬프는 시스템 TZ와 무관하게 항상 한국 표준시(KST, UTC+9, DST 없음).
KST = timezone(timedelta(hours=9))
NOW = datetime.now(KST).strftime("%Y%m%d_%H%M%S")
BASE_DIR   = os.path.join(str(PROJECT_ROOT), "output", NOW)
FRAMES_DIR = os.path.join(BASE_DIR, "image")
SENSOR_DIR = os.path.join(BASE_DIR, "sensor")
DETECT_DIR = os.path.join(BASE_DIR, "detect")
CSV_FILENAME = os.path.join(SENSOR_DIR, f"sensor_data_{NOW}.csv")
DETECT_CSV   = os.path.join(DETECT_DIR, f"detections_{NOW}.csv")

# 오프라인 (기존 output 자료 일괄 추론) 모드: output/{session}/detect_offline/ 하위에 저장
# (라이브 모드의 detect/와 한 세션 폴더 안에서 일관되게 유지)
OFFLINE_INPUT_BASE = str(PROJECT_ROOT / "output")
OFFLINE_SUBDIR     = "detect_offline"
CONNECTION_PROBE_TIMEOUT = 2.0  # IP Webcam 가용성 1회 확인 시 타임아웃 (초)

# --- UDP wire-up (live mode → Unity ProjectionUdpReceiver) ---
UDP_ENABLED = True
UDP_HOST = "127.0.0.1"
UDP_PORT = 9870
UDP_CAM_WIDTH = 3840
UDP_CAM_HEIGHT = 2160
UDP_CAM_FOV_H_DEG = 84.0  # Samsung Galaxy Z Flip 7 후면 메인 카메라 추정값
UDP_ORIENTATION = "landscape-left"
# 실시간 동기화 모드 — True 면 추론 결과 검출 0개 프레임도 UDP 송신(detections=[])
# 하여 Unity 가 캡처 타임라인의 공백을 그대로 본다. 기존 동작(검출 있을 때만 송신)
# 으로 돌리려면 False.
UDP_REALTIME_MODE = True

# Sensor list indices (raw IP Webcam JSON order, 0-based after extract_values).
QUAT_OFFSET = 16   # qx, qy, qz, qw occupy 16..19
PROJECTION_SUBDIR = "projection"

latest_sensor_data = []
sensor_timestamp = 0
latest_gps_data = {}   # keys: lat, lng, alt, speed, bearing, accuracy
is_running = True

# maxsize=1: 워커가 따라가지 못하는 프레임은 드롭하고 항상 최신 1장만 추론 대기
# Tuple: (frame_id, sys_time, frame_bgr, quat_xyzw|None, gps_lat_lng_alt|None)
inference_queue: "queue.Queue[tuple[int, float, np.ndarray, tuple|None, tuple|None]]" = queue.Queue(maxsize=1)

DETECT_HEADERS = [
    "frame_id", "sys_time",
    "class_id", "class_name", "confidence",
    "x1", "y1", "x2", "y2",
]

SENSOR_HEADERS = [
    "Ax (m/s²)", "Ay (m/s²)", "Az (m/s²)",
    "Mx (µT)", "My (µT)", "Mz (µT)",
    "GYRx (rad/s)", "GYRy (rad/s)", "GYRz (rad/s)",
    "pressure (mbar)",
    "Gx (m/s²)", "Gy (m/s²)", "Gz (m/s²)",
    "LAx (m/s²)", "LAy (m/s²)", "LAz (m/s²)",
    "x*sin(θ/2)", "y*sin(θ/2)", "z*sin(θ/2)", "cos(θ/2)",
    "Accuracy"
]

GPS_HEADERS = [
    "GPS_lat", "GPS_lng", "GPS_alt (m)", "GPS_accuracy (m)"
]

def extract_values(data_dict):
    values = []
    ts = None
    for sensor_name, sensor_info in data_dict.items():
        if 'data' in sensor_info and len(sensor_info['data']) > 0:
            data_point = sensor_info['data'][0]
            if ts is None:
                ts = data_point[0]
            sensor_vals = data_point[1]
            if isinstance(sensor_vals, list):
                values.extend(sensor_vals)
            else:
                values.append(sensor_vals)
    return ts, values

def parse_gps(resp_json):
    # 실제 응답: {"gps": {"latitude":..., "longitude":..., "altitude":..., "accuracy":...}, "network": {...}}
    gps = resp_json.get("gps", {})
    return {
        "lat":      gps.get("latitude",  ""),
        "lng":      gps.get("longitude", ""),
        "alt":      gps.get("altitude",  ""),
        "accuracy": gps.get("accuracy",  ""),
    }

def sensor_worker():
    global latest_sensor_data, sensor_timestamp, is_running
    while is_running:
        try:
            response = requests.get(SENSOR_URL, timeout=5)
            if response.status_code == 200:
                ts, vals = extract_values(response.json())
                if ts is not None:
                    sensor_timestamp = ts
                    latest_sensor_data = vals
        except requests.exceptions.RequestException:
            pass
        time.sleep(0.05)

def gps_worker():
    global latest_gps_data, is_running
    while is_running:
        try:
            gps_resp = requests.get(GPS_URL, timeout=2)
            if gps_resp.status_code == 200:
                latest_gps_data = parse_gps(gps_resp.json())
        except requests.exceptions.RequestException:
            pass
        time.sleep(1)

def probe_ipwebcam():
    """IP Webcam 서버 가용성 1회 확인 (짧은 타임아웃).

    Returns:
        True if SENSOR_URL이 200을 돌려주면, 아니면 False.
    """
    try:
        r = requests.get(SENSOR_URL, timeout=CONNECTION_PROBE_TIMEOUT)
        return r.status_code == 200
    except requests.exceptions.RequestException:
        return False

def _parse_frame_id_from_name(filename):
    """'frame_000123.jpg' → 123. 매칭 실패 시 None."""
    m = re.search(r"(\d+)", os.path.basename(filename))
    return int(m.group(1)) if m else None

def infer_session(session_path, model, box_ann, lbl_ann, force=False):
    """한 세션(output/<session>/image/*.jpg)에 RF-DETR 일괄 추론.

    결과: output/<session>/detect_offline/
      - det_frame_{id}.jpg (annotated)
      - detections_{session}.csv
    이미 CSV가 있으면 skip (force=True면 덮어씀). 처리한 프레임 수를 반환.
    """
    session_name = os.path.basename(session_path.rstrip(os.sep))
    image_dir = os.path.join(session_path, "image")
    out_dir = os.path.join(session_path, OFFLINE_SUBDIR)
    out_csv = os.path.join(out_dir, f"detections_{session_name}.csv")

    if os.path.exists(out_csv) and not force:
        print(f"  [skip] {session_name}: 이미 결과 존재 ({out_csv}) — 덮어쓰려면 force/--force")
        return 0

    image_paths = sorted(glob.glob(os.path.join(image_dir, "*.jpg")))
    if not image_paths:
        print(f"  [skip] {session_name}: 이미지 0장")
        return 0

    os.makedirs(out_dir, exist_ok=True)
    print(f"  [{session_name}] {len(image_paths)}장 처리 시작 → {out_dir}")

    n = 0
    with open(out_csv, mode='w', newline='', encoding='utf-8-sig') as f:
        writer = csv.writer(f)
        writer.writerow(DETECT_HEADERS)

        for i, img_path in enumerate(image_paths):
            frame_bgr = cv2.imread(img_path)
            if frame_bgr is None:
                print(f"    [경고] 읽기 실패: {img_path}")
                continue

            frame_id = _parse_frame_id_from_name(img_path)
            if frame_id is None:
                frame_id = i
            # 오프라인은 캡처 sys_time이 없으므로 파일 mtime 사용 (sensor CSV의 sys_time과 매칭 안 됨 — frame_id로 조인)
            sys_time = os.path.getmtime(img_path)

            try:
                detections, annotated = predict_and_annotate(frame_bgr, model, box_ann, lbl_ann)
            except Exception as e:
                print(f"    [추론 오류] {img_path}: {type(e).__name__} - {e}")
                continue

            out_img = os.path.join(out_dir, f"det_frame_{frame_id:06d}.jpg")
            cv2.imwrite(out_img, cv2.cvtColor(annotated, cv2.COLOR_RGB2BGR))

            write_detection_rows(writer, frame_id, sys_time, detections)

            if (i + 1) % 25 == 0 or (i + 1) == len(image_paths):
                f.flush()
                print(f"    {i + 1}/{len(image_paths)} ...")

            n += 1

    print(f"  [{session_name}] 완료. ({n} 프레임)")
    return n


def run_offline_inference():
    """output/* 의 모든 세션 일괄 추론 (배치). 단일 세션은 infer.py / infer_session 사용."""
    sessions = sorted(
        d for d in glob.glob(os.path.join(OFFLINE_INPUT_BASE, "*"))
        if os.path.isdir(d) and os.path.isdir(os.path.join(d, "image"))
    )
    if not sessions:
        print(f"[오프라인] '{OFFLINE_INPUT_BASE}/' 하위에 image/가 있는 세션이 없습니다. 처리할 데이터가 없습니다.")
        return

    print(f"[오프라인] 처리 대상 세션 {len(sessions)}개 발견.")
    model, box_ann, lbl_ann = load_rfdetr_model()

    total_processed = 0
    for session_path in sessions:
        total_processed += infer_session(session_path, model, box_ann, lbl_ann)

    print(f"\n[오프라인] 전체 완료. 총 {total_processed} 프레임 처리됨. 결과: ./{OFFLINE_INPUT_BASE}/<session>/{OFFLINE_SUBDIR}/")

def load_rfdetr_model():
    """RF-DETR 모델과 supervision annotator들을 로드. (라이브/오프라인 공용)"""
    import supervision as sv
    from rfdetr_plus import RFDETR2XLarge
    print("[RF-DETR] 모델 로딩 중...")
    model = RFDETR2XLarge(
        pretrain_weights=MODEL_WEIGHTS,
        accept_platform_model_license=True,
    )
    model.optimize_for_inference()
    print("[RF-DETR] 모델 로딩 완료.")
    return model, sv.BoxAnnotator(), sv.LabelAnnotator()

def predict_and_annotate(frame_bgr, model, box_annotator, label_annotator):
    """한 BGR 프레임에 대해 추론 + annotation 수행.

    Returns:
        (detections, annotated_rgb): supervision Detections와 RGB numpy 이미지.
    """
    rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
    pil_image = Image.fromarray(rgb)
    detections = model.predict(pil_image, threshold=INFERENCE_THRESHOLD)

    labels = []
    for class_id, confidence in zip(detections.class_id, detections.confidence):
        c_id = int(class_id)
        class_name = CUSTOM_CLASSES.get(c_id, f"Unknown_ID_{c_id}")
        labels.append(f"{class_name} {confidence:.2f}")

    image_np = np.array(pil_image)
    annotated = box_annotator.annotate(scene=image_np.copy(), detections=detections)
    annotated = label_annotator.annotate(scene=annotated, detections=detections, labels=labels)
    return detections, annotated

def write_detection_rows(writer, frame_id, sys_time, detections):
    """detections의 각 박스를 CSV 한 행씩 기록."""
    for box, c_id, conf in zip(detections.xyxy, detections.class_id, detections.confidence):
        x1, y1, x2, y2 = (float(v) for v in box)
        c_id_int = int(c_id)
        class_name = CUSTOM_CLASSES.get(c_id_int, f"Unknown_ID_{c_id_int}")
        writer.writerow([frame_id, sys_time, c_id_int, class_name, float(conf), x1, y1, x2, y2])

def _save_live_meta(origin_gps):
    """세션 첫 GPS를 origin으로 채택한 메타데이터를 저장.
    Unity의 GeoAnchor가 이 origin_lat/lng/alt를 자기 Inspector 값과 맞춰야 함."""
    meta_dir = os.path.join(BASE_DIR, PROJECTION_SUBDIR)
    os.makedirs(meta_dir, exist_ok=True)
    meta_path = os.path.join(meta_dir, f"meta_{NOW}.json")
    with open(meta_path, "w") as f:
        json.dump({
            "origin_lat": origin_gps[0],
            "origin_lng": origin_gps[1],
            "origin_alt": origin_gps[2],
            "fov_h_deg": UDP_CAM_FOV_H_DEG,
            "width": UDP_CAM_WIDTH,
            "height": UDP_CAM_HEIGHT,
            "orientation": UDP_ORIENTATION,
            "unity_axes": "X=East, Y=Up, Z=North",
        }, f, indent=2)
    print(f"[meta] {meta_path}")


def _build_unity_detection_msgs(detections, K, R_pc, quat, cam_gps, origin_gps):
    """detections → Unity send_frame용 dict 리스트로 변환."""
    msgs = []
    for box, c_id, conf in zip(detections.xyxy, detections.class_id, detections.confidence):
        x1, y1, x2, y2 = (float(b) for b in box)
        u = 0.5 * (x1 + x2)
        v = y2  # bbox 하단 중심
        # Unity-axis ray direction only; the 인천 GPSEncoder receiver derives
        # world position from raw camera GPS, so origin_u is no longer sent.
        _, dir_u = unity_ray_from_pixel(
            u, v, K, quat, R_pc,
            cam_lat=cam_gps[0], cam_lng=cam_gps[1], cam_alt=cam_gps[2],
            origin_lat=origin_gps[0], origin_lng=origin_gps[1], origin_alt=origin_gps[2],
        )
        c_id_int = int(c_id)
        msgs.append({
            "class_name": CUSTOM_CLASSES.get(c_id_int, f"Unknown_ID_{c_id_int}"),
            "class_id": c_id_int,
            "confidence": float(conf),
            "u": u, "v": v,
            "cam_lat": float(cam_gps[0]), "cam_lng": float(cam_gps[1]), "cam_alt": float(cam_gps[2]),
            "direction": [float(dir_u[0]), float(dir_u[1]), float(dir_u[2])],
        })
    return msgs


def inference_worker():
    """RF-DETR 추론 워커 (라이브 모드).

    큐에서 (frame_id, sys_time, BGR frame, quat, gps)를 받아 추론하고
      - annotated 이미지 → DETECT_DIR
      - 박스별 결과 → DETECT_CSV
      - (UDP_ENABLED 시) Unity로 ray 송출
    캡처 fps가 추론 fps를 초과하면 큐에 한 장만 유지되므로 자연스럽게 frame drop.
    """
    global is_running

    model, box_annotator, label_annotator = load_rfdetr_model()

    udp_sender = None
    K = R_pc = None
    origin_gps = None  # 첫 GPS 샘플을 잡으면 그 세션 내내 ENU origin으로 사용
    if UDP_ENABLED:
        K = intrinsics_from_fov(UDP_CAM_WIDTH, UDP_CAM_HEIGHT, UDP_CAM_FOV_H_DEG)
        R_pc = R_phone_cam_landscape_left()
        udp_sender = UdpSender(UDP_HOST, UDP_PORT)
        print(f"[UDP] sending to {UDP_HOST}:{UDP_PORT}")

    print("[추론 워커] 추론 대기 시작.")

    with open(DETECT_CSV, mode='w', newline='', encoding='utf-8-sig') as f:
        writer = csv.writer(f)
        writer.writerow(DETECT_HEADERS)

        while is_running:
            try:
                frame_id, sys_time, frame_bgr, quat, gps = inference_queue.get(timeout=0.5)
            except queue.Empty:
                continue

            try:
                detections, annotated = predict_and_annotate(frame_bgr, model, box_annotator, label_annotator)

                out_path = os.path.join(DETECT_DIR, f"det_frame_{frame_id:06d}.jpg")
                cv2.imwrite(out_path, cv2.cvtColor(annotated, cv2.COLOR_RGB2BGR))

                write_detection_rows(writer, frame_id, sys_time, detections)
                f.flush()

                n = len(detections.xyxy)

                # UDP wire-up: detector 출력 → ray 변환 → Unity 송출.
                # REALTIME_MODE: 검출 0개여도 camera pose 만 담은 빈 패킷을 보내
                # Unity 가 캡처 타임라인의 공백을 그대로 보게 한다. 그렇지 않으면
                # 기존처럼 검출 있을 때만 송신(안전·기존 호환).
                should_send = (udp_sender is not None and quat is not None and gps is not None
                               and (n > 0 or UDP_REALTIME_MODE))
                if should_send:
                    if origin_gps is None:
                        origin_gps = gps
                        _save_live_meta(origin_gps)
                    try:
                        msgs = (_build_unity_detection_msgs(detections, K, R_pc, quat, gps, origin_gps)
                                if n > 0 else [])
                        fwd, up = unity_camera_axes_from_quat(quat, R_pc)
                        udp_sender.send_frame(frame_id, sys_time, msgs,
                                              cam_forward=fwd.tolist(), cam_up=up.tolist())
                    except Exception as e:
                        print(f"[UDP 오류] frame_{frame_id}: {type(e).__name__} - {e}")

                if n > 0:
                    print(f"[추론] frame_{frame_id:06d}: {n} objects")
                elif UDP_REALTIME_MODE and should_send:
                    # 빈 프레임 송신은 너무 시끄럽지 않게 25프레임마다 한 줄 로그.
                    if frame_id % 25 == 0:
                        print(f"[추론] frame_{frame_id:06d}: 0 objects (realtime keepalive)")
            except Exception as e:
                print(f"[추론 오류] frame_{frame_id}: {type(e).__name__} - {e}")

    if udp_sender is not None:
        udp_sender.close()
    print("[추론 워커] 종료.")

def run_live_capture(enable_inference=True):
    """폰 IP Webcam에서 영상+센서+GPS 캡처·저장.

    enable_inference=True  : RF-DETR 추론 + Unity UDP 송출까지 (기존 라이브 동작)
    enable_inference=False : 촬영·저장만 (capture.py 용 — RF-DETR/UDP 없음, 경량)
    """
    global is_running, latest_sensor_data, sensor_timestamp

    # 세션 디렉토리 생성
    os.makedirs(FRAMES_DIR, exist_ok=True)
    os.makedirs(SENSOR_DIR, exist_ok=True)
    if enable_inference:
        os.makedirs(DETECT_DIR, exist_ok=True)

    sensor_thread = threading.Thread(target=sensor_worker, daemon=True)
    gps_thread    = threading.Thread(target=gps_worker,    daemon=True)
    sensor_thread.start()
    gps_thread.start()
    infer_thread = None
    if enable_inference:
        infer_thread = threading.Thread(target=inference_worker, daemon=True)
        infer_thread.start()

    while not latest_sensor_data:
        time.sleep(0.1)
    
    final_headers = ['frame_id', 'img_filename', 'sys_time', 'sensor_time'] + SENSOR_HEADERS + GPS_HEADERS
    
    # 영상 수신 버퍼 크기를 줄여 지연 시간(Latency) 최소화
    os.environ["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = "video_size;1920x1080|probesize;32"
    cap = cv2.VideoCapture(VIDEO_URL)
    
    if not cap.isOpened():
        print("비디오 스트림 오류. IP를 확인하세요.")
        is_running = False
        sensor_thread.join()
        return

    frame_count = 0
    error_count = 0
    
    with open(CSV_FILENAME, mode='w', newline='', encoding='utf-8-sig') as file:
        writer = csv.writer(file)
        writer.writerow(final_headers)
        
        while True:
            ret, frame = cap.read()
            
            # --- overread 에러 대응 (프레임 드랍 처리) ---
            if not ret or frame is None:
                error_count += 1
                # 100번 연속으로 실패하면 스트림이 완전히 끊긴 것으로 간주
                if error_count > 100:
                    print("네트워크 연결이 끊어졌습니다. 수집을 강제 종료합니다.")
                    break
                continue # 깨진 프레임은 버리고 다음 프레임을 즉시 읽음
            
            error_count = 0 # 정상 프레임 수신 시 에러 카운트 초기화
            # ----------------------------------------------

            sys_time = time.time()
            img_filename = f"frame_{frame_count:06d}.jpg"
            img_path = os.path.join(FRAMES_DIR, img_filename)
            
            current_sensor_time = sensor_timestamp
            current_sensor_vals = latest_sensor_data.copy()
            current_gps = latest_gps_data.copy()

            if len(current_sensor_vals) != len(SENSOR_HEADERS):
                current_sensor_vals = current_sensor_vals[:21] + [''] * (21 - len(current_sensor_vals))

            gps_vals = [
                current_gps.get("lat",      ""),
                current_gps.get("lng",      ""),
                current_gps.get("alt",      ""),
                current_gps.get("accuracy", ""),
            ]

            cv2.imwrite(img_path, frame)

            # 캡처 시점의 IMU 자세와 GPS를 함께 묶어 보내야 frame ↔ sensor 동기화가 성립.
            quat_xyzw = None
            try:
                qvals = current_sensor_vals[QUAT_OFFSET:QUAT_OFFSET + 4]
                if all(v not in ('', None) for v in qvals):
                    quat_xyzw = tuple(float(v) for v in qvals)
            except (ValueError, TypeError):
                pass

            gps_tuple = None
            try:
                lat = current_gps.get("lat", "")
                lng = current_gps.get("lng", "")
                alt = current_gps.get("alt", "")
                if lat not in ('', None) and lng not in ('', None):
                    gps_tuple = (float(lat), float(lng), float(alt) if alt not in ('', None) else 0.0)
            except (ValueError, TypeError):
                pass

            # --- 추론 큐로 최신 프레임 전달 (drop-old: 워커가 못 따라오는 프레임은 버림) ---
            if enable_inference:
                try:
                    inference_queue.get_nowait()
                except queue.Empty:
                    pass
                try:
                    inference_queue.put_nowait((frame_count, sys_time, frame.copy(), quat_xyzw, gps_tuple))
                except queue.Full:
                    pass
            # -----------------------------------------------------------------------

            row = [frame_count, img_filename, sys_time, current_sensor_time] + current_sensor_vals + gps_vals
            writer.writerow(row)
            
            if frame_count % 10 == 0:
                print(f"Captured {frame_count} frames... (Sensor: {current_sensor_vals[0]:.2f} m/s²)")
                
            cv2.imshow('IP Webcam Feed', frame)
            frame_count += 1
            
            if cv2.waitKey(1) & 0xFF == ord('q'):
                break

    is_running = False
    sensor_thread.join()
    gps_thread.join()
    if infer_thread is not None:
        infer_thread.join()
    cap.release()
    cv2.destroyAllWindows()
    print("\n데이터 수집 완료.")

def main():
    print(f"[{NOW}] IP Webcam 연결 확인 중... ({SENSOR_URL}, timeout={CONNECTION_PROBE_TIMEOUT}s)")
    if probe_ipwebcam():
        print("[연결] IP Webcam 응답 OK. 라이브 캡처 모드로 진행합니다.")
        run_live_capture()
    else:
        print("[연결] IP Webcam 신호 감지되지 않음. 오프라인 추론 모드로 전환합니다.")
        run_offline_inference()

if __name__ == "__main__":
    main()