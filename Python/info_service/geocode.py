"""오프라인 역지오코딩: GPS(lat,lng) → OSM 건물.

fetch_incheon_osm.py 가 만든 GeoJSON 을 shapely STRtree 로 색인하고,
point-in-polygon 으로 가리키는 건물을 찾는다. 네트워크/요청제한 없음, 1ms 미만.

좌표는 WGS84 위경도 그대로 사용 (Unity 캘리브레이션과 무관 — 프로브가 이미
GPS 로 변환해서 넘겨준다). shapely 는 (x=lon, y=lat) 순.
"""
import json
import math
from pathlib import Path

from shapely import STRtree
from shapely.geometry import Point, shape
from shapely.ops import nearest_points, triangulate

DEFAULT_GEOJSON = Path(__file__).resolve().parent / "data" / "incheon_buildings.geojson"

# 포함 건물이 없을 때 "근처" 로 인정할 최대 거리(m).
NEAREST_MAX_METERS = 25.0


def approx_distance_m(lat1, lon1, lat2, lon2):
    dlat = (lat2 - lat1) * 111320.0
    dlon = (lon2 - lon1) * 111320.0 * math.cos(math.radians((lat1 + lat2) * 0.5))
    return math.hypot(dlat, dlon)


def _footprint_flat(geom):
    """건물 폴리곤 → Unity 오버레이용 삼각형들. lat,lng 평면 좌표를 6개씩(삼각형 1개)
    이어붙인 flat 리스트(JsonUtility 호환). 오목 폴리곤도 내부 삼각형만 채운다."""
    poly = geom
    if geom.geom_type == "MultiPolygon":
        poly = max(geom.geoms, key=lambda g: g.area)
    if poly.geom_type != "Polygon":
        return []
    flat = []
    for tri in triangulate(poly):
        if not poly.contains(tri.representative_point()):
            continue
        xs, ys = tri.exterior.coords.xy   # (lng, lat), 닫힘점 포함 4개
        for k in range(3):
            flat.append(round(ys[k], 8))   # lat
            flat.append(round(xs[k], 8))   # lng
    return flat


def _box_corners(geom):
    """건물 최소 회전 사각형(OBB)의 4코너 → [lat,lng]×4 flat(8개). lat/lng 공간에서 계산하므로
    건물의 방향(회전)이 코너에 그대로 담긴다 — Unity 가 GpsToWorld 로 변환해 사용."""
    poly = geom
    if geom.geom_type == "MultiPolygon":
        poly = max(geom.geoms, key=lambda g: g.area)
    if poly.geom_type != "Polygon":
        return []
    mrr = poly.minimum_rotated_rectangle
    if mrr.geom_type != "Polygon":
        return []
    xs, ys = mrr.exterior.coords.xy   # (lng, lat), 5개(닫힘) → 앞 4
    out = []
    for k in range(4):
        out.append(round(ys[k], 8))   # lat
        out.append(round(xs[k], 8))   # lng
    return out


def _address_from_tags(t):
    if t.get("addr:full"):
        return t["addr:full"]
    parts = [t.get(k) for k in (
        "addr:province", "addr:city", "addr:district",
        "addr:subdistrict", "addr:street", "addr:housenumber")]
    parts = [p for p in parts if p]
    return " ".join(parts) if parts else None


class Geocoder:
    def __init__(self, geojson_path=DEFAULT_GEOJSON):
        self.path = Path(geojson_path)
        self.geoms = []
        self.props = []
        self._tree = None
        self.load()

    def load(self):
        if not self.path.exists():
            raise FileNotFoundError(
                f"건물 GeoJSON 없음: {self.path}\n먼저 실행: python3 fetch_incheon_osm.py")
        fc = json.loads(self.path.read_text(encoding="utf-8"))
        self.geoms, self.props = [], []
        for f in fc.get("features", []):
            try:
                g = shape(f["geometry"])
            except Exception:  # noqa: BLE001 - 깨진 지오메트리 스킵
                continue
            if g.is_empty or not g.is_valid:
                g = g.buffer(0)  # self-intersection 등 복구 시도
            if g.is_empty:
                continue
            self.geoms.append(g)
            self.props.append(f.get("properties", {}))
        self._tree = STRtree(self.geoms) if self.geoms else None
        return len(self.geoms)

    @property
    def count(self):
        return len(self.geoms)

    def reverse(self, lat, lng):
        """가리키는 건물 정보. 항상 dict 반환(found=False 포함)."""
        empty = {"found": False, "lat": lat, "lng": lng}
        if self._tree is None:
            return empty
        pt = Point(lng, lat)

        # 1) 점을 포함하는 건물(여러 개면 면적 최소 = 가장 구체적).
        #    STRtree.query 는 predicate(input, tree) 로 평가하므로 point-in-polygon 은 "within".
        idxs = self._tree.query(pt, predicate="within")
        if len(idxs):
            i, dist, exact = min(idxs, key=lambda k: self.geoms[k].area), 0.0, True
        else:
            # 2) 포함 없음 → 최근접 건물 외곽까지 거리가 임계 내면 근사 매칭.
            ni = self._tree.nearest(pt)
            if ni is None:
                return empty
            ni = int(ni)
            np_geom, _ = nearest_points(self.geoms[ni], pt)
            dist = approx_distance_m(lat, lng, np_geom.y, np_geom.x)
            if dist > NEAREST_MAX_METERS:
                return empty
            i, exact = ni, False
        res = self._result(i, lat, lng, distance_m=dist, exact=exact)
        res["footprint"] = _footprint_flat(self.geoms[i])     # 평면 색면용(미사용 시 무해)
        res["box"] = _box_corners(self.geoms[i])              # OBB 4코너(박스 베이스)
        return res

    def nearby(self, lat, lng, radius_m, limit=60):
        """반경 내 건물 목록(중심점 기준 result dict, 가까운 순). 프리페치용."""
        if self._tree is None:
            return []
        pt = Point(lng, lat)
        deg = radius_m / 111320.0
        out = []
        for i in self._tree.query(pt.buffer(deg)):
            i = int(i)
            g = self.geoms[i]
            if g.contains(pt):
                dist = 0.0
            else:
                npg, _ = nearest_points(g, pt)
                dist = approx_distance_m(lat, lng, npg.y, npg.x)
            if dist <= radius_m:
                c = g.centroid
                out.append(self._result(i, c.y, c.x, dist, exact=(dist == 0.0)))
        out.sort(key=lambda r: r["distance_m"])
        return out[:limit]

    def _result(self, i, lat, lng, distance_m, exact):
        t = self.props[i]
        c = self.geoms[i].centroid
        return {
            "found": True,
            "exact": exact,
            "lat": lat,
            "lng": lng,
            "osm_type": t.get("osm_type"),
            "osm_id": t.get("osm_id"),
            "building_key": f"{t.get('osm_type')}/{t.get('osm_id')}",
            "name": t.get("name") or t.get("name:ko") or t.get("name:en"),
            "building_type": t.get("building"),
            "address": _address_from_tags(t),
            "centroid": [c.y, c.x],
            "distance_m": round(distance_m, 1),
            "tags": t,
        }
