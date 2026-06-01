"""
Pixel → Unity world projection.

Coordinate frames (all right-handed unless noted):
  - Image  : pixels (u right, v down), origin top-left
  - Camera : OpenCV — X right, Y down, Z forward (into scene)
  - Phone  : Android natural/portrait — X screen-right, Y screen-up, Z out-of-screen
  - World  : ENU — X East, Y North, Z Up (Android world frame)
  - Unity  : left-handed — we map ENU(E,N,U) → Unity(X=E, Y=U, Z=N)

Phone IMU quaternion (qx, qy, qz, qw) rotates a vector from phone frame to ENU:
    v_world = R(q) @ v_phone
"""

import numpy as np

WGS84_A = 6378137.0
WGS84_F = 1.0 / 298.257223563
WGS84_E2 = WGS84_F * (2 - WGS84_F)

# Standard atmosphere
P0_MBAR = 1013.25
T0_K = 288.15
L = 0.0065
G = 9.80665
M = 0.0289644
R = 8.31447


def intrinsics_from_fov(width, height, fov_h_deg, fov_v_deg=None):
    fov_h = np.deg2rad(fov_h_deg)
    fx = width / (2.0 * np.tan(fov_h / 2.0))
    if fov_v_deg is None:
        fy = fx
    else:
        fov_v = np.deg2rad(fov_v_deg)
        fy = height / (2.0 * np.tan(fov_v / 2.0))
    return np.array([[fx, 0, width / 2.0],
                     [0, fy, height / 2.0],
                     [0,  0, 1.0]], dtype=np.float64)


def pixel_to_ray_cam(u, v, K):
    d = np.linalg.inv(K) @ np.array([u, v, 1.0])
    return d / np.linalg.norm(d)


def quat_to_R(qx, qy, qz, qw):
    n = np.sqrt(qx * qx + qy * qy + qz * qz + qw * qw)
    qx, qy, qz, qw = qx / n, qy / n, qz / n, qw / n
    xx, yy, zz = qx * qx, qy * qy, qz * qz
    xy, xz, yz = qx * qy, qx * qz, qy * qz
    wx, wy, wz = qw * qx, qw * qy, qw * qz
    return np.array([
        [1 - 2 * (yy + zz),     2 * (xy - wz),     2 * (xz + wy)],
        [    2 * (xy + wz), 1 - 2 * (xx + zz),     2 * (yz - wx)],
        [    2 * (xz - wy),     2 * (yz + wx), 1 - 2 * (xx + yy)],
    ], dtype=np.float64)


def R_phone_cam_landscape_left():
    """Rear camera, phone held landscape-left (USB port on the right, volume on top).

    Camera (cam, OpenCV): X right, Y down, Z forward
    Phone (Android natural-portrait): X right, Y up, Z out of screen
    In landscape-left the device is rotated 90° counter-clockwise from portrait, so:
        image right  (+X_c) = +Y_p (phone "up" in natural frame)
        image down   (+Y_c) = +X_p (phone "right" in natural frame)
        camera fwd   (+Z_c) = -Z_p (rear camera looks away from screen)
    """
    return np.array([
        [0,  1,  0],
        [1,  0,  0],
        [0,  0, -1],
    ], dtype=np.float64)


def R_phone_cam_landscape_right():
    """Rear camera, phone held landscape-right (USB port on the left, volume on bottom).

    Mirror of landscape-left about the camera optical axis:
        image right  (+X_c) = -Y_p
        image down   (+Y_c) = -X_p
        camera fwd   (+Z_c) = -Z_p
    """
    return np.array([
        [ 0, -1,  0],
        [-1,  0,  0],
        [ 0,  0, -1],
    ], dtype=np.float64)


# Default for IP Webcam landscape captures
default_R_phone_cam = R_phone_cam_landscape_left


def geodetic_to_ecef(lat_deg, lng_deg, alt_m):
    lat = np.deg2rad(lat_deg)
    lng = np.deg2rad(lng_deg)
    sl, cl = np.sin(lat), np.cos(lat)
    so, co = np.sin(lng), np.cos(lng)
    N = WGS84_A / np.sqrt(1 - WGS84_E2 * sl * sl)
    X = (N + alt_m) * cl * co
    Y = (N + alt_m) * cl * so
    Z = (N * (1 - WGS84_E2) + alt_m) * sl
    return np.array([X, Y, Z])


def geodetic_to_enu(lat_deg, lng_deg, alt_m, origin_lat, origin_lng, origin_alt):
    ecef = geodetic_to_ecef(lat_deg, lng_deg, alt_m)
    ecef0 = geodetic_to_ecef(origin_lat, origin_lng, origin_alt)
    lat0 = np.deg2rad(origin_lat)
    lng0 = np.deg2rad(origin_lng)
    sl, cl = np.sin(lat0), np.cos(lat0)
    so, co = np.sin(lng0), np.cos(lng0)
    Rne = np.array([
        [-so,        co,       0 ],
        [-sl * co, -sl * so,   cl],
        [ cl * co,  cl * so,   sl],
    ])
    return Rne @ (ecef - ecef0)


def pressure_to_altitude(p_mbar, p0_mbar=P0_MBAR):
    """ISA barometric altitude (m). Use as alt source when GPS alt is unreliable."""
    return (T0_K / L) * (1.0 - (p_mbar / p0_mbar) ** (R * L / (G * M)))


def ray_ground_intersect(p_world, d_world, ground_z=0.0):
    if abs(d_world[2]) < 1e-9:
        return None
    t = (ground_z - p_world[2]) / d_world[2]
    if t < 0:
        return None
    return p_world + t * d_world


def enu_to_unity(enu):
    e, n, u = enu
    return np.array([e, u, n])


def unity_camera_axes_from_quat(quat_xyzw, R_phone_cam):
    """Phone IMU 쿼터니언 + 카메라 마운트 회전 → Unity-world 카메라 forward/up.

    Unity Camera 는 +Z forward, +Y up. OpenCV cam frame 의 +Z(forward),
    -Y(=Unity up) 를 phone frame → ENU → Unity 축으로 옮긴다.

    Returns:
        (forward_unity_3, up_unity_3): Unity 월드 좌표계의 단위벡터.
    """
    R_w_p = quat_to_R(*quat_xyzw)
    f_cam = np.array([0.0, 0.0, 1.0])     # OpenCV cam forward
    u_cam = np.array([0.0, -1.0, 0.0])    # Unity cam up = -(OpenCV cam +Y down)
    f_enu = R_w_p @ (R_phone_cam @ f_cam)
    u_enu = R_w_p @ (R_phone_cam @ u_cam)
    return enu_to_unity(f_enu), enu_to_unity(u_enu)


def unity_ray_from_pixel(
    u, v, K,
    quat_xyzw,
    R_phone_cam,
    cam_lat, cam_lng, cam_alt,
    origin_lat, origin_lng, origin_alt,
):
    """Pixel → Unity-frame ray (origin, direction). Ground intersect is done in Unity
    so that the real Incheon.fbx terrain (mesh) is hit, not a flat plane.

    Returns:
        (origin_unity, dir_unity): 3-vectors in Unity world coords (X=East, Y=Up, Z=North).
    """
    d_cam = pixel_to_ray_cam(u, v, K)
    d_phone = R_phone_cam @ d_cam
    R_w_p = quat_to_R(*quat_xyzw)
    d_world_enu = R_w_p @ d_phone

    p_world_enu = geodetic_to_enu(cam_lat, cam_lng, cam_alt,
                                  origin_lat, origin_lng, origin_alt)
    return enu_to_unity(p_world_enu), enu_to_unity(d_world_enu)


def project_pixel_to_flat_unity(
    u, v, K,
    quat_xyzw,
    R_phone_cam,
    cam_lat, cam_lng, cam_alt,
    origin_lat, origin_lng, origin_alt,
    ground_z=0.0,
):
    """Debug helper: assumes flat ground at z=ground_z (ENU). Use unity_ray_from_pixel
    in production so Unity can hit the actual terrain."""
    d_cam = pixel_to_ray_cam(u, v, K)
    d_phone = R_phone_cam @ d_cam
    R_w_p = quat_to_R(*quat_xyzw)
    d_world = R_w_p @ d_phone

    p_world = geodetic_to_enu(cam_lat, cam_lng, cam_alt,
                              origin_lat, origin_lng, origin_alt)
    hit = ray_ground_intersect(p_world, d_world, ground_z=ground_z)
    if hit is None:
        return None, None
    return enu_to_unity(hit), hit


# Capture defaults for Samsung Galaxy Z Flip 7, IP Webcam landscape mode
DEFAULT_WIDTH = 3840
DEFAULT_HEIGHT = 2160
DEFAULT_FOV_H_DEG = 84.0  # Galaxy main rear camera, ~ Z Flip 7 — refine via calibration


if __name__ == "__main__":
    K = intrinsics_from_fov(DEFAULT_WIDTH, DEFAULT_HEIGHT, DEFAULT_FOV_H_DEG)
    R_pc = default_R_phone_cam()

    # Phone pitched forward 45° about its X axis (synthetic).
    theta = np.deg2rad(45.0)
    quat = (np.sin(theta / 2), 0.0, 0.0, np.cos(theta / 2))

    # Origin & camera pose from sensor CSV first row, then lifted 30 m for the test.
    origin = (35.573675, 129.189446, 69.0)
    cam = (35.573675, 129.189446, 69.0 + 30)

    # bbox bottom-center at image horizontal center, 75% down.
    u, v = DEFAULT_WIDTH / 2, DEFAULT_HEIGHT * 0.75

    origin_u, dir_u = unity_ray_from_pixel(
        u, v, K, quat, R_pc,
        cam_lat=cam[0], cam_lng=cam[1], cam_alt=cam[2],
        origin_lat=origin[0], origin_lng=origin[1], origin_alt=origin[2],
    )
    print(f"K =\n{K}\n")
    print(f"Unity ray origin    = {origin_u}")
    print(f"Unity ray direction = {dir_u}    |d|={np.linalg.norm(dir_u):.4f}")

    # Flat-ground sanity check (for debugging only — production uses Unity raycast).
    unity_hit, enu_hit = project_pixel_to_flat_unity(
        u, v, K, quat, R_pc,
        cam_lat=cam[0], cam_lng=cam[1], cam_alt=cam[2],
        origin_lat=origin[0], origin_lng=origin[1], origin_alt=origin[2],
        ground_z=0.0,
    )
    print(f"Flat-ground ENU hit = {enu_hit}")
    print(f"Flat-ground Unity   = {unity_hit}")
