"""sfmap_bake — offline bake OSM + elevation data into per-chunk .bin files."""

import argparse
import sys
import time
from datetime import datetime, timezone


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
    p.add_argument("--chunk-size", type=float, default=1964.0, metavar="METERS", help="Chunk size in world metres (default: 1964)")
    p.add_argument("--chunks-x", type=int, default=2, metavar="N", help="Number of chunks along X axis (default: 2)")
    p.add_argument("--chunks-z", type=int, default=2, metavar="N", help="Number of chunks along Z axis (default: 2)")
    p.add_argument("--out", default="./chunks/", metavar="DIR", help="Output directory (default: ./chunks/)")
    p.add_argument("--only", nargs="+", type=parse_chunk_pair, metavar="col,row", help="Bake only the specified chunks (e.g. --only 0,0 1,0)")
    p.add_argument("--hmap-res", type=int, default=513, metavar="N", help="Heightmap resolution per chunk (default: 513)")
    p.add_argument("--no-sidewalks", action="store_true", help="Skip sidewalk geometry")
    return p


def _chunk_list(args) -> list:
    """Resolve the (col, row) chunks to bake from --chunks-x/-z and --only."""
    if args.only:
        # Explicit set — bake exactly these, de-duplicated, in stable order.
        seen = set()
        out = []
        for cr in args.only:
            if cr not in seen:
                seen.add(cr)
                out.append(cr)
        return out
    return [(col, row) for row in range(args.chunks_z) for col in range(args.chunks_x)]


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    # Imported here so --help works without the heavy scientific deps installed.
    from sfmap import osm, elevation, serialize
    from sfmap.chunk import bake_chunk
    from sfmap.geometry import intersection

    t_start = time.perf_counter()
    print(f"[sfmap_bake] preset='{args.preset}'  osm={args.osm}  elev={args.elev}")

    # --- Parse inputs once -------------------------------------------------
    full_graph = osm.parse(args.osm)
    print(f"[sfmap_bake] parsed graph: {len(full_graph.nodes)} nodes, "
          f"{len(full_graph.edges)} edges, {len(full_graph.buildings)} buildings")

    full_hmap = elevation.parse(args.elev, full_graph.source_bounds, full_graph.origin, args.hmap_res)
    print(f"[sfmap_bake] heightmap {full_hmap.resolution}² "
          f"elev[{full_hmap.min_elevation_m:.1f}, {full_hmap.max_elevation_m:.1f}]m")

    # --- Intersection polygons + road boundaries (once on the full graph) --
    polygons = intersection.compute_polygons(full_graph)
    boundaries = intersection.compute_boundaries(full_graph, polygons)

    # --- Per-chunk bake ----------------------------------------------------
    chunks = _chunk_list(args)
    include_sidewalks = not args.no_sidewalks
    chunk_origins = []
    for i, (col, row) in enumerate(chunks):
        t_chunk = time.perf_counter()
        chunk = bake_chunk(
            col, row, full_graph, full_hmap, polygons, boundaries,
            args.chunk_size, args.hmap_res, include_sidewalks,
        )
        serialize.write_chunk(chunk, args.out)
        chunk_origins.append((col, row, chunk.world_x, chunk.world_z))
        print(f"[sfmap_bake] chunk {i + 1}/{len(chunks)} ({col},{row}): "
              f"{len(chunk.meshes)} meshes — {time.perf_counter() - t_chunk:.2f}s")

    # --- Manifest ----------------------------------------------------------
    bounds = full_graph.source_bounds
    serialize.write_manifest(
        preset=args.preset,
        chunk_size_m=args.chunk_size,
        chunks_x=args.chunks_x,
        chunks_z=args.chunks_z,
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
