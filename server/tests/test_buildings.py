"""Building facts endpoints (#299): sidecar import, list/filter/paginate, get one."""


def _facts(osm_id=65307880, neighborhood="Mission", building_type="retail"):
    return {
        "osm_id": osm_id, "neighborhood": neighborhood, "building_type": building_type,
        "footprint_shape": "corner", "width_m": 11.4, "depth_m": 18.2, "height_m": 12.0,
        "floor_count": 4, "base_y": 42.3, "facade_height_m": 12.0,
        "street_facades": [{"edge_index": 2, "bearing_deg": 117.0, "street_osm_id": 8412731,
                            "score": 0.94, "edge": [1.0, 2.0, 3.0, 4.0]}],
        "footprint_hash": "a3f1c9d2",
    }


def _sidecar(*buildings):
    return {"version": 2, "buildings": list(buildings)}


def test_import_sidecar_and_get_building(client):
    r = client.post("/buildings/import-sidecar", json=_sidecar(_facts()))
    assert r.status_code == 200 and r.json() == {"imported": 1}

    b = client.get("/buildings/65307880").json()
    assert b["neighborhood"] == "Mission" and b["building_type"] == "retail"
    # Nested facade geometry survives the round-trip.
    assert b["street_facades"][0]["edge"] == [1.0, 2.0, 3.0, 4.0]
    assert b["footprint_hash"] == "a3f1c9d2"


def test_get_unknown_building_404(client):
    assert client.get("/buildings/999").status_code == 404


def test_reimport_updates_in_place(client):
    client.post("/buildings/import-sidecar", json=_sidecar(_facts()))
    # Re-bake reclassifies the same building — must update, not duplicate.
    client.post("/buildings/import-sidecar",
                json=_sidecar(_facts(building_type="commercial")))
    assert client.get("/buildings").json()["total"] == 1
    assert client.get("/buildings/65307880").json()["building_type"] == "commercial"


def test_list_empty(client):
    page = client.get("/buildings").json()
    assert page == {"buildings": [], "total": 0, "limit": 100, "offset": 0}


def test_list_filter_by_neighborhood_and_type(client):
    client.post("/buildings/import-sidecar", json=_sidecar(
        _facts(osm_id=1, neighborhood="Mission", building_type="retail"),
        _facts(osm_id=2, neighborhood="Mission", building_type="residential"),
        _facts(osm_id=3, neighborhood="Sunset", building_type="retail"),
    ))
    assert client.get("/buildings").json()["total"] == 3

    mission = client.get("/buildings?neighborhood=Mission").json()
    assert mission["total"] == 2 and {b["osm_id"] for b in mission["buildings"]} == {1, 2}

    retail = client.get("/buildings?type=retail").json()
    assert retail["total"] == 2 and {b["osm_id"] for b in retail["buildings"]} == {1, 3}

    both = client.get("/buildings?neighborhood=Mission&type=retail").json()
    assert both["total"] == 1 and both["buildings"][0]["osm_id"] == 1


def test_list_pagination(client):
    client.post("/buildings/import-sidecar", json=_sidecar(
        *(_facts(osm_id=i) for i in range(1, 6))
    ))
    p1 = client.get("/buildings?limit=2&offset=0").json()
    assert p1["total"] == 5 and [b["osm_id"] for b in p1["buildings"]] == [1, 2]

    p3 = client.get("/buildings?limit=2&offset=4").json()
    assert [b["osm_id"] for b in p3["buildings"]] == [5]
