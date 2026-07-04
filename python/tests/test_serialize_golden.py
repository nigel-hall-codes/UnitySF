"""Golden-file round-trip test for the chunk ``.bin`` writer.

This is the safety net for the #417 refactor PRs: any change that alters the bytes
``serialize.write_chunk`` emits for a fixed input must either be intentional (regenerate
the golden) or it fails here. The byte layout is the contract the C# importer depends on,
specified normatively in ``docs/chunk-bin-format.md``.

Regenerate the golden fixture after an *intentional* format change (run from ``python/``):

    python -c "from tests.test_serialize_golden import write_golden; write_golden()"
"""
import numpy as np

from pathlib import Path

from sfmap.elevation import HeightmapData
from sfmap.types import ChunkData, MeshEntry, MeshType
from sfmap.serialize import write_chunk

GOLDEN = Path(__file__).parent / "fixtures" / "chunk_01_02.bin"


def build_fixture_chunk() -> ChunkData:
    """A tiny, fully-deterministic chunk exercising every field of the .bin layout.

    Kept small on purpose (3x3 heightmap, two meshes) so the golden file is a few
    hundred bytes and a diff is human-inspectable. It deliberately covers:
      - a signed/negative ``osm_id`` (int64),
      - a mesh with explicit normals AND a mesh with none (the zero-fill branch),
      - more than one mesh, and a non-square index count.
    """
    # Row-major 3x3 heightmap, normalised [0,1] float32 — arbitrary but fixed values.
    values = np.array(
        [[0.00, 0.25, 0.50],
         [0.10, 0.35, 0.60],
         [0.20, 0.45, 1.00]],
        dtype=np.float32,
    )
    hmap = HeightmapData(
        values=values,
        resolution=3,
        min_elevation_m=10.0,
        max_elevation_m=50.0,
        # World extent is not part of the .bin payload (write_chunk serialises only
        # values/resolution/min/max elevation) but the dataclass requires it.
        world_x_min=100.0,
        world_z_min=200.0,
        world_width=300.0,
        world_height=300.0,
    )

    road = MeshEntry(
        mesh_type=MeshType.ROAD,
        osm_id=42,
        vertices=[(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0)],
        normals=[(0.0, 1.0, 0.0), (0.0, 1.0, 0.0), (0.0, 1.0, 0.0)],
        uvs=[(0.0, 0.0), (1.0, 0.0), (0.0, 1.0)],
        indices=[0, 1, 2],
    )
    building = MeshEntry(
        mesh_type=MeshType.BUILDING,
        osm_id=-7,  # negative → exercises signed int64 encoding
        vertices=[(2.0, 0.0, 2.0), (3.0, 0.0, 2.0), (2.0, 1.0, 2.0), (3.0, 1.0, 2.0)],
        normals=[],  # empty → write_chunk fills vert_count*3 zeros
        uvs=[(0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)],
        indices=[0, 1, 2, 1, 3, 2],
    )

    return ChunkData(
        col=1,
        row=2,
        world_x=100.0,
        world_z=200.0,
        chunk_size_m=300.0,
        heightmap=hmap,
        meshes=[road, building],
    )


def write_golden() -> Path:
    """(Re)generate the committed golden fixture from ``build_fixture_chunk``."""
    GOLDEN.parent.mkdir(parents=True, exist_ok=True)
    # write_chunk names the file from col/row; move it onto the golden path.
    produced = write_chunk(build_fixture_chunk(), str(GOLDEN.parent))
    if produced != GOLDEN:
        produced.replace(GOLDEN)
    return GOLDEN


def test_golden_fixture_exists():
    assert GOLDEN.exists(), (
        f"missing golden fixture {GOLDEN}; regenerate with "
        "`python -c \"from tests.test_serialize_golden import write_golden; write_golden()\"`"
    )


def test_write_chunk_is_byte_identical_to_golden(tmp_path):
    produced = write_chunk(build_fixture_chunk(), str(tmp_path))
    assert produced.read_bytes() == GOLDEN.read_bytes(), (
        "write_chunk output diverged from the golden .bin. If this change is "
        "intentional, regenerate the golden and update docs/chunk-bin-format.md."
    )


def test_write_chunk_is_deterministic(tmp_path):
    a = write_chunk(build_fixture_chunk(), str(tmp_path / "a")).read_bytes()
    b = write_chunk(build_fixture_chunk(), str(tmp_path / "b")).read_bytes()
    assert a == b


def test_corrupting_one_header_byte_is_detected(tmp_path):
    """Sensitivity check: a single flipped header byte must break the comparison.

    Guards against a test that passes trivially (e.g. comparing a file to itself).
    Byte offset 8 is the first byte of ``chunk_col`` (after magic u32 + version u32).
    """
    good = write_chunk(build_fixture_chunk(), str(tmp_path)).read_bytes()
    corrupt = bytearray(good)
    corrupt[8] ^= 0xFF  # flip the low byte of chunk_col in the 40-byte header
    assert bytes(corrupt) != GOLDEN.read_bytes(), (
        "flipping a header byte did not change the bytes — the golden comparison "
        "would not catch a real format regression"
    )
