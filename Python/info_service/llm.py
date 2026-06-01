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

import requests

OLLAMA_HOST = os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.environ.get("INFO_LLM_MODEL", "qwen2.5:14b")
TIMEOUT = float(os.environ.get("INFO_LLM_TIMEOUT", "60"))

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
    try:
        r = requests.get(f"{host}/api/tags", timeout=timeout)
        return r.status_code == 200
    except requests.RequestException:
        return False


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
        print(f"[llm] generate 실패: {ex}")
        return None
    # 이름은 사실 소스(Kakao>GIS>OSM)를 신뢰 — LLM 이 변형하지 못하게 고정.
    return {
        "title": _best_name(geo),
        "category": data.get("category") or _category_from_use((geo.get("gis") or {}).get("use")) or "건물",
        "summary": data.get("summary") or "",
        "detail": data.get("detail") or "",
    }
