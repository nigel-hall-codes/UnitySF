"""POST /export/unity must write the exact SFBuildingTemplates/ shape the Unity importer
(#269) consumes — including the array-of-pairs form for the §2 map fields."""
import json

import pytest

from app.export import _filter_by_scope, _neighborhood_template_weights
from app.models import DistrictDef, TemplateWeight
from test_api import _palette, _part, _template


def _seed(client):
    client.post("/parts", json=_part())
    client.put("/parts/window_sunset_2x3/glb",
               files={"file": ("g.glb", b"glTF-bytes", "model/gltf-binary")})
    client.post("/templates", json=_template())
    client.post("/palettes", json=_palette())


def test_export_writes_full_library(client, tmp_path):
    _seed(client)
    out = tmp_path / "drop"
    r = client.post("/export/unity", json={"outDir": str(out)})
    assert r.status_code == 200
    summary = r.json()
    assert (summary["parts"], summary["templates"], summary["palettes"], summary["glbsCopied"]) == (1, 1, 1, 1)

    # library.json manifest
    manifest = json.loads((out / "library.json").read_text(encoding="utf-8"))
    assert manifest["version"] == 1 and manifest["neighborhoods"] == ["Sunset"]

    # Part JSON: array-of-pairs roleSubmeshes + the GLB copied to its declared path.
    part = json.loads((out / "Parts" / "window_sunset_2x3.part.json").read_text(encoding="utf-8"))
    assert part["category"] == "Window"
    assert part["roleSubmeshes"] == [{"submesh": 0, "role": "Base"}, {"submesh": 1, "role": "Glass"}]
    assert part["mountDepth_m"] == -0.08
    assert (out / "Parts" / "window_sunset_2x3.glb").read_bytes() == b"glTF-bytes"

    # Template JSON: exact[].roles serialised with the "from" key (Unity importer's shape).
    tpl = json.loads((out / "Templates" / "trivial_window.template.json").read_text(encoding="utf-8"))
    assert tpl["exact"][0]["roles"][0] == {"from": "Base", "to": "Base"}
    assert tpl["exact"][0]["facade"] == "Front"

    # Palette JSON.
    pal = json.loads((out / "Palettes" / "Sunset.palette.json").read_text(encoding="utf-8"))
    assert pal["roles"][0]["mode"] == "pick" and pal["roles"][0]["colors"] == ["#E9DFC6"]


def test_export_includes_overrides(client, tmp_path):
    client.post("/building-specific", json={"osm_id": 65307880, "footprint_hash": "a3f1c9d2"})
    out = tmp_path / "drop2"
    client.post("/export/unity", json={"outDir": str(out)})
    ov = json.loads((out / "Overrides" / "65307880.override.json").read_text(encoding="utf-8"))
    assert ov["osm_id"] == 65307880 and ov["footprint_hash"] == "a3f1c9d2"


def test_export_syncs_glb_path_when_part_declared_none(client, tmp_path):
    # Part authored without a `glb` field, then a binary uploaded → export must point the
    # part's `glb` at where it copied the binary (else the importer can't find the mesh).
    p = {"id": "doorless", "category": "Door"}   # no glb field
    client.post("/parts", json=p)
    client.put("/parts/doorless/glb", files={"file": ("d.glb", b"GLB", "model/gltf-binary")})
    out = tmp_path / "drop3"
    assert client.post("/export/unity", json={"outDir": str(out)}).json()["glbsCopied"] == 1
    part = json.loads((out / "Parts" / "doorless.part.json").read_text(encoding="utf-8"))
    assert part["glb"] == "Parts/doorless.glb"
    assert (out / "Parts" / "doorless.glb").read_bytes() == b"GLB"


def test_export_default_dir_used_when_omitted(client, tmp_path):
    # create_app was given default_export_dir = tmp_path/"export"; an empty outDir uses it.
    client.post("/palettes", json=_palette("Mission"))
    r = client.post("/export/unity", json={})
    assert r.status_code == 200
    assert (tmp_path / "export" / "Palettes" / "Mission.palette.json").exists()


def test_export_compiles_zones_into_rules_and_strips_zones_key(client, tmp_path):
    # Zones are the authoring format (#326 D1); Unity must only ever see the
    # compiled rules[], never zones[] — and existing exact[] must survive untouched.
    tpl = _template()
    tpl["zones"] = [{
        "id": "z1", "type": "Window", "facade": "Front",
        "shape": {"kind": "rect", "points": [[0.2, 0.0], [0.8, 0.0], [0.8, 1.0], [0.2, 1.0]]},
        "floorRange": {"min": 1, "max": 2},
        "rules": {
            "allowedParts": [{"part": "window_sunset_2x3", "weight": 1.0}],
            "countRange": {"min": 2, "max": 2},
        },
    }]
    client.post("/parts", json=_part())
    client.post("/templates", json=tpl)

    out = tmp_path / "drop_zones"
    r = client.post("/export/unity", json={"outDir": str(out)})
    assert r.status_code == 200

    written = json.loads((out / "Templates" / f"{tpl['id']}.template.json").read_text(encoding="utf-8"))
    assert "zones" not in written
    assert written["exact"][0]["part"] == tpl["exact"][0]["part"]  # form-authored exact[] untouched
    assert len(written["rules"]) == 1
    compiled_rule = written["rules"][0]
    assert compiled_rule["facade"] == "Front"
    assert compiled_rule["span"] == [0.2, 0.8]
    assert compiled_rule["floorRange"] == {"min": 1, "max": 2}
    assert compiled_rule["variants"] == ["window_sunset_2x3"]


def test_neighborhood_template_weights_flattens_by_neighborhood():
    districts = [
        DistrictDef(id="mission", neighborhoods=["Mission"], templateWeights=[
            TemplateWeight(template="victorian_a", weight=50), TemplateWeight(template="victorian_b", weight=50),
        ]),
        DistrictDef(id="sunset", neighborhoods=["Sunset", "Parkside"], templateWeights=[
            TemplateWeight(template="stucco_a", weight=1),
        ]),
    ]
    out = {row["neighborhood"]: row["weights"] for row in _neighborhood_template_weights(districts)}
    assert set(out.keys()) == {"Mission", "Sunset", "Parkside"}
    assert out["Mission"] == [{"template": "victorian_a", "weight": 50}, {"template": "victorian_b", "weight": 50}]
    assert out["Sunset"] == out["Parkside"] == [{"template": "stucco_a", "weight": 1}]


def test_neighborhood_template_weights_later_district_wins_on_overlap():
    districts = [
        DistrictDef(id="a", neighborhoods=["Mission"], templateWeights=[TemplateWeight(template="t1", weight=10)]),
        DistrictDef(id="b", neighborhoods=["Mission"], templateWeights=[TemplateWeight(template="t1", weight=90)]),
    ]
    out = _neighborhood_template_weights(districts)
    assert out == [{"neighborhood": "Mission", "weights": [{"template": "t1", "weight": 90}]}]


def test_neighborhood_template_weights_empty_when_no_districts():
    assert _neighborhood_template_weights([]) == []


def test_export_manifest_includes_district_template_weights(client, tmp_path):
    _seed(client)
    client.post("/districts", json={
        "id": "sunset-district", "name": "Sunset", "neighborhoods": ["Sunset"],
        "templateWeights": [{"template": "trivial_window", "weight": 3}],
        "palette": "Sunset", "signStyle": "Modern",
    })
    out = tmp_path / "drop_districts"
    r = client.post("/export/unity", json={"outDir": str(out)})
    assert r.status_code == 200

    manifest = json.loads((out / "library.json").read_text(encoding="utf-8"))
    assert manifest["districtTemplateWeights"] == [
        {"neighborhood": "Sunset", "weights": [{"template": "trivial_window", "weight": 3}]},
    ]


def test_export_manifest_district_weights_empty_when_no_districts(client, tmp_path):
    _seed(client)
    out = tmp_path / "drop_no_districts"
    client.post("/export/unity", json={"outDir": str(out)})
    manifest = json.loads((out / "library.json").read_text(encoding="utf-8"))
    assert manifest["districtTemplateWeights"] == []


# --- Scoped export (#346, design §3.4 Generation) ---------------------------

def _facts(osm_id, neighborhood):
    return {
        "osm_id": osm_id, "neighborhood": neighborhood, "building_type": "retail",
        "footprint_shape": "corner", "width_m": 11.4, "depth_m": 18.2,
        "floor_count": 4, "footprint_hash": f"hash{osm_id}",
    }


def _seed_two_buildings(client):
    client.post("/buildings/import-sidecar", json={"version": 2, "buildings": [
        _facts(1001, "Mission"), _facts(2002, "Sunset"),
    ]})
    client.post("/building-specific", json={"osm_id": 1001, "footprint_hash": "hash1001"})
    client.post("/building-specific", json={"osm_id": 2002, "footprint_hash": "hash2002"})


def test_export_default_scope_is_city_and_includes_everything(client, tmp_path):
    _seed(client)
    _seed_two_buildings(client)
    out = tmp_path / "drop_city"
    r = client.post("/export/unity", json={"outDir": str(out)})
    assert r.status_code == 200
    result = r.json()
    assert result["scope"] == "city" and result["overrides"] == 2
    assert (out / "Overrides" / "1001.override.json").exists()
    assert (out / "Overrides" / "2002.override.json").exists()


def test_export_scope_building_includes_only_that_building(client, tmp_path):
    _seed(client)
    _seed_two_buildings(client)
    out = tmp_path / "drop_building"
    r = client.post("/export/unity", json={"outDir": str(out), "scope": "building", "osm_id": 1001})
    assert r.status_code == 200
    result = r.json()
    assert result["scope"] == "building" and result["overrides"] == 1
    assert (out / "Overrides" / "1001.override.json").exists()
    assert not (out / "Overrides" / "2002.override.json").exists()
    # Parts/templates/palettes stay full even for a building-scoped export.
    assert result["parts"] == 1 and result["templates"] == 1


def test_export_scope_neighborhood_includes_only_matching_buildings(client, tmp_path):
    _seed(client)
    _seed_two_buildings(client)
    out = tmp_path / "drop_neighborhood"
    r = client.post("/export/unity", json={"outDir": str(out), "scope": "neighborhood", "neighborhood": "Sunset"})
    assert r.status_code == 200
    result = r.json()
    assert result["scope"] == "neighborhood" and result["overrides"] == 1
    assert (out / "Overrides" / "2002.override.json").exists()
    assert not (out / "Overrides" / "1001.override.json").exists()


def test_filter_by_scope_neighborhood_without_neighborhood_raises(store):
    # Defense in depth: main.py's endpoint already 400s before this is ever reachable via HTTP,
    # but the helper itself must not silently fall through to an unscoped export for a second caller.
    with pytest.raises(ValueError):
        _filter_by_scope(store, [1001, 2002], "neighborhood", None, "")


def test_export_scope_building_without_osm_id_400s(client, tmp_path):
    r = client.post("/export/unity", json={"outDir": str(tmp_path / "x"), "scope": "building"})
    assert r.status_code == 400


def test_export_scope_neighborhood_without_neighborhood_400s(client, tmp_path):
    r = client.post("/export/unity", json={"outDir": str(tmp_path / "x"), "scope": "neighborhood"})
    assert r.status_code == 400


def test_export_scope_building_filters_canvas_decals_too(client, tmp_path):
    # A building with only a facade canvas (no building-specific override authored directly)
    # must still be scoped correctly — the override is synthesized from the canvas.
    client.post("/buildings/import-sidecar", json={"version": 2, "buildings": [
        _facts(3003, "Mission"), _facts(4004, "Mission"),
    ]})
    for osm_id in (3003, 4004):
        client.post("/canvas", json={
            "osm_id": osm_id, "facade": "Front", "footprint_hash": f"hash{osm_id}",
            "layers": [{"kind": "paint", "layer": 0, "mountDepth_m": 0.02,
                        "strokes": [{"points": [[0.1, 0.1], [0.9, 0.9]], "color": "#ff0000", "width": 0.02}]}],
        })
    out = tmp_path / "drop_canvas_scope"
    r = client.post("/export/unity", json={"outDir": str(out), "scope": "building", "osm_id": 3003})
    assert r.status_code == 200
    assert (out / "Overrides" / "3003.override.json").exists()
    assert not (out / "Overrides" / "4004.override.json").exists()
