"""GET/POST /districts — DistrictDef entity (design #326 D4)."""


def _district(did="mission"):
    return {
        "id": did, "name": "Mission",
        "neighborhoods": ["Mission"],
        "templateWeights": [{"template": "victorian_a", "weight": 50}, {"template": "victorian_b", "weight": 50}],
        "palette": "Mission",
        "signStyle": "Bilingual",
        "version": 1,
    }


def test_districts_empty_by_default(client):
    assert client.get("/districts").json() == []


def test_district_roundtrip_and_upsert(client):
    assert client.post("/districts", json=_district()).status_code == 200
    districts = client.get("/districts").json()
    assert len(districts) == 1
    d = districts[0]
    assert d["id"] == "mission" and d["signStyle"] == "Bilingual"
    assert d["templateWeights"] == [
        {"template": "victorian_a", "weight": 50}, {"template": "victorian_b", "weight": 50},
    ]

    # Re-POST same id updates in place (no duplicate) — matches parts/templates/palettes.
    client.post("/districts", json={**_district(), "name": "The Mission"})
    districts = client.get("/districts").json()
    assert len(districts) == 1 and districts[0]["name"] == "The Mission"


def test_district_defaults():
    from app.models import DistrictDef
    d = DistrictDef(id="sunset")
    assert d.name == "" and d.neighborhoods == [] and d.templateWeights == []
    assert d.palette == "" and d.signStyle == "Modern" and d.version == 1


def test_multiple_districts_independent(client):
    client.post("/districts", json=_district("mission"))
    client.post("/districts", json=_district("sunset"))
    ids = {d["id"] for d in client.get("/districts").json()}
    assert ids == {"mission", "sunset"}
