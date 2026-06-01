"""Fire-and-forget JSON-over-UDP sender.

Used by both the live capture (IP_webcam.py inference worker) and the offline
replay (replay_offline.py) so Unity sees identical packet formats in both paths.

Packet format (one datagram per frame):
    {"frame_id": int, "sys_time": float,
     "detections": [
        {"class_name": str, "class_id": int, "confidence": float,
         "u": float, "v": float,
         "cam_lat": float, "cam_lng": float, "cam_alt": float,  # camera GPS (WGS84)
         "direction": [dx, dy, dz]},    # unit ray, Unity axes X=E,Y=U,Z=N
        ...
     ]}

Unity converts cam GPS -> world via the 인천 GPSEncoder calibration
(ProjectionUdpReceiver). Direction is orientation-derived and frame-independent.
"""

import json
import socket


class UdpSender:
    def __init__(self, host="127.0.0.1", port=9870):
        self.addr = (host, port)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def send_frame(self, frame_id, sys_time, detections, cam_forward=None, cam_up=None):
        body = {
            "frame_id": int(frame_id),
            "sys_time": float(sys_time),
            "detections": detections,
        }
        if cam_forward is not None:
            body["cam_forward"] = [float(x) for x in cam_forward]
        if cam_up is not None:
            body["cam_up"] = [float(x) for x in cam_up]
        payload = json.dumps(body, separators=(",", ":")).encode("utf-8")
        self.sock.sendto(payload, self.addr)

    def close(self):
        self.sock.close()
