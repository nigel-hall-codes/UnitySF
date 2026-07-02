"""PUT/GET /parts/{id}/thumb (#344) — same Unity-push model as building thumbnails (#318)."""
from test_api import _part

_PNG = b"\x89PNG\r\n\x1a\n" + b"png-body"
_JPEG = b"\xff\xd8\xff" + b"jpeg-body"


def test_part_thumb_upload_requires_existing_part(client):
    assert client.put("/parts/ghost/thumb", content=_PNG).status_code == 404


def test_part_thumb_upload_rejects_non_image(client):
    client.post("/parts", json=_part())
    r = client.put("/parts/window_sunset_2x3/thumb", content=b"not-an-image")
    assert r.status_code == 415


def test_get_part_thumb_404_when_none(client):
    client.post("/parts", json=_part())
    assert client.get("/parts/window_sunset_2x3/thumb").status_code == 404


def test_part_thumb_upload_and_download_png(client):
    client.post("/parts", json=_part())
    r = client.put("/parts/window_sunset_2x3/thumb", content=_PNG)
    assert r.status_code == 200 and r.json() == {"part": "window_sunset_2x3", "bytes": len(_PNG)}

    got = client.get("/parts/window_sunset_2x3/thumb")
    assert got.status_code == 200 and got.content == _PNG
    assert got.headers["content-type"] == "image/png"


def test_part_thumb_download_reports_jpeg_content_type(client):
    client.post("/parts", json=_part())
    client.put("/parts/window_sunset_2x3/thumb", content=_JPEG)
    got = client.get("/parts/window_sunset_2x3/thumb")
    assert got.content == _JPEG and got.headers["content-type"] == "image/jpeg"


def test_part_thumb_reupload_replaces(client):
    client.post("/parts", json=_part())
    client.put("/parts/window_sunset_2x3/thumb", content=_PNG)
    client.put("/parts/window_sunset_2x3/thumb", content=_JPEG)
    got = client.get("/parts/window_sunset_2x3/thumb")
    assert got.content == _JPEG and got.headers["content-type"] == "image/jpeg"


def test_part_thumb_independent_per_part(client):
    client.post("/parts", json=_part("window_a"))
    client.post("/parts", json=_part("window_b"))
    client.put("/parts/window_a/thumb", content=_PNG)
    assert client.get("/parts/window_a/thumb").status_code == 200
    assert client.get("/parts/window_b/thumb").status_code == 404
