"""footprint_hash must match the Python bake byte-for-byte (design D3, data-model §6.1).

If these drift, a building-specific override authored on the server silently stops
matching the bake's hash at Unity import. So this asserts the server's implementation
agrees with the *actual bake* implementation on a range of footprints.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))          # worktree root
sys.path.insert(0, os.path.join(HERE, ".."))            # server/ (for app)
sys.path.insert(0, os.path.join(REPO, "python"))        # the bake (for sfmap)

from app.footprint_hash import footprint_hash as server_hash  # noqa: E402
from sfmap.classify import footprint_hash as bake_hash        # noqa: E402

_RINGS = [
    [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)],                       # square CCW
    [(0.0, 0.0), (0.0, 10.0), (10.0, 10.0), (10.0, 0.0)],                       # square CW
    [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0), (0.0, 0.0)],           # closed dup
    [(0.0, 0.0), (10.0, 0.0), (10.0, 5.0), (5.0, 5.0), (5.0, 10.0), (0.0, 10.0)],  # L
    [(-12.3, 4.1), (3.7, -2.2), (8.0, 9.9), (-5.5, 14.0)],                      # negative coords
    [(0.125, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)],                     # on the 0.125 half-grid
]


def test_server_hash_matches_bake_for_various_footprints():
    for ring in _RINGS:
        assert server_hash(ring) == bake_hash(ring), f"hash drift on {ring}"


def test_hash_is_8_hex_and_winding_invariant():
    sq = _RINGS[0]
    h = server_hash(sq)
    assert len(h) == 8 and int(h, 16) >= 0
    assert server_hash(list(reversed(sq))) == h        # winding-invariant
    assert server_hash(sq[2:] + sq[:2]) == h           # start-vertex-invariant
