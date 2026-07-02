"""Zone -> ProceduralRule compile-down (design #326 D1)."""
from app.models import IntRange, Zone, ZoneRules, ZoneShape
from app.zones import compile_zone, compile_zones


def _zone(**overrides):
    base = dict(
        id="z1", type="Window", facade="Front",
        shape=ZoneShape(kind="rect", points=[[0.2, 0.0], [0.8, 0.0], [0.8, 1.0], [0.2, 1.0]]),
        floorRange=IntRange(min=1, max=2),
        rules=ZoneRules(
            allowedParts=[{"part": "window_a", "weight": 1.0}],
            countRange=IntRange(min=2, max=4),
        ),
    )
    base.update(overrides)
    return Zone(**base)


def test_compile_zone_maps_span_and_floor_range():
    rule = compile_zone(_zone())
    assert rule.span == [0.2, 0.8]
    assert (rule.floorRange.min, rule.floorRange.max) == (1, 2)
    assert (rule.repeat.countMin, rule.repeat.countMax) == (2, 4)


def test_compile_zone_returns_none_with_no_allowed_parts():
    zone = _zone(rules=ZoneRules(countRange=IntRange(min=1, max=1)))
    assert compile_zone(zone) is None
    assert compile_zones([zone]) == []


def test_compile_zone_expands_weighted_variants_proportionally():
    zone = _zone(rules=ZoneRules(
        allowedParts=[
            {"part": "victorian_a", "weight": 50},
            {"part": "victorian_b", "weight": 30},
            {"part": "victorian_c", "weight": 20},
        ],
        countRange=IntRange(min=1, max=3),
    ))
    rule = compile_zone(zone)
    counts = {p: rule.variants.count(p) for p in ("victorian_a", "victorian_b", "victorian_c")}
    assert sum(counts.values()) == len(rule.variants)
    # Proportional ordering preserved at 20-slot resolution (50/30/20 -> 10/6/4).
    assert counts["victorian_a"] > counts["victorian_b"] > counts["victorian_c"]


def test_compile_zone_drops_zero_weight_parts():
    zone = _zone(rules=ZoneRules(
        allowedParts=[
            {"part": "victorian_a", "weight": 1.0},
            {"part": "never_placed", "weight": 0.0},
        ],
        countRange=IntRange(min=1, max=1),
    ))
    rule = compile_zone(zone)
    assert "never_placed" not in rule.variants
    assert rule.variants == ["victorian_a"]


def test_compile_zone_all_zero_weights_returns_none():
    zone = _zone(rules=ZoneRules(
        allowedParts=[{"part": "a", "weight": 0.0}],
        countRange=IntRange(min=1, max=1),
    ))
    assert compile_zone(zone) is None


def test_compile_zone_single_part_ignores_weight():
    zone = _zone(rules=ZoneRules(
        allowedParts=[{"part": "only_one", "weight": 0.01}],
        countRange=IntRange(min=1, max=1),
    ))
    rule = compile_zone(zone)
    assert rule.variants == ["only_one"]


def test_compile_zone_alignment_maps_to_constraints_and_jitter():
    grid = compile_zone(_zone(rules=ZoneRules(
        allowedParts=[{"part": "a", "weight": 1}], alignment="Grid", randomOffset=0.3,
        countRange=IntRange(min=1, max=1),
    )))
    assert grid.constraints.alignToFloorLine is False
    assert grid.jitter.x == 0.0

    # FloorLine only sets the vertical constraint — randomOffset does not leak
    # into horizontal jitter (design #326 D1's alignment mapping is per-knob).
    floor_line = compile_zone(_zone(rules=ZoneRules(
        allowedParts=[{"part": "a", "weight": 1}], alignment="FloorLine", randomOffset=0.3,
        countRange=IntRange(min=1, max=1),
    )))
    assert floor_line.constraints.alignToFloorLine is True
    assert floor_line.jitter.x == 0.0

    free = compile_zone(_zone(rules=ZoneRules(
        allowedParts=[{"part": "a", "weight": 1}], alignment="Free", randomOffset=0.15,
        countRange=IntRange(min=1, max=1),
    )))
    assert free.constraints.alignToFloorLine is False
    assert free.jitter.x == 0.15


def test_compile_zone_spacing_defaults_when_unset():
    zone = _zone(rules=ZoneRules(
        allowedParts=[{"part": "a", "weight": 1}], countRange=IntRange(min=1, max=1),
    ))
    assert compile_zone(zone).repeat.spacingMeters == 0.1


def test_compile_zone_uses_authored_min_spacing():
    from app.models import FloatRange
    zone = _zone(rules=ZoneRules(
        allowedParts=[{"part": "a", "weight": 1}],
        countRange=IntRange(min=1, max=1),
        spacingMeters=FloatRange(min=2.5, max=4.0),
    ))
    assert compile_zone(zone).repeat.spacingMeters == 2.5
