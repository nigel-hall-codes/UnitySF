"""Chunk-grid geometry: data extent and the list of chunks to bake.

Extracted from the ``sfmap_bake`` CLI (#425) so the grid layout logic lives in
the library and can be reused/tested independently of the argparse driver.
"""
from __future__ import annotations

from typing import List, Optional, Tuple


def geometry_extent(graph) -> Tuple[float, float, float, float]:
    """Return (min_x, min_z, max_x, max_z) over every croppable element in the graph.

    The OSM <bounds> element undercounts the real extent: boundary-crossing ways
    pull in nodes outside the declared box, and the projection centres world
    coordinates on the bounds centre. Anchoring the chunk grid to this actual
    bounding box (rather than the bounds rect or world origin) is what keeps the
    whole map inside the grid. Covers nodes, edge centerlines, and building
    footprints — the three things crop_to_chunk filters on.
    """
    xs, zs = [], []
    for n in graph.nodes.values():
        xs.append(n.world_x)
        zs.append(n.world_z)
    for e in graph.edges:
        for x, z in e.centerline:
            xs.append(x)
            zs.append(z)
    for b in graph.buildings:
        for x, z in b.footprint:
            xs.append(x)
            zs.append(z)
    return min(xs), min(zs), max(xs), max(zs)


def chunk_list(
    only: Optional[List[Tuple[int, int]]], chunks_x: int, chunks_z: int
) -> List[Tuple[int, int]]:
    """Resolve the (col, row) chunks to bake from the grid size and --only."""
    if only:
        # Explicit set — bake exactly these, de-duplicated, in stable order.
        seen = set()
        out = []
        for cr in only:
            if cr not in seen:
                seen.add(cr)
                out.append(cr)
        return out
    return [(col, row) for row in range(chunks_z) for col in range(chunks_x)]
