"""건물 정보 캐시 — 건물키(OSM/GIS id) → 카드 정보 JSON.

메모리 dict(읽기) + sqlite(영속). 서버 재시작/재방문 시 즉시 히트.
프리페치가 미리 채워 두면 B/자동 표시 경로는 캐시 히트로 무지연.

schema_version: 카드 스키마가 바뀔 때마다 증가. 다른 스키마로 저장된 캐시는
get() 에서 무효화하여 자동 재생성한다 (구 LLM 자유텍스트 → 신 정형 필드 전환 같은 경우).
"""
import json
import sqlite3
import threading
import time
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parent / "data" / "info_cache.sqlite"


class InfoCache:
    def __init__(self, db_path=DEFAULT_DB):
        self.db_path = Path(db_path)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._conn = sqlite3.connect(str(self.db_path), check_same_thread=False)
        self._conn.execute(
            "CREATE TABLE IF NOT EXISTS info "
            "(key TEXT PRIMARY KEY, value TEXT, model TEXT, ts REAL, schema TEXT DEFAULT '')")
        # 구버전 DB 에 schema 컬럼이 없으면 ALTER 로 추가 (실패 시 이미 존재).
        try:
            self._conn.execute("ALTER TABLE info ADD COLUMN schema TEXT DEFAULT ''")
        except sqlite3.OperationalError:
            pass
        self._conn.commit()
        # 메모리 dict: key → (value, schema). 다른 스키마는 get() 에서 None 으로 처리.
        self._mem = {}
        for k, v, sc in self._conn.execute("SELECT key, value, schema FROM info"):
            try:
                self._mem[k] = (json.loads(v), sc or "")
            except json.JSONDecodeError:
                pass

    def get(self, key, expected_schema=None):
        """expected_schema 지정 시 일치하지 않는 캐시는 무효(미스) 처리."""
        tup = self._mem.get(key)
        if tup is None:
            return None
        value, schema = tup
        if expected_schema is not None and schema != expected_schema:
            return None
        return value

    def has(self, key, expected_schema=None):
        return self.get(key, expected_schema) is not None

    def set(self, key, value, model="", schema=""):
        self._mem[key] = (value, schema)
        with self._lock:
            self._conn.execute(
                "INSERT OR REPLACE INTO info(key,value,model,ts,schema) VALUES(?,?,?,?,?)",
                (key, json.dumps(value, ensure_ascii=False), model, time.time(), schema))
            self._conn.commit()

    def count(self):
        return len(self._mem)
