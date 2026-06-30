# Home PC Asset Server (design #266)

The **authoring source of truth** for the SF Building Template pipeline: a FastAPI service
with a SQLite metadata store and an on-disk asset store. It is a **separate codebase** from
the Unity project and the Python bake — its only contract with the generation side is the
library drop written by `POST /export/unity` into `Assets/SFBuildingTemplates/`, which the
Unity importer (#269) consumes. See `sdlc/#266/data-model.md` §5 (API) and §2 (shapes).

## Run

```bash
cd server
pip install -r requirements.txt
SFSERVER_DB=sfserver.db SFSERVER_ASSETS=assets SFSERVER_EXPORT_DIR="../My project (2)/Assets/SFBuildingTemplates" \
  uvicorn app.main:app --reload
```

## API (data-model.md §5)

| Method & path | Purpose |
|---|---|
| `GET  /neighborhoods` | authored neighborhoods (one per palette) |
| `GET  /building-types` | classification vocabulary |
| `GET  /parts` · `POST /parts` | list / author parts (PartDef) |
| `PUT  /parts/{id}/glb` | upload a part's GLB binary (multipart) |
| `GET  /templates` · `POST /templates` | list / author templates |
| `GET  /palettes` · `POST /palettes` | list / author neighborhood palettes |
| `POST /building-specific` | store an override (osm_id + footprint_hash) |
| `POST /ai/signs/generate` · `GET /signs` | server-mediated AI sign gen (swappable provider) → PNG+thumb+metadata |
| `POST /canvas` · `GET /canvas/{osm_id}[/{facade}]` | facade canvas CRUD (#278); strokes flatten to a paint PNG + facadeDecals on export |
| `POST /export/unity` | materialise `Assets/SFBuildingTemplates/` (the seam) |

`POST /ai/signs/generate` is server-mediated (the iPad never calls a provider directly): a `SignProvider` is selected by name from a registry, so the backend (ChatGPT image gen / Nano Banana / future) is swappable. A built-in `local-stub` provider renders a deterministic placeholder PNG with no external calls/deps, so the store+export path runs offline.

## footprint_hash parity

`app/footprint_hash.py` implements the normative §6.1 algorithm **byte-for-byte identically**
to the Python bake (`python/sfmap/classify.py`), so a building-specific override authored here
matches the bake's hash at Unity import (design D3). `tests/test_footprint_hash.py` asserts the
two implementations agree.

## Wire shapes

The exported JSON matches the Unity importer's DTOs exactly, including the JsonUtility-friendly
**array-of-pairs** form for fields §2 draws as maps (`roleSubmeshes`, `exact[].roles`, palette
`roles`) — see `app/models.py`.

## Test

```bash
cd server && python -m pytest -q
```
