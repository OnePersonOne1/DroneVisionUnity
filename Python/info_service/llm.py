"""Ollama LLM 클라이언트 — 건물 컨텍스트(GIS건물통합정보 + Kakao + OSM) → 한국어 정보 JSON.

서빙은 Ollama(자체 /api/chat). 모델은 env 로 교체:
    INFO_LLM_MODEL=qwen2.5:14b      (기본, 4090 24GB 적합)
    INFO_LLM_MODEL=exaone3.5:7.8b   (한국어 특화·최저지연)
    INFO_LLM_MODEL=qwen2.5:32b      (GPU 전용 시 품질 상한)

입력 geo dict 가 담는 보강 필드:
    geo["gis"]   = GIS건물통합정보 속성(용도·구조·높이·층수·준공·면적…) 또는 None
    geo["kakao"] = {building_name, road_address, …} 또는 None
출력 스키마(값 한국어, 키는 파싱 안정용 영어):
    {"title": str, "category": str, "summary": str, "detail": str}
"""
import json
import os
import time

import requests

OLLAMA_HOST = os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.environ.get("INFO_LLM_MODEL", "qwen2.5:14b")
# 기본 timeout 짧게 — GPU 가 다른 작업으로 busy 하면 generate 가 hang.
# LLM 실패 시 fallback briefing 으로 즉시 빠지도록 (INFO_LLM_TIMEOUT 으로 조정 가능).
TIMEOUT = float(os.environ.get("INFO_LLM_TIMEOUT", "15"))
# 실패 cooldown — 한 번 timeout/에러 나면 N초간 LLM 호출 skip. GPU hang 이 풀릴 시간 확보.
FAIL_COOLDOWN = float(os.environ.get("INFO_LLM_FAIL_COOLDOWN", "60"))
_unavailable_until = 0.0


def _mark_unavailable():
    global _unavailable_until
    _unavailable_until = time.time() + FAIL_COOLDOWN

SYSTEM = (
    "당신은 지도 위 건물 정보를 간결하게 정리해 보여주는 도우미입니다. "
    "주어진 건축물대장/지도 데이터(이름·용도·구조·층수·높이·준공연도·주소 등)를 "
    "바탕으로 한국어로 작성하세요. 제공된 사실만 사용하고, 용도·규모·역사 등 주어지지 "
    "않은 정보는 추측하지 마세요. 정보가 적으면 이름과 위치만 간결히 쓰세요. "
    "반드시 아래 JSON 형식으로만 답하세요: "
    '{"title": 건물명, "category": 분류(예: 공공기관·교육·주거·상업·업무·산업·문화), '
    '"summary": 한 줄 요약, "detail": 2~3문장 설명}'
)


def _best_name(geo):
    g = geo.get("gis") or {}
    kk = geo.get("kakao") or {}
    return (kk.get("building_name") or g.get("name")
            or geo.get("name") or "이름 미상 건물")


def _category_from_use(use):
    if not use:
        return None
    table = {"공동주택": "주거", "단독주택": "주거", "교육연구": "교육", "공장": "산업",
             "업무시설": "업무", "근린생활": "근린생활", "판매시설": "상업",
             "숙박": "숙박", "문화": "문화", "의료": "의료", "종교": "종교",
             "운수": "교통", "위험물": "산업", "분뇨": "환경"}
    for k, v in table.items():
        if k in use:
            return v
    return use


def _nz(v):
    """비어있거나 0 이면 False (GIS 의 '0층'·'0m' 같은 무의미값 제외용)."""
    return v not in (None, "", "0", "0.0", "0.00")


def _facts(geo):
    g = geo.get("gis") or {}
    kk = geo.get("kakao") or {}
    lines = []
    name = kk.get("building_name") or g.get("name") or geo.get("name")
    if name:
        lines.append(f"이름: {name}")
    addr = kk.get("road_address") or g.get("address") or geo.get("address")
    if addr:
        lines.append(f"주소: {addr}")
    if g.get("use"):
        lines.append(f"용도: {g['use']}")
    if g.get("structure"):
        lines.append(f"구조: {g['structure']}")
    fa, fb = g.get("floors_above"), g.get("floors_below")
    if _nz(fa):
        fl = f"지상 {fa}층"
        if _nz(fb):
            fl += f", 지하 {fb}층"
        lines.append(f"층수: {fl}")
    if _nz(g.get("height_m")):
        lines.append(f"높이: {g['height_m']}m")
    if g.get("approval_date"):
        lines.append(f"사용승인일: {g['approval_date']}")
    if _nz(g.get("total_floor_area")):
        lines.append(f"연면적: {g['total_floor_area']}㎡")
    if str(g.get("violation")).upper() == "Y":
        lines.append("위반건축물: 예")
    if not g and geo.get("building_type") and geo["building_type"] != "yes":
        lines.append(f"용도(OSM): {geo['building_type']}")
    if not lines:
        lines.append(f"좌표: {geo.get('lat')}, {geo.get('lng')} (정보 없음)")
    return "\n".join(lines)


def fallback_info(geo):
    """LLM 미사용/실패 시 GIS/OSM 필드만으로 구성한 기본 정보 (항상 무언가 반환)."""
    g = geo.get("gis") or {}
    name = _best_name(geo)
    cat = _category_from_use(g.get("use")) or "건물"
    addr = (geo.get("kakao") or {}).get("road_address") or g.get("address") or geo.get("address")
    bits = []
    if g.get("use"):
        bits.append(g["use"])
    if g.get("floors_above"):
        bits.append(f"지상{g['floors_above']}층")
    if g.get("structure"):
        bits.append(g["structure"])
    detail = f"{name}, {', '.join(bits)}." if bits else ""
    return {"title": name, "category": cat,
            "summary": addr or "주소 정보 없음", "detail": detail}


def available(host=OLLAMA_HOST, timeout=3):
    # 직전 실패 cooldown 중이면 즉시 False — generate hang 시 호출 폭증 방지.
    if time.time() < _unavailable_until:
        return False
    try:
        r = requests.get(f"{host}/api/tags", timeout=timeout)
        return r.status_code == 200
    except requests.RequestException:
        _mark_unavailable()
        return False


## ── 상황 브리핑 (situation assessment) ─────────────────────────────────
# 작업2: /assess 가 정형 feature 를 만들어 LLM 에 넘기면, LLM 은 문장만 합성.
# 판정(risk/spreading/priority) 은 룰이 이미 끝낸 상태로 들어옴 — LLM 은 변경 금지.
BRIEFING_SYSTEM = (
    "당신은 재난 상황 모니터링을 보조하는 한국어 상황 브리핑 작성자입니다. "
    "제공된 feature(화재 위치·시간대·주변 건물 용도/층수/추정 재실/거리)만 사용해 "
    "한국어로 간결한 상황 브리핑을 작성합니다. 외부 지식·추측·새 사실 추가 금지. "
    "주어진 risk 등급과 확산 판정은 변경하지 말고 문장으로 풀어 쓰기만 합니다. "
    "반드시 다음 JSON 형식으로만 응답하세요: "
    '{"briefing": "3~5문장 한국어 상황 요약 — 위험도, 시간대 영향, 우선 주목 건물, 권장 사항"}'
)


def _facts_for_briefing(features):
    """feature dict → LLM 입력용 사실 텍스트 (한국어). 추측 여지 차단을 위해 모든 필드 명시."""
    lines = []
    lines.append(f"시각: {features.get('sim_time_iso','?')}  (시간대: {features.get('time_bucket','?')})")
    fc, sc = features.get("fire_count", 0), features.get("smoke_count", 0)
    lines.append(f"화재 신고: fire={fc}건, smoke={sc}건  (총 {fc + sc}건)")
    risk = features.get("risk", {})
    lines.append(f"위험도 판정(룰): {risk.get('level','?')} — {risk.get('reason','')}")
    lines.append(f"확산 판정(룰): {features.get('spreading','?')}")
    pri = features.get("priority_buildings", []) or []
    if pri:
        lines.append("주변 건물 (우선순위순):")
        for b in pri[:8]:
            occ = b.get("occupancy") or {}
            lines.append(
                f"  - {b.get('title','?')}"
                f" | 용도={b.get('use') or '-'}"
                f" | 층수={b.get('floors') or '-'}"
                f" | 추정재실={occ.get('level','-')}({occ.get('count_est','?')}명)"
                f" | 거리={b.get('min_dist_m', 0):.0f}m"
            )
    else:
        lines.append("주변 건물 정보 없음 (반경 내 건물 미식별).")
    stations = features.get("nearest_fire_stations") or []
    if stations:
        lines.append("가까운 119 안전센터 (top-5, 가까운 순):")
        for s in stations[:5]:
            vt = ",".join((s.get("vehicle_types") or [])[:6]) or "-"
            lines.append(f"  - {s.get('name','?')}  거리 {s.get('distance_m',0):.0f}m  "
                         f"보유 {s.get('vehicle_total',0)}대  차종=[{vt}]  "
                         f"주소={s.get('road_address') or s.get('jibun_address') or '-'}")
    flags = features.get("hazard_flags") or {}
    if flags:
        marks = []
        if flags.get("highrise"):    marks.append("고층")
        if flags.get("industrial"):  marks.append("산업/위험물")
        if flags.get("educational"): marks.append("교육시설")
        if flags.get("crowded"):     marks.append("인원밀집")
        if flags.get("multi_fire"):  marks.append("다중화재")
        if flags.get("hot_zone"):    marks.append("핫존")
        lines.append(f"현장 특성 플래그: {', '.join(marks) if marks else '특이사항 없음'}")
    rec = features.get("vehicle_recommendation") or []
    if rec:
        lines.append("권장 차량 (룰 산출 — 변경 금지, 표현만 다듬을 것):")
        for r in rec:
            mark = "✓" if r.get("ok") else "⚠"
            lines.append(f"  {mark} {r['type']}: 필요 {r['needed']}대 / 가용(top-5) {r['available']}대 — {r['reason']}")
    return "\n".join(lines)


def briefing(features, model=None, host=OLLAMA_HOST, timeout=TIMEOUT):
    """상황 feature → 한국어 브리핑 문장 (Ollama). 실패 시 None — 호출부가 fallback_briefing 사용.

    LLM 은 "주어진 feature 만 사용·룰 판정 변경 금지·JSON only" 제약 하에 문장만 만든다.
    """
    model = model or OLLAMA_MODEL
    user = "다음 사실만으로 상황 브리핑을 작성하세요. 등급·확산 판정은 변경 금지.\n" + _facts_for_briefing(features)
    try:
        r = requests.post(
            f"{host}/api/chat",
            json={
                "model": model,
                "messages": [{"role": "system", "content": BRIEFING_SYSTEM},
                             {"role": "user", "content": user}],
                "format": "json",
                "stream": False,
                "options": {"temperature": 0.2, "num_predict": 384},
            },
            timeout=timeout,
        )
        r.raise_for_status()
        data = json.loads(r.json()["message"]["content"])
    except (requests.RequestException, KeyError, json.JSONDecodeError) as ex:
        print(f"[llm] briefing 실패: {ex} — {FAIL_COOLDOWN:.0f}s 동안 LLM skip")
        _mark_unavailable()
        return None
    return (data.get("briefing") or "").strip() or None


def fallback_briefing(features):
    """LLM 미가동/실패 시 정형 조립. /assess 가 항상 무언가 반환할 수 있도록."""
    time_bucket = features.get("time_bucket", "?")
    # occupancy 의 한글 라벨 재사용.
    try:
        from occupancy import TIME_LABEL_KO
        tb_ko = TIME_LABEL_KO.get(time_bucket, time_bucket)
    except ImportError:
        tb_ko = time_bucket
    fc = features.get("fire_count", 0)
    sc = features.get("smoke_count", 0)
    risk = features.get("risk", {})
    spreading = features.get("spreading", "?")
    pri = features.get("priority_buildings", []) or []
    top_names = ", ".join((b.get("title") or "건물") for b in pri[:3])

    parts = [
        f"[{tb_ko}] 화재 {fc}건·연기 {sc}건 보고.",
        f"위험도 {risk.get('level','-')} ({risk.get('reason','-')}).",
        f"확산 상태: {spreading}.",
    ]
    if top_names:
        parts.append(f"주요 인접 건물: {top_names}.")
    return " ".join(parts)


def generate(geo, model=None, host=OLLAMA_HOST, timeout=TIMEOUT):
    """건물 컨텍스트로 LLM 정보 생성. 실패 시 None (호출부가 fallback)."""
    model = model or OLLAMA_MODEL
    user = f"다음 건물의 정보를 작성하세요.\n{_facts(geo)}"
    try:
        r = requests.post(
            f"{host}/api/chat",
            json={
                "model": model,
                "messages": [{"role": "system", "content": SYSTEM},
                             {"role": "user", "content": user}],
                "format": "json",
                "stream": False,
                "options": {"temperature": 0.3, "num_predict": 256},
            },
            timeout=timeout,
        )
        r.raise_for_status()
        data = json.loads(r.json()["message"]["content"])
    except (requests.RequestException, KeyError, json.JSONDecodeError) as ex:
        print(f"[llm] generate 실패: {ex} — {FAIL_COOLDOWN:.0f}s 동안 LLM skip")
        _mark_unavailable()
        return None
    # 이름은 사실 소스(Kakao>GIS>OSM)를 신뢰 — LLM 이 변형하지 못하게 고정.
    return {
        "title": _best_name(geo),
        "category": data.get("category") or _category_from_use((geo.get("gis") or {}).get("use")) or "건물",
        "summary": data.get("summary") or "",
        "detail": data.get("detail") or "",
    }
