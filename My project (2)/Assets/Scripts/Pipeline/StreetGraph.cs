using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SFMap.Pipeline
{
    public class StreetNode
    {
        public long OsmId;
        public Vector3 WorldPosition;
        public bool IsIntersection;
        public HighwayType? TrafficControl;
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

        public IEnumerable<StreetNode> IntersectionNodes =>
            Nodes.Values.Where(n => n.IsIntersection);
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
