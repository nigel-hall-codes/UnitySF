"""sfmap_bake — offline bake OSM + elevation data into per-chunk .bin files."""

import argparse
import math
import sys
import time
from datetime import datetime, timezone


# Default heightfield source low-pass radius (σ, metres). Mirrors
# sfmap.elevation._HMAP_SMOOTH_SIGMA_M; kept here as a literal so build_parser()
# (and --help) need not import scipy via sfmap.elevation. See #233.
_DEFAULT_HMAP_SMOOTH_M = 2.0


def parse_chunk_pair(value: str):
    """Parse 'col,row' into a (int, int) tuple."""
    try:
        col, row = value.split(",")
        return int(col), int(row)
    except ValueError:
        raise argparse.ArgumentTypeError(f"Expected 'col,row', got '{value}'")


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="sfmap_bake",
        description="Bake OSM + elevation data into per-chunk binary files for Unity import.",
    )
    p.add_argument("--osm", required=True, metavar="FILE", help="Input .osm file")
    p.add_argument("--elev", required=True, metavar="FILE", help="Input elevation CSV")
    p.add_argument("--preset", default="default", metavar="NAME", help="Preset name (default: default)")
    p.add_argument("--chunk-size", type=float, default=300.0, metavar="METERS", help="Chunk size in world metres (default: 300)")
    p.add_argument("--chunks-x", type=int, default=None, metavar="N", help="Number of chunks along X axis (default: auto-fit to data extent)")
    p.add_argument("--chunks-z", type=int, default=None, metavar="N", help="Number of chunks along Z axis (default: auto-fit to data extent)")
    p.add_argument("--out", default="./chunks/", metavar="DIR", help="Output directory (default: ./chunks/)")
    p.add_argument("--only", nargs="+", type=parse_chunk_pair, metavar="col,row", help="Bake only the specified chunks (e.g. --only 0,0 1,0)")
    p.add_argument("--hmap-res", type=int, default=129, metavar="N", help="Heightmap resolution per chunk (default: 129)")
    p.add_argument("--hmap-smooth", type=float, default=_DEFAULT_HMAP_SMOOTH_M, metavar="METRES",
                   help="Source low-pass radius (Gaussian sigma, metres) suppressing triangle-edge "
                        f"creases in the heightfield; 0 disables (default: {_DEFAULT_HMAP_SMOOTH_M})")
    p.add_argument("--vertical-exaggeration", type=float, default=1.3, metavar="FACTOR",
                   help="Scale terrain relief by this factor to read as steep as real life "
                        "(1.0 = true elevation; default: 1.3)")
    p.add_argument("--no-sidewalks", action="store_true", help="Skip sidewalk geometry")
    p.add_argument("--parking", default=None, metavar="FILE",
                   help="SF parking-regulations CSV; places parked cars along regulated kerbs")
    p.add_argument("--neighborhoods", default=None, metavar="FILE",
                   help="SF Analysis Neighborhoods GeoJSON (e.g. data/sf_analysis_neighborhoods.geojson); "
                        "loaded as a point-in-polygon lookup for building neighborhood classification")
    p.add_argument("--no-parking-roads", default=None, metavar="FILE",
                   help="JSON list of street names that never allow parked cars (manual override; "
                        "freeways/trunks and OSM parking:*=no tags are excluded automatically)")
    p.add_argument("--no-parking-fallback", action="store_true",
                   help="With --parking, do NOT fill streets the CSV omits with sidewalk-placed "
                        "cars (default: fill them)")
    return p


def _chunk_list(only, chunks_x: int, chunks_z: int) -> list:
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


def _geometry_extent(graph):
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


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    # Imported here so --help works without the heavy scientific deps installed.
    from sfmap import osm, elevation, serialize
    from sfmap.chunk import bake_chunk
    from sfmap.geometry import intersection, parking

    t_start = time.perf_counter()
    print(f"[sfmap_bake] preset='{args.preset}'  osm={args.osm}  elev={args.elev}")

    # --- Parse inputs once -------------------------------------------------
    full_graph = osm.parse(args.osm)
    print(f"[sfmap_bake] parsed graph: {len(full_graph.nodes)} nodes, "
          f"{len(full_graph.edges)} edges, {len(full_graph.buildings)} buildings")

    full_hmap = elevation.parse(args.elev, full_graph.source_bounds, full_graph.origin, args.hmap_res,
                                smooth_sigma_m=args.hmap_smooth)
    elevation.apply_vertical_exaggeration(full_hmap, args.vertical_exaggeration)
    print(f"[sfmap_bake] heightmap {full_hmap.resolution}² "
          f"elev[{full_hmap.min_elevation_m:.1f}, {full_hmap.max_elevation_m:.1f}]m "
          f"(×{args.vertical_exaggeration:g} vertical exaggeration)")

    # --- Intersection polygons + road boundaries (once on the full graph) --
    polygons = intersection.compute_polygons(full_graph)
    boundaries = intersection.compute_boundaries(full_graph, polygons)

    # --- Parking regulations (optional) — project kerb features once -------
    parking_segments = None
    parking_fallback = False
    if args.parking:
        parking_segments = parking.parse_parking_csv(args.parking, full_graph.origin)
        parking_fallback = not args.no_parking_fallback
        print(f"[sfmap_bake] parsed parking: {len(parking_segments)} kerb segment(s) "
              f"from {args.parking}"
              f"{' (+ sidewalk fallback for uncovered streets)' if parking_fallback else ''}")

    # --- Neighborhood boundaries (optional) — project polygons once --------
    # Loads the lookup so a building's neighborhood can be resolved from its
    # centroid. The sidecar that consumes it lands with #266; here we just prove
    # the input loads (acceptance: "sfmap_bake can load it via --neighborhoods").
    neighborhoods = None
    if args.neighborhoods:
        from sfmap.geometry import neighborhood
        neighborhoods = neighborhood.load_neighborhoods(args.neighborhoods, full_graph.origin)
        print(f"[sfmap_bake] parsed neighborhoods: {len(neighborhoods)} polygon(s) "
              f"from {args.neighborhoods}")

    # Manual no-parking road list (optional) — OSM already excludes freeways/trunks
    # and explicitly-tagged kerbs; this covers roads we know locally that OSM hasn't.
    no_parking_roads = None
    if args.no_parking_roads:
        no_parking_roads = parking.load_no_parking_roads(args.no_parking_roads)
        print(f"[sfmap_bake] no-parking roads: {len(no_parking_roads)} street name(s) "
              f"from {args.no_parking_roads}")

    # --- Anchor the chunk grid at the data's SW corner ---------------------
    # The projection centres world coords on the OSM bounds, so geometry straddles
    # the origin into negative XZ. Anchoring the grid at (0,0) would drop everything
    # left of / below the origin; anchor it at the real geometry min instead.
    min_x, min_z, max_x, max_z = _geometry_extent(full_graph)
    base_x, base_z = min_x, min_z
    chunks_x = args.chunks_x if args.chunks_x is not None \
        else max(1, math.ceil((max_x - base_x) / args.chunk_size))
    chunks_z = args.chunks_z if args.chunks_z is not None \
        else max(1, math.ceil((max_z - base_z) / args.chunk_size))
    print(f"[sfmap_bake] geometry extent x[{min_x:.1f}, {max_x:.1f}] "
          f"z[{min_z:.1f}, {max_z:.1f}] -> grid {chunks_x}x{chunks_z} "
          f"@ {args.chunk_size:.0f}m anchored at ({base_x:.1f}, {base_z:.1f})")

    # --- Per-chunk bake ----------------------------------------------------
    chunks = _chunk_list(args.only, chunks_x, chunks_z)
    include_sidewalks = not args.no_sidewalks
    chunk_origins = []
    for i, (col, row) in enumerate(chunks):
        t_chunk = time.perf_counter()
        chunk = bake_chunk(
            col, row, full_graph, full_hmap, polygons, boundaries,
            args.chunk_size, args.hmap_res, include_sidewalks,
            base_x=base_x, base_z=base_z,
            parking_segments=parking_segments,
            parking_fallback=parking_fallback,
            no_parking_roads=no_parking_roads,
        )
        serialize.write_chunk(chunk, args.out)
        serialize.write_road_names(chunk, args.out)
        serialize.write_parked_cars(chunk, args.out)
        chunk_origins.append((col, row, chunk.world_x, chunk.world_z))
        print(f"[sfmap_bake] chunk {i + 1}/{len(chunks)} ({col},{row}): "
              f"{len(chunk.meshes)} meshes — {time.perf_counter() - t_chunk:.2f}s")

    # --- Manifest ----------------------------------------------------------
    bounds = full_graph.source_bounds
    serialize.write_manifest(
        preset=args.preset,
        chunk_size_m=args.chunk_size,
        chunks_x=chunks_x,
        chunks_z=chunks_z,
        osm_file=args.osm,
        osm_bounds_dict={
            "minLat": bounds.min_lat, "maxLat": bounds.max_lat,
            "minLon": bounds.min_lon, "maxLon": bounds.max_lon,
        },
        hmap_resolution=args.hmap_res,
        min_elevation_m=full_hmap.min_elevation_m,
        max_elevation_m=full_hmap.max_elevation_m,
        chunk_origins=chunk_origins,
        out_dir=args.out,
        generated_iso=datetime.now(timezone.utc).isoformat(timespec="seconds"),
    )

    print(f"[sfmap_bake] wrote {len(chunk_origins)} chunk(s) + manifest.json to {args.out} "
          f"— {time.perf_counter() - t_start:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
