"""Zone -> ProceduralRule compile-down (design #326 D1).

Zones are the authoring format (semantic regions drawn on the facade canvas in
the iPad app). ``ProceduralRule``/``ExactPlacement`` remain the execution format
``BuildingAssembler.cs`` reads, so zones never cross the wire to the Unity
importer — only their compiled rules do (see ``export.export_unity``).
"""
from __future__ import annotations

from typing import List, Optional

from .models import Constraints, Jitter, ProceduralRule, Repeat, Zone, ZoneShape

# Weighted `allowedParts` are approximated by repeating each part id in the
# uniform-pick `variants[]` list proportional to its weight — BuildingAssembler
# picks a variant with `rng % len(variants)` (PlaceProcedural/PickVariant), so
# repetition count == relative odds. 20 slots gives ~5% granularity, enough
# resolution for the spec's worked example (Victorian A 50% / B 30% / C 20%).
_VARIANT_RESOLUTION = 20

# BuildingAssembler itself floors spacing at 0.1m (PlaceProcedural). An unset
# zone spacing just defers to that floor and lets `countRange` govern density.
_DEFAULT_SPACING_M = 0.1


def _expand_weighted_variants(allowed_parts) -> List[str]:
    if not allowed_parts:
        return []
    if len(allowed_parts) == 1:
        return [allowed_parts[0].part]
    weights = [max(wp.weight, 0.0) for wp in allowed_parts]
    total = sum(weights) or 1.0
    variants: List[str] = []
    for wp, w in zip(allowed_parts, weights):
        reps = max(1, round(w / total * _VARIANT_RESOLUTION))
        variants.extend([wp.part] * reps)
    return variants


def _span_from_shape(shape: ZoneShape) -> List[float]:
    # Both rect and polygon shapes compile to their horizontal bounding box —
    # ProceduralRule.span is a flat [x0, x1] band. Polygon fidelity beyond the
    # bbox is deferred (design #326 open question 3; rect ships first).
    xs = [p[0] for p in shape.points if len(p) >= 1]
    if not xs:
        return [0.0, 1.0]
    return [min(xs), max(xs)]


def compile_zone(zone: Zone) -> Optional[ProceduralRule]:
    """Compile one authored Zone into the ProceduralRule BuildingAssembler reads.

    Returns None for a zone with no allowed parts — nothing to place.
    """
    variants = _expand_weighted_variants(zone.rules.allowedParts)
    if not variants:
        return None

    spacing = zone.rules.spacingMeters.min if zone.rules.spacingMeters.min > 0 else _DEFAULT_SPACING_M
    # Grid alignment relies on the assembler's own even (i+0.5)/count spacing —
    # no horizontal jitter. FloorLine/Free apply the zone's randomOffset as jitter.
    jitter_x = 0.0 if zone.rules.alignment == "Grid" else zone.rules.randomOffset

    return ProceduralRule(
        part=variants[0],
        facade=zone.facade,
        floorRange=zone.floorRange,
        span=_span_from_shape(zone.shape),
        repeat=Repeat(
            spacingMeters=spacing,
            countMin=zone.rules.countRange.min,
            countMax=zone.rules.countRange.max,
        ),
        constraints=Constraints(alignToFloorLine=zone.rules.alignment == "FloorLine"),
        jitter=Jitter(x=jitter_x),
        variants=variants,
    )


def compile_zones(zones: List[Zone]) -> List[ProceduralRule]:
    """Compile all zones on a template, skipping any with no placeable parts."""
    return [rule for zone in zones if (rule := compile_zone(zone)) is not None]
