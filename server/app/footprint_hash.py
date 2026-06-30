"""footprint_hash — the normative §6.1 algorithm (design #266 data-model.md).

This MUST be byte-for-byte identical to the Python bake's implementation
(`python/sfmap/classify.py::footprint_hash`), because a building-specific override
authored here is matched at Unity import against the bake's hash — a mismatch
silently disables the override (design D3). The two implementations are kept in
parity by `tests/test_footprint_hash.py`, which asserts they agree on real inputs.

Algorithm: drop the closing vertex; quantise each (x, z) to a 0.25 m grid with
half-away-from-zero rounding (pinned so the hash is reproducible across languages);
canonicalise ordering by forcing CCW winding *then* rotating to start at the
lexicographically smallest vertex; serialise "x.xx,z.zz;" per vertex; take the first
8 hex of SHA-256.
"""
from __future__ import annotations

import hashlib
import math
from typing import List, Sequence, Tuple

Point = Tuple[float, float]

_GRID_M = 0.25


def _drop_closing(ring: Sequence[Point]) -> List[Point]:
    pts = list(ring)
    if len(pts) >= 2 and pts[0] == pts[-1]:
        pts = pts[:-1]
    return pts


def _signed_area(ring: Sequence[Point]) -> float:
    n = len(ring)
    s = 0.0
    for i in range(n):
        x0, z0 = ring[i]
        x1, z1 = ring[(i + 1) % n]
        s += x0 * z1 - x1 * z0
    return 0.5 * s


def _quantize(v: float) -> float:
    """Snap to the 0.25 m grid, half-away-from-zero (pinned for cross-language parity)."""
    n = v / _GRID_M
    n = math.floor(n + 0.5) if n >= 0 else math.ceil(n - 0.5)
    return n * _GRID_M + 0.0  # +0.0 collapses a possible -0.0 for stable text


def footprint_hash(ring: Sequence[Point]) -> str:
    """First 8 hex of SHA-256 over the canonicalised, quantised footprint ring."""
    pts = [(_quantize(x), _quantize(z)) for (x, z) in _drop_closing(ring)]
    if not pts:
        return hashlib.sha256(b"").hexdigest()[:8]

    if _signed_area(pts) < 0:           # force CCW winding before rotating
        pts = list(reversed(pts))
    start = min(range(len(pts)), key=lambda i: pts[i])
    pts = pts[start:] + pts[:start]     # rotate to the lexicographically smallest vertex

    serialized = "".join(f"{x:.2f},{z:.2f};" for (x, z) in pts)
    return hashlib.sha256(serialized.encode("utf-8")).hexdigest()[:8]
