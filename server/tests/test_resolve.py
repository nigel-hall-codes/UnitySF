"""POST /templates/{id}/resolve — placement resolution (design #326 D2)."""
from app.models import (
    BuildingFacts,
    Constraints,
    ExactPlacement,
    IntRange,
    PartDef,
    ProceduralRule,
    Repeat,
    SizeM,
    StreetFacade,
    TemplateDef,
    Zone,
    ZoneRules,
    ZoneShape,
)
from app.resolve import resolve_template


def _facts(street_facades=None, floor_count=3, osm_id=1):
    return BuildingFacts(
        osm_id=osm_id, neighborhood="Sunset", floor_count=floor_count,
        street_facades=street_facades if street_facades is not None else [
            StreetFacade(edge_index=0, bearing_deg=0.0, edge=[0.0, 0.0, 10.0, 0.0]),
        ],
    )


def _window_part():
    return PartDef(id="window_a", category="Window", size_m=SizeM(w=1.2, h=1.6, d=0.15))


def test_resolve_passes_through_exact_placements_with_part_size():
    tpl = TemplateDef(id="t1", exact=[
        ExactPlacement(part="window_a", facade="Front", floor=1, x=0.5, y=0.5, scale=2.0),
    ])
    placements = resolve_template(tpl, _facts(), seed=1, parts_by_id={"window_a": _window_part()})
    assert len(placements) == 1
    p = placements[0]
    assert (p.part, p.floor, p.x, p.y, p.scale) == ("window_a", 1, 0.5, 0.5, 2.0)
    assert (p.w_m, p.h_m) == (2.4, 3.2)  # size_m * scale


def test_resolve_exact_with_unsupported_facade_yields_nothing():
    # Only Front/Street carry sidecar geometry today (matches BuildingAssembler.FacadesFor).
    tpl = TemplateDef(id="t1", exact=[ExactPlacement(part="window_a", facade="Back", x=0.5, y=0.5)])
    placements = resolve_template(tpl, _facts(), seed=1, parts_by_id={})
    assert placements == []


def test_resolve_exact_street_facade_repeats_per_edge():
    tpl = TemplateDef(id="t1", exact=[ExactPlacement(part="sign_a", facade="Street", x=0.5, y=0.5)])
    facts = _facts(street_facades=[
        StreetFacade(edge_index=0, edge=[0.0, 0.0, 10.0, 0.0]),
        StreetFacade(edge_index=1, edge=[10.0, 0.0, 10.0, 8.0]),
    ])
    placements = resolve_template(tpl, facts, seed=1, parts_by_id={})
    assert len(placements) == 2


def test_resolve_procedural_rule_places_repeated_variants():
    tpl = TemplateDef(id="t1", rules=[
        ProceduralRule(
            part="", facade="Front", span=[0.0, 1.0], floorRange=IntRange(min=0, max=2),
            repeat=Repeat(spacingMeters=1.0, countMin=3, countMax=3),
            variants=["window_a"],
        ),
    ])
    placements = resolve_template(tpl, _facts(floor_count=3), seed=42, parts_by_id={"window_a": _window_part()})
    # 3 floors (0,1,2) x 3 slots each = 9 procedural placements.
    assert len(placements) == 9
    assert all(p.part == "window_a" for p in placements)


def test_resolve_is_deterministic_for_same_seed_and_varies_across_seeds():
    tpl = TemplateDef(id="t1", rules=[
        ProceduralRule(
            part="", facade="Front", span=[0.0, 1.0], floorRange=IntRange(min=0, max=0),
            repeat=Repeat(spacingMeters=0.5, countMin=4, countMax=4),
            jitter={"x": 0.4}, variants=["a", "b", "c"],
        ),
    ])
    facts = _facts()
    r1 = resolve_template(tpl, facts, seed=7, parts_by_id={})
    r2 = resolve_template(tpl, facts, seed=7, parts_by_id={})
    r3 = resolve_template(tpl, facts, seed=8, parts_by_id={})
    assert [(p.part, p.x) for p in r1] == [(p.part, p.x) for p in r2]
    assert [(p.part, p.x) for p in r1] != [(p.part, p.x) for p in r3]


def test_resolve_avoid_exact_suppresses_colliding_procedural_slot():
    # A single procedural slot lands exactly on the exact placement's x (span midpoint);
    # avoidExact must suppress it since it falls within the exclusion radius.
    tpl = TemplateDef(
        id="t1",
        exact=[ExactPlacement(part="door_a", facade="Front", floor=0, x=0.5, y=0.5)],
        rules=[ProceduralRule(
            part="", facade="Front", span=[0.4, 0.6], floorRange=IntRange(min=0, max=0),
            repeat=Repeat(spacingMeters=1.0, countMin=1, countMax=1),
            constraints=Constraints(avoidExact=True, minSpacingMeters=1.0),
            variants=["window_a"],
        )],
    )
    placements = resolve_template(tpl, _facts(), seed=1, parts_by_id={})
    assert [p for p in placements if p.part == "window_a"] == []


def test_resolve_zones_append_to_rules():
    tpl = TemplateDef(
        id="t1",
        rules=[ProceduralRule(
            part="", facade="Front", span=[0.0, 0.4], floorRange=IntRange(min=0, max=0),
            repeat=Repeat(spacingMeters=1.0, countMin=1, countMax=1), variants=["form_a"],
        )],
        zones=[Zone(
            id="z1", type="Window", facade="Front",
            shape=ZoneShape(kind="rect", points=[[0.6, 0.0], [1.0, 0.0], [1.0, 1.0], [0.6, 1.0]]),
            floorRange=IntRange(min=0, max=0),
            rules=ZoneRules(
                allowedParts=[{"part": "zone_a", "weight": 1.0}],
                countRange=IntRange(min=1, max=1),
            ),
        )],
    )
    placements = resolve_template(tpl, _facts(), seed=1, parts_by_id={})
    parts = {p.part for p in placements}
    assert parts == {"form_a", "zone_a"}


def test_resolve_endpoint_uses_synthetic_facts(client):
    client.post("/parts", json={"id": "window_a", "category": "Window",
                                 "size_m": {"w": 1.2, "h": 1.6, "d": 0.15}})
    client.post("/templates", json={
        "id": "t1",
        "exact": [{"part": "window_a", "facade": "Front", "floor": 0, "x": 0.5, "y": 0.5}],
    })
    body = {
        "facts": {
            "osm_id": 999999, "neighborhood": "Sunset", "floor_count": 2,
            "street_facades": [{"edge_index": 0, "edge": [0.0, 0.0, 10.0, 0.0]}],
        },
        "seed": 3,
    }
    r = client.post("/templates/t1/resolve", json=body)
    assert r.status_code == 200
    data = r.json()
    assert len(data["placements"]) == 1
    assert data["placements"][0]["part"] == "window_a"


def test_resolve_endpoint_404s_for_unknown_template(client):
    r = client.post("/templates/ghost/resolve", json={"osm_id": 1, "seed": 1})
    assert r.status_code == 404


def test_resolve_endpoint_404s_for_unknown_building(client):
    client.post("/templates", json={"id": "t1"})
    r = client.post("/templates/t1/resolve", json={"osm_id": 424242, "seed": 1})
    assert r.status_code == 404


def test_resolve_endpoint_requires_osm_id_or_facts(client):
    client.post("/templates", json={"id": "t1"})
    r = client.post("/templates/t1/resolve", json={"seed": 1})
    assert r.status_code == 400
