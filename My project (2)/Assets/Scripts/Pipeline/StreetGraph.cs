using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SFMap.Pipeline
{
    public enum IntersectionType { StopSign, TrafficSignals }

    public class StreetNode
    {
        public long OsmId;
        public Vector3 WorldPosition;
        public bool IsIntersection;
        public IntersectionType? TrafficControl;
    }

    public class StreetEdge
    {
        public long OsmWayId;
        public StreetNode From;
        public StreetNode To;
        public HighwayType Type;
        public bool IsOneWay;
        public Vector3[] Centerline;
        public float Width => Type.RoadWidth();
    }

    public class BuildingWay
    {
        public long OsmId;
        public Vector3[] Footprint;
        public float Height;
    }

    public class StreetGraph
    {
        public OsmBounds SourceBounds;
        public IReadOnlyDictionary<long, StreetNode> Nodes;
        public IReadOnlyList<StreetEdge> Edges;
        public IReadOnlyList<BuildingWay> Buildings;
        public IReadOnlyDictionary<StreetNode, IReadOnlyList<StreetEdge>> Adjacency;

        public IEnumerable<StreetNode> IntersectionNodes =>
            Nodes.Values.Where(n => n.IsIntersection);

        // Returns a new StreetGraph containing only elements whose centroid falls inside chunk.WorldRect.
        public StreetGraph CropToChunk(ChunkBounds chunk)
        {
            var rect = chunk.WorldRect;

            var keptEdges = new List<StreetEdge>();
            foreach (var e in Edges)
            {
                var c = Centroid(e.Centerline);
                if (rect.Contains(new Vector2(c.x, c.z)))
                    keptEdges.Add(e);
            }

            var keptNodeIds = new HashSet<long>();
            foreach (var e in keptEdges)
            {
                keptNodeIds.Add(e.From.OsmId);
                keptNodeIds.Add(e.To.OsmId);
            }
            foreach (var n in Nodes.Values)
            {
                if (n.IsIntersection && rect.Contains(new Vector2(n.WorldPosition.x, n.WorldPosition.z)))
                    keptNodeIds.Add(n.OsmId);
            }

            var keptNodes = new Dictionary<long, StreetNode>(keptNodeIds.Count);
            foreach (var id in keptNodeIds)
                if (Nodes.TryGetValue(id, out var n))
                    keptNodes[id] = n;

            var keptBuildings = new List<BuildingWay>();
            foreach (var b in Buildings)
            {
                var c = Centroid(b.Footprint);
                if (rect.Contains(new Vector2(c.x, c.z)))
                    keptBuildings.Add(b);
            }

            return new StreetGraph
            {
                SourceBounds = SourceBounds,
                Nodes        = keptNodes,
                Edges        = keptEdges,
                Buildings    = keptBuildings,
            };
        }

        static Vector3 Centroid(Vector3[] pts)
        {
            var sum = Vector3.zero;
            foreach (var p in pts) sum += p;
            return sum / pts.Length;
        }
    }

    public enum HighwayType
    {
        Residential,
        Primary,
        Secondary,
        Tertiary,
        Service,
        Footway,
        Unclassified,
    }

    public static class HighwayTypeExtensions
    {
        public static float RoadWidth(this HighwayType t) => t switch
        {
            HighwayType.Primary      => 10f,
            HighwayType.Secondary    => 9f,
            HighwayType.Tertiary     => 8f,
            HighwayType.Residential  => 7f,
            HighwayType.Service      => 4f,
            HighwayType.Unclassified => 6f,
            _                        => 0f,
        };
    }
}
