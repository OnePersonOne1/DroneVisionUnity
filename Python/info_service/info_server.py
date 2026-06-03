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
import math
import os
import threading
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from datetime import datetime, timezone, timedelta
from typing import List, Optional

import uvicorn
from fastapi import FastAPI
from pydantic import BaseModel

import kakao
import llm
from cache import InfoCache
from fire_stations import FireStations
from geocode import Geocoder
from gis_enrich import GisBuildings
from occupancy import classify_time, occupancy_estimate, TIME_LABEL_KO

KST = timezone(timedelta(hours=9))

# 카드 스키마 버전 — 변경 시 캐시 자동 무효화.
CARD_SCHEMA = "v2"

GEOJSON = os.environ.get("INFO_GEOJSON")
GIS_CSV = os.environ.get("INFO_GIS_CSV")
FIRESTATIONS_CSV = os.environ.get("INFO_FIRESTATIONS_CSV")
HOST = os.environ.get("INFO_HOST", "127.0.0.1")
PORT = int(os.environ.get("INFO_PORT", "8077"))
PREFETCH_WORKERS = int(os.environ.get("INFO_PREFETCH_WORKERS", "4"))

state = {"geocoder": None, "cache": None, "gis": None, "stations": None}

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
    state["stations"] = FireStations(FIRESTATIONS_CSV) if FIRESTATIONS_CSV else FireStations()
    print(f"[info_server] OSM 건물 {gc.count}개 ({gc.path})")
    print(f"[info_server] GIS 건물 {state['gis'].count}개 ({state['gis'].path})")
    print(f"[info_server] 119안전센터 {state['stations'].count}개 ({state['stations'].path})")
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


def get_or_build_card(lat, lng):
    """공통 헬퍼: 좌표 → (geo, info_dict, source).
    /building_info 와 /assess 가 공유. 캐시 히트면 즉답, 미스면 Kakao+build_card+캐시.
    found=False 시 geo 만 반환 (info=None).
    """
    geo = enrich_offline(lat, lng)
    if not geo.get("found"):
        return geo, None, None
    g = geo.get("gis") or {}
    geo["gis_floors"] = _to_int(g.get("floors_above"))
    geo["gis_height_m"] = _to_float(g.get("height_m"))

    key = geo["building_key"]
    cache = state["cache"]
    cached = cache.get(key, expected_schema=CARD_SCHEMA)
    if cached is not None:
        return geo, cached, "cache"

    add_kakao(geo, lat, lng)
    info = build_card(geo)
    cache.set(key, info, model="", schema=CARD_SCHEMA)
    source = "gis" if geo.get("gis") else "osm"
    return geo, info, source


@app.post("/building_info")
def building_info(q: GpsQuery):
    geo, info, source = get_or_build_card(q.lat, q.lng)
    if not geo.get("found"):
        return geo
    geo["info"] = info
    geo["info_source"] = source
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


# ─────────────────────────────────────────────────────────────────────────
#  /assess — 모의 화재 상황 판단 (룰 기반 risk/spreading + LLM 브리핑 합성)
# ─────────────────────────────────────────────────────────────────────────

class FireSpec(BaseModel):
    lat: float
    lng: float
    # "fire_region" | "smoke_region" (호환: "fire" | "smoke" 도 허용)
    cls: str = "fire_region"


class AssessQuery(BaseModel):
    fires: List[FireSpec] = []
    # ISO 8601 KST. None 이면 서버 KST 현재 시각.
    sim_time_iso: Optional[str] = None
    radius_m: float = 200.0
    # provenance 추적용 (Unity 가 부여한 detection id 가 있다면).
    detection_ids: Optional[List[str]] = None
    # 우선순위 건물 응답 최대 개수.
    top_n: int = 8


def _is_smoke(cls):
    return "smoke" in (cls or "").lower()


def _is_fire(cls):
    c = (cls or "").lower()
    return ("fire" in c) and not ("smoke" in c)


def _parse_sim_time(iso):
    """sim_time_iso → (datetime, time_bucket). None 이면 서버 KST."""
    if iso:
        try:
            dt = datetime.fromisoformat(iso)
        except ValueError:
            dt = datetime.now(KST)
    else:
        dt = datetime.now(KST)
    # 입력에 tz 정보 없으면 KST 로 간주.
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=KST)
    else:
        dt = dt.astimezone(KST)
    return dt, classify_time(dt.hour, dt.minute)


def _approx_distance_m(lat1, lng1, lat2, lng2):
    dlat = (lat2 - lat1) * 111320.0
    dlng = (lng2 - lng1) * 111320.0 * math.cos(math.radians((lat1 + lat2) * 0.5))
    return math.hypot(dlat, dlng)


def _compute_risk(buildings, fire_count, smoke_count):
    """결정론 위험도 — 룰. LLM 무관.
        high : 총 추정재실 ≥ 1000명, 또는 high-밀도 건물 ≥ 3동, 또는 화재 ≥ 3건.
        mid  : 총 추정재실 ≥ 200명,  또는 high-밀도 건물 ≥ 1동, 또는 화재 ≥ 2건.
        low  : 그 외.
    """
    total_pop = sum((b["occupancy"] or {}).get("count_est", 0) for b in buildings)
    high_dense = sum(1 for b in buildings if (b["occupancy"] or {}).get("level") == "high")
    if total_pop >= 1000 or high_dense >= 3 or fire_count >= 3:
        level = "high"
    elif total_pop >= 200 or high_dense >= 1 or fire_count >= 2:
        level = "mid"
    else:
        level = "low"
    reason = (f"총 추정재실 {total_pop}명, 고밀도 건물 {high_dense}동, "
              f"화재 {fire_count}건·연기 {smoke_count}건")
    return {"level": level, "reason": reason,
            "total_pop_est": total_pop, "high_density_count": high_dense}


def _extract_floors_int(b):
    """build_card 의 'floors' 문자열 '지상 N층 / 지하 M층' → int(N). 없으면 0."""
    s = b.get("floors") or ""
    # '지상 ' 뒤 숫자만 추출.
    import re
    m = re.search(r"지상\s*(\d+)\s*층", s)
    return int(m.group(1)) if m else 0


def _site_hazard_flags(fires, buildings):
    """주변 건물 + 화재 정보 → 위험 특성 플래그.

    return dict:
      highrise        높이 ≥ 30m 또는 지상 ≥ 10층 건물 존재
      industrial      use 에 '공장|위험물|산업|화학|위험|유류|저장' 포함 건물
      educational     '교육|학교' 포함 (대피 우선순위)
      crowded         lunch/commute 시 상업·교육·근린에 high-occupancy
      multi_fire      활성 화재(fire) ≥ 2
      hot_zone        직접 명중(<5m) 건물 ≥ 2
    """
    flags = {"highrise": False, "industrial": False, "educational": False,
             "crowded": False, "multi_fire": False, "hot_zone": False}
    if fires:
        fire_n = sum(1 for f in fires if "smoke" not in (f.cls or "").lower())
        flags["multi_fire"] = fire_n >= 2
    hot = 0
    for b in buildings:
        use = (b.get("use") or "")
        if b.get("height_m") and b["height_m"] >= 30: flags["highrise"] = True
        if _extract_floors_int(b) >= 10:               flags["highrise"] = True
        if any(k in use for k in ("공장", "위험물", "산업", "화학", "유류", "저장")):
            flags["industrial"] = True
        if any(k in use for k in ("교육", "학교", "유치원", "어린이")):
            flags["educational"] = True
        occ = b.get("occupancy") or {}
        if occ.get("level") == "high":
            flags["crowded"] = True
        if b.get("min_dist_m", 999) < 5.0:
            hot += 1
    flags["hot_zone"] = hot >= 2
    return flags


# 차종 alias — fire_stations._norm_vehicle 과 동일 키 사용.
# 권장 룰이 요구하는 type 이름.
def _recommend_vehicles(flags, fire_count, smoke_count):
    """현장 특성 → 권장 차종 + 최소 대수 + 근거. 룰 only, LLM 무관."""
    needs = []

    # 기본 진압 — 화재가 있을 때만 (smoke 만 있을 때는 펌프차 최소화).
    pump_min = max(1, fire_count + 1) if fire_count > 0 else 1
    needs.append({"type": "펌프차", "min_count": pump_min,
                  "reason": f"기본 진압 (화재 {fire_count}건 +1대 여유)"})

    # 급수.
    needs.append({"type": "물탱크차", "min_count": max(1, fire_count),
                  "reason": "급수 보강"})

    # 응급.
    amb_min = 2 if flags["crowded"] or flags["highrise"] else 1
    needs.append({"type": "구급차", "min_count": amb_min,
                  "reason": "부상자 응급" + (" (인원 밀집)" if amb_min > 1 else "")})

    # 고층/대공간.
    if flags["highrise"]:
        needs.append({"type": "사다리차", "min_count": 1,
                      "reason": "고층 진입·고소 구조 (≥10층 또는 30m+)"})
        needs.append({"type": "굴절차", "min_count": 1,
                      "reason": "고층 외부 진압·구조"})

    # 산업/위험물.
    if flags["industrial"]:
        needs.append({"type": "화학차", "min_count": 1,
                      "reason": "산업/위험물 환경 화학적 진압"})
        needs.append({"type": "고성능화학차", "min_count": 1,
                      "reason": "대용량 거품·약제 살포"})
        needs.append({"type": "무인방수차", "min_count": 1,
                      "reason": "폭발·열복사 위험 격리 진압"})

    # 다중 화재 — pump 추가.
    if flags["multi_fire"]:
        needs[0]["min_count"] += 1
        needs[0]["reason"] += " · 다중 화재 가산"

    # 교육시설 — 안내·생활안전.
    if flags["educational"]:
        needs.append({"type": "생활안전차", "min_count": 1,
                      "reason": "교육시설 대피 안내·인명검색"})

    return needs


def _aggregate_inventory(stations):
    """top-N 센터의 차종 합계 — {차종: 누적 대수}."""
    # 단순 가정: 한 센터의 vehicle_types list 의 각 항목이 1대.
    # 대수 합(vehicle_total) 과 차종 수가 다른 경우는 type list 기준.
    inv = {}
    for s in stations:
        for v in s.get("vehicle_types") or []:
            inv[v] = inv.get(v, 0) + 1
    return inv


def _shortfall(needs, inv):
    """각 권장 차종이 inventory 로 충당 가능한지 체크."""
    out = []
    for n in needs:
        avail = inv.get(n["type"], 0)
        ok = avail >= n["min_count"]
        out.append({"type": n["type"], "needed": n["min_count"],
                    "available": avail, "ok": ok, "reason": n["reason"]})
    return out


def _compute_spreading(buildings, fire_count, smoke_count):
    """확산 판정 — 룰.
        spreading : 화재점에서 5m 이내(직접 명중) 건물 ≥ 2동.
        potential : smoke ≥ 1 또는 화재점 ≤ 30m 인접 건물 ≥ 3동.
        localized : 그 외.
    """
    direct_hits = sum(1 for b in buildings if b["min_dist_m"] < 5.0)
    near_hits = sum(1 for b in buildings if b["min_dist_m"] < 30.0)
    if direct_hits >= 2:
        return "spreading"
    if smoke_count >= 1 or near_hits >= 3:
        return "potential"
    return "localized"


@app.post("/assess")
def assess(q: AssessQuery):
    """모의 화재 입력 → 룰 기반 risk/spreading + 정형 feature → LLM 브리핑.
    LLM 출력은 표시 전용 (판정에 영향 없음). LLM 미가동 시 template 폴백."""
    sim_dt, time_bucket = _parse_sim_time(q.sim_time_iso)
    sim_time_iso = sim_dt.isoformat()

    fires_typed = list(q.fires)
    fire_count = sum(1 for f in fires_typed if _is_fire(f.cls))
    smoke_count = sum(1 for f in fires_typed if _is_smoke(f.cls))

    # 1) 각 화재 반경 내 건물 수집 + key 로 dedup. 각 건물의 화재까지 최단 거리 보존.
    by_key = {}    # key → {geo, info, min_dist_m, nearest_fire_idx}
    geocoder = state["geocoder"]
    for fi, f in enumerate(fires_typed):
        if geocoder is None:
            break
        nb = geocoder.nearby(f.lat, f.lng, q.radius_m, limit=120)
        for b in nb:
            key = b["building_key"]
            # 거리는 geocoder 의 distance_m 그대로 (이 화재 기준).
            d = float(b.get("distance_m", q.radius_m))
            if key not in by_key:
                # 정형 카드(get_or_build_card) — 캐시 우선.
                geo, info, source = get_or_build_card(b["lat"], b["lng"])
                if not geo.get("found"):
                    continue
                by_key[key] = {
                    "geo": geo, "info": info, "source": source,
                    "min_dist_m": d, "nearest_fire": fi,
                }
            else:
                # 더 가까운 화재가 있으면 거리 갱신.
                if d < by_key[key]["min_dist_m"]:
                    by_key[key]["min_dist_m"] = d
                    by_key[key]["nearest_fire"] = fi

    # 2) 각 건물에 occupancy_estimate 부착.
    buildings = []
    for key, e in by_key.items():
        g = e["geo"].get("gis") or {}
        info = e["info"] or {}
        occ = occupancy_estimate(
            use=g.get("use"),
            floors_above=g.get("floors_above"),
            floor_area=g.get("total_floor_area"),
            time_bucket=time_bucket,
            category=info.get("category"),
        )
        buildings.append({
            "key": key,
            "title": info.get("title") or e["geo"].get("name") or "이름 미상 건물",
            "use": info.get("use"),
            "floors": info.get("floors"),
            "height_m": info.get("height_m"),
            "min_dist_m": e["min_dist_m"],
            "nearest_fire_idx": e["nearest_fire"],
            "occupancy": occ,
            "info_source": e["source"],
        })

    # 3) 룰 기반 risk / spreading.
    risk = _compute_risk(buildings, fire_count, smoke_count)
    spreading = _compute_spreading(buildings, fire_count, smoke_count)

    # 4) priority_buildings = (count_est desc, min_dist asc) top N.
    buildings.sort(key=lambda b: (-(b["occupancy"]["count_est"]), b["min_dist_m"]))
    priority = buildings[: max(1, q.top_n)]

    # 4b) top-5 가까운 119안전센터 (로컬 CSV 기반). 화재 중심점 기준.
    nearest_fs_top = []
    if fires_typed and state.get("stations") is not None:
        cx = sum(f.lat for f in fires_typed) / len(fires_typed)
        cy = sum(f.lng for f in fires_typed) / len(fires_typed)
        nearest_fs_top = state["stations"].nearest(cx, cy, n=5)

    # 4c) 현장 특성 → 권장 차종 + top-5 가용 차량 inventory 비교.
    hazard_flags = _site_hazard_flags(fires_typed, priority)
    vehicle_needs = _recommend_vehicles(hazard_flags, fire_count, smoke_count)
    inventory = _aggregate_inventory(nearest_fs_top)
    coverage = _shortfall(vehicle_needs, inventory)

    # 5) LLM 브리핑 (실패 시 template).
    features = {
        "sim_time_iso": sim_time_iso,
        "time_bucket": time_bucket,
        "fire_count": fire_count,
        "smoke_count": smoke_count,
        "fires": [{"lat": f.lat, "lng": f.lng, "cls": f.cls} for f in fires_typed],
        "risk": risk,
        "spreading": spreading,
        "priority_buildings": priority,
        "nearest_fire_stations": nearest_fs_top,
        "hazard_flags": hazard_flags,
        "vehicle_recommendation": coverage,
        "vehicle_inventory": inventory,
    }
    briefing_text = None
    briefing_source = "template"
    if llm.available():
        briefing_text = llm.briefing(features)
        if briefing_text is not None:
            briefing_source = "llm"
    if briefing_text is None:
        briefing_text = llm.fallback_briefing(features)

    return {
        "risk": risk,
        "spreading": spreading,
        "priority_buildings": priority,
        "nearest_fire_stations": nearest_fs_top,
        "hazard_flags": hazard_flags,
        "vehicle_recommendation": coverage,
        "vehicle_inventory": inventory,
        "briefing": briefing_text,
        "provenance": {
            "building_keys": [b["key"] for b in buildings],
            "detection_ids": q.detection_ids or [],
            "sim_time": sim_time_iso,
            "time_bucket": time_bucket,
            "time_label_ko": TIME_LABEL_KO.get(time_bucket, time_bucket),
            "fire_count": fire_count,
            "smoke_count": smoke_count,
            "briefing_source": briefing_source,
            "radius_m": q.radius_m,
        },
    }


if __name__ == "__main__":
    uvicorn.run(app, host=HOST, port=PORT)
