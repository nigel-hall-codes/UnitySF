"""POST /export/unity — materialise the Assets/SFBuildingTemplates/ library drop.

This is the *sole* authoring↔generation seam (design #266 §central tension): everything
the server stores is written here into the on-disk layout the Unity importer (#269)
consumes. The JSON shapes are produced via ``model_dump(by_alias=True)`` so they match the
importer's DTOs exactly (notably ``from``/``to`` role pairs and the array-of-pairs maps).
"""
from __future__ import annotations

import json
import re
import shutil
from datetime import datetime, timezone
from pathlib import Path

from .canvas import flatten_paint
from .models import DistrictDef, ExportResult
from .store import Store
from .zones import compile_zones

# Authored ids / neighborhood names become filenames; neutralise anything that could
# escape the target dir (path separators, "..") so a crafted id can't write elsewhere.
_UNSAFE = re.compile(r"[^A-Za-z0-9_-]")


def _safe(name: str) -> str:
    return _UNSAFE.sub("_", name) or "_"


def _within(root: Path, path: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def _neighborhood_template_weights(districts: list[DistrictDef]) -> list[dict]:
    """Flatten district.templateWeights[] out to per-neighborhood weight tables (#343):
    a district covers neighborhoods[], so the manifest keys weights the way the assembler
    can actually look them up (by a building's neighborhood, not its district). If two
    districts cover the same neighborhood, the one later in store order (SQLite rowid —
    original insertion order, NOT most-recently-edited) wins per template id."""
    by_neighborhood: dict[str, dict[str, float]] = {}
    for d in districts:
        for n in d.neighborhoods:
            bucket = by_neighborhood.setdefault(n, {})
            for tw in d.templateWeights:
                bucket[tw.template] = tw.weight
    return [
        {"neighborhood": n, "weights": [{"template": t, "weight": w} for t, w in weights.items()]}
        for n, weights in by_neighborhood.items()
    ]


def export_unity(store: Store, out_dir: str, now_iso: str | None = None) -> ExportResult:
    """Write the full library drop under ``out_dir`` and return a summary.

    Layout (data-model.md §2): ``library.json`` manifest; ``Parts/<id>.part.json`` (+ the
    part's ``<id>.glb`` if a binary was uploaded); ``Palettes/<neighborhood>.palette.json``;
    ``Templates/<id>.template.json``; ``Overrides/<osm_id>.override.json``.
    """
    root = Path(out_dir)
    parts_dir = root / "Parts"
    palettes_dir = root / "Palettes"
    templates_dir = root / "Templates"
    overrides_dir = root / "Overrides"
    signs_dir = root / "Signs"
    for d in (parts_dir, palettes_dir, templates_dir, overrides_dir, signs_dir):
        d.mkdir(parents=True, exist_ok=True)

    parts = store.list_parts()
    templates = store.list_templates()
    palettes = store.list_palettes()
    overrides = store.list_overrides()
    signs = store.list_signs()

    glbs_copied = 0
    for p in parts:
        data = p.model_dump(by_alias=True)
        src = store.glb_path(p.id)
        if src is not None:
            # Copy the uploaded binary and make the part's declared `glb` agree with where it
            # landed (default Parts/<id>.glb), so the importer can always locate the mesh even
            # when the author left `glb` empty. Refuse a path that escapes the drop.
            rel = p.glb or f"Parts/{_safe(p.id)}.glb"
            dst = root / rel
            if _within(root, dst):
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(src, dst)
                data["glb"] = rel
                glbs_copied += 1
            else:
                data["glb"] = ""
        _write_json(parts_dir / f"{_safe(p.id)}.part.json", data)

    for t in templates:
        data = t.model_dump(by_alias=True)
        # Zones are the authoring format; compile them into rules[] here so the
        # Unity importer/BuildingAssembler never see zones[] (design #326 D1).
        # Compiled rules append to any form-authored rules[] — zones never replace.
        compiled = compile_zones(t.zones)
        if compiled:
            data["rules"] = data["rules"] + [r.model_dump(by_alias=True) for r in compiled]
        data.pop("zones", None)
        _write_json(templates_dir / f"{_safe(t.id)}.template.json", data)
    for pal in palettes:
        _write_json(palettes_dir / f"{_safe(pal.neighborhood)}.palette.json", pal.model_dump(by_alias=True))
    # Building-specific overrides — collected into a map (keyed by osm_id) so the facade-canvas
    # export below can merge facadeDecals[] into the SAME per-building override file. Written
    # after canvases. osm_id is an int, so the filename is safe.
    override_map: dict = {ov.osm_id: ov.model_dump(by_alias=True) for ov in overrides}

    # Signs: the PNG + thumbnail binaries and a <signId>.sign.json record (data-model §2).
    # Consumed by the building-specific / facade-decal path (#273/#278), not #269.
    signs_written = 0
    for s in signs:
        sid = _safe(s.signId)
        png = store.sign_png_path(s.signId)
        thumb = store.sign_thumb_path(s.signId)
        if png is None:
            continue  # metadata with no rendered bytes — skip, nothing to dress with
        shutil.copyfile(png, signs_dir / f"{sid}.png")
        if thumb is not None:
            shutil.copyfile(thumb, signs_dir / f"{sid}.thumb.png")
        _write_json(signs_dir / f"{sid}.sign.json", s.model_dump(by_alias=True))
        signs_written += 1

    # Facade canvases (#281, hybrid export #278): flatten each facade's freehand strokes into one
    # paint PNG, and emit facadeDecals[] (the paint layer + any discrete placed images / AI signs)
    # into the building's override file. The full layered document stays server-side; only the
    # flattened-where-cheap form crosses into Unity (#280 consumes facadeDecals).
    canvas_decals = 0
    paint_textures = 0
    canvases_by_building: dict = {}
    for c in store.list_canvases():
        canvases_by_building.setdefault(c.osm_id, []).append(c)

    for osm_id, canvases in canvases_by_building.items():
        decals = []
        fp_hash = ""
        for c in canvases:
            fp_hash = fp_hash or c.footprint_hash
            paint_layers = [layer for layer in c.layers if layer.kind == "paint"]
            strokes = [s for layer in paint_layers for s in layer.strokes]
            png = flatten_paint(strokes)
            if png is not None:
                # _safe(...).lower() is filename-only; the decal's `facade` field keeps the
                # original case (that is what the importer keys placement off, not the filename).
                tex_rel = f"Signs/paint_{osm_id}_{_safe(c.facade).lower()}.png"
                (root / tex_rel).write_bytes(png)
                paint_textures += 1
                decals.append({
                    "facade": c.facade,
                    "rect": [0.0, 0.0, 1.0, 1.0],
                    "layer": min((layer.layer for layer in paint_layers), default=0),
                    "texture": tex_rel,
                    "mountDepth_m": paint_layers[0].mountDepth_m if paint_layers else 0.02,
                })
            # Placed images / AI signs stay discrete decals (reusing existing sign PNGs). An
            # image layer identified only by signAsset resolves to that sign's PNG (#275).
            for layer in c.layers:
                if layer.kind != "image":
                    continue
                tex = layer.texture or (f"Signs/{_safe(layer.signAsset)}.png" if layer.signAsset else "")
                if not tex:
                    continue
                d = {"facade": c.facade, "rect": layer.rect, "layer": layer.layer,
                     "texture": tex, "mountDepth_m": layer.mountDepth_m}
                if layer.signAsset:
                    d["signAsset"] = layer.signAsset
                decals.append(d)

        if not decals:
            continue
        canvas_decals += len(decals)
        ov = override_map.get(osm_id)
        if ov is None:
            ov = {"osm_id": osm_id, "footprint_hash": fp_hash,
                  "placements": [], "suppress": [], "version": 2}
            override_map[osm_id] = ov
        # Keep the override's existing footprint_hash if it has one; else adopt the canvas's.
        if not ov.get("footprint_hash"):
            ov["footprint_hash"] = fp_hash
        # Sort by (layer, mountDepth_m) to match the importer contract (design.md §decal importer).
        ov["facadeDecals"] = sorted(decals, key=lambda d: (d["layer"], d["mountDepth_m"]))
        ov["version"] = 2   # bump: facadeDecals added

    for osm_id, ov in override_map.items():
        _write_json(overrides_dir / f"{osm_id}.override.json", ov)

    manifest = {
        "version": 1,
        "exportedAt": now_iso or datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "neighborhoods": [pal.neighborhood for pal in palettes],
        # District template weights (#326 D4/#343), flattened to per-neighborhood weight
        # tables — the assembler's tie-break only knows a building's neighborhood, not its
        # district, so this is the shape it actually needs at selection time.
        "districtTemplateWeights": _neighborhood_template_weights(store.list_districts()),
    }
    _write_json(root / "library.json", manifest)

    return ExportResult(
        outDir=str(root),
        version=1,
        parts=len(parts),
        templates=len(templates),
        palettes=len(palettes),
        overrides=len(override_map),
        glbsCopied=glbs_copied,
        signs=signs_written,
        facadeDecals=canvas_decals,
        paintTextures=paint_textures,
    )


def _write_json(path: Path, obj) -> None:
    path.write_text(json.dumps(obj, indent=2, ensure_ascii=False), encoding="utf-8")
