"""AI sign generation: provider abstraction, store, swappability, export (data-model §5)."""
import json
import struct

from app.ai_signs import (
    LocalStubProvider,
    SignProvider,
    _aspect_to_size,
    encode_solid_png,
    register_provider,
    slug,
)
from app.main import create_app
from app.models import SignRequest

_PNG_SIG = b"\x89PNG\r\n\x1a\n"


def _req(text="LUCCA", business="deli", aspect="3:1"):
    return {"businessType": business, "neighborhood": "Mission", "text": text,
            "aspectRatio": aspect, "stylePreset": "vintage_handpainted"}


# --- the dependency-free PNG encoder ---------------------------------------

def test_encode_solid_png_is_valid_and_sized():
    png = encode_solid_png(12, 4, (10, 20, 30))
    assert png.startswith(_PNG_SIG)
    # IHDR width/height live at bytes 16..24 (after sig[8] + len[4] + "IHDR"[4]).
    w, h = struct.unpack(">II", png[16:24])
    assert (w, h) == (12, 4)


def test_aspect_to_size():
    assert _aspect_to_size("3:1", base_h=64) == (192, 64)
    assert _aspect_to_size("1:1", base_h=64) == (64, 64)
    assert _aspect_to_size("garbage", base_h=64) == (64, 64)   # falls back to square


def test_local_stub_is_deterministic():
    p = LocalStubProvider()
    a = p.generate(SignRequest(**_req()))
    b = p.generate(SignRequest(**_req()))
    assert a == b and a[0].startswith(_PNG_SIG) and a[1].startswith(_PNG_SIG)


# --- endpoint: generate, store, list ---------------------------------------

def test_generate_stores_reusable_png_and_metadata(client, store):
    r = client.post("/ai/signs/generate", json=_req())
    assert r.status_code == 200
    sign = r.json()
    assert sign["signId"] == "sign_lucca_deli"
    assert sign["png"] == "Signs/sign_lucca_deli.png"
    assert sign["thumb"] == "Signs/sign_lucca_deli.thumb.png"
    assert sign["provider"] == "local-stub"
    # PNG bytes persisted in the asset store and reusable.
    png_path = store.sign_png_path("sign_lucca_deli")
    assert png_path is not None and png_path.read_bytes().startswith(_PNG_SIG)
    # Metadata listed back.
    assert [s["signId"] for s in client.get("/signs").json()] == ["sign_lucca_deli"]


def test_provider_is_swappable(store, tmp_path):
    # A custom provider registered by name is selected per-request and recorded.
    class TaggedProvider(SignProvider):
        name = "test-fake"

        def generate(self, req):
            return encode_solid_png(2, 2, (1, 2, 3)), encode_solid_png(1, 1, (1, 2, 3))

    register_provider("test-fake", TaggedProvider)
    app = create_app(store, default_export_dir=str(tmp_path))
    from fastapi.testclient import TestClient
    c = TestClient(app)

    # Per-request override.
    s = c.post("/ai/signs/generate", json={**_req(), "provider": "test-fake"}).json()
    assert s["provider"] == "test-fake"
    # Unknown provider → 400.
    assert c.post("/ai/signs/generate", json={**_req(), "provider": "nope"}).status_code == 400
    # Default + available are reported.
    prov = c.get("/ai/signs/providers").json()
    assert prov["default"] == "local-stub" and "test-fake" in prov["available"]


def test_signs_exported_into_library(client, tmp_path):
    client.post("/ai/signs/generate", json=_req())
    out = tmp_path / "drop"
    res = client.post("/export/unity", json={"outDir": str(out)}).json()
    assert res["signs"] == 1
    assert (out / "Signs" / "sign_lucca_deli.png").read_bytes().startswith(_PNG_SIG)
    assert (out / "Signs" / "sign_lucca_deli.thumb.png").exists()
    meta = json.loads((out / "Signs" / "sign_lucca_deli.sign.json").read_text(encoding="utf-8"))
    assert meta["text"] == "LUCCA" and meta["provider"] == "local-stub"


def test_slug():
    assert slug("LUCCA Deli!") == "lucca_deli"
    assert slug("") == "x"
