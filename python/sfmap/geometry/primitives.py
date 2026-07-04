"""Shared geometry primitives for the bake's mesh/stamp passes.

These helpers were extracted from ``road.py`` (#423): they are pure XZ/elevation
geometry with no road-specific state, and are used across road, sidewalk,
intersection, building, parking meshing and the terrain stamp. Behaviour is
byte-for-byte identical to the original private ``road._*`` implementations —
only the names changed (the leading underscore was dropped) and the module moved.
"""
from __future__ import annotations

import math
from typing import List, Optional, Tuple

from ..elevation import HeightmapData

# Max XZ length of a centerline segment before it is subdivided. Raw OSM
# centerlines only carry a vertex at each tagged node, so a straight block can be
# a single segment tens of metres long. On steep grades that segment becomes one
# long flat quad that facets across the hill, and the matching stamp ramps the
# terrain linearly over the same span — diverging from the finely-sampled natural
# terrain at the road edges and tearing a seam. Densifying to a few metres (well
# under the default ~2.3 m heightmap cell pitch range) lets both the mesh and the
# stamp follow the hill's curvature. See issue #219.
MAX_SEG_M = 4.0

# Arc-length window (metres) of the centerline vertical-profile smoother. The
# smoother suppresses the high-frequency vertical noise roads inherit from the
# uneven terrain heightfield (sparse contours, triangle-edge creases) while
# leaving genuine grades intact. Tuned/validated separately in #232; keep small
# enough not to flatten real grade changes. See issues #230/#231.
SMOOTH_WINDOW_M = 12.0

MeshArrays = Tuple[
    List[Tuple[float, float, float]],  # vertices (x, y, z)
    List[Tuple[float, float]],          # UVs (u, v)
    List[int],                          # triangle indices (CW winding)
]


def vertex_normals(
    verts: List[Tuple[float, float, float]],
    indices: List[int],
) -> List[Tuple[float, float, float]]:
    """Area-weighted per-vertex normals, all oriented +Y (ground faces up).

    Emitting explicit normals (instead of leaving the importer to call
    RecalculateNormals per mesh) lets a road's welded end-cap carry the same
    straight-up normal as the intersection fan it meets, so the shared edge
    shades continuously instead of showing a hard seam. Raw cross products are
    summed (so larger triangles weight more) and each accumulated normal is
    flipped to +Y, since every road/fan surface faces up.
    """
    acc = [[0.0, 0.0, 0.0] for _ in verts]
    for t in range(0, len(indices), 3):
        a, b, c = indices[t], indices[t + 1], indices[t + 2]
        ax, ay, az = verts[a]
        bx, by, bz = verts[b]
        cx, cy, cz = verts[c]
        ux, uy, uz = bx - ax, by - ay, bz - az
        vx, vy, vz = cx - ax, cy - ay, cz - az
        nx = uy * vz - uz * vy
        ny = uz * vx - ux * vz
        nz = ux * vy - uy * vx
        if ny < 0.0:  # ground faces up; ignore triangle winding handedness
            nx, ny, nz = -nx, -ny, -nz
        for vi in (a, b, c):
            acc[vi][0] += nx
            acc[vi][1] += ny
            acc[vi][2] += nz

    out: List[Tuple[float, float, float]] = []
    for nx, ny, nz in acc:
        length = math.sqrt(nx * nx + ny * ny + nz * nz)
        if length < 1e-9:
            out.append((0.0, 1.0, 0.0))
        else:
            out.append((nx / length, ny / length, nz / length))
    return out


def sample_elevation(hmap: HeightmapData, x: float, z: float) -> float:
    norm = hmap.sample_bilinear(x, z)
    return hmap.min_elevation_m + norm * (hmap.max_elevation_m - hmap.min_elevation_m)


def densify_polyline(
    cl: List[Tuple[float, float]],
    max_seg: float,
) -> List[Tuple[float, float]]:
    """Insert evenly-spaced points so no XZ segment exceeds ``max_seg`` metres.

    Shared by road meshing and stamping so both follow the heightfield at the
    same resolution on steep grades (#219). Endpoints and existing vertices are
    preserved; only interior subdivision points are added.
    """
    if len(cl) < 2 or max_seg <= 0.0:
        return cl
    out: List[Tuple[float, float]] = [cl[0]]
    for i in range(1, len(cl)):
        x0, z0 = cl[i - 1]
        x1, z1 = cl[i]
        seg = math.hypot(x1 - x0, z1 - z0)
        if seg > max_seg:
            steps = int(math.ceil(seg / max_seg))
            for s in range(1, steps):
                t = s / steps
                out.append((x0 + t * (x1 - x0), z0 + t * (z1 - z0)))
        out.append((x1, z1))
    return out


def smooth_centerline_profile(
    cl_xz: List[Tuple[float, float]],
    y: List[float],
    window_m: float = SMOOTH_WINDOW_M,
) -> List[float]:
    """Smooth the *vertical* profile of a densified road centerline.

    Roads inherit high-frequency vertical noise from the uneven terrain
    heightfield (sparse contour samples, triangle-edge creases). This filters
    that wiggle out of ``y`` while letting genuine grades through. It is applied
    **identically** in the road mesh pass (``road._build_single_road``) and the
    stamp pass (``stamping.stamp_roads``) so the stamped grade and the resampled
    mesh surface stay consistent — diverging would tear a seam at the road edge,
    the exact failure #219 fixed. See #230/#231.

    Properties (relied on by tests and by the stamp/mesh invariant):

    - **XZ untouched** — only ``y`` is filtered; ``cl_xz`` is read-only.
    - **Endpoints exact** — ``y[0]`` and ``y[-1]`` pass through unchanged so the
      road stays welded to its intersection nodes and chunk-boundary anchors.
    - **Straight grades pass through** — each interior point is replaced by a
      local *linear* (degree-1) least-squares fit over an arc-length window, so a
      constant-grade ramp is reproduced exactly (a line fit of collinear points
      has zero residual) while only curvature/second-derivative content within
      the window is attenuated. This is why a linear fit is preferred over a
      moving average, which would round off real grade changes.

    Arc length is measured along **XZ**, which is bit-identical between the two
    passes for identical input, so the filtered ``y`` is identical too.
    """
    n = len(y)
    if n < 3 or window_m <= 0.0:
        return list(y)

    # Cumulative XZ arc length — the smoothing coordinate.
    s = [0.0] * n
    for i in range(1, n):
        dx = cl_xz[i][0] - cl_xz[i - 1][0]
        dz = cl_xz[i][1] - cl_xz[i - 1][1]
        s[i] = s[i - 1] + math.hypot(dx, dz)

    half = window_m * 0.5
    out = list(y)  # endpoints (0, n-1) left exact by construction
    lo = 0
    hi = 0
    for i in range(1, n - 1):
        si = s[i]
        while s[lo] < si - half:
            lo += 1
        if hi < i:
            hi = i
        while hi < n - 1 and s[hi + 1] <= si + half:
            hi += 1

        # Tricube-weighted least-squares line fit, evaluated at ds = 0 (= s[i]).
        sw = swx = swy = swxx = swxy = 0.0
        for j in range(lo, hi + 1):
            ds = s[j] - si
            u = abs(ds) / half
            w = 1.0 - u * u * u
            w = w * w * w  # tricube
            sw += w
            swx += w * ds
            swy += w * y[j]
            swxx += w * ds * ds
            swxy += w * ds * y[j]

        denom = sw * swxx - swx * swx
        if abs(denom) < 1e-12:
            continue  # window too narrow to fit a line — leave the point as-is
        slope = (sw * swxy - swx * swy) / denom
        out[i] = (swy - slope * swx) / sw  # intercept = fitted value at s[i]

    return out


def clip_polyline_to_rect(
    cl: List[Tuple[float, float]],
    x_min: float, z_min: float,
    x_max: float, z_max: float,
) -> List[Tuple[float, float]]:
    """Clip a 2D XZ polyline to [x_min,x_max]×[z_min,z_max], inserting crossings.

    Segments that cross the boundary get an interpolated point at the crossing.
    Points outside are dropped. Returns the clipped polyline (may be empty).
    """
    def _inside(x: float, z: float) -> bool:
        return x_min <= x <= x_max and z_min <= z <= z_max

    def _clip_seg(p0x: float, p0z: float, p1x: float, p1z: float):
        """Parametric clip; returns (t_enter, t_exit) for the visible portion, or None."""
        t0, t1 = 0.0, 1.0
        dx, dz = p1x - p0x, p1z - p0z
        for p, d, lo, hi in ((p0x, dx, x_min, x_max), (p0z, dz, z_min, z_max)):
            if abs(d) < 1e-10:
                if not (lo <= p <= hi):
                    return None
            else:
                ta, tb = (lo - p) / d, (hi - p) / d
                if ta > tb:
                    ta, tb = tb, ta
                t0, t1 = max(t0, ta), min(t1, tb)
                if t0 > t1 + 1e-10:
                    return None
        return t0, t1

    result: List[Tuple[float, float]] = []
    if not cl:
        return result

    x0, z0 = cl[0]
    if _inside(x0, z0):
        result.append((x0, z0))

    for i in range(1, len(cl)):
        px, pz = cl[i - 1]
        x, z = cl[i]
        in_p = _inside(px, pz)
        in_c = _inside(x, z)

        if not in_p or not in_c:
            ts = _clip_seg(px, pz, x, z)
            if ts is not None:
                t0, t1 = ts
                dx, dz = x - px, z - pz
                if not in_p and t0 > 1e-10:
                    result.append((px + t0 * dx, pz + t0 * dz))
                if not in_c and t1 < 1.0 - 1e-10:
                    result.append((px + t1 * dx, pz + t1 * dz))

        if in_c:
            result.append((x, z))

    return result


def anchor_centerline(
    cl: List[Tuple[float, float, float]],
    from_pt: Optional[Tuple[float, float, float]],
    to_pt: Optional[Tuple[float, float, float]],
) -> List[Tuple[float, float, float]]:
    """Trim centerline to intersection boundary points, dropping interior vertices."""
    if not cl:
        return cl
    if from_pt is None and to_pt is None:
        return cl

    n = len(cl)
    arc = [0.0] * n
    for i in range(1, n):
        dx = cl[i][0] - cl[i - 1][0]
        dy = cl[i][1] - cl[i - 1][1]
        dz = cl[i][2] - cl[i - 1][2]
        arc[i] = arc[i - 1] + math.sqrt(dx * dx + dy * dy + dz * dz)
    total = arc[-1]

    if from_pt is not None:
        dx = cl[0][0] - from_pt[0]; dy = cl[0][1] - from_pt[1]; dz = cl[0][2] - from_pt[2]
        start_arc = math.sqrt(dx * dx + dy * dy + dz * dz)
    else:
        start_arc = 0.0

    if to_pt is not None:
        dx = cl[-1][0] - to_pt[0]; dy = cl[-1][1] - to_pt[1]; dz = cl[-1][2] - to_pt[2]
        end_arc = total - math.sqrt(dx * dx + dy * dy + dz * dz)
    else:
        end_arc = total

    if end_arc - start_arc < 0.01:
        return cl

    result = [from_pt if from_pt is not None else cl[0]]
    for i in range(1, n - 1):
        if start_arc < arc[i] < end_arc:
            result.append(cl[i])
    result.append(to_pt if to_pt is not None else cl[-1])
    return result


def forward(cl: List[Tuple[float, float, float]], i: int) -> Tuple[float, float, float]:
    n = len(cl)
    if i == 0:
        dx = cl[1][0] - cl[0][0]; dy = cl[1][1] - cl[0][1]; dz = cl[1][2] - cl[0][2]
    elif i == n - 1:
        dx = cl[-1][0] - cl[-2][0]; dy = cl[-1][1] - cl[-2][1]; dz = cl[-1][2] - cl[-2][2]
    else:
        dx = cl[i + 1][0] - cl[i - 1][0]; dy = cl[i + 1][1] - cl[i - 1][1]; dz = cl[i + 1][2] - cl[i - 1][2]
    length = math.sqrt(dx * dx + dy * dy + dz * dz)
    if length < 1e-6:
        return (1.0, 0.0, 0.0)
    return (dx / length, dy / length, dz / length)


def cross_up(fwd: Tuple[float, float, float]) -> Tuple[float, float, float]:
    """cross(up=(0,1,0), fwd) — gives the right vector in XZ (y component is 0)."""
    # cross(up, fwd) = (up.y*fwd.z - up.z*fwd.y,  up.z*fwd.x - up.x*fwd.z,  up.x*fwd.y - up.y*fwd.x)
    #                = (1*fwd.z - 0,                0 - 0,                     0 - 1*fwd.x)
    #                = (fwd.z,                      0,                         -fwd.x)
    return (fwd[2], 0.0, -fwd[0])
