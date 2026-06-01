"""`전체 건물 정보.csv`(GIS건물통합정보 AL_D010, 헤더 A0~A28) + `설명서.xlsx` 통합.

일반화된 A0~A28 헤더를 설명서의 한글 컬럼명으로 치환한 자기설명적(self-describing)
통합본을 만든다. 데이터 행은 그대로 보존(중복/좌표없음 행도 유지 — 정제는 파이프라인
로드 단계에서).

출력: PROJECT_ROOT/gis_building_info.csv (UTF-8 BOM, Excel 호환)

사용: python3 build_integrated_csv.py
"""
import csv
import re
from pathlib import Path

# Excel 이 번지(본번-부번)를 날짜로 오인 변환한 것을 복원.
#   "12-1"  -> "12월 01일"  (월=본번, 일=부번)
#   "5-68"  -> "May-68"     (영문 월 약어 + 부번)
_MONTHS = {"jan": "1", "feb": "2", "mar": "3", "apr": "4", "may": "5", "jun": "6",
           "jul": "7", "aug": "8", "sep": "9", "oct": "10", "nov": "11", "dec": "12"}


def fix_jibun(v):
    if not v or v == "None":
        return v
    m = re.match(r"^(\d{1,2})월\s*(\d{1,2})일$", v)          # 한글 날짜
    if m:
        return f"{int(m.group(1))}-{int(m.group(2))}"
    m = re.match(r"^([A-Za-z]{3})-(\d+)$", v)                 # 영문 월 약어
    if m and m.group(1).lower() in _MONTHS:
        return f"{_MONTHS[m.group(1).lower()]}-{m.group(2)}"
    return v

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
SRC = PROJECT_ROOT / "전체 건물 정보.csv"
OUT = PROJECT_ROOT / "gis_building_info.csv"

# 설명서.xlsx (GIS건물통합정보 AL_D010) 기준 컬럼 정의. 마지막 4개(BID/좌표/레이어)는
# 원본에 추가된 처리 컬럼이라 명확한 한글명으로.
HEADERS = [
    "원천도형ID", "GIS건물통합식별번호", "고유번호", "법정동코드", "법정동명",
    "지번", "특수지코드", "특수지구분명", "건축물용도코드", "건축물용도명",
    "건축물구조코드", "건축물구조명", "건축물면적(㎡)", "사용승인일자", "연면적",
    "대지면적(㎡)", "높이(m)", "건폐율(%)", "용적율(%)", "건축물ID",
    "위반건축물여부", "참조체계연계키", "데이터기준일자", "원천시도시군구코드", "건물명",
    "건물동명", "지상층수", "지하층수", "데이터생성변경일자",
    "BID", "위도", "경도", "레이어",
]


def main():
    rows = list(csv.reader(open(SRC, encoding="utf-8-sig")))
    src_header, data = rows[0], rows[1:]
    if len(src_header) != len(HEADERS):
        raise SystemExit(
            f"컬럼 수 불일치: 원본 {len(src_header)} vs 매핑 {len(HEADERS)}. "
            f"설명서/원본 변경 여부 확인 필요.")
    ji = HEADERS.index("지번")
    fixed = 0
    for r in data:
        if len(r) > ji:
            new = fix_jibun(r[ji])
            if new != r[ji]:
                r[ji] = new
                fixed += 1
    with open(OUT, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(HEADERS)
        w.writerows(data)
    print(f"[done] {len(data)}행 → {OUT}  (지번 날짜오류 복원 {fixed}건)")
    print("헤더:", ", ".join(HEADERS))


if __name__ == "__main__":
    main()
