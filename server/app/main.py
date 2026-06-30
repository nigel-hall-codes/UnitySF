"""FastAPI app — Home PC Asset Server (design #266 data-model.md §5).

The authoring source of truth. Stores parts/templates/palettes/overrides and, on
``POST /export/unity``, materialises the ``Assets/SFBuildingTemplates/`` drop the Unity
importer (#269) consumes. AI sign generation (``POST /ai/signs/generate``) is deferred to
#275; this server provides the storage + export surface its acceptance needs.

Build a test app with ``create_app(store)``; the module-level ``app`` builds a store from
``SFSERVER_DB`` / ``SFSERVER_ASSETS`` / ``SFSERVER_EXPORT_DIR`` env vars for ``uvicorn``.
"""
from __future__ import annotations

import os
from contextlib import asynccontextmanager
from typing import List, Optional

from fastapi import FastAPI, HTTPException, UploadFile

from .export import export_unity
from .models import (
    BuildingSpecificDef,
    ExportRequest,
    ExportResult,
    PaletteDef,
    PartDef,
    TemplateDef,
)
from .store import Store

# Classification vocabulary (data-model.md §1 building_type is the OSM building=* tag).
# A representative set the authoring UI can offer; not exhaustive — building_type is a
# passthrough string, so unknown values still flow through.
BUILDING_TYPES: List[str] = [
    "", "yes", "residential", "house", "detached", "apartments", "terrace",
    "commercial", "retail", "office", "industrial", "warehouse", "mixed_use",
    "church", "school", "hospital", "hotel", "civic", "garage", "roof",
]


def create_app(store: Optional[Store] = None, default_export_dir: str = "") -> FastAPI:
    """Build the app. Tests pass an explicit ``store``; the module-level app passes
    None and a startup handler builds the env-configured store then — so merely
    importing this module never touches the filesystem."""
    @asynccontextmanager
    async def lifespan(app_: FastAPI):
        # Build the env-configured store on startup when none was injected — so the
        # filesystem is only touched when the server actually runs, never on import.
        if app_.state.store is None:
            app_.state.store = _build_default_store()
        yield

    app = FastAPI(title="SF Building Template Asset Server", version="1.0", lifespan=lifespan)
    app.state.store = store
    app.state.default_export_dir = default_export_dir

    def S() -> Store:
        return app.state.store

    # -- read vocabularies --------------------------------------------------

    @app.get("/building-types")
    def building_types() -> List[str]:
        return BUILDING_TYPES

    @app.get("/neighborhoods")
    def neighborhoods() -> List[dict]:
        # Each authored palette defines a neighborhood; report it + that a palette exists.
        return [{"neighborhood": p.neighborhood, "palette": True} for p in S().list_palettes()]

    # -- parts --------------------------------------------------------------

    @app.get("/parts")
    def list_parts() -> List[PartDef]:
        return S().list_parts()

    @app.post("/parts")
    def create_part(part: PartDef) -> PartDef:
        S().upsert_part(part)
        return part

    @app.put("/parts/{part_id}/glb")
    async def upload_glb(part_id: str, file: UploadFile) -> dict:
        if S().get_part(part_id) is None:
            raise HTTPException(status_code=404, detail=f"unknown part '{part_id}'")
        data = await file.read()
        S().save_glb(part_id, data)
        return {"part": part_id, "bytes": len(data)}

    # -- templates ----------------------------------------------------------

    @app.get("/templates")
    def list_templates() -> List[TemplateDef]:
        return S().list_templates()

    @app.post("/templates")
    def create_template(tpl: TemplateDef) -> TemplateDef:
        S().upsert_template(tpl)
        return tpl

    # -- palettes -----------------------------------------------------------

    @app.get("/palettes")
    def list_palettes() -> List[PaletteDef]:
        return S().list_palettes()

    @app.post("/palettes")
    def create_palette(pal: PaletteDef) -> PaletteDef:
        S().upsert_palette(pal)
        return pal

    # -- building-specific overrides ---------------------------------------

    @app.post("/building-specific")
    def create_override(ov: BuildingSpecificDef) -> BuildingSpecificDef:
        S().upsert_override(ov)
        return ov

    # -- export -------------------------------------------------------------

    @app.post("/export/unity")
    def export(req: ExportRequest) -> ExportResult:
        out_dir = req.outDir or app.state.default_export_dir
        if not out_dir:
            raise HTTPException(status_code=400, detail="no outDir given and no default configured")
        return export_unity(S(), out_dir)

    return app


def _build_default_store() -> Store:
    db = os.environ.get("SFSERVER_DB", "sfserver.db")
    assets = os.environ.get("SFSERVER_ASSETS", "assets")
    return Store(db, assets)


# Module-level app for `uvicorn app.main:app`.
app = create_app(default_export_dir=os.environ.get("SFSERVER_EXPORT_DIR", ""))
