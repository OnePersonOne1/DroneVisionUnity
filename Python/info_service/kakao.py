"""Kakao Local API — 2차(보조) enrich. 좌표→건물명·도로명주소.

핫패스 금지 원칙: 캐시 미스 시 생성 단계 / 프리페치 백그라운드에서만 호출하고
결과는 캐시됨(같은 건물 1회). 키 없거나 오류면 None 반환(폴백은 GIS/OSM).

키: 환경변수 KAKAO_REST_API_KEY (코드에 하드코딩 금지).
"""
import json
import os
import urllib.error
import urllib.parse
import urllib.request

KEY = os.environ.get("KAKAO_REST_API_KEY", "")
BASE = "https://dapi.kakao.com/v2/local"
TIMEOUT = float(os.environ.get("KAKAO_TIMEOUT", "4"))


def available():
    return bool(KEY)


def _get(path, params):
    url = f"{BASE}{path}?" + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={"Authorization": f"KakaoAK {KEY}"})
    try:
        return json.loads(urllib.request.urlopen(req, timeout=TIMEOUT).read())
    except (urllib.error.URLError, OSError, json.JSONDecodeError) as ex:
        print(f"[kakao] 호출 실패: {ex}")
        return None


def coord2address(lat, lng):
    """좌표 → {building_name, road_address, jibun_address, zone_no} 또는 None."""
    if not KEY:
        return None
    d = _get("/geo/coord2address.json", {"x": lng, "y": lat})
    if not d or not d.get("documents"):
        return None
    doc = d["documents"][0]
    road = doc.get("road_address") or {}
    jibun = doc.get("address") or {}

    def s(v):
        return (v or "").strip() or None

    return {
        "building_name": s(road.get("building_name")),
        "road_address": s(road.get("address_name")),
        "jibun_address": s(jibun.get("address_name")),
        "zone_no": s(road.get("zone_no")),
    }


def search_keyword(query, lat, lng, radius_m=5000, sort="distance", size=15):
    """키워드 + 좌표 + 반경 → 가까운 순 장소 리스트.
    예: query='소방서' 로 인근 소방서 검색. 반환 = documents 리스트 또는 [].

    각 document 의 거리는 'distance' (미터, str). place_name·address·road_address·phone 등 포함.
    """
    if not KEY:
        return []
    params = {
        "query": query,
        "x": lng, "y": lat,
        "radius": int(radius_m),
        "sort": sort,
        "size": int(size),
    }
    d = _get("/search/keyword.json", params)
    if not d or not d.get("documents"):
        return []
    return d["documents"]


def nearest_place(query, lat, lng, radius_m=5000):
    """search_keyword 결과 중 가장 가까운 장소 1건 (또는 None).
    distance 정렬을 신뢰하되, 값이 없으면 직접 계산해서 최소를 고름.
    """
    docs = search_keyword(query, lat, lng, radius_m, sort="distance", size=15)
    if not docs:
        return None

    def parse_dist(doc):
        try:
            return float(doc.get("distance") or "0")
        except (TypeError, ValueError):
            return float("inf")

    best = min(docs, key=parse_dist)

    def s(v):
        return (v or "").strip() or None

    try:
        bx, by = float(best.get("x")), float(best.get("y"))
    except (TypeError, ValueError):
        bx = by = None
    return {
        "name": s(best.get("place_name")),
        "category": s(best.get("category_name")),
        "address": s(best.get("road_address_name")) or s(best.get("address_name")),
        "phone": s(best.get("phone")),
        "lat": by, "lng": bx,
        "distance_m": parse_dist(best),
    }
