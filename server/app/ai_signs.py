"""AI sign generation — provider abstraction (design #266 §AI Sign Generator, §5).

The server mediates all AI sign generation: the iPad never calls a provider directly
(PDF constraint). A provider takes a :class:`SignRequest` and returns ``(png, thumb)``
bytes; concrete backends (ChatGPT image gen, Nano Banana, future) live behind the
:class:`SignProvider` interface and are selected by name from a registry, so the backend
is swappable without touching the endpoint, the stored asset, or the export format.

Ships one built-in provider, ``local-stub``, that synthesises a deterministic
placeholder PNG with **no external calls or dependencies** (a tiny pure-stdlib PNG
encoder) — enough to exercise the whole store-and-export path offline and in tests.
"""
from __future__ import annotations

import hashlib
import re
import struct
import zlib
from typing import Callable, Dict, Tuple

from .models import SignRequest

# (png_bytes, thumb_png_bytes)
SignBytes = Tuple[bytes, bytes]


# ---------------------------------------------------------------------------
# Minimal PNG encoder (no Pillow dependency)
# ---------------------------------------------------------------------------

def encode_solid_png(width: int, height: int, rgb: Tuple[int, int, int]) -> bytes:
    """Encode a solid-colour 8-bit RGB PNG. Small, deterministic, dependency-free."""
    width = max(1, width)
    height = max(1, height)

    def _chunk(tag: bytes, data: bytes) -> bytes:
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)  # 8-bit, colour type 2 (RGB)
    row = b"\x00" + bytes(rgb) * width                            # filter byte 0 + pixels
    idat = zlib.compress(row * height, 9)
    return sig + _chunk(b"IHDR", ihdr) + _chunk(b"IDAT", idat) + _chunk(b"IEND", b"")


# ---------------------------------------------------------------------------
# Provider interface + registry
# ---------------------------------------------------------------------------

class SignProvider:
    """A sign-image backend. Concrete providers subclass and implement ``generate``."""

    name: str = "base"

    def generate(self, req: SignRequest) -> SignBytes:  # pragma: no cover - abstract
        raise NotImplementedError


_REGISTRY: Dict[str, Callable[[], SignProvider]] = {}


def register_provider(name: str, factory: Callable[[], SignProvider]) -> None:
    _REGISTRY[name] = factory


def available_providers() -> list[str]:
    return sorted(_REGISTRY)


def get_provider(name: str) -> SignProvider:
    """Instantiate the registered provider ``name`` (raises KeyError if unknown)."""
    if name not in _REGISTRY:
        raise KeyError(name)
    return _REGISTRY[name]()


def _aspect_to_size(aspect: str, base_h: int = 64) -> Tuple[int, int]:
    """Map an "W:H" aspect string to a (width, height) in pixels; defaults to 1:1."""
    m = re.match(r"^\s*(\d+(?:\.\d+)?)\s*:\s*(\d+(?:\.\d+)?)\s*$", aspect or "")
    if not m:
        return base_h, base_h
    w, h = float(m.group(1)), float(m.group(2))
    if w <= 0 or h <= 0:
        return base_h, base_h
    return max(1, round(base_h * w / h)), base_h


def _color_for(req: SignRequest) -> Tuple[int, int, int]:
    """Deterministic colour from the request, so the same request always renders the same."""
    seed = hashlib.sha256(
        f"{req.stylePreset}|{req.neighborhood}|{req.businessType}|{req.text}".encode("utf-8")
    ).digest()
    return seed[0], seed[1], seed[2]


class LocalStubProvider(SignProvider):
    """Offline placeholder backend: a deterministic solid-colour PNG + a small thumbnail.

    No network, no API key — stands in for a real image model so the storage/export path
    works end to end. The colour and size are a pure function of the request.
    """

    name = "local-stub"

    def generate(self, req: SignRequest) -> SignBytes:
        w, h = _aspect_to_size(req.aspectRatio)
        rgb = _color_for(req)
        png = encode_solid_png(w, h, rgb)
        # Thumbnail: a fixed-height shrink of the same colour/aspect.
        tw, th = _aspect_to_size(req.aspectRatio, base_h=16)
        thumb = encode_solid_png(tw, th, rgb)
        return png, thumb


register_provider(LocalStubProvider.name, LocalStubProvider)

# The default provider when none is configured.
DEFAULT_PROVIDER = LocalStubProvider.name


def slug(value: str) -> str:
    """Lowercase, keep [a-z0-9], collapse the rest to single underscores."""
    s = re.sub(r"[^a-z0-9]+", "_", (value or "").lower()).strip("_")
    return s or "x"
