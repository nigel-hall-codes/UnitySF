"""Template placement resolution (design #326 D2).

The single deterministic placement algorithm — ported from BuildingAssembler.cs's
``PlaceExact``/``PlaceProcedural``/``PickVariant`` so the iPad can preview a template's
generated result in seconds via ``POST /templates/{id}/resolve``, instead of waiting on
a Unity render. Output stays in the same normalized facade-UV space Exact/ProceduralRule
placements already use (x, y in [0,1]) — no world-space geometry (edges/bearing) is
computed, since the schematic preview only needs relative layout + part size.

The caller supplies an explicit ``seed`` rather than relying on an osm_id-derived one —
that is what lets "Generate Variants 1..N" (design §3.2) be the same call with seeds
1..N, including for synthetic (non-real) buildings that have no osm_id at all.
"""
from __future__ import annotations

from typing import Dict, List, Tuple

from .models import BuildingFacts, ExactPlacement, PartDef, ProceduralRule, ResolvedPlacement, StreetFacade, TemplateDef
from .zones import compile_zones

# BuildingAssembler itself floors spacing at 0.1m (PlaceProcedural) — mirrored here.
_MIN_SPACING_M = 0.1


def _clamp01(v: float) -> float:
    return max(0.0, min(1.0, v))


def _clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


class _Rng:
    """xorshift32, bit-for-bit matching BuildingAssembler.cs's ``Rng`` struct."""

    __slots__ = ("_s",)

    def __init__(self, seed: int):
        self._s = (seed & 0xFFFFFFFF) or 1

    def next_uint(self) -> int:
        s = self._s
        s = (s ^ (s << 13)) & 0xFFFFFFFF
        s = (s ^ (s >> 17)) & 0xFFFFFFFF
        s = (s ^ (s << 5)) & 0xFFFFFFFF
        self._s = s
        return s

    def next_float(self) -> float:
        return (self.next_uint() & 0xFFFFFF) / 16777216.0

    def range(self, lo: float, hi: float) -> float:
        return lo + (hi - lo) * self.next_float()


def _seed_for(seed: int, rule_index: int, slot_index: int) -> int:
    """FNV-1a of (seed, ruleIndex, slotIndex) — mirrors BuildingAssembler.SeedFor(osmId,
    ruleIndex, slotIndex), with the caller-supplied ``seed`` standing in for Unity's
    osm_id-derived one."""
    h = 2166136261
    for shift in range(0, 64, 8):
        h ^= (seed >> shift) & 0xFF
        h = (h * 16777619) & 0xFFFFFFFF
    for shift in range(0, 32, 8):
        h ^= (rule_index >> shift) & 0xFF
        h = (h * 16777619) & 0xFFFFFFFF
    for shift in range(0, 32, 8):
        h ^= (slot_index >> shift) & 0xFF
        h = (h * 16777619) & 0xFFFFFFFF
    return h


def _facade_length(facade: StreetFacade) -> float:
    if len(facade.edge) < 4:
        return 0.0
    dx, dz = facade.edge[2] - facade.edge[0], facade.edge[3] - facade.edge[1]
    return (dx * dx + dz * dz) ** 0.5


def _facades_for(facade_name: str, facts: BuildingFacts) -> List[StreetFacade]:
    # Mirrors BuildingAssembler.FacadesFor: only Front (primary street facade) and
    # Street (every ranked street facade) carry sidecar geometry today — other faces
    # (Back/Left/Right) aren't carried in the sidecar and resolve to nothing, same as
    # Unity's warn-and-skip.
    if facade_name == "Front":
        return facts.street_facades[:1]
    if facade_name == "Street":
        return list(facts.street_facades)
    return []


def _part_size(part: PartDef | None, scale: float) -> Tuple[float, float]:
    if part is None:
        return (0.0, 0.0)
    s = scale if scale > 0 else 1.0
    return (part.size_m.w * s, part.size_m.h * s)


def _pick_variant(rule: ProceduralRule, rng: _Rng) -> str:
    if rule.variants:
        return rule.variants[rng.next_uint() % len(rule.variants)]
    return rule.part


def _place_exact(exact: List[ExactPlacement], facts: BuildingFacts,
                  parts_by_id: Dict[str, PartDef]) -> List[ResolvedPlacement]:
    placements = []
    for p in exact:
        for _facade in _facades_for(p.facade, facts):
            scale = p.scale if p.scale > 0 else 1.0
            w_m, h_m = _part_size(parts_by_id.get(p.part), scale)
            placements.append(ResolvedPlacement(
                part=p.part, facade=p.facade, floor=p.floor,
                x=_clamp01(p.x), y=_clamp01(p.y), scale=scale, w_m=w_m, h_m=h_m,
            ))
    return placements


def _place_roof_parts(roof_parts: List[str], facts: BuildingFacts,
                       parts_by_id: Dict[str, PartDef]) -> List[ResolvedPlacement]:
    # Mirrors BuildingAssembler.PlaceRoofParts: each roofPart is placed once per
    # street-facing facade, centred (x=0.5) at the roofline (y=1.0 — PlacePart's ny=1
    # overshoots the top floor band and gets clamped to the real facade_height_m, which
    # this normalized-space endpoint doesn't model, so 1.0 is the closest UV equivalent).
    placements = []
    for part_id in roof_parts:
        if not part_id:
            continue
        for _facade in _facades_for("Street", facts):
            w_m, h_m = _part_size(parts_by_id.get(part_id), 1.0)
            placements.append(ResolvedPlacement(
                part=part_id, facade="Roof", floor=facts.floor_count,
                x=0.5, y=1.0, scale=1.0, w_m=w_m, h_m=h_m,
            ))
    return placements


def _exact_marks(exact: List[ExactPlacement], facts: BuildingFacts) -> Dict[Tuple[int, int], List[float]]:
    marks: Dict[Tuple[int, int], List[float]] = {}
    for p in exact:
        for facade in _facades_for(p.facade, facts):
            marks.setdefault((facade.edge_index, p.floor), []).append(_clamp01(p.x))
    return marks


def _near_exact_mark(marks: Dict[Tuple[int, int], List[float]], edge_index: int,
                      floor: int, nx: float, radius: float) -> bool:
    return any(abs(ex - nx) < radius for ex in marks.get((edge_index, floor), []))


def _place_procedural(rule: ProceduralRule, rule_index: int, facts: BuildingFacts, seed: int,
                       parts_by_id: Dict[str, PartDef],
                       exact_marks: Dict[Tuple[int, int], List[float]]) -> List[ResolvedPlacement]:
    if not rule.part and not rule.variants:
        return []

    x0 = _clamp01(rule.span[0]) if len(rule.span) > 0 else 0.0
    x1 = _clamp01(rule.span[1]) if len(rule.span) > 1 else 1.0
    margin = _clamp01(rule.constraints.edgeMargin)
    x0 = _clamp01(x0 + margin)
    x1 = _clamp01(x1 - margin)
    if x1 <= x0:
        return []

    floor_min = max(rule.floorRange.min, 0)
    floor_max = max(rule.floorRange.max, floor_min)
    floor_max = min(floor_max, max(facts.floor_count, 1) - 1)
    if floor_max < floor_min:
        return []

    placements: List[ResolvedPlacement] = []
    for facade in _facades_for(rule.facade, facts):
        facade_len = _facade_length(facade)
        if facade_len < 1e-3:
            continue

        span_meters = (x1 - x0) * facade_len
        spacing = max(rule.repeat.spacingMeters, rule.constraints.minSpacingMeters, _MIN_SPACING_M)
        count = int(span_meters // spacing)
        count = max(count, max(rule.repeat.countMin, 0))
        if rule.repeat.countMax > 0:
            count = min(count, rule.repeat.countMax)
        if count <= 0:
            continue

        exclusion = max(rule.constraints.minSpacingMeters, spacing * 0.5) / facade_len

        for floor in range(floor_min, floor_max + 1):
            for i in range(count):
                rng = _Rng(_seed_for(seed, rule_index, floor * 65536 + i))

                prob = 1.0 if rule.probability <= 0 else _clamp01(rule.probability)
                if prob < 1.0 and rng.next_float() >= prob:
                    continue

                t = 0.5 if count == 1 else (i + 0.5) / count
                nx = x0 + t * (x1 - x0)
                if rule.jitter.x != 0.0:
                    nx += rng.range(-rule.jitter.x, rule.jitter.x) / facade_len
                nx = _clamp(nx, x0, x1)

                if rule.constraints.avoidExact and _near_exact_mark(
                        exact_marks, facade.edge_index, floor, nx, exclusion):
                    continue

                ny = 0.0 if rule.constraints.alignToFloorLine else 0.5

                part_id = _pick_variant(rule, rng)
                scale = 1.0
                if len(rule.jitter.scale) >= 2:
                    scale = rng.range(rule.jitter.scale[0], rule.jitter.scale[1])
                # BuildingAssembler draws jitter.rotation last for this slot's rng; skipped
                # here on purpose since ResolvedPlacement has no rotation field and each slot
                # gets a fresh _Rng, so omitting this trailing draw doesn't shift any other value.

                w_m, h_m = _part_size(parts_by_id.get(part_id), scale)
                placements.append(ResolvedPlacement(
                    part=part_id, facade=rule.facade, floor=floor, x=nx, y=ny,
                    scale=scale, w_m=w_m, h_m=h_m,
                ))
    return placements


def resolve_template(template: TemplateDef, facts: BuildingFacts, seed: int,
                      parts_by_id: Dict[str, PartDef]) -> List[ResolvedPlacement]:
    """Resolve one template against building facts + a seed into a flat placement list.

    Compiled zones append to form-authored rules[] (never replace — mirrors export.py),
    so a zone-only template resolves exactly like a rules-only one authored by hand.
    """
    placements = _place_exact(template.exact, facts, parts_by_id)
    placements.extend(_place_roof_parts(template.roofParts, facts, parts_by_id))
    exact_marks = _exact_marks(template.exact, facts)

    rules = list(template.rules) + compile_zones(template.zones)
    for rule_index, rule in enumerate(rules):
        placements.extend(_place_procedural(rule, rule_index, facts, seed, parts_by_id, exact_marks))
    return placements
