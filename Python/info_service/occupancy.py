"""결정론적 재실 추정 — (건물 용도 카테고리 × 시간대) → 등급/추정 인구.

순수 함수, LLM 무관. /assess 가 호출해 LLM 브리핑의 "사실 입력" 으로 사용.
LLM 은 이 결과를 변형하지 않고 문장으로만 풀어 쓴다.

근거(rule_id) 도 함께 반환해 provenance 추적 가능.

룰 테이블 가정 (한국 도시 환경 baseline):
  - 업무시설: 평일 주간(09-17) 만석, 점심엔 일부 외출, 야간/주말 거의 비움.
  - 주거: 출퇴근 시간 / 심야가 피크, 주간엔 낮음 (워킹맘·시니어 정도).
  - 상업·판매: 점심과 퇴근 후 피크, 심야 비움.
  - 교육: 평일 주간만 활성, 야간 비움.
  - 의료·종교 등은 카테고리 기본값으로 후술.

density (명/㎡, 연면적 기준) 는 통계청 건물별 평균 재실밀도 (0.05~0.10 명/㎡ 사무실
오피스, 0.02~0.05 주거) 와 BSI 화재안전 표준 (0.08 명/㎡ 판매·운집) 참고.
실측치 아닌 추정 baseline — 인스펙터/실 데이터로 추후 보정 권장.
"""

# ── 시간대 분류 ───────────────────────────────────────────────────────────
# 출근 07:00-09:30, 점심 11:30-13:30, 퇴근 17:00-20:00, 심야 22:00-06:00.
# 나머지(09:30-11:30, 13:30-17:00) 는 일반 주간.
def classify_time(hour, minute=0):
    h = hour + (minute or 0) / 60.0
    if 7 <= h < 9.5:    return "commute_am"
    if 11.5 <= h < 13.5: return "lunch"
    if 17 <= h < 20:    return "commute_pm"
    if h >= 22 or h < 6: return "night"
    return "daytime"


TIME_BUCKETS = ["commute_am", "daytime", "lunch", "commute_pm", "night"]

# 한국어 라벨 — provenance / UI 표시용.
TIME_LABEL_KO = {
    "commute_am":  "출근 시간대",
    "daytime":     "주간 업무 시간",
    "lunch":       "점심 시간대",
    "commute_pm":  "퇴근 시간대",
    "night":       "심야",
}


# ── 룰 테이블 ─────────────────────────────────────────────────────────────
# (카테고리, time_bucket) → (level, density_per_sqm)
# 카테고리는 llm._category_from_use 매핑 결과를 사용 (업무/주거/상업/교육/근린생활/...)
RULES = {
    # 업무시설 — 평일 주간 피크.
    ("업무",     "commute_am"): ("mid",  0.05),
    ("업무",     "daytime"):    ("high", 0.10),
    ("업무",     "lunch"):      ("mid",  0.07),
    ("업무",     "commute_pm"): ("low",  0.02),
    ("업무",     "night"):      ("low",  0.005),
    # 주거 — 심야/출퇴근 시간 피크.
    ("주거",     "commute_am"): ("mid",  0.04),
    ("주거",     "daytime"):    ("low",  0.02),
    ("주거",     "lunch"):      ("mid",  0.03),
    ("주거",     "commute_pm"): ("mid",  0.04),
    ("주거",     "night"):      ("high", 0.05),
    # 상업·판매 — 점심·퇴근 후 피크.
    ("상업",     "commute_am"): ("low",  0.02),
    ("상업",     "daytime"):    ("mid",  0.04),
    ("상업",     "lunch"):      ("high", 0.08),
    ("상업",     "commute_pm"): ("high", 0.08),
    ("상업",     "night"):      ("low",  0.01),
    # 교육 — 평일 주간만.
    ("교육",     "commute_am"): ("mid",  0.05),
    ("교육",     "daytime"):    ("high", 0.08),
    ("교육",     "lunch"):      ("high", 0.07),
    ("교육",     "commute_pm"): ("low",  0.02),
    ("교육",     "night"):      ("low",  0.005),
    # 근린생활(편의·식당) — 점심/저녁 피크.
    ("근린생활", "commute_am"): ("low",  0.02),
    ("근린생활", "daytime"):    ("mid",  0.04),
    ("근린생활", "lunch"):      ("high", 0.07),
    ("근린생활", "commute_pm"): ("high", 0.06),
    ("근린생활", "night"):      ("low",  0.01),
    # 의료 — 24h 활동, 주간 피크.
    ("의료",     "commute_am"): ("mid",  0.04),
    ("의료",     "daytime"):    ("high", 0.06),
    ("의료",     "lunch"):      ("mid",  0.05),
    ("의료",     "commute_pm"): ("mid",  0.04),
    ("의료",     "night"):      ("mid",  0.03),   # 응급실/병동 baseline
    # 숙박 — 심야 피크, 주간엔 낮음.
    ("숙박",     "commute_am"): ("mid",  0.03),
    ("숙박",     "daytime"):    ("low",  0.02),
    ("숙박",     "lunch"):      ("low",  0.02),
    ("숙박",     "commute_pm"): ("mid",  0.04),
    ("숙박",     "night"):      ("high", 0.05),
    # 문화·종교·교통·산업 — 평이한 주간 활성.
    ("문화",     "daytime"):    ("mid",  0.04),
    ("문화",     "lunch"):      ("mid",  0.04),
    ("문화",     "commute_pm"): ("high", 0.06),
    ("문화",     "night"):      ("low",  0.01),
    ("문화",     "commute_am"): ("low",  0.02),
    ("교통",     "commute_am"): ("high", 0.08),
    ("교통",     "daytime"):    ("mid",  0.04),
    ("교통",     "lunch"):      ("mid",  0.04),
    ("교통",     "commute_pm"): ("high", 0.08),
    ("교통",     "night"):      ("low",  0.01),
    ("산업",     "commute_am"): ("mid",  0.04),
    ("산업",     "daytime"):    ("high", 0.05),
    ("산업",     "lunch"):      ("mid",  0.03),
    ("산업",     "commute_pm"): ("mid",  0.04),
    ("산업",     "night"):      ("low",  0.01),
}

# (카테고리 알 수 없음) 폴백 — "건물" 으로 들어왔을 때.
DEFAULT_DENSITY_BY_BUCKET = {
    "commute_am":  ("mid", 0.03),
    "daytime":     ("mid", 0.04),
    "lunch":       ("mid", 0.04),
    "commute_pm":  ("mid", 0.03),
    "night":       ("low", 0.02),
}


def _coerce_area(floor_area):
    """연면적 → float ㎡. 빈 값/0/음수는 None."""
    if floor_area in (None, "", "0", "0.0", "0.00"):
        return None
    try:
        v = float(floor_area)
    except (TypeError, ValueError):
        return None
    return v if v > 0 else None


def _coerce_floors(floors):
    """지상층수 → int. 0/빈 값은 1 (단층 가정 — 너무 낮게 잡지 않음)."""
    try:
        v = int(float(floors))
    except (TypeError, ValueError):
        return 1
    return max(1, v)


def occupancy_estimate(use, floors_above, floor_area, time_bucket, category=None):
    """
    use            GIS 건축물용도명 (한국어, '업무시설' 등) — 카테고리 매핑 입력.
    floors_above   지상 층수 (int|None|str).
    floor_area     연면적 총합 (㎡, float|None|str).
    time_bucket    classify_time() 결과.
    category       이미 매핑된 카테고리 ('업무'/'주거'/...). 없으면 _category_from_use 호출.

    return: {level, count_est, rule_id, density, category, time_bucket}
        level     'low' | 'mid' | 'high'
        count_est int  추정 동시 재실 인원
        rule_id   적용된 룰 식별자 (provenance 추적)
        density   적용 밀도 (명/㎡)
        category  결정된 카테고리
        time_bucket 그대로 echo
    """
    # 지역 import — 모듈 cycle 방지.
    from llm import _category_from_use

    cat = category or _category_from_use(use) or "기타"
    key = (cat, time_bucket)
    if key in RULES:
        level, density = RULES[key]
        rule_id = f"{cat}×{time_bucket}"
    else:
        # 카테고리 미매핑 또는 룰 미정의 — 시간대 default 사용.
        level, density = DEFAULT_DENSITY_BY_BUCKET.get(time_bucket, ("mid", 0.03))
        rule_id = f"default[{cat}|{time_bucket}]"

    area = _coerce_area(floor_area)
    if area is None:
        # 연면적 미상 — 층수 × 평균 층면적 가정 (300㎡/층 baseline; 인스펙터 보정 권장).
        floors = _coerce_floors(floors_above)
        area = 300.0 * floors
        rule_id += "+area_est"

    count_est = int(area * density)
    return {
        "level": level,
        "count_est": count_est,
        "rule_id": rule_id,
        "density": density,
        "category": cat,
        "time_bucket": time_bucket,
    }


# ── 자체 sanity test (모듈 import 시 무영향) ──────────────────────────────
if __name__ == "__main__":
    # 시간대 분류 sanity.
    assert classify_time(8)  == "commute_am"
    assert classify_time(10) == "daytime"
    assert classify_time(12) == "lunch"
    assert classify_time(18, 30) == "commute_pm"
    assert classify_time(2)  == "night"
    assert classify_time(23) == "night"

    # 시간대 전환 시 occupancy 변화 확인.
    day  = occupancy_estimate("업무시설", 21, 50000, "daytime")
    night= occupancy_estimate("업무시설", 21, 50000, "night")
    assert day["level"]  == "high" and night["level"] == "low"
    assert day["count_est"]  > night["count_est"] * 10

    home_night = occupancy_estimate("공동주택", 15, 30000, "night")
    home_day   = occupancy_estimate("공동주택", 15, 30000, "daytime")
    assert home_night["level"] == "high" and home_day["level"] == "low"

    # 연면적 미상 → 추정 폴백.
    no_area = occupancy_estimate("업무시설", 5, None, "daytime")
    assert no_area["count_est"] > 0
    assert "area_est" in no_area["rule_id"]

    print("[occupancy] self-test OK")
    print("  업무×주간  :", day)
    print("  업무×심야  :", night)
    print("  주거×심야  :", home_night)
    print("  연면적미상 :", no_area)
