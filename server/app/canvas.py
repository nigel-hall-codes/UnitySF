"""Facade-canvas flattening — rasterise freehand strokes to one PNG (design #278).

The hybrid export rule (#278): a facade's freehand **stroke** layers flatten into a single
``paint_<osm_id>_<facade>.png`` (alpha), while placed images / AI signs stay discrete decals.
This module owns the flatten: a tiny pure-stdlib RGBA PNG encoder + a disc-stamp stroke
rasteriser (no Pillow), so the whole canvas → override path runs offline and in CI.
"""
from __future__ import annotations

import math
import struct
import zlib
from typing import List, Optional, Tuple

from .models import Stroke

_PAINT_SIZE = 256  # px of the flattened paint texture (square; the facade rect maps it to the wall)


def encode_rgba_png(width: int, height: int, pixels: bytes) -> bytes:
    """Encode an 8-bit RGBA PNG (colour type 6). ``pixels`` is row-major top→bottom RGBA."""
    def _chunk(tag: bytes, data: bytes) -> bytes:
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)   # bit depth 8, colour type 6 (RGBA)
    stride = width * 4
    raw = bytearray()
    for y in range(height):
        raw.append(0)                                   # filter byte 0 per scanline
        raw += pixels[y * stride:(y + 1) * stride]
    idat = zlib.compress(bytes(raw), 9)
    return sig + _chunk(b"IHDR", ihdr) + _chunk(b"IDAT", idat) + _chunk(b"IEND", b"")


def _hex_rgb(hexstr: str) -> Tuple[int, int, int]:
    s = (hexstr or "").lstrip("#")
    if len(s) == 3:
        s = "".join(ch * 2 for ch in s)
    if len(s) != 6:
        return (0, 0, 0)
    try:
        return int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16)
    except ValueError:
        return (0, 0, 0)


def _stamp_disc(buf: bytearray, size: int, cx: float, cy: float, radius: int,
                r: int, g: int, b: int) -> None:
    x0, x1 = max(0, int(cx - radius)), min(size - 1, int(cx + radius))
    y0, y1 = max(0, int(cy - radius)), min(size - 1, int(cy + radius))
    r2 = radius * radius
    for py in range(y0, y1 + 1):
        dy = py - cy
        row = py * size
        for px in range(x0, x1 + 1):
            dx = px - cx
            if dx * dx + dy * dy <= r2:
                i = (row + px) * 4
                buf[i], buf[i + 1], buf[i + 2], buf[i + 3] = r, g, b, 255


def rasterize_strokes(strokes: List[Stroke], size: int = _PAINT_SIZE) -> bytes:
    """Flatten freehand strokes into one RGBA PNG (transparent where unpainted).

    Stroke points are normalized facade UV: x left→right, y **bottom→top** (the canvas /
    facade convention), so y is flipped to PNG's top→bottom rows. Each stroke is drawn as
    overlapping discs of its (normalized) width along its polyline.
    """
    buf = bytearray(size * size * 4)   # zero-initialised → fully transparent
    for s in strokes:
        r, g, b = _hex_rgb(s.color)
        radius = max(1, round((s.width or 0.0) * size / 2))
        pts = s.points or []
        if len(pts) == 1:
            x, y = pts[0]
            _stamp_disc(buf, size, x * size, (1.0 - y) * size, radius, r, g, b)
            continue
        for i in range(len(pts) - 1):
            x0, y0 = pts[i]
            x1, y1 = pts[i + 1]
            px0, py0 = x0 * size, (1.0 - y0) * size
            px1, py1 = x1 * size, (1.0 - y1) * size
            steps = max(1, int(math.hypot(px1 - px0, py1 - py0)))
            for t in range(steps + 1):
                f = t / steps
                _stamp_disc(buf, size, px0 + (px1 - px0) * f, py0 + (py1 - py0) * f, radius, r, g, b)
    return encode_rgba_png(size, size, bytes(buf))


def flatten_paint(strokes: List[Stroke]) -> Optional[bytes]:
    """The flattened paint PNG for a facade's strokes, or None when there are no strokes."""
    if not strokes:
        return None
    return rasterize_strokes(strokes)
