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

from .models import (
    BuildingFacts,
    BuildingSpecificDef,
    FacadeCanvas,
    PaletteDef,
    PartDef,
    SignDef,
    TemplateDef,
)

_SCHEMA = """
CREATE TABLE IF NOT EXISTS parts     (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS templates (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS palettes  (neighborhood TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS overrides (osm_id INTEGER PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS signs     (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS canvases  (key TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS buildings (
    osm_id        INTEGER PRIMARY KEY,
    neighborhood  TEXT NOT NULL DEFAULT '',
    building_type TEXT NOT NULL DEFAULT '',
    json          TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_buildings_filter ON buildings (neighborhood, building_type);
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
        (self.assets_dir / "signs").mkdir(parents=True, exist_ok=True)

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

    # -- signs --------------------------------------------------------------

    def upsert_sign(self, sign: SignDef) -> None:
        self._upsert("signs", "id", sign.signId, sign)

    def list_signs(self) -> List[SignDef]:
        return [SignDef.model_validate_json(j) for j in self._all("signs")]

    def get_sign(self, sign_id: str) -> Optional[SignDef]:
        row = self._one("signs", "id", sign_id)
        return SignDef.model_validate_json(row) if row else None

    def save_sign_png(self, sign_id: str, png: bytes, thumb: bytes) -> None:
        (self.assets_dir / "signs" / f"{sign_id}.png").write_bytes(png)
        (self.assets_dir / "signs" / f"{sign_id}.thumb.png").write_bytes(thumb)

    def sign_png_path(self, sign_id: str) -> Optional[Path]:
        path = self.assets_dir / "signs" / f"{sign_id}.png"
        return path if path.exists() else None

    def sign_thumb_path(self, sign_id: str) -> Optional[Path]:
        path = self.assets_dir / "signs" / f"{sign_id}.thumb.png"
        return path if path.exists() else None

    # -- facade canvases (keyed by osm_id:facade) ---------------------------

    @staticmethod
    def _canvas_key(osm_id: int, facade: str) -> str:
        return f"{osm_id}:{facade}"

    def upsert_canvas(self, canvas: FacadeCanvas) -> None:
        self._upsert("canvases", "key", self._canvas_key(canvas.osm_id, canvas.facade), canvas)

    def list_canvases(self) -> List[FacadeCanvas]:
        return [FacadeCanvas.model_validate_json(j) for j in self._all("canvases")]

    def list_canvases_for(self, osm_id: int) -> List[FacadeCanvas]:
        return [c for c in self.list_canvases() if c.osm_id == osm_id]

    def get_canvas(self, osm_id: int, facade: str) -> Optional[FacadeCanvas]:
        row = self._one("canvases", "key", self._canvas_key(osm_id, facade))
        return FacadeCanvas.model_validate_json(row) if row else None

    # -- building facts (#299; filterable by neighborhood/type) -------------

    def upsert_building(self, b: BuildingFacts) -> None:
        payload = b.model_dump_json(by_alias=True)
        with self._lock:
            self._conn.execute(
                "INSERT INTO buildings (osm_id, neighborhood, building_type, json) "
                "VALUES (?, ?, ?, ?) ON CONFLICT(osm_id) DO UPDATE SET "
                "neighborhood = excluded.neighborhood, "
                "building_type = excluded.building_type, json = excluded.json",
                (b.osm_id, b.neighborhood, b.building_type, payload),
            )
            self._conn.commit()

    def get_building(self, osm_id: int) -> Optional[BuildingFacts]:
        row = self._one("buildings", "osm_id", osm_id)
        return BuildingFacts.model_validate_json(row) if row else None

    def list_buildings(self, neighborhood: Optional[str] = None,
                       building_type: Optional[str] = None,
                       limit: int = 100, offset: int = 0) -> tuple[List[BuildingFacts], int]:
        """Return (page, total) — total is the full filtered count, page honors
        limit/offset. Ordered by osm_id for stable pagination across requests."""
        where, params = [], []
        if neighborhood:
            where.append("neighborhood = ?")
            params.append(neighborhood)
        if building_type:
            where.append("building_type = ?")
            params.append(building_type)
        clause = f" WHERE {' AND '.join(where)}" if where else ""
        with self._lock:
            total = self._conn.execute(
                f"SELECT COUNT(*) FROM buildings{clause}", params
            ).fetchone()[0]
            cur = self._conn.execute(
                f"SELECT json FROM buildings{clause} ORDER BY osm_id LIMIT ? OFFSET ?",
                (*params, limit, offset),
            )
            page = [BuildingFacts.model_validate_json(r[0]) for r in cur.fetchall()]
        return page, total

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
