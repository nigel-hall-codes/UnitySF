"""sfmap_bake — offline bake OSM + elevation data into per-chunk .bin files."""

import argparse
import sys


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


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    # Placeholder — pipeline stages will be wired here as their modules are implemented.
    print(f"sfmap_bake: osm={args.osm} elev={args.elev} preset={args.preset}")
    print(f"  chunks={args.chunks_x}x{args.chunks_z} size={args.chunk_size}m hmap-res={args.hmap_res}")
    print(f"  out={args.out} only={args.only} no-sidewalks={args.no_sidewalks}")
    print("(pipeline not yet implemented)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
