"""GIS건물통합정보(통합본 gis_building_info.csv) 기반 속성 enrich.

GPS(lat,lng) → 최근접 GIS 건물점의 권위적 속성(용도·구조·높이·층수·준공·면적 등).
오프라인·무료·요청제한 없음 → LLM 사실 grounding 의 1차 소스. 좌표(위도/경도) 있는
행만 색인. 점(centroid) 데이터라 최근접 매칭(임계 거리 내).
"""
import csv
import math
from pathlib import Path

from shapely import STRtree
from shapely.geometry import Point

DEFAULT_CSV = Path(__file__).resolve().parent.parent.parent / "gis_building_info.csv"
MAX_MATCH_METERS = 50.0


def _clean(v):
    return None if v in ("", "None", None) else v


def approx_distance_m(lat1, lon1, lat2, lon2):
    dlat = (lat2 - lat1) * 111320.0
    dlon = (lon2 - lon1) * 111320.0 * math.cos(math.radians((lat1 + lat2) * 0.5))
    return math.hypot(dlat, dlon)


class GisBuildings:
    def __init__(self, path=DEFAULT_CSV):
        self.path = Path(path)
        self.pts = []
        self.recs = []
        self._tree = None
        self.load()

    def load(self):
        if not self.path.exists():
            print(f"[gis] CSV 없음: {self.path} — GIS enrich 비활성")
            return 0
        rows = list(csv.reader(open(self.path, encoding="utf-8-sig")))
        hdr = rows[0]
        idx = {h: i for i, h in enumerate(hdr)}

        def g(r, name):
            i = idx.get(name)
            if i is None or i >= len(r):
                return None
            return _clean(r[i])

        for r in rows[1:]:
            lat = g(r, "위도")
            lng = g(r, "경도")
            try:
                lat = float(lat); lng = float(lng)
            except (TypeError, ValueError):
                continue
            self.pts.append(Point(lng, lat))
            self.recs.append({
                "name": g(r, "건물명") or g(r, "건물동명"),
                "use": g(r, "건축물용도명"),
                "structure": g(r, "건축물구조명"),
                "height_m": g(r, "높이(m)"),
                "floors_above": g(r, "지상층수"),
                "floors_below": g(r, "지하층수"),
                "approval_date": g(r, "사용승인일자"),
                "total_floor_area": g(r, "연면적"),
                "building_area": g(r, "건축물면적(㎡)"),
                "land_area": g(r, "대지면적(㎡)"),
                "coverage_ratio": g(r, "건폐율(%)"),
                "floor_ratio": g(r, "용적율(%)"),
                "violation": g(r, "위반건축물여부"),
                "jibun": g(r, "지번"),
                "dong": g(r, "법정동명"),
                "bldg_id": g(r, "건축물ID"),
                "lat": lat, "lng": lng,
            })
        self._tree = STRtree(self.pts) if self.pts else None
        return len(self.pts)

    @property
    def count(self):
        return len(self.pts)

    @staticmethod
    def _has_attrs(rec):
        def nz(v):
            return v not in (None, "", "0", "0.0", "0.00")
        return (bool(rec.get("use")) or bool(rec.get("name"))
                or nz(rec.get("floors_above")) or nz(rec.get("height_m")))

    def nearest(self, lat, lng, max_m=MAX_MATCH_METERS):
        """반경 내 '속성 있는' 최근접 GIS 건물점을 반환(빈 점 무시). 없으면 None.

        빈 점(용도·층수·높이·이름 전무)을 매칭하면 '지상 0층' 등이 LLM 에 들어가
        환각을 유발하므로 제외한다.
        """
        if self._tree is None:
            return None
        pt = Point(lng, lat)
        deg = max_m / 111320.0
        best, bestd = None, 1e18
        for i in self._tree.query(pt.buffer(deg)):
            i = int(i)
            rec = self.recs[i]
            d = approx_distance_m(lat, lng, rec["lat"], rec["lng"])
            if d > max_m or not self._has_attrs(rec):
                continue
            if d < bestd:
                best, bestd = i, d
        if best is None:
            return None
        rec = self.recs[best]
        out = dict(rec)
        out["distance_m"] = round(bestd, 1)
        out["key"] = (f"gis/{rec['bldg_id']}" if rec.get("bldg_id")
                      else f"gis/{rec['lat']:.6f},{rec['lng']:.6f}")
        addr = " ".join(x for x in (rec.get("dong"), rec.get("jibun")) if x)
        out["address"] = addr or None
        return out
