"""Scipy-free bake configuration constants.

Kept dependency-free so lightweight callers (the ``sfmap_bake`` argparse setup,
``heightcache_guard``) can import a value without pulling in scipy via
``sfmap.elevation``. The single source of truth for values that were previously
duplicated across those modules (#425).
"""

# Default source low-pass radius (metres, σ). The rasterizer fills the grid by
# linear barycentric interpolation per Delaunay triangle, which is only C⁰ — the
# gradient jumps across every triangle edge, seeding crease lines and the road
# waviness #230 is about. A small Gaussian low-pass over the rasterized grid
# attenuates those creases at the source (also smoothing buildings/sidewalks
# that sample the same field) while leaving real, longer-wavelength relief
# intact. Kept smaller than the ~8 m contour-point spacing so genuine grade is
# preserved. 0 disables. Validated/tuned on real terrain in #232. See #233.
HMAP_SMOOTH_SIGMA_M = 2.0
