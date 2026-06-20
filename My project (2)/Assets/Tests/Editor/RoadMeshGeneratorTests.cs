using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using SFMap.Pipeline;

namespace SFMap.Tests
{
    public class RoadMeshGeneratorTests
    {
        // Use an out-of-range coord so tests don't collide with real generated assets.
        static readonly ChunkCoord TestCoord = new ChunkCoord(99, 99);

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
            // 200×200 world rect centred at origin, 9×9 heightmap, all cells at 0m (min=0, max=10).
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
                Values = new float[9, 9],   // all zeros → 0m elevation
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
            var meshes = RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Assert.AreEqual(1, meshes.Count);
        }

        [Test]
        public void Generate_FootwayEdge_IsSkipped()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f), HighwayType.Footway);
            var meshes = RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Assert.AreEqual(0, meshes.Count);
        }

        [Test]
        public void Generate_SingleSegment_CorrectVertexAndTriangleCounts()
        {
            // Centerline has 2 points → 4 verts (2 sides), 1 quad = 6 triangle indices.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Mesh m = meshes[0];
            Assert.AreEqual(4, m.vertexCount,        "2 centerline pts × 2 (left/right)");
            Assert.AreEqual(6, m.triangles.Length,   "1 quad = 2 tris = 6 indices");
        }

        [Test]
        public void Generate_MeshName_MatchesOsmWayId()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f), wayId: 42L);
            var meshes = RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Assert.AreEqual("road_42", meshes[0].name);
        }

        [Test]
        public void Generate_StampsHeightmapCellUnderRoad()
        {
            // Road: X=0→10, Z=0 (residential, width=7, halfW=3.5).
            // Cell [4,4] maps to worldX=0, worldZ=0 — on the road centreline.
            // Pre-set that cell to max elevation (1f normalised = 10m) then verify it's stamped flat.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(10f, 0f, 0f));
            hm.Values[4, 4] = 1f;   // 10m — should be overwritten

            RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);

            // Road surface elevation = 0m (from the flat 0m heightmap), so normalised = 0.
            Assert.AreEqual(0f, hm.Values[4, 4], 0.001f,
                "Cell under road centreline should be stamped to road elevation");
        }

        [Test]
        public void Generate_CellOutsideRoadFootprint_Unchanged()
        {
            // Cell [0,0] = worldX=-100, worldZ=-100, well outside the road footprint.
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(10f, 0f, 0f));
            hm.Values[0, 0] = 0.8f;

            RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);

            Assert.AreEqual(0.8f, hm.Values[0, 0], 0.001f,
                "Cell outside road footprint must not be modified");
        }

        [Test]
        public void Generate_UVs_SpanZeroToOneAlongLength()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Vector2[] uvs = meshes[0].uv;

            // verts: [0]=left-start, [1]=right-start, [2]=left-end, [3]=right-end
            Assert.AreEqual(0f, uvs[0].y, 0.001f, "Start V should be 0");
            Assert.AreEqual(0f, uvs[1].y, 0.001f, "Start V should be 0");
            Assert.AreEqual(1f, uvs[2].y, 0.001f, "End V should be 1");
            Assert.AreEqual(1f, uvs[3].y, 0.001f, "End V should be 1");

            Assert.AreEqual(0f, uvs[0].x, 0.001f, "Left U should be 0");
            Assert.AreEqual(1f, uvs[1].x, 0.001f, "Right U should be 1");
        }

        [Test]
        public void Generate_FlatRoad_NormalsPointUp()
        {
            var (graph, hm, rect) = FlatSetup(Vector3.zero, new Vector3(20f, 0f, 0f));
            var meshes = RoadMeshGenerator.Generate(graph, hm, rect, TestCoord);
            Vector3[] normals = meshes[0].normals;
            foreach (var n in normals)
                Assert.Greater(n.y, 0.9f, "Normals on a flat road should point up");
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

            var meshes = RoadMeshGenerator.Generate(graph, hm, worldRect, TestCoord);
            Assert.AreEqual(2, meshes.Count, "Footway should be excluded; 2 driveable edges → 2 meshes");
        }
    }
}
