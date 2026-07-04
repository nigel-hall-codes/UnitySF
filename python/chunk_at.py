"""chunk_at — resolve a landmark, street intersection, or lat/lon to the NxN chunk ring.

Given an existing bake manifest (for the grid anchor + chunk size) it projects the
point with the SAME projection the bake uses, finds the chunk it lands in, and prints
the surrounding ring as a JSON blob the /baker skill consumes to build `--only`.

Three ways to name the point (mutually exclusive):
  --landmark "Dolores Park"          gazetteer lookup (instant; see --gazetteer)
  --at 37.7596,-122.4269             literal decimal lat,lon (instant)
  --intersection "20th and Kansas"   street corner, resolved from the OSM (needs --osm)

  python -m chunk_at --manifest ../chunks_full/manifest.json --landmark "Dolores Park" --ring 3
  python -m chunk_at --manifest ../chunks_full/manifest.json --at 37.7596,-122.4269 --ring 5
  python -m chunk_at --manifest ../chunks_full/manifest.json --ring 3 \
      --intersection "20th and Kansas" --osm ../full_sf_map.osm

The manifest already records the grid origin and chunk size, so landmark/at modes never
touch the OSM. Intersection mode streams the OSM once (way names only) to find the shared
node of the two streets — authoritative and exact, but it pays the OSM scan (~tens of s on
the full city). Output (stdout) is a single JSON line.
"""
import argparse
import json
import re
import sys
from pathlib import Path

from sfmap.projection import OsmBounds, GeoOrigin, to_world_xz


def _norm(s: str) -> str:
    """Case- and spacing-insensitive key (also drops periods)."""
    return " ".join(s.lower().replace(".", "").split())


# Street-type suffixes stripped before matching, so user "20th"/"Kansas" match OSM
# way names "20th Street"/"Kansas Street". Keep both long and short forms.
_STREET_SUFFIXES = {
    "street", "st", "avenue", "ave", "av", "boulevard", "blvd", "drive", "dr",
    "road", "rd", "way", "lane", "ln", "place", "pl", "court", "ct", "terrace",
    "ter", "highway", "hwy", "alley", "aly", "circle", "cir", "plaza", "plz",
    "parkway", "pkwy", "row", "walk", "path", "steps", "bo",
}

# Connectors that separate the two streets in an --intersection string.
_INTERSECTION_SPLIT = re.compile(r"\s*(?:&|/|@|\+|\band\b)\s*", re.IGNORECASE)


def _street_key(name: str) -> str:
    """Normalise a street name and drop a trailing street-type word for matching."""
    toks = _norm(name).split()
    if len(toks) > 1 and toks[-1] in _STREET_SUFFIXES:
        toks = toks[:-1]
    return " ".join(toks)


def load_gazetteer(path: str) -> dict:
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    out = {}
    for name, v in raw.items():
        if name.startswith("_"):  # skip _comment and friends
            continue
        out[_norm(name)] = {"name": name, "lat": float(v["lat"]), "lon": float(v["lon"])}
    return out


def resolve_intersection(osm_path: str, street_a: str, street_b: str):
    """Stream the OSM once and return (lat, lon, node_id, n_shared) for the corner.

    Matches each street by its type-stripped key, collects the node refs each touches
    (with locations), and intersects the two sets. Returns the lowest shared node id
    for determinism; n_shared > 1 means the streets meet at more than one point.
    Raises ValueError with a diagnostic if there is no shared node.
    """
    import osmium  # lazy — only intersection mode needs the heavy dep

    key_a, key_b = _street_key(street_a), _street_key(street_b)

    class _H(osmium.SimpleHandler):
        def __init__(self):
            super().__init__()
            self.a = {}  # ref -> (lat, lon)
            self.b = {}

        def way(self, w):
            name = w.tags.get("name")
            if not name:
                return
            k = _street_key(name)
            is_a = k == key_a
            is_b = k == key_b
            if not (is_a or is_b):
                return
            for nd in w.nodes:
                if not nd.location.valid():
                    continue
                coord = (nd.location.lat, nd.location.lon)
                if is_a:
                    self.a[nd.ref] = coord
                if is_b:
                    self.b[nd.ref] = coord

    h = _H()
    h.apply_file(osm_path, locations=True)
    shared = sorted(set(h.a) & set(h.b))
    if not shared:
        raise ValueError(
            f"no shared node for '{street_a}' (matched {len(h.a)} nodes) and "
            f"'{street_b}' (matched {len(h.b)} nodes) — check the street names "
            f"or pass --at LAT,LON"
        )
    ref = shared[0]
    lat, lon = h.a[ref]
    return lat, lon, ref, len(shared)


def _fail(msg: str) -> int:
    print(json.dumps({"error": msg}))
    return 1


def main() -> int:
    p = argparse.ArgumentParser(prog="chunk_at")
    p.add_argument("--manifest", required=True, help="Path to an existing bake manifest.json")
    p.add_argument("--ring", type=int, default=3, help="Odd ring size NxN (default 3)")
    grp = p.add_mutually_exclusive_group(required=True)
    grp.add_argument("--landmark", metavar="NAME", help="Gazetteer name (see --gazetteer)")
    grp.add_argument("--at", metavar="LAT,LON", help="Explicit decimal lat,lon")
    grp.add_argument("--intersection", metavar='"A and B"', help="Street corner, e.g. '20th and Kansas'")
    p.add_argument("--osm", metavar="FILE", help="OSM file (required with --intersection)")
    p.add_argument("--gazetteer", default="landmarks.json", help="Landmark JSON (default landmarks.json)")
    args = p.parse_args()

    if args.ring < 1 or args.ring % 2 == 0:
        return _fail(f"--ring must be a positive odd number, got {args.ring}")

    try:
        m = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    except OSError as e:
        return _fail(f"cannot read manifest {args.manifest}: {e}")

    b = m["osmBounds"]
    origin = GeoOrigin.from_bounds(
        OsmBounds(b["minLat"], b["maxLat"], b["minLon"], b["maxLon"])
    )
    cs = float(m["chunkSize"])
    cx, cz = int(m["chunksX"]), int(m["chunksZ"])
    # chunk (0,0)'s world origin is the grid's SW anchor (base_x, base_z).
    try:
        c00 = next(c for c in m["chunks"] if c["col"] == 0 and c["row"] == 0)
    except StopIteration:
        return _fail("manifest has no chunk (0,0) to anchor the grid")
    base_x, base_z = c00["worldX"], c00["worldZ"]

    # --- resolve the point -------------------------------------------------
    extra = {}
    if args.at:
        try:
            lat_s, lon_s = args.at.split(",")
            lat, lon = float(lat_s), float(lon_s)
        except ValueError:
            return _fail(f"--at expects 'lat,lon', got '{args.at}'")
        label = f"{lat:.5f},{lon:.5f}"
        extra["via"] = "latlon"
    elif args.intersection:
        if not args.osm:
            return _fail("--intersection requires --osm FILE")
        parts = [s for s in _INTERSECTION_SPLIT.split(args.intersection.strip()) if s]
        if len(parts) != 2:
            return _fail(f"--intersection expects 'A and B' (one connector), got "
                         f"'{args.intersection}' -> {parts}")
        try:
            lat, lon, node_id, n_shared = resolve_intersection(args.osm, parts[0], parts[1])
        except (ValueError, OSError) as e:
            return _fail(str(e))
        label = f"{parts[0].strip()} & {parts[1].strip()}"
        extra = {"via": "intersection", "streets": [parts[0].strip(), parts[1].strip()],
                 "node": node_id, "ambiguous": n_shared > 1, "sharedNodes": n_shared}
    else:
        gaz = load_gazetteer(args.gazetteer)
        key = _norm(args.landmark)
        if key not in gaz:
            near = [g["name"] for k, g in gaz.items() if key in k or k in key]
            hint = f" Did you mean: {', '.join(near)}?" if near else \
                   f" Known: {', '.join(g['name'] for g in gaz.values())}."
            return _fail(f"landmark '{args.landmark}' not in gazetteer.{hint} "
                         f"Or pass --at LAT,LON / --intersection 'A and B'.")
        lat, lon, label = gaz[key]["lat"], gaz[key]["lon"], gaz[key]["name"]
        extra["via"] = "gazetteer"

    x, z = to_world_xz(lon, lat, origin)
    col = int((x - base_x) // cs)
    row = int((z - base_z) // cs)
    if not (0 <= col < cx and 0 <= row < cz):
        return _fail(f"{label} projects to chunk ({col},{row}) outside the {cx}x{cz} "
                     f"grid — point is beyond the baked extent")

    half = args.ring // 2
    only = [(c, r) for r in range(row - half, row + half + 1)
                   for c in range(col - half, col + half + 1)
                   if 0 <= c < cx and 0 <= r < cz]

    print(json.dumps({
        "label": label,
        "lat": lat, "lon": lon,
        "world": [round(x, 1), round(z, 1)],
        "center": [col, row],
        "ring": args.ring,
        "chunkSize": cs,
        "grid": [cx, cz],
        "only": [f"{c},{r}" for c, r in only],
        "count": len(only),
        "clamped": len(only) < args.ring * args.ring,
        **extra,
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
