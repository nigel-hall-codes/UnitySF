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

from fastapi import FastAPI, HTTPException, Query, Request, UploadFile
from fastapi.responses import FileResponse

from .ai_signs import DEFAULT_PROVIDER, available_providers, get_provider, slug
from .export import export_unity
from .models import (
    BuildingFacts,
    BuildingPage,
    BuildingSpecificDef,
    DistrictDef,
    ExportRequest,
    ExportResult,
    FacadeCanvas,
    PaletteDef,
    PartDef,
    ResolvedFacade,
    ResolveRequest,
    SidecarDoc,
    SignDef,
    SignRequest,
    TemplateDef,
)
from .resolve import resolve_template
from .store import Store

# Classification vocabulary (data-model.md §1 building_type is the OSM building=* tag).
# A representative set the authoring UI can offer; not exhaustive — building_type is a
# passthrough string, so unknown values still flow through.
BUILDING_TYPES: List[str] = [
    "", "yes", "residential", "house", "detached", "apartments", "terrace",
    "commercial", "retail", "office", "industrial", "warehouse", "mixed_use",
    "church", "school", "hospital", "hotel", "civic", "garage", "roof",
]


def _image_media_type(head: bytes) -> Optional[str]:
    """Sniff a PNG/JPEG signature from the first bytes, or None if neither. Used to
    reject non-image thumbnail uploads and to serve stored thumbs with the right
    Content-Type (the on-disk name is always thumb.jpg regardless of encoding)."""
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if head.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    return None


def create_app(store: Optional[Store] = None, default_export_dir: str = "",
               sign_provider: str = "") -> FastAPI:
    """Build the app. Tests pass an explicit ``store``; the module-level app passes
    None and a startup handler builds the env-configured store then — so merely
    importing this module never touches the filesystem. ``sign_provider`` names the
    default AI-sign backend (overridable per request)."""
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
    app.state.sign_provider = sign_provider or DEFAULT_PROVIDER

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

    @app.get("/parts/{part_id}/glb")
    def get_part_glb(part_id: str) -> FileResponse:
        if S().get_part(part_id) is None:
            raise HTTPException(status_code=404, detail=f"unknown part '{part_id}'")
        path = S().glb_path(part_id)
        if path is None:
            raise HTTPException(status_code=404, detail=f"no GLB uploaded for part '{part_id}'")
        return FileResponse(str(path), media_type="model/gltf-binary",
                            filename=f"{part_id}.glb")

    # -- templates ----------------------------------------------------------

    @app.get("/templates")
    def list_templates() -> List[TemplateDef]:
        return S().list_templates()

    @app.post("/templates")
    def create_template(tpl: TemplateDef) -> TemplateDef:
        S().upsert_template(tpl)
        return tpl

    # POST /templates/{id}/resolve (#326 D2): resolve a template against real (osm_id)
    # or synthetic (facts) building facts + a seed into a flat placement list. Powers
    # the iPad's variant preview and district preview without a Unity round-trip.
    @app.post("/templates/{template_id}/resolve")
    def resolve(template_id: str, req: ResolveRequest) -> ResolvedFacade:
        tpl = S().get_template(template_id)
        if tpl is None:
            raise HTTPException(status_code=404, detail=f"unknown template '{template_id}'")

        if req.facts is not None:
            facts = req.facts
        elif req.osm_id is not None:
            facts = S().get_building(req.osm_id)
            if facts is None:
                raise HTTPException(status_code=404, detail=f"unknown building '{req.osm_id}'")
        else:
            raise HTTPException(status_code=400, detail="resolve requires osm_id or facts")

        parts_by_id = {p.id: p for p in S().list_parts()}
        placements = resolve_template(tpl, facts, req.seed, parts_by_id)
        palette = next((p for p in S().list_palettes() if p.neighborhood == facts.neighborhood), None)
        return ResolvedFacade(placements=placements, paletteRoles=palette.roles if palette else [])

    # -- palettes -----------------------------------------------------------

    @app.get("/palettes")
    def list_palettes() -> List[PaletteDef]:
        return S().list_palettes()

    @app.post("/palettes")
    def create_palette(pal: PaletteDef) -> PaletteDef:
        S().upsert_palette(pal)
        return pal

    # -- districts (#326 D4; config layer over the existing neighborhood key) -

    @app.get("/districts")
    def list_districts() -> List[DistrictDef]:
        return S().list_districts()

    @app.post("/districts")
    def create_district(district: DistrictDef) -> DistrictDef:
        S().upsert_district(district)
        return district

    # -- building-specific overrides ---------------------------------------

    @app.post("/building-specific")
    def create_override(ov: BuildingSpecificDef) -> BuildingSpecificDef:
        S().upsert_override(ov)
        return ov

    # -- building facts (#299; ingested from the bake sidecar, browsed by the iPad) -

    @app.post("/buildings/import-sidecar")
    def import_sidecar(doc: SidecarDoc) -> dict:
        # Fail loud on a schema drift rather than silently coercing an off-version
        # sidecar into the v2 BuildingFacts shape (missing fields would default,
        # extra fields would be dropped). The bake emits version 2 (serialize.py).
        if doc.version != 2:
            raise HTTPException(status_code=400,
                                detail=f"unsupported sidecar version {doc.version} (expected 2)")
        # Upsert each building by osm_id — a re-import of the same chunk updates in
        # place (the bake is deterministic, so re-baking must not duplicate).
        for b in doc.buildings:
            S().upsert_building(b)
        return {"imported": len(doc.buildings)}

    @app.get("/buildings")
    def list_buildings(
        neighborhood: str = "",
        type: str = "",
        limit: int = Query(100, ge=1, le=1000),
        offset: int = Query(0, ge=0),
    ) -> BuildingPage:
        page, total = S().list_buildings(neighborhood or None, type or None, limit, offset)
        return BuildingPage(buildings=page, total=total, limit=limit, offset=offset)

    @app.get("/buildings/{osm_id}")
    def get_building(osm_id: int) -> BuildingFacts:
        b = S().get_building(osm_id)
        if b is None:
            raise HTTPException(status_code=404, detail=f"unknown building '{osm_id}'")
        return b

    # -- building thumbnails (#318; Unity uploads a rendered 3D preview post-import) -

    @app.put("/buildings/{osm_id}/thumb")
    async def upload_thumb(osm_id: int, request: Request) -> dict:
        # Require the building to exist first (same guard as GLB part upload) — a
        # thumb for an unknown osm_id would be an orphan the browser never lists.
        if S().get_building(osm_id) is None:
            raise HTTPException(status_code=404, detail=f"unknown building '{osm_id}'")
        data = await request.body()
        if _image_media_type(data) is None:
            raise HTTPException(status_code=415, detail="thumbnail body must be PNG or JPEG")
        S().save_thumb(osm_id, data)
        return {"building": osm_id, "bytes": len(data)}

    @app.get("/buildings/{osm_id}/thumb")
    def get_thumb(osm_id: int) -> FileResponse:
        path = S().thumb_path(osm_id)
        if path is None:
            raise HTTPException(status_code=404, detail=f"no thumbnail for building '{osm_id}'")
        media_type = _image_media_type(path.read_bytes()[:8]) or "image/jpeg"
        return FileResponse(str(path), media_type=media_type)

    # -- facade canvases (#278/#281; layered doc stays server-side, flattened on export) -

    @app.post("/canvas")
    def save_canvas(canvas: FacadeCanvas) -> FacadeCanvas:
        S().upsert_canvas(canvas)
        return canvas

    @app.get("/canvas/{osm_id}")
    def list_building_canvases(osm_id: int) -> List[FacadeCanvas]:
        return S().list_canvases_for(osm_id)

    @app.get("/canvas/{osm_id}/{facade}")
    def get_canvas(osm_id: int, facade: str) -> FacadeCanvas:
        canvas = S().get_canvas(osm_id, facade)
        if canvas is None:
            raise HTTPException(status_code=404, detail=f"no canvas for {osm_id}/{facade}")
        return canvas

    # Facade backdrop (#317): a reference render the iPad traces over. It's a drawing
    # aid only — stored on disk, never persisted into the canvas doc or exported to Unity.
    @app.put("/canvas/{osm_id}/{facade}/backdrop")
    async def upload_backdrop(osm_id: int, facade: str, request: Request) -> dict:
        data = await request.body()
        if not data:
            raise HTTPException(status_code=400, detail="empty backdrop body")
        S().save_backdrop(osm_id, facade, data)
        return {"osm_id": osm_id, "facade": facade, "bytes": len(data)}

    @app.get("/canvas/{osm_id}/{facade}/backdrop")
    def get_backdrop(osm_id: int, facade: str) -> FileResponse:
        path = S().backdrop_path(osm_id, facade)
        if path is None:
            raise HTTPException(status_code=404, detail=f"no backdrop for {osm_id}/{facade}")
        # Sniff PNG vs JPEG from the magic bytes so the stored image serves with its true type.
        head = path.read_bytes()[:8]
        media = "image/png" if head.startswith(b"\x89PNG\r\n\x1a\n") else "image/jpeg"
        return FileResponse(str(path), media_type=media)

    @app.get("/signs/{sign_id}/png")
    def get_sign_png(sign_id: str) -> FileResponse:
        if S().get_sign(sign_id) is None:
            raise HTTPException(status_code=404, detail=f"unknown sign '{sign_id}'")
        path = S().sign_png_path(sign_id)
        if path is None:
            raise HTTPException(status_code=404, detail=f"no PNG for sign '{sign_id}'")
        return FileResponse(str(path), media_type="image/png")

    @app.get("/signs/{sign_id}/thumb")
    def get_sign_thumb(sign_id: str) -> FileResponse:
        if S().get_sign(sign_id) is None:
            raise HTTPException(status_code=404, detail=f"unknown sign '{sign_id}'")
        path = S().sign_thumb_path(sign_id)
        if path is None:
            raise HTTPException(status_code=404, detail=f"no thumbnail for sign '{sign_id}'")
        return FileResponse(str(path), media_type="image/png")

    # -- AI signs (server-mediated; the iPad never calls a provider directly) -

    @app.get("/signs")
    def list_signs() -> List[SignDef]:
        return S().list_signs()

    @app.get("/ai/signs/providers")
    def sign_providers() -> dict:
        return {"default": app.state.sign_provider, "available": available_providers()}

    @app.post("/ai/signs/generate")
    def generate_sign(req: SignRequest) -> SignDef:
        name = req.provider or app.state.sign_provider
        try:
            provider = get_provider(name)
        except KeyError:
            raise HTTPException(status_code=400, detail=f"unknown sign provider '{name}'")
        png, thumb = provider.generate(req)
        # signId is derived from text + businessType ONLY (data-model §5: "LUCCA"+"deli" →
        # "sign_lucca_deli"). Style/neighborhood/aspect are intentionally excluded so a
        # business's sign is one reusable canonical asset; re-generating updates it in place.
        sign_id = f"sign_{slug(req.text)}_{slug(req.businessType)}"
        S().save_sign_png(sign_id, png, thumb)
        sign = SignDef(
            signId=sign_id,
            png=f"Signs/{sign_id}.png",
            thumb=f"Signs/{sign_id}.thumb.png",
            provider=provider.name,
            version=1,
            businessType=req.businessType,
            neighborhood=req.neighborhood,
            text=req.text,
            aspectRatio=req.aspectRatio,
            stylePreset=req.stylePreset,
        )
        S().upsert_sign(sign)
        return sign

    # -- export -------------------------------------------------------------

    @app.post("/export/unity")
    def export(req: ExportRequest) -> ExportResult:
        out_dir = req.outDir or app.state.default_export_dir
        if not out_dir:
            raise HTTPException(status_code=400, detail="no outDir given and no default configured")
        if req.scope == "building" and req.osm_id is None:
            raise HTTPException(status_code=400, detail="scope 'building' requires osm_id")
        if req.scope == "neighborhood" and not req.neighborhood:
            raise HTTPException(status_code=400, detail="scope 'neighborhood' requires neighborhood")
        return export_unity(S(), out_dir, scope=req.scope, osm_id=req.osm_id, neighborhood=req.neighborhood)

    return app


def _build_default_store() -> Store:
    db = os.environ.get("SFSERVER_DB", "sfserver.db")
    assets = os.environ.get("SFSERVER_ASSETS", "assets")
    return Store(db, assets)


# Module-level app for `uvicorn app.main:app`.
app = create_app(default_export_dir=os.environ.get("SFSERVER_EXPORT_DIR", ""))
