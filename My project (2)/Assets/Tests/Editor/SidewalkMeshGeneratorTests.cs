using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using SFMap.Pipeline;

namespace SFMap.Tests
{
    public class SidewalkMeshGeneratorTests
    {
        static readonly ChunkCoord TestCoord = new ChunkCoord(98, 98);

        [TearDown]
        public void Cleanup()
        {
#if UNITY_EDITOR
            string testDir = GeneratedAssets.ChunkDir(TestCoord);
            if (AssetDatabase.IsValidFolder(testDir))
                AssetDatabase.DeleteAsset(testDir);
#endif
        }

        // ---- helpers ----

        static (StreetGraph graph, HeightmapData heightmap, Rect worldRect) FlatSetup(
            Vector3 from, Vector3 to, HighwayType type = HighwayType.Residential, long wayId = 1L)
        {
            var nodeA = new StreetNode { OsmId = 1, WorldPosition = from };
            var nodeB = new StreetNode { OsmId = 2, WorldPosition = to };
            var edge = new StreetEdge
            {
                OsmWayId = wayId,
                From = nodeA,
                To = nodeB,
                Type = type,
                Centerline = new[] { from, to },
            };
            var graph = new StreetGraph
            {
                Nodes = new Dictionary<long, StreetNode> { { 1, nodeA }, { 2, nodeB } },
                Edges = new List<StreetEdge> { edge },
                Buildings = new List<BuildingWay>(),
                SourceBounds = new OsmBounds(0, 1, 0, 1),
            };
            var heightmap = new HeightmapData
            {
                Values = new float[9, 9],
                Resolution = 9,
                MinElevationMeters = 0f,
                MaxElevationMeters = 10f,
            };
            var worldRect = new Rect(-100f, -100f, 200f, 200f);
            return (graph, heightmap, worldRect);
        }

        // ---- tests ----

        [Test]
        public void Generate_SingleResidentialEdge_ReturnsOneMesh()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Assert.AreEqual(1, meshes.Count);
        }

        [Test]
        public void Generate_FootwayEdge_IsSkipped()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f), HighwayType.Footway);
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Assert.AreEqual(0, meshes.Count);
        }

        [Test]
        public void Generate_SingleSegment_CorrectVertexAndTriangleCounts()
        {
            // 2-point centerline → 2 cross-sections × 4 verts = 8 verts.
            // 1 segment × 2 strips × 2 triangles × 3 indices = 12 triangle indices.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Mesh m = meshes[0];
            Assert.AreEqual(8,  m.vertexCount,      "2 pts × 4 verts per cross-section");
            Assert.AreEqual(12, m.triangles.Length,  "2 strips × 1 quad × 2 tris × 3 indices");
        }

        [Test]
        public void Generate_MeshName_MatchesOsmWayId()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f), wayId: 77L);
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Assert.AreEqual("sidewalk_77", meshes[0].name);
        }

        [Test]
        public void Generate_SidewalkVertices_OffsetFromRoadEdge()
        {
            // Road going +Z, Residential width=7, halfW=3.5, sidewalk outer offset=3.5+1.5=5.
            // Vert 0 (left-outer at start): x should be -(halfW + 1.5) = -5.
            // Vert 1 (left-inner at start): x should be -halfW = -3.5.
            // Vert 2 (right-inner at start): x should be +halfW = +3.5.
            // Vert 3 (right-outer at start): x should be +(halfW + 1.5) = +5.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(0f, 0f, 20f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Vector3[] verts = meshes[0].vertices;

            Assert.AreEqual(-5f,  verts[0].x, 0.001f, "left-outer x");
            Assert.AreEqual(-3.5f, verts[1].x, 0.001f, "left-inner x");
            Assert.AreEqual( 3.5f, verts[2].x, 0.001f, "right-inner x");
            Assert.AreEqual( 5f,   verts[3].x, 0.001f, "right-outer x");
        }

        [Test]
        public void Generate_SidewalkVertices_RaisedAboveRoad()
        {
            // Flat 0m terrain → road elevation = 0m → sidewalk Y = 0.05m.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            foreach (var v in meshes[0].vertices)
                Assert.AreEqual(0.05f, v.y, 0.001f, $"All verts should be 0.05m above flat terrain");
        }

        [Test]
        public void Generate_FlatRoad_NormalsPointUp()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            foreach (var n in meshes[0].normals)
                Assert.Greater(n.y, 0.9f, "Normals on flat sidewalk should point up");
        }

        [Test]
        public void Generate_MultipleEdges_ReturnsMeshPerNonFootway()
        {
            var nodeA = new StreetNode { OsmId = 1, WorldPosition = Vector3.zero };
            var nodeB = new StreetNode { OsmId = 2, WorldPosition = new Vector3(10f, 0f, 0f) };
            var nodeC = new StreetNode { OsmId = 3, WorldPosition = new Vector3(0f,  0f, 10f) };

            var graph = new StreetGraph
            {
                Nodes = new Dictionary<long, StreetNode> { { 1, nodeA }, { 2, nodeB }, { 3, nodeC } },
                Edges = new List<StreetEdge>
                {
                    new StreetEdge { OsmWayId = 1, From = nodeA, To = nodeB, Type = HighwayType.Residential,
                                     Centerline = new[] { nodeA.WorldPosition, nodeB.WorldPosition } },
                    new StreetEdge { OsmWayId = 2, From = nodeA, To = nodeC, Type = HighwayType.Footway,
                                     Centerline = new[] { nodeA.WorldPosition, nodeC.WorldPosition } },
                    new StreetEdge { OsmWayId = 3, From = nodeB, To = nodeC, Type = HighwayType.Primary,
                                     Centerline = new[] { nodeB.WorldPosition, nodeC.WorldPosition } },
                },
                Buildings = new List<BuildingWay>(),
                SourceBounds = new OsmBounds(0, 1, 0, 1),
            };
            var hm = new HeightmapData
            {
                Values = new float[9, 9], Resolution = 9,
                MinElevationMeters = 0f, MaxElevationMeters = 10f,
            };
            var worldRect = new Rect(-100f, -100f, 200f, 200f);

            var meshes = SidewalkMeshGenerator.Generate(graph, hm, worldRect, TestCoord);
            Assert.AreEqual(2, meshes.Count, "Footway excluded; 2 driveable edges → 2 meshes");
        }

        [Test]
        public void Generate_UVs_OuterEdgeHasUOne_InnerEdgeHasUZero()
        {
            // Verts per cross-section: [0]=left-outer U=1, [1]=left-inner U=0,
            //                          [2]=right-inner U=0, [3]=right-outer U=1.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Vector2[] uvs = meshes[0].uv;

            Assert.AreEqual(1f, uvs[0].x, 0.001f, "left-outer U=1");
            Assert.AreEqual(0f, uvs[1].x, 0.001f, "left-inner U=0");
            Assert.AreEqual(0f, uvs[2].x, 0.001f, "right-inner U=0");
            Assert.AreEqual(1f, uvs[3].x, 0.001f, "right-outer U=1");
        }

        [Test]
        public void Generate_UVs_VSpansZeroToOneAlongLength()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = SidewalkMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Vector2[] uvs = meshes[0].uv;

            // First cross-section (verts 0..3) → V=0
            for (int vi = 0; vi < 4; vi++)
                Assert.AreEqual(0f, uvs[vi].y, 0.001f, $"vert {vi} V should be 0 at start");
            // Last cross-section (verts 4..7) → V=1
            for (int vi = 4; vi < 8; vi++)
                Assert.AreEqual(1f, uvs[vi].y, 0.001f, $"vert {vi} V should be 1 at end");
        }
    }
}
