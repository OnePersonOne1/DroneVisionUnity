"""인천 맵 영역의 OSM 건물 풋프린트를 Overpass API 로 1회 수집 → GeoJSON 저장.

역지오코딩(GPS→건물)의 오프라인 데이터 원천. 인터넷이 되는 환경에서 한 번만
실행하면 되고, 이후 geocode.py 가 이 GeoJSON 을 로컬 공간색인으로 읽는다.

기본 bbox 는 CubeGPSDisplay anchor(37.384312, 126.655307) 주변. 맵이 더 넓으면
--dlat/--dlng 를 키워라.

사용:
    python3 fetch_incheon_osm.py                       # 기본 bbox
    python3 fetch_incheon_osm.py --dlat 0.05 --dlng 0.06
    python3 fetch_incheon_osm.py --bbox 37.35 126.62 37.42 126.70   # s w n e
"""
import argparse
import json
import time
from pathlib import Path

import requests

ANCHOR_LAT = 37.384312
ANCHOR_LNG = 126.655307
DATA_DIR = Path(__file__).resolve().parent / "data"
OUT_PATH = DATA_DIR / "incheon_buildings.geojson"

OVERPASS_ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
]


def build_query(s, w, n, e):
    # out geom: way/relation 의 지오메트리를 인라인으로 받아 폴리곤 조립이 쉬움.
    return f"""
[out:json][timeout:180];
(
  way["building"]({s},{w},{n},{e});
  relation["building"]["type"="multipolygon"]({s},{w},{n},{e});
);
out geom tags;
"""


def fetch(query):
    last_err = None
    for url in OVERPASS_ENDPOINTS:
        for attempt in range(3):
            try:
                print(f"[fetch] POST {url} (attempt {attempt + 1})")
                # 일부 Overpass 인스턴스는 기본 python-requests UA 를 406 으로 막는다.
                r = requests.post(url, data={"data": query}, timeout=200,
                                  headers={"User-Agent": "DroneVisionUnity/1.0 (info_service)"})
                r.raise_for_status()
                return r.json()
            except Exception as ex:  # noqa: BLE001 - 엔드포인트/네트워크 폴백용
                last_err = ex
                print(f"[fetch] 실패: {ex} → 재시도/폴백")
                time.sleep(3)
    raise RuntimeError(f"Overpass 호출 실패: {last_err}")


def ring_from_geometry(geom):
    """Overpass 'geometry' ([{lat,lon},...]) → [[lon,lat],...] 폐곡선."""
    coords = [[pt["lon"], pt["lat"]] for pt in geom if "lat" in pt and "lon" in pt]
    if len(coords) < 3:
        return None
    if coords[0] != coords[-1]:
        coords.append(coords[0])
    return coords


def feature_from_way(el):
    ring = ring_from_geometry(el.get("geometry", []))
    if ring is None:
        return None
    return {
        "type": "Feature",
        "geometry": {"type": "Polygon", "coordinates": [ring]},
        "properties": {"osm_type": "way", "osm_id": el["id"], **el.get("tags", {})},
    }


def feature_from_relation(el):
    # multipolygon: outer 역할 멤버들을 각각 폴리곤으로, 가장 큰 것을 대표로.
    outers = []
    for m in el.get("members", []):
        if m.get("type") == "way" and m.get("role") in ("outer", ""):
            ring = ring_from_geometry(m.get("geometry", []))
            if ring:
                outers.append(ring)
    if not outers:
        return None
    if len(outers) == 1:
        geometry = {"type": "Polygon", "coordinates": [outers[0]]}
    else:
        geometry = {"type": "MultiPolygon", "coordinates": [[r] for r in outers]}
    return {
        "type": "Feature",
        "geometry": geometry,
        "properties": {"osm_type": "relation", "osm_id": el["id"], **el.get("tags", {})},
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lat0", type=float, default=ANCHOR_LAT)
    ap.add_argument("--lng0", type=float, default=ANCHOR_LNG)
    ap.add_argument("--dlat", type=float, default=0.035, help="anchor 기준 위도 반경(도). 기본 ~3.9km.")
    ap.add_argument("--dlng", type=float, default=0.045, help="anchor 기준 경도 반경(도). 기본 ~4km.")
    ap.add_argument("--bbox", nargs=4, type=float, default=None,
                    metavar=("S", "W", "N", "E"), help="직접 bbox 지정(남 서 북 동).")
    ap.add_argument("--out", default=str(OUT_PATH))
    args = ap.parse_args()

    if args.bbox:
        s, w, n, e = args.bbox
    else:
        s, w = args.lat0 - args.dlat, args.lng0 - args.dlng
        n, e = args.lat0 + args.dlat, args.lng0 + args.dlng
    print(f"[bbox] S={s} W={w} N={n} E={e}")

    data = fetch(build_query(s, w, n, e))
    feats = []
    for el in data.get("elements", []):
        f = feature_from_way(el) if el["type"] == "way" else feature_from_relation(el)
        if f:
            feats.append(f)

    fc = {"type": "FeatureCollection",
          "bbox": [w, s, e, n],
          "features": feats}
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(fc, ensure_ascii=False), encoding="utf-8")
    named = sum(1 for f in feats if f["properties"].get("name"))
    print(f"[done] 건물 {len(feats)}개 (이름 있는 것 {named}개) → {out}")


if __name__ == "__main__":
    main()
