"""건물 정보 백엔드 (Phase 2).

Unity BuildingInfoService 가 HTTP/JSON 으로 호출한다.
역지오코딩(오프라인 OSM, 핫패스) + LLM 보강(Ollama, 캐시) + 프리페치.

실행:
    pip install -r requirements.txt
    python3 fetch_incheon_osm.py          # 1회 (인천 건물 GeoJSON 수집)
    ollama serve &                        # 별도, LLM 보강용 (없어도 OSM 폴백 동작)
    python3 info_server.py                # localhost:8077

검증:
    curl localhost:8077/health
    curl -X POST localhost:8077/building_info -H 'Content-Type: application/json' \
         -d '{"lat":37.3828277587891,"lng":126.656120300293,"llm":true}'
    curl -X POST localhost:8077/prefetch -H 'Content-Type: application/json' \
         -d '{"center_lat":37.3828,"center_lng":126.6561,"radius_m":150}'
"""
import os
import threading
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager

import uvicorn
from fastapi import FastAPI
from pydantic import BaseModel

import kakao
import llm
from cache import InfoCache
from geocode import Geocoder
from gis_enrich import GisBuildings

GEOJSON = os.environ.get("INFO_GEOJSON")
GIS_CSV = os.environ.get("INFO_GIS_CSV")
HOST = os.environ.get("INFO_HOST", "127.0.0.1")
PORT = int(os.environ.get("INFO_PORT", "8077"))
PREFETCH_WORKERS = int(os.environ.get("INFO_PREFETCH_WORKERS", "4"))

state = {"geocoder": None, "cache": None, "gis": None}

# 프리페치 LLM 생성용 백그라운드 풀 (Ollama 가 동시 요청을 병렬 처리함).
_executor = ThreadPoolExecutor(max_workers=PREFETCH_WORKERS)
_inflight = set()
_inflight_lock = threading.Lock()


@asynccontextmanager
async def lifespan(app: FastAPI):
    gc = Geocoder(GEOJSON) if GEOJSON else Geocoder()
    state["geocoder"] = gc
    state["cache"] = InfoCache()
    state["gis"] = GisBuildings(GIS_CSV) if GIS_CSV else GisBuildings()
    print(f"[info_server] OSM 건물 {gc.count}개 ({gc.path})")
    print(f"[info_server] GIS 건물 {state['gis'].count}개 ({state['gis'].path})")
    print(f"[info_server] 캐시 {state['cache'].count()}개 (sqlite)")
    print(f"[info_server] LLM={llm.OLLAMA_MODEL} available={llm.available()} | Kakao={kakao.available()}")
    yield
    state["geocoder"] = None
    state["cache"] = None
    state["gis"] = None


app = FastAPI(title="DroneVision Building Info", lifespan=lifespan)


class GpsQuery(BaseModel):
    lat: float
    lng: float
    llm: bool = False   # True 면 캐시 미스 시 LLM 동기 생성(B 키 on-demand 경로).


class PrefetchQuery(BaseModel):
    center_lat: float
    center_lng: float
    radius_m: float = 150.0
    limit: int = 60


def enrich_offline(lat, lng):
    """OSM 폴리곤(식별) + GIS건물통합정보(권위적 속성) 병합. 오프라인·무료, Kakao 미포함."""
    geo = state["geocoder"].reverse(lat, lng)
    gisrec = state["gis"].nearest(lat, lng) if state["gis"] else None
    if not geo.get("found") and gisrec is None:
        return {"found": False, "lat": lat, "lng": lng}
    if not geo.get("found"):
        # OSM 폴리곤엔 없지만 GIS 점이 가까우면 GIS 로 식별.
        geo = {"found": True, "lat": lat, "lng": lng,
               "building_key": gisrec["key"], "name": None, "address": None}
    geo["gis"] = gisrec
    if gisrec:
        geo["name"] = gisrec.get("name") or geo.get("name")
        geo["address"] = gisrec.get("address") or geo.get("address")
    return geo


def add_kakao(geo, lat, lng):
    """Kakao 2차 보강(건물명/도로명). 키 없거나 실패면 무변경. (캐시 미스/프리페치에서만)"""
    if not kakao.available():
        return geo
    kk = kakao.coord2address(lat, lng)
    if kk:
        geo["kakao"] = kk
        if kk.get("building_name"):
            geo["name"] = kk["building_name"]
        if kk.get("road_address"):
            geo["address"] = kk["road_address"]
    return geo


@app.get("/health")
def health():
    gc = state["geocoder"]; cache = state["cache"]; gis = state["gis"]
    return {
        "status": "ok",
        "osm_buildings": gc.count if gc else 0,
        "gis_buildings": gis.count if gis else 0,
        "cached": cache.count() if cache else 0,
        "llm_model": llm.OLLAMA_MODEL,
        "llm_available": llm.available(),
        "kakao_available": kakao.available(),
    }


def _to_int(v):
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return 0


def _to_float(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return 0.0


@app.post("/building_info")
def building_info(q: GpsQuery):
    geo = enrich_offline(q.lat, q.lng)
    if not geo.get("found"):
        return geo
    g = geo.get("gis") or {}
    geo["gis_floors"] = _to_int(g.get("floors_above"))     # 박스 높이용(층수)
    geo["gis_height_m"] = _to_float(g.get("height_m"))     # 박스 높이 폴백(m)
    key = geo["building_key"]
    cached = state["cache"].get(key)
    if cached is not None:
        geo["info"], geo["info_source"] = cached, "cache"
        return geo
    if q.llm and llm.available():
        add_kakao(geo, q.lat, q.lng)          # 캐시 미스 시에만 Kakao 1회
        info = llm.generate(geo)
        if info is not None:
            state["cache"].set(key, info, llm.OLLAMA_MODEL)
            geo["info"], geo["info_source"] = info, "llm"
            return geo
    geo["info"] = llm.fallback_info(geo)
    geo["info_source"] = "gis" if geo.get("gis") else "osm"
    return geo


def _gen_and_cache(lat, lng, inflight_key):
    """워커 스레드: enrich(OSM+GIS+Kakao) → LLM 생성 → 캐시."""
    try:
        geo = enrich_offline(lat, lng)
        if not geo.get("found"):
            return
        add_kakao(geo, lat, lng)
        info = llm.generate(geo)
        if info is not None:
            state["cache"].set(geo["building_key"], info, llm.OLLAMA_MODEL)
    finally:
        with _inflight_lock:
            _inflight.discard(inflight_key)


@app.post("/prefetch")
def prefetch(q: PrefetchQuery):
    """반경 내 미캐시 건물의 enrich+LLM 생성을 백그라운드 풀에 큐잉하고 즉시 반환.

    호출부(Unity)는 블로킹되지 않고, 캐시는 백그라운드에서 채워진다 → 이후 조회가 캐시 히트.
    중복 큐잉은 _inflight 로 방지.
    """
    builds = state["geocoder"].nearby(q.center_lat, q.center_lng, q.radius_m, q.limit)
    cache = state["cache"]
    queued = already = 0
    have_llm = llm.available()
    for b in builds:
        key = b["building_key"]
        if cache.get(key) is not None:
            already += 1
            continue
        if not have_llm:
            continue
        with _inflight_lock:
            if key in _inflight:
                continue
            _inflight.add(key)
        _executor.submit(_gen_and_cache, b["lat"], b["lng"], key)
        queued += 1
    return {"nearby": len(builds), "queued": queued,
            "already_cached": already, "cached_total": cache.count()}


if __name__ == "__main__":
    uvicorn.run(app, host=HOST, port=PORT)
