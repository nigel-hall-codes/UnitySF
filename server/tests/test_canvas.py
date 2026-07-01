"""Facade canvas: CRUD, stroke flatten, and facadeDecals export (#281, design #278)."""
import json
import struct

from app.canvas import encode_rgba_png, flatten_paint, rasterize_strokes
from app.models import Stroke

_PNG_SIG = b"\x89PNG\r\n\x1a\n"


def _canvas(osm_id=65307880, facade="Front"):
    return {
        "osm_id": osm_id, "facade": facade, "footprint_hash": "a3f1c9d2",
        "layers": [
            {"kind": "paint", "layer": 0, "mountDepth_m": 0.02,
             "strokes": [{"points": [[0.1, 0.1], [0.9, 0.9]], "color": "#ff0000", "width": 0.05}]},
            {"kind": "image", "layer": 1, "mountDepth_m": 0.03,
             "rect": [0.4, 0.6, 0.6, 0.82], "texture": "Signs/sign_lucca_deli.png",
             "signAsset": "sign_lucca_deli"},
        ],
        "version": 1,
    }


# --- the RGBA PNG encoder + rasteriser -------------------------------------

def test_encode_rgba_png_valid_and_sized():
    px = bytes([10, 20, 30, 255]) * (4 * 3)
    png = encode_rgba_png(4, 3, px)
    assert png.startswith(_PNG_SIG)
    w, h, bitdepth, ctype = struct.unpack(">IIBB", png[16:26])
    assert (w, h, bitdepth, ctype) == (4, 3, 8, 6)   # 8-bit RGBA


def test_rasterize_strokes_paints_pixels():
    png = rasterize_strokes([Stroke(points=[[0.0, 0.5], [1.0, 0.5]], color="#00ff00", width=0.1)], size=32)
    assert png.startswith(_PNG_SIG)
    # A horizontal stroke across the middle must leave some opaque green pixels.
    assert len(png) > len(encode_rgba_png(32, 32, bytes(32 * 32 * 4)))  # bigger than fully-transparent


def test_flatten_paint_none_when_no_strokes():
    assert flatten_paint([]) is None


# --- CRUD ------------------------------------------------------------------

def test_canvas_crud_roundtrip(client):
    assert client.post("/canvas", json=_canvas()).status_code == 200
    one = client.get("/canvas/65307880/Front").json()
    assert one["footprint_hash"] == "a3f1c9d2" and len(one["layers"]) == 2
    assert [c["facade"] for c in client.get("/canvas/65307880").json()] == ["Front"]
    assert client.get("/canvas/65307880/Back").status_code == 404


# --- backdrop upload/serve (#317) ------------------------------------------

def test_backdrop_put_get_roundtrip_png(client):
    png = encode_rgba_png(2, 2, bytes([1, 2, 3, 255]) * 4)
    put = client.put("/canvas/65307880/Front/backdrop", content=png)
    assert put.status_code == 200 and put.json()["bytes"] == len(png)
    got = client.get("/canvas/65307880/Front/backdrop")
    assert got.status_code == 200
    assert got.headers["content-type"] == "image/png"
    assert got.content == png


def test_backdrop_get_404_when_absent(client):
    assert client.get("/canvas/65307880/Back/backdrop").status_code == 404


def test_backdrop_put_replaces_and_sniffs_jpeg(client):
    jpeg = b"\xff\xd8\xff\xe0" + b"\x00" * 16          # JPEG magic, arbitrary tail
    client.put("/canvas/1/Right/backdrop", content=b"\x89PNG\r\n\x1a\n old")
    client.put("/canvas/1/Right/backdrop", content=jpeg)   # overwrite
    got = client.get("/canvas/1/Right/backdrop")
    assert got.content == jpeg and got.headers["content-type"] == "image/jpeg"


def test_backdrop_put_empty_body_rejected(client):
    assert client.put("/canvas/1/Front/backdrop", content=b"").status_code == 400


# --- export → facadeDecals --------------------------------------------------

def test_export_writes_paint_png_and_facade_decals(client, tmp_path):
    client.post("/canvas", json=_canvas())
    out = tmp_path / "drop"
    res = client.post("/export/unity", json={"outDir": str(out)}).json()
    assert res["paintTextures"] == 1 and res["facadeDecals"] == 2

    # Flattened paint PNG written under Signs/.
    paint = out / "Signs" / "paint_65307880_front.png"
    assert paint.read_bytes().startswith(_PNG_SIG)

    # Override file carries facadeDecals[] (paint layer 0 + discrete sign layer 1) + the hash.
    ov = json.loads((out / "Overrides" / "65307880.override.json").read_text(encoding="utf-8"))
    assert ov["version"] == 2 and ov["footprint_hash"] == "a3f1c9d2"
    decals = ov["facadeDecals"]
    assert [d["layer"] for d in decals] == [0, 1]                  # sorted by layer
    assert decals[0]["texture"] == "Signs/paint_65307880_front.png"
    assert decals[0]["rect"] == [0.0, 0.0, 1.0, 1.0]
    assert decals[1]["texture"] == "Signs/sign_lucca_deli.png"
    assert decals[1]["signAsset"] == "sign_lucca_deli"


def test_export_merges_facade_decals_into_existing_override(client, tmp_path):
    # A building-specific override authored separately must keep its placements AND gain decals.
    client.post("/building-specific", json={
        "osm_id": 65307880, "footprint_hash": "a3f1c9d2",
        "placements": [{"part": "door_x", "facade": "Front", "y": 0.0}]})
    client.post("/canvas", json=_canvas())
    out = tmp_path / "drop2"
    client.post("/export/unity", json={"outDir": str(out)})
    ov = json.loads((out / "Overrides" / "65307880.override.json").read_text(encoding="utf-8"))
    assert len(ov["placements"]) == 1 and ov["placements"][0]["part"] == "door_x"   # preserved
    assert "facadeDecals" in ov and ov["version"] == 2                               # merged
