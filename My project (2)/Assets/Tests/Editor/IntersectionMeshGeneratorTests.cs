using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline;

namespace SFMap.Tests
{
    public class IntersectionMeshGeneratorTests
    {
        // 4-way cross: center node + four stub nodes (E/W/N/S), all 7m-wide residential.
        static StreetGraph FourWayCross()
        {
            var center = new StreetNode { OsmId = 0, WorldPosition = Vector3.zero, IsIntersection = true };
            var east   = new StreetNode { OsmId = 1, WorldPosition = new Vector3( 20f, 0f,   0f) };
            var west   = new StreetNode { OsmId = 2, WorldPosition = new Vector3(-20f, 0f,   0f) };
            var north  = new StreetNode { OsmId = 3, WorldPosition = new Vector3(  0f, 0f,  20f) };
            var south  = new StreetNode { OsmId = 4, WorldPosition = new Vector3(  0f, 0f, -20f) };

            var edges = new List<StreetEdge>
            {
                new StreetEdge { OsmWayId = 1, From = center, To = east,
                    Type = HighwayType.Residential, Centerline = new[] { Vector3.zero, east.WorldPosition  } },
                new StreetEdge { OsmWayId = 2, From = center, To = west,
                    Type = HighwayType.Residential, Centerline = new[] { Vector3.zero, west.WorldPosition  } },
                new StreetEdge { OsmWayId = 3, From = center, To = north,
                    Type = HighwayType.Residential, Centerline = new[] { Vector3.zero, north.WorldPosition } },
                new StreetEdge { OsmWayId = 4, From = center, To = south,
                    Type = HighwayType.Residential, Centerline = new[] { Vector3.zero, south.WorldPosition } },
            };

            return new StreetGraph
            {
                Nodes = new Dictionary<long, StreetNode> { {0,center},{1,east},{2,west},{3,north},{4,south} },
                Edges = edges,
                Buildings = new List<BuildingWay>(),
                SourceBounds = new OsmBounds(0, 1, 0, 1),
            };
        }

        [Test]
        public void ComputeSetbacks_FourWayCross_OneEntryPerEdge()
        {
            var setbacks = IntersectionMeshGenerator.ComputeSetbacks(FourWayCross());
            Assert.AreEqual(4, setbacks.Count);
        }

        [Test]
        public void ComputeSetbacks_FourWayCross_SetbackIsHalfWidth()
        {
            // At a 90° miter join with residential roads (halfW = 3.5m):
            //   miter is at (3.5, 3.5) → projection along each arm = 3.5m
            var setbacks = IntersectionMeshGenerator.ComputeSetbacks(FourWayCross());
            foreach (var kv in setbacks)
            {
                Assert.AreEqual(3.5f, kv.Value.from, 0.01f,
                    "Symmetric 90° join → setback equals halfWidth");
                Assert.AreEqual(0f, kv.Value.to,   0.01f,
                    "Far end has no intersection → toSetback is 0");
            }
        }

        [Test]
        public void ComputeSetbacks_NoIntersections_ReturnsEmpty()
        {
            var a = new StreetNode { OsmId = 1, WorldPosition = Vector3.zero,              IsIntersection = false };
            var b = new StreetNode { OsmId = 2, WorldPosition = new Vector3(10f, 0f, 0f), IsIntersection = false };
            var graph = new StreetGraph
            {
                Nodes = new Dictionary<long, StreetNode> { {1,a},{2,b} },
                Edges = new List<StreetEdge>
                {
                    new StreetEdge { OsmWayId = 1, From = a, To = b,
                        Type = HighwayType.Residential, Centerline = new[] { a.WorldPosition, b.WorldPosition } },
                },
                Buildings = new List<BuildingWay>(),
                SourceBounds = new OsmBounds(0, 1, 0, 1),
            };
            Assert.AreEqual(0, IntersectionMeshGenerator.ComputeSetbacks(graph).Count);
        }

        [Test]
        public void ComputeSetbacks_BothEndsAtIntersections_SetsBothFromAndTo()
        {
            // Edge whose From AND To are both 4-way-like intersections.
            var nodeA = new StreetNode { OsmId = 0, WorldPosition = Vector3.zero,               IsIntersection = true };
            var nodeB = new StreetNode { OsmId = 5, WorldPosition = new Vector3(30f, 0f, 0f),   IsIntersection = true };
            var stub1 = new StreetNode { OsmId = 1, WorldPosition = new Vector3(  0f, 0f,  20f) };
            var stub2 = new StreetNode { OsmId = 2, WorldPosition = new Vector3(  0f, 0f, -20f) };
            var stub3 = new StreetNode { OsmId = 3, WorldPosition = new Vector3( 30f, 0f,  20f) };
            var stub4 = new StreetNode { OsmId = 4, WorldPosition = new Vector3( 30f, 0f, -20f) };

            var shared = new StreetEdge
            {
                OsmWayId = 99, From = nodeA, To = nodeB, Type = HighwayType.Residential,
                Centerline = new[] { nodeA.WorldPosition, nodeB.WorldPosition },
            };
            var edges = new List<StreetEdge>
            {
                shared,
                new StreetEdge { OsmWayId = 1, From = nodeA, To = stub1, Type = HighwayType.Residential,
                    Centerline = new[] { nodeA.WorldPosition, stub1.WorldPosition } },
                new StreetEdge { OsmWayId = 2, From = nodeA, To = stub2, Type = HighwayType.Residential,
                    Centerline = new[] { nodeA.WorldPosition, stub2.WorldPosition } },
                new StreetEdge { OsmWayId = 3, From = nodeB, To = stub3, Type = HighwayType.Residential,
                    Centerline = new[] { nodeB.WorldPosition, stub3.WorldPosition } },
                new StreetEdge { OsmWayId = 4, From = nodeB, To = stub4, Type = HighwayType.Residential,
                    Centerline = new[] { nodeB.WorldPosition, stub4.WorldPosition } },
            };
            var graph = new StreetGraph
            {
                Nodes = new Dictionary<long, StreetNode> { {0,nodeA},{5,nodeB},{1,stub1},{2,stub2},{3,stub3},{4,stub4} },
                Edges = edges,
                Buildings = new List<BuildingWay>(),
                SourceBounds = new OsmBounds(0, 1, 0, 1),
            };

            var setbacks = IntersectionMeshGenerator.ComputeSetbacks(graph);
            Assert.IsTrue(setbacks.ContainsKey(shared));
            Assert.Greater(setbacks[shared].from, 0f, "From end should have setback");
            Assert.Greater(setbacks[shared].to,   0f, "To end should have setback");
        }
    }
}
