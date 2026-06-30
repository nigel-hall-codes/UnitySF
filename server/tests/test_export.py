"""POST /export/unity must write the exact SFBuildingTemplates/ shape the Unity importer
(#269) consumes — including the array-of-pairs form for the §2 map fields."""
import json

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


def test_export_default_dir_used_when_omitted(client, tmp_path):
    # create_app was given default_export_dir = tmp_path/"export"; an empty outDir uses it.
    client.post("/palettes", json=_palette("Mission"))
    r = client.post("/export/unity", json={})
    assert r.status_code == 200
    assert (tmp_path / "export" / "Palettes" / "Mission.palette.json").exists()
