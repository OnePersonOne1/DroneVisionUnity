"""건물 정보 캐시 — 건물키(OSM id) → 생성된 정보 JSON.

메모리 dict(읽기) + sqlite(영속). 서버 재시작/재방문 시 즉시 히트.
프리페치가 미리 채워 두면 B/자동 표시 경로는 캐시 히트로 무지연.
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
            "(key TEXT PRIMARY KEY, value TEXT, model TEXT, ts REAL)")
        self._conn.commit()
        self._mem = {}
        for k, v in self._conn.execute("SELECT key, value FROM info"):
            try:
                self._mem[k] = json.loads(v)
            except json.JSONDecodeError:
                pass

    def get(self, key):
        return self._mem.get(key)

    def has(self, key):
        return key in self._mem

    def set(self, key, value, model=""):
        self._mem[key] = value
        with self._lock:
            self._conn.execute(
                "INSERT OR REPLACE INTO info(key,value,model,ts) VALUES(?,?,?,?)",
                (key, json.dumps(value, ensure_ascii=False), model, time.time()))
            self._conn.commit()

    def count(self):
        return len(self._mem)
