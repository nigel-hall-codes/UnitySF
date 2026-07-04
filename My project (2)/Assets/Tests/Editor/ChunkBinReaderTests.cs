using System.IO;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline;

namespace SFMap.Tests
{
    /// <summary>
    /// Exercises ChunkBinReader against the SAME golden fixture the Python round-trip test
    /// writes (python/tests/fixtures/chunk_01_02.bin, built by test_serialize_golden.py).
    /// Both language sides pinning one fixture is the whole point (#426, #422): if the byte
    /// format ever drifts, exactly one of the two suites goes red first.
    /// </summary>
    public class ChunkBinReaderTests
    {
        // Repo layout: <root>/My project (2)/Assets  and  <root>/python/tests/fixtures/...
        static string GoldenFixturePath()
        {
            string repoRoot = Directory.GetParent(Application.dataPath).Parent.FullName;
            return Path.Combine(repoRoot, "python", "tests", "fixtures", "chunk_01_02.bin");
        }

        [Test]
        public void ReadsGoldenFixtureHeader()
        {
            var c = ChunkBinReader.Read(GoldenFixturePath());
            Assert.AreEqual(1, c.Col);
            Assert.AreEqual(2, c.Row);
            Assert.AreEqual(100f, c.WorldX, 1e-6f);
            Assert.AreEqual(200f, c.WorldZ, 1e-6f);
            Assert.AreEqual(300f, c.ChunkSizeM, 1e-6f);
            Assert.AreEqual(10f, c.MinElevM, 1e-6f);
            Assert.AreEqual(50f, c.MaxElevM, 1e-6f);
            Assert.AreEqual(3, c.HmapRes);
        }

        [Test]
        public void ReadsGoldenFixtureHeightmapRowMajor()
        {
            var c = ChunkBinReader.Read(GoldenFixturePath());
            // Same values (row-major) as build_fixture_chunk() in test_serialize_golden.py.
            Assert.AreEqual(0.00f, c.Heights[0, 0], 1e-6f);
            Assert.AreEqual(0.25f, c.Heights[0, 1], 1e-6f);
            Assert.AreEqual(0.50f, c.Heights[0, 2], 1e-6f);
            Assert.AreEqual(0.10f, c.Heights[1, 0], 1e-6f);
            Assert.AreEqual(0.35f, c.Heights[1, 1], 1e-6f);
            Assert.AreEqual(1.00f, c.Heights[2, 2], 1e-6f);
        }

        [Test]
        public void ReadsGoldenFixtureMeshes()
        {
            var c = ChunkBinReader.Read(GoldenFixturePath());
            Assert.AreEqual(2, c.Meshes.Count);

            var road = c.Meshes[0];
            Assert.AreEqual(ChunkMeshType.Road, road.Type);
            Assert.AreEqual(42L, road.OsmId);
            Assert.AreEqual(3, road.Vertices.Length);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), road.Vertices[1]);
            Assert.AreEqual(new Vector3(0f, 1f, 0f), road.Normals[0]);
            Assert.AreEqual(new Vector2(1f, 0f), road.Uvs[1]);
            Assert.AreEqual(new[] { 0, 1, 2 }, road.Indices);

            var bldg = c.Meshes[1];
            Assert.AreEqual(ChunkMeshType.Building, bldg.Type);
            Assert.AreEqual(-7L, bldg.OsmId);            // negative int64 round-trips
            Assert.AreEqual(4, bldg.Vertices.Length);
            Assert.AreEqual(Vector3.zero, bldg.Normals[0]); // empty Python normals -> zero-filled
            Assert.AreEqual(new[] { 0, 1, 2, 1, 3, 2 }, bldg.Indices);
        }

        [Test]
        public void FormatConstantsMatchSpec()
        {
            Assert.AreEqual(0x4B4E4843u, ChunkBinReader.Magic); // "CHNK"
            Assert.AreEqual(1u, ChunkBinReader.Version);
        }

        [Test]
        public void BadMagicThrows()
        {
            byte[] bad = new byte[64]; // all zeros -> magic 0 != CHNK
            using var reader = new BinaryReader(new MemoryStream(bad));
            Assert.Throws<InvalidDataException>(() => ChunkBinReader.Read(reader, "bad"));
        }
    }
}
