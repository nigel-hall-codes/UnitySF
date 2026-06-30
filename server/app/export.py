"""POST /export/unity — materialise the Assets/SFBuildingTemplates/ library drop.

This is the *sole* authoring↔generation seam (design #266 §central tension): everything
the server stores is written here into the on-disk layout the Unity importer (#269)
consumes. The JSON shapes are produced via ``model_dump(by_alias=True)`` so they match the
importer's DTOs exactly (notably ``from``/``to`` role pairs and the array-of-pairs maps).
"""
from __future__ import annotations

import json
import shutil
from datetime import datetime, timezone
from pathlib import Path

from .models import ExportResult
from .store import Store


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
    for d in (parts_dir, palettes_dir, templates_dir, overrides_dir):
        d.mkdir(parents=True, exist_ok=True)

    parts = store.list_parts()
    templates = store.list_templates()
    palettes = store.list_palettes()
    overrides = store.list_overrides()

    glbs_copied = 0
    for p in parts:
        _write_json(parts_dir / f"{p.id}.part.json", p.model_dump(by_alias=True))
        src = store.glb_path(p.id)
        if src is not None:
            # Mirror the GLB to the path the part's `glb` field points at (default Parts/<id>.glb).
            rel = p.glb or f"Parts/{p.id}.glb"
            dst = root / rel
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, dst)
            glbs_copied += 1

    for t in templates:
        _write_json(templates_dir / f"{t.id}.template.json", t.model_dump(by_alias=True))
    for pal in palettes:
        _write_json(palettes_dir / f"{pal.neighborhood}.palette.json", pal.model_dump(by_alias=True))
    for ov in overrides:
        _write_json(overrides_dir / f"{ov.osm_id}.override.json", ov.model_dump(by_alias=True))

    manifest = {
        "version": 1,
        "exportedAt": now_iso or datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "neighborhoods": [pal.neighborhood for pal in palettes],
    }
    _write_json(root / "library.json", manifest)

    return ExportResult(
        outDir=str(root),
        version=1,
        parts=len(parts),
        templates=len(templates),
        palettes=len(palettes),
        overrides=len(overrides),
        glbsCopied=glbs_copied,
    )


def _write_json(path: Path, obj) -> None:
    path.write_text(json.dumps(obj, indent=2, ensure_ascii=False), encoding="utf-8")
