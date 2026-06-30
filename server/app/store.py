"""SQLite metadata store + on-disk asset (GLB/PNG) store.

The authoring source of truth (design #266 §central tension). Each authored object is
persisted as its validated JSON keyed by id; binary assets (GLB parts, sign PNGs) live
on disk under ``assets/``. Deliberately small — SQLite first, Postgres later (design §5).
"""
from __future__ import annotations

import json
import sqlite3
import threading
from pathlib import Path
from typing import List, Optional

from .models import BuildingSpecificDef, PaletteDef, PartDef, TemplateDef

_SCHEMA = """
CREATE TABLE IF NOT EXISTS parts     (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS templates (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS palettes  (neighborhood TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS overrides (osm_id INTEGER PRIMARY KEY, json TEXT NOT NULL);
"""


class Store:
    """Thread-safe (single-connection + lock) store over SQLite and a disk asset dir."""

    def __init__(self, db_path: str, assets_dir: str):
        self._lock = threading.Lock()
        self._conn = sqlite3.connect(db_path, check_same_thread=False)
        self._conn.executescript(_SCHEMA)
        self._conn.commit()
        self.assets_dir = Path(assets_dir)
        (self.assets_dir / "parts").mkdir(parents=True, exist_ok=True)

    def close(self) -> None:
        self._conn.close()

    # -- parts --------------------------------------------------------------

    def upsert_part(self, part: PartDef) -> None:
        self._upsert("parts", "id", part.id, part)

    def list_parts(self) -> List[PartDef]:
        return [PartDef.model_validate_json(j) for j in self._all("parts")]

    def get_part(self, part_id: str) -> Optional[PartDef]:
        row = self._one("parts", "id", part_id)
        return PartDef.model_validate_json(row) if row else None

    # -- templates ----------------------------------------------------------

    def upsert_template(self, tpl: TemplateDef) -> None:
        self._upsert("templates", "id", tpl.id, tpl)

    def list_templates(self) -> List[TemplateDef]:
        return [TemplateDef.model_validate_json(j) for j in self._all("templates")]

    # -- palettes -----------------------------------------------------------

    def upsert_palette(self, pal: PaletteDef) -> None:
        self._upsert("palettes", "neighborhood", pal.neighborhood, pal)

    def list_palettes(self) -> List[PaletteDef]:
        return [PaletteDef.model_validate_json(j) for j in self._all("palettes")]

    # -- overrides ----------------------------------------------------------

    def upsert_override(self, ov: BuildingSpecificDef) -> None:
        self._upsert("overrides", "osm_id", ov.osm_id, ov)

    def list_overrides(self) -> List[BuildingSpecificDef]:
        return [BuildingSpecificDef.model_validate_json(j) for j in self._all("overrides")]

    # -- GLB binaries -------------------------------------------------------

    def save_glb(self, part_id: str, data: bytes) -> Path:
        path = self.assets_dir / "parts" / f"{part_id}.glb"
        path.write_bytes(data)
        return path

    def glb_path(self, part_id: str) -> Optional[Path]:
        path = self.assets_dir / "parts" / f"{part_id}.glb"
        return path if path.exists() else None

    # -- internals ----------------------------------------------------------

    def _upsert(self, table: str, key_col: str, key, model) -> None:
        payload = model.model_dump_json(by_alias=True)
        with self._lock:
            self._conn.execute(
                f"INSERT INTO {table} ({key_col}, json) VALUES (?, ?) "
                f"ON CONFLICT({key_col}) DO UPDATE SET json = excluded.json",
                (key, payload),
            )
            self._conn.commit()

    def _all(self, table: str) -> List[str]:
        with self._lock:
            cur = self._conn.execute(f"SELECT json FROM {table} ORDER BY rowid")
            return [r[0] for r in cur.fetchall()]

    def _one(self, table: str, key_col: str, key) -> Optional[str]:
        with self._lock:
            cur = self._conn.execute(f"SELECT json FROM {table} WHERE {key_col} = ?", (key,))
            row = cur.fetchone()
            return row[0] if row else None
