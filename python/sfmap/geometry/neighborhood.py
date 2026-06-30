"""Neighborhood lookup — point-in-polygon over the DataSF Analysis Neighborhoods.

The bake's ``--neighborhoods <path.geojson>`` input is the DataSF "Analysis
Neighborhoods" boundary set: 41 polygons in WGS84 lon/lat, each carrying one
``nhood`` name (see ``python/data/README.md`` for provenance + licence). It is the
same neighborhood vocabulary the parking CSV's ``analysis_neighborhood`` column
uses, so building and kerb classifications agree.

This module projects those polygons into the map's world XZ once (via the same
:func:`to_world_xz` the rest of the bake uses) and answers "which neighborhood is
this world point in?" — the lookup that fills the ``neighborhood`` field of the
building classification sidecar (design #266 ``data-model.md`` §1). A point outside
every polygon returns ``""`` (the design's default for buildings off the boundary
set, e.g. Treasure Island gaps or out-of-extent geometry).

The sidecar emission itself lives with #266; this module only loads the input and
exposes the lookup so the bake can ``--neighborhoods`` an input today.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from typing import List, Sequence, Tuple

from ..projection import GeoOrigin, to_world_xz

# A projected linear ring: a closed polyline of (x, z) world-space points.
Ring = List[Tuple[float, float]]


def _point_in_ring(x: float, z: float, ring: Sequence[Tuple[float, float]]) -> bool:
    """Crossing-number test: is (x, z) inside the closed polygon ``ring``?

    Standard ray cast along +X. Points exactly on an edge are not guaranteed a
    particular side, which is immaterial here — building centroids never land on a
    boundary line to float precision.
    """
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, zi = ring[i]
        xj, zj = ring[j]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            inside = not inside
        j = i
    return inside


@dataclass
class NeighborhoodPolygon:
    """One Analysis Neighborhood, projected to world XZ.

    ``parts`` holds one ``(exterior, holes)`` pair per polygon of the source
    (Multi)Polygon; a point is inside the part when it's inside the exterior ring
    and outside every hole. ``bbox`` (min_x, min_z, max_x, max_z) bounds all parts
    for a cheap reject before the ray cast. (The shipped dataset has no holes, but
    the structure handles them so a future boundary revision can't silently break.)
    """
    name: str
    parts: List[Tuple[Ring, List[Ring]]]
    bbox: Tuple[float, float, float, float]

    def contains(self, x: float, z: float) -> bool:
        min_x, min_z, max_x, max_z = self.bbox
        if x < min_x or x > max_x or z < min_z or z > max_z:
            return False
        for exterior, holes in self.parts:
            if _point_in_ring(x, z, exterior) and not any(
                _point_in_ring(x, z, h) for h in holes
            ):
                return True
        return False


@dataclass
class NeighborhoodIndex:
    """The projected neighborhood polygons, with a world-point name lookup."""
    polygons: List[NeighborhoodPolygon]

    def __len__(self) -> int:
        return len(self.polygons)

    def lookup(self, x: float, z: float) -> str:
        """Name of the neighborhood containing world point (x, z), or ``""`` if none.

        First containing polygon wins. The Analysis Neighborhoods tile the city
        without overlap, so a point falls in at most one — order is immaterial.
        """
        for poly in self.polygons:
            if poly.contains(x, z):
                return poly.name
        return ""


def _geometry_parts(geom: dict) -> List[list]:
    """Normalise a GeoJSON geometry to a list of polygons (each ``[exterior, *holes]``)."""
    gtype = geom.get("type")
    coords = geom.get("coordinates") or []
    if gtype == "Polygon":
        return [coords]
    if gtype == "MultiPolygon":
        return list(coords)
    return []


def load_neighborhoods(path: str, origin: GeoOrigin) -> NeighborhoodIndex:
    """Load the Analysis Neighborhoods GeoJSON and project every polygon to world XZ.

    Reads the ``nhood`` property as the neighborhood name; features without it (or
    with no polygon geometry) are skipped. Coordinates are GeoJSON ``[lon, lat]``,
    projected through ``origin`` so the polygons share the map's world space.
    """
    with open(path, encoding="utf-8") as f:
        data = json.load(f)

    polygons: List[NeighborhoodPolygon] = []
    for feature in data.get("features", []):
        name = (feature.get("properties") or {}).get("nhood") or ""
        geom = feature.get("geometry") or {}
        xs: List[float] = []
        zs: List[float] = []
        parts: List[Tuple[Ring, List[Ring]]] = []
        for poly in _geometry_parts(geom):
            if not poly:
                continue
            exterior = [to_world_xz(lon, lat, origin) for lon, lat in poly[0]]
            holes = [
                [to_world_xz(lon, lat, origin) for lon, lat in ring] for ring in poly[1:]
            ]
            parts.append((exterior, holes))
            for px, pz in exterior:
                xs.append(px)
                zs.append(pz)
        if not parts:
            continue
        bbox = (min(xs), min(zs), max(xs), max(zs))
        polygons.append(NeighborhoodPolygon(name=name, parts=parts, bbox=bbox))

    return NeighborhoodIndex(polygons)
