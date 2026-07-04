"""heightcache_guard — keep the elevation .heightcache in sync with the smoothing pipeline.

The cache (``<csv>.r<res>.heightcache``) is keyed only by CSV path + resolution. It is NOT
keyed by the ``--hmap-smooth`` value or the elevation code that produced it, so a pipeline
change (e.g. a new smoothing default from a pulled commit) is silently ignored on the next
bake — ``elevation.parse`` loads the stale cache and returns before re-smoothing. See the
note in ``sfmap/elevation.parse``: "changing this value requires re-baking / clear_cache".

This guard fingerprints the two inputs the cache loader does NOT re-check — the effective
``--hmap-smooth`` value and the contents of ``sfmap/elevation.py`` — and records it in a
sidecar under ``python/.heightcache_fpr/`` (kept out of Assets/ so Unity won't import it).

  check:  before a bake — clear the cache when the fingerprint changed, there is no sidecar
          (unknown provenance), or --force is given. Prints a JSON decision line.
  commit: after a successful bake — record the current fingerprint so an unchanged pipeline
          reuses the cache next time (fast).

  python -m heightcache_guard check  --csv <csv> --res 129 [--hmap-smooth X] [--force]
  python -m heightcache_guard commit --csv <csv> --res 129 [--hmap-smooth X]

Bounds/resolution mismatches (e.g. map.osm vs full_sf_map) are already handled by the cache
loader itself, so they are deliberately NOT part of this fingerprint.
"""
import argparse
import hashlib
import json
import sys
from pathlib import Path

_HERE = Path(__file__).resolve().parent
_ELEV_SRC = _HERE / "sfmap" / "elevation.py"
_FPR_DIR = _HERE / ".heightcache_fpr"


def _effective_smooth(explicit):
    """The --hmap-smooth value the bake will actually use (explicit, else CLI default)."""
    if explicit is not None:
        return float(explicit)
    # Public single source of truth (scipy-free), shared with the bake CLI (#425).
    from sfmap.config import HMAP_SMOOTH_SIGMA_M
    return float(HMAP_SMOOTH_SIGMA_M)


def _fingerprint(smooth: float) -> str:
    h = hashlib.sha256()
    h.update(f"smooth={smooth:.6g}\n".encode("utf-8"))
    h.update(_ELEV_SRC.read_bytes())
    return h.hexdigest()[:16]


def _cache_path(csv: str, res: int) -> Path:
    return Path(f"{csv}.r{res}.heightcache")


def _fpr_path(csv: str, res: int) -> Path:
    # Key by basename+res; the cache content is bound to one CSV, and basenames are unique here.
    return _FPR_DIR / f"{Path(csv).name}.r{res}.fpr"


def main() -> int:
    p = argparse.ArgumentParser(prog="heightcache_guard")
    p.add_argument("phase", choices=["check", "commit"])
    p.add_argument("--csv", required=True, help="Elevation CSV path (same as bake --elev)")
    p.add_argument("--res", type=int, required=True, help="Heightmap resolution (same as --hmap-res)")
    p.add_argument("--hmap-smooth", type=float, default=None,
                   help="Effective --hmap-smooth; omit to use the bake's current default")
    p.add_argument("--force", action="store_true", help="(check) clear the cache regardless")
    a = p.parse_args()

    smooth = _effective_smooth(a.hmap_smooth)
    fp = _fingerprint(smooth)
    cache = _cache_path(a.csv, a.res)
    fpr = _fpr_path(a.csv, a.res)

    if a.phase == "commit":
        _FPR_DIR.mkdir(parents=True, exist_ok=True)
        fpr.write_text(fp, encoding="utf-8")
        print(json.dumps({"phase": "commit", "fingerprint": fp, "smooth": smooth,
                          "sidecar": str(fpr)}))
        return 0

    # phase == check
    existed = cache.exists()
    stored = fpr.read_text(encoding="utf-8").strip() if fpr.exists() else None

    if not existed:
        action, reason = "absent", "no cache present; bake will interpolate"
    elif a.force:
        cache.unlink(); action, reason = "cleared", "forced (--fresh)"
    elif stored is None:
        cache.unlink(); action, reason = "cleared", "no fingerprint sidecar — provenance unknown, re-interp to be safe"
    elif stored != fp:
        cache.unlink(); action, reason = "cleared", f"smoothing pipeline changed ({stored} -> {fp})"
    else:
        action, reason = "kept", "fingerprint matches — cache is fresh"

    print(json.dumps({"phase": "check", "cache": str(cache), "existed": existed,
                      "action": action, "reason": reason, "fingerprint": fp, "smooth": smooth}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
