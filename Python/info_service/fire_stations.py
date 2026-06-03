"""인천광역시 119안전센터 CSV 로드 + 거리 기반 top-N + 차량 inventory.

CSV (CP949): 인천광역시_119안전센터 위치_20260512.csv
컬럼: 기관코드,상위본부명,센터명,센터구분,도로명주소,지번주소,행정구역명,
      경도,위도,배치차량종류,배치차량대수,기준일자

차량 inventory:
  - 배치차량종류: 콤마 분리 (각 차종 별 1대 가정. 실제 대수는 배치차량대수 의 합.)
  - 배치차량대수: 센터 총 보유 대수.
  배치차량종류 항목 수가 항상 대수와 일치하지는 않음 (예: '펌프차,물탱크차,굴절차,구급차'=4 인데 대수=5). 차종 list 만 그대로 노출, 대수는 합계 메타로.

거리: equirectangular (geographic 짧은 거리에서 충분).
LLM 무관 — 순수 파이썬, /assess 가 결과를 LLM 에 fact 로 전달.
"""
import csv
import math
import os
from pathlib import Path

DEFAULT_CSV = Path(__file__).resolve().parent.parent.parent / "인천광역시_119안전센터 위치_20260512.csv"


def _to_float(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def _to_int(v):
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return 0


def _norm_vehicle(v):
    """차종명 정규화 — 공백 제거, '고성능 화학차' = '고성능화학차' 식 통일."""
    if not v:
        return ""
    s = "".join(v.split())   # 모든 공백 제거
    # 흔한 변형 통일.
    if s in ("펌프차(2)", "예비펌프차", "저상펌프차"):
        return "펌프차"
    if s in ("예비구급차", "특별구급차", "특별구급차등", "음압구급차"):
        return "구급차"
    if s in ("굴절사다리차", "고가사다리차", "소형사다리차", "사다리차"):
        return "사다리차"
    if s in ("굴절차", "고가차"):
        return s
    if s in ("고성능화학차",):
        return "고성능화학차"
    if s in ("화학차",):
        return "화학차"
    if s in ("무인방수차",):
        return "무인방수차"
    if s in ("물탱크차",):
        return "물탱크차"
    return s


def _approx_distance_m(lat1, lng1, lat2, lng2):
    dlat = (lat2 - lat1) * 111320.0
    dlng = (lng2 - lng1) * 111320.0 * math.cos(math.radians((lat1 + lat2) * 0.5))
    return math.hypot(dlat, dlng)


class FireStations:
    def __init__(self, path=None):
        self.path = Path(path) if path else DEFAULT_CSV
        self.records = []
        self.load()

    def load(self):
        if not self.path.exists():
            print(f"[fire_stations] CSV 없음: {self.path}")
            return 0
        # 인코딩: utf-8 / utf-8-sig / cp949 / euc-kr 순으로 시도.
        text = None
        for enc in ("utf-8-sig", "utf-8", "cp949", "euc-kr"):
            try:
                with open(self.path, encoding=enc) as f:
                    text = f.read()
                break
            except UnicodeDecodeError:
                continue
        if text is None:
            print(f"[fire_stations] {self.path} 인코딩 해결 실패")
            return 0

        rdr = csv.DictReader(text.splitlines())
        for r in rdr:
            lat = _to_float(r.get("위도"))
            lng = _to_float(r.get("경도"))
            if lat is None or lng is None:
                continue
            raw = (r.get("배치차량종류") or "").strip()
            vlist = [_norm_vehicle(v.strip()) for v in raw.split(",") if v.strip()]
            vlist = [v for v in vlist if v]
            # 이름 fallback — CSV 일부 row 가 본부(소방서) 자체라 센터명이 비어있음.
            # 그 경우 상위본부명을 그대로 사용 (예: '인천송도소방서').
            raw_name = (r.get("센터명") or "").strip()
            parent_name = (r.get("상위본부명") or "").strip()
            display_name = raw_name or (parent_name + " 본부" if parent_name else "(이름미상)")
            self.records.append({
                "code":           r.get("기관코드"),
                "parent":         parent_name or None,
                "name":           display_name,
                "kind":           r.get("센터구분"),
                "road_address":   r.get("도로명주소"),
                "jibun_address":  r.get("지번주소"),
                "district":       r.get("행정구역명"),
                "lat":            lat,
                "lng":            lng,
                "vehicle_types":      vlist,
                "vehicle_types_raw":  raw,
                "vehicle_total":      _to_int(r.get("배치차량대수")),
                "ref_date":           r.get("기준일자"),
            })
        return len(self.records)

    @property
    def count(self):
        return len(self.records)

    def nearest(self, lat, lng, n=5):
        """top-N 가까운 센터. 각 항목에 distance_m 부여."""
        if not self.records:
            return []
        out = []
        for r in self.records:
            d = _approx_distance_m(lat, lng, r["lat"], r["lng"])
            out.append(dict(r, distance_m=round(d, 1)))
        out.sort(key=lambda x: x["distance_m"])
        return out[: max(1, n)]


# ── 자체 테스트 ─────────────────────────────────────────────────────────
if __name__ == "__main__":
    fs = FireStations()
    print(f"loaded {fs.count} 119 안전센터")
    if fs.count > 0:
        # 송도 근처 (37.3828, 126.6561) top-5.
        top = fs.nearest(37.3828, 126.6561, n=5)
        for s in top:
            print(f"  {s['distance_m']:7.0f}m  {s['name']:22} "
                  f"차종 {len(s['vehicle_types'])}종({s['vehicle_total']}대): {','.join(s['vehicle_types'])}")
