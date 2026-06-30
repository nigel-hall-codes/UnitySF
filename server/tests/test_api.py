"""API round-trips: authored objects can be stored and listed back (data-model §5)."""


def _part(pid="window_sunset_2x3"):
    return {
        "id": pid, "category": "Window", "glb": f"Parts/{pid}.glb",
        "size_m": {"w": 1.2, "h": 1.6, "d": 0.15},
        "roleSubmeshes": [{"submesh": 0, "role": "Base"}, {"submesh": 1, "role": "Glass"}],
        "anchor": "BottomCenter", "mountDepth_m": -0.08, "version": 1,
    }


def _template(tid="trivial_window"):
    return {
        "id": tid, "displayName": "Trivial Window",
        "compatibility": {"width_m": {"min": 0, "max": 1000}, "floor_count": {"min": 1, "max": 100}},
        "exact": [{"part": "window_sunset_2x3", "facade": "Front", "floor": 0, "x": 0.5, "y": 0.5,
                   "roles": [{"from": "Base", "to": "Base"}]}],
        "version": 1,
    }


def _palette(n="Sunset"):
    return {"neighborhood": n, "roles": [{"role": "Base", "colors": ["#E9DFC6"], "mode": "pick"}], "version": 1}


def test_building_types_and_empty_lists(client):
    assert "retail" in client.get("/building-types").json()
    assert client.get("/parts").json() == []
    assert client.get("/neighborhoods").json() == []


def test_part_roundtrip_and_upsert(client):
    assert client.post("/parts", json=_part()).status_code == 200
    parts = client.get("/parts").json()
    assert len(parts) == 1 and parts[0]["id"] == "window_sunset_2x3"
    assert parts[0]["roleSubmeshes"][1] == {"submesh": 1, "role": "Glass"}
    # Re-POST same id updates in place (no duplicate).
    client.post("/parts", json={**_part(), "anchor": "TopLeft"})
    parts = client.get("/parts").json()
    assert len(parts) == 1 and parts[0]["anchor"] == "TopLeft"


def test_template_roundtrip_preserves_role_pair_from_key(client):
    client.post("/templates", json=_template())
    tpl = client.get("/templates").json()[0]
    # The "from" alias must survive storage round-trip (not become "from_").
    assert tpl["exact"][0]["roles"][0] == {"from": "Base", "to": "Base"}


def test_palette_and_neighborhoods(client):
    client.post("/palettes", json=_palette())
    assert client.get("/neighborhoods").json() == [{"neighborhood": "Sunset", "palette": True}]


def test_glb_upload_requires_existing_part(client):
    assert client.put("/parts/ghost/glb", files={"file": ("g.glb", b"\x00", "model/gltf-binary")}).status_code == 404
    client.post("/parts", json=_part())
    r = client.put("/parts/window_sunset_2x3/glb",
                   files={"file": ("g.glb", b"glTF-bytes", "model/gltf-binary")})
    assert r.status_code == 200 and r.json()["bytes"] == len(b"glTF-bytes")


def test_building_specific_override_stored(client):
    ov = {"osm_id": 65307880, "footprint_hash": "a3f1c9d2",
          "placements": [{"part": "sign_lucca", "facade": "Front", "y": 0.85}]}
    assert client.post("/building-specific", json=ov).status_code == 200
