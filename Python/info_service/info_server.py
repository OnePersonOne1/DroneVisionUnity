"""건물 정보 백엔드 (Phase 2).

Unity BuildingInfoService 가 HTTP/JSON 으로 호출한다.
역지오코딩(오프라인 OSM, 핫패스) + GIS건물통합정보 + Kakao 결정론 enrich → 정형 카드 + 프리페치.

카드는 정형 필드만 사용한다 — title/category/address/floors/height_m/use/approval_date 는
GIS/Kakao 에서 직접 채우고, summary 는 그 필드들의 template 조합이다. LLM(Ollama) 은 카드
경로에서 호출하지 않는다 (latency·환각 회피). `llm.py` 모듈 자체는 다른 작업(상황평가 등)에서
재사용을 위해 남겨두지만 building_info / prefetch 에서는 호출하지 않는다.

실행:
    pip install -r requirements.txt
    python3 fetch_incheon_osm.py          # 1회 (인천 건물 GeoJSON 수집)
    python3 info_server.py                # localhost:8077

검증:
    curl localhost:8077/health
    curl -X POST localhost:8077/building_info -H 'Content-Type: application/json' \
         -d '{"lat":37.3828277587891,"lng":126.656120300293}'
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

# 카드 스키마 버전 — 변경 시 캐시 자동 무효화.
CARD_SCHEMA = "v2"

GEOJSON = os.environ.get("INFO_GEOJSON")
GIS_CSV = os.environ.get("INFO_GIS_CSV")
HOST = os.environ.get("INFO_HOST", "127.0.0.1")
PORT = int(os.environ.get("INFO_PORT", "8077"))
PREFETCH_WORKERS = int(os.environ.get("INFO_PREFETCH_WORKERS", "4"))

state = {"geocoder": None, "cache": None, "gis": None}

# 프리페치 enrich 용 백그라운드 풀. LLM 이 빠져 작업 자체는 가벼우나, sqlite write
# 와 Kakao 요청 분산을 위해 풀 유지.
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
    print(f"[info_server] 캐시 {state['cache'].count()}개 (sqlite, schema={CARD_SCHEMA})")
    print(f"[info_server] Kakao={kakao.available()} | LLM={llm.OLLAMA_MODEL} avail={llm.available()} (카드 경로 미사용)")
    if not kakao.available():
        print("[info_server] *** KAKAO_REST_API_KEY 환경변수가 비어 있음. "
              "Kakao 건물명/주소 보강 비활성 → '이름 미상 건물' 비중 증가. "
              "실행 시 'KAKAO_REST_API_KEY=... python3 info_server.py' 로 키 지정 권장. ***")
    yield
    state["geocoder"] = None
    state["cache"] = None
    state["gis"] = None


app = FastAPI(title="DroneVision Building Info", lifespan=lifespan)


class GpsQuery(BaseModel):
    lat: float
    lng: float
    # 호환을 위해 필드 유지 (Unity 가 보냄). 카드 경로는 LLM 미사용이라 무시.
    llm: bool = False


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
    """Kakao 2차 보강(건물명/도로명/지번). 키 없거나 실패면 무변경.

    address 는 road_address > jibun_address 순. road 가 없는 작은 건물에서도 jibun 으로 주소 확보 → title fallback 의 단축본 재료로 사용.
    """
    if not kakao.available():
        return geo
    kk = kakao.coord2address(lat, lng)
    if kk:
        geo["kakao"] = kk
        if kk.get("building_name"):
            geo["name"] = kk["building_name"]
        addr = kk.get("road_address") or kk.get("jibun_address")
        if addr:
            geo["address"] = addr
    return geo


def _short_addr_title(addr):
    """주소를 title 용 단축본으로. '인천 연수구 송도동 170-1' → '송도동 170-1'.

    이름이 전혀 없는 건물에 '이름 미상 건물' 대신 위치 식별자로 사용.
    """
    if not addr:
        return None
    parts = addr.split()
    if len(parts) >= 2:
        return " ".join(parts[-2:])
    return addr


# ── 정형 카드 빌더 ─────────────────────────────────────────────────────────
def _nz(v):
    """비어있거나 0 이면 False — GIS 의 '0층'·'0m' 같은 무의미값 제외."""
    return v not in (None, "", "0", "0.0", "0.00")


def _floors_str(g):
    """지상/지하 층수 문자열. 둘 다 0/빈 값이면 None."""
    fa = g.get("floors_above") if g else None
    fb = g.get("floors_below") if g else None
    if not _nz(fa) and not _nz(fb):
        return None
    parts = []
    if _nz(fa):
        parts.append(f"지상 {fa}층")
    if _nz(fb):
        parts.append(f"지하 {fb}층")
    return " / ".join(parts)


def _height_m(g):
    """높이를 float 로 변환 — 0/빈 값은 None."""
    if not g:
        return None
    h = g.get("height_m")
    if not _nz(h):
        return None
    try:
        f = float(h)
    except (TypeError, ValueError):
        return None
    return f if f > 0 else None


def _summary_template(title, g, address):
    """정형 필드 조합 1문장. 누락 필드는 자연 생략 (조각 join).

    GIS structure 값은 보통 '철근콘크리트구조' 처럼 자체로 '구조' 접미사를 포함하므로
    템플릿에서 '구조' 를 덧붙이지 않고 raw 그대로 사용한다.
    """
    if not g:
        return f"{title} (위치 정보만 확인)" if address else title
    structure = g.get("structure")
    use = g.get("use")
    fa = g.get("floors_above") if _nz(g.get("floors_above")) else None
    fb = g.get("floors_below") if _nz(g.get("floors_below")) else None
    h = _height_m(g)
    approval = g.get("approval_date")

    bits = []
    if structure:
        bits.append(structure)          # 예: "철근콘크리트구조"
    if fa and fb:
        bits.append(f"지상 {fa}층·지하 {fb}층")
    elif fa:
        bits.append(f"지상 {fa}층")
    elif fb:
        bits.append(f"지하 {fb}층")
    if h:
        bits.append(f"높이 {h:.1f}m")
    if use:
        bits.append(use)
    if not bits:
        return f"{title} — {address}" if address else title
    body = " · ".join(bits)
    s = f"{body} 건물"
    if approval:
        s += f" (사용승인 {approval})"
    return s + "."


def build_card(geo):
    """결정론적 정형 카드. LLM 호출 없음.

    필드:
        schema_version: 카드 스키마 버전 (캐시 무효화용)
        title          Kakao.building_name > GIS.name > OSM.name > "이름 미상 건물"
        category       _category_from_use(GIS.use) 룰 기반 매핑 > "건물"
        address        Kakao.road_address > GIS.address > OSM.address > None
        floors         "지상 N층" / "지상 N층 / 지하 M층" / None
        height_m       float | None (0 은 None)
        use            GIS.use | None
        approval_date  GIS.approval_date | None
        summary        위 필드들의 template 문장
    """
    g = geo.get("gis") or {}
    kk = geo.get("kakao") or {}

    # 이름 우선순위 (실명만): Kakao building_name > GIS name > OSM name.
    real_name = (kk.get("building_name")
                 or g.get("name")
                 or geo.get("name"))
    category = llm._category_from_use(g.get("use")) or "건물"
    # 주소: Kakao road > Kakao jibun > GIS > OSM.
    address = (kk.get("road_address")
               or kk.get("jibun_address")
               or g.get("address")
               or geo.get("address"))
    # 실명 없으면 주소 단축본 (예: "송도동 170-1") 으로 식별. "이름 미상 건물" 은 마지막 폴백.
    title = real_name or _short_addr_title(address) or "이름 미상 건물"
    floors = _floors_str(g)
    height = _height_m(g)
    use = g.get("use") if g.get("use") else None
    approval = g.get("approval_date") if g.get("approval_date") else None
    summary = _summary_template(title, g, address)

    return {
        "schema_version": CARD_SCHEMA,
        "title": title,
        "category": category,
        "address": address,
        "floors": floors,
        "height_m": height,
        "use": use,
        "approval_date": approval,
        "summary": summary,
        # backward-compat: 기존 Unity Info 가 detail 필드를 읽음. 사용은 안 함.
        "detail": "",
    }


@app.get("/health")
def health():
    gc = state["geocoder"]; cache = state["cache"]; gis = state["gis"]
    return {
        "status": "ok",
        "osm_buildings": gc.count if gc else 0,
        "gis_buildings": gis.count if gis else 0,
        "cached": cache.count() if cache else 0,
        "card_schema": CARD_SCHEMA,
        "kakao_available": kakao.available(),
        # llm_* 는 정보용. 카드 경로에서 호출하지 않음.
        "llm_model": llm.OLLAMA_MODEL,
        "llm_available": llm.available(),
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
    cache = state["cache"]
    cached = cache.get(key, expected_schema=CARD_SCHEMA)
    if cached is not None:
        geo["info"], geo["info_source"] = cached, "cache"
        return geo

    # 캐시 미스 — Kakao 1회 보강 + 정형 카드 빌드 + 캐시.
    add_kakao(geo, q.lat, q.lng)
    info = build_card(geo)
    cache.set(key, info, model="", schema=CARD_SCHEMA)
    geo["info"] = info
    geo["info_source"] = "gis" if geo.get("gis") else "osm"
    return geo


def _enrich_and_cache(lat, lng, inflight_key):
    """워커 스레드: enrich(OSM+GIS+Kakao) → 정형 카드 → 캐시."""
    try:
        geo = enrich_offline(lat, lng)
        if not geo.get("found"):
            return
        add_kakao(geo, lat, lng)
        info = build_card(geo)
        state["cache"].set(geo["building_key"], info, model="", schema=CARD_SCHEMA)
    finally:
        with _inflight_lock:
            _inflight.discard(inflight_key)


@app.post("/prefetch")
def prefetch(q: PrefetchQuery):
    """반경 내 미캐시 건물의 enrich+카드 빌드를 백그라운드 풀에 큐잉하고 즉시 반환.

    호출부(Unity)는 블로킹되지 않고, 캐시는 백그라운드에서 채워진다 → 이후 조회가 캐시 히트.
    중복 큐잉은 _inflight 로 방지. LLM 호출 없음 — Kakao 없어도 GIS/OSM 으로 카드 생성.
    """
    builds = state["geocoder"].nearby(q.center_lat, q.center_lng, q.radius_m, q.limit)
    cache = state["cache"]
    queued = already = 0
    for b in builds:
        key = b["building_key"]
        if cache.get(key, expected_schema=CARD_SCHEMA) is not None:
            already += 1
            continue
        with _inflight_lock:
            if key in _inflight:
                continue
            _inflight.add(key)
        _executor.submit(_enrich_and_cache, b["lat"], b["lng"], key)
        queued += 1
    return {"nearby": len(builds), "queued": queued,
            "already_cached": already, "cached_total": cache.count()}


if __name__ == "__main__":
    uvicorn.run(app, host=HOST, port=PORT)
