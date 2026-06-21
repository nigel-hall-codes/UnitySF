using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace SFMap.Pipeline
{
    public static class OsmParser
    {
        static readonly HashSet<string> RoadHighwayValues = new HashSet<string>
        {
            "motorway", "motorway_link", "trunk", "trunk_link",
            "primary", "primary_link", "secondary", "secondary_link",
            "tertiary", "tertiary_link", "residential", "living_street",
            "service", "unclassified", "road",
        };

        // Parses map.osm and returns a fully projected StreetGraph.
        // Also calls GeoProjection.Initialize() from the <bounds> element.
        public static StreetGraph Parse(string osmFilePath)
        {
            var doc = XDocument.Load(osmFilePath);
            var root = doc.Root;

            var boundsEl = root.Element("bounds");
            var osmBounds = new OsmBounds(
                double.Parse(boundsEl.Attribute("minlat").Value, CultureInfo.InvariantCulture),
                double.Parse(boundsEl.Attribute("maxlat").Value, CultureInfo.InvariantCulture),
                double.Parse(boundsEl.Attribute("minlon").Value, CultureInfo.InvariantCulture),
                double.Parse(boundsEl.Attribute("maxlon").Value, CultureInfo.InvariantCulture)
            );
            GeoProjection.Initialize(osmBounds);

            var rawNodes = ParseRawNodes(root);
            var rawWays = ParseRawWays(root);

            var highwayWays = rawWays.Where(w => IsHighwayRoad(w.tags)).ToList();
            var buildingWays = rawWays.Where(w => w.tags.ContainsKey("building")).ToList();

            var nodeRefCounts = CountNodeRefs(highwayWays);
            var streetNodes = BuildStreetNodes(nodeRefCounts, rawNodes);
            var edges = BuildEdges(highwayWays, streetNodes);
            var adjacency = BuildAdjacency(edges);
            var buildings = BuildBuildings(buildingWays, rawNodes);

            return new StreetGraph
            {
                SourceBounds = osmBounds,
                Nodes = streetNodes,
                Edges = edges,
                Adjacency = adjacency,
                Buildings = buildings,
            };
        }

        static Dictionary<long, (double lat, double lon, Dictionary<string, string> tags)>
            ParseRawNodes(XElement root)
        {
            var dict = new Dictionary<long, (double, double, Dictionary<string, string>)>();
            foreach (var el in root.Elements("node"))
            {
                long id = long.Parse(el.Attribute("id").Value);
                double lat = double.Parse(el.Attribute("lat").Value, CultureInfo.InvariantCulture);
                double lon = double.Parse(el.Attribute("lon").Value, CultureInfo.InvariantCulture);
                dict[id] = (lat, lon, ReadTags(el));
            }
            return dict;
        }

        static List<(long id, long[] nodeIds, Dictionary<string, string> tags)>
            ParseRawWays(XElement root)
        {
            var list = new List<(long, long[], Dictionary<string, string>)>();
            foreach (var el in root.Elements("way"))
            {
                long id = long.Parse(el.Attribute("id").Value);
                long[] nodeIds = el.Elements("nd")
                    .Select(nd => long.Parse(nd.Attribute("ref").Value))
                    .ToArray();
                list.Add((id, nodeIds, ReadTags(el)));
            }
            return list;
        }

        // A node is an intersection if it appears in 2 or more distinct highway ways.
        static Dictionary<long, int> CountNodeRefs(
            List<(long id, long[] nodeIds, Dictionary<string, string> tags)> highwayWays)
        {
            var counts = new Dictionary<long, int>();
            foreach (var (_, nodeIds, _) in highwayWays)
            {
                var seen = new HashSet<long>();
                foreach (long nid in nodeIds)
                {
                    if (seen.Add(nid))
                    {
                        counts.TryGetValue(nid, out int c);
                        counts[nid] = c + 1;
                    }
                }
            }
            return counts;
        }

        static Dictionary<long, StreetNode> BuildStreetNodes(
            Dictionary<long, int> refCounts,
            Dictionary<long, (double lat, double lon, Dictionary<string, string> tags)> rawNodes)
        {
            var nodes = new Dictionary<long, StreetNode>(refCounts.Count);
            foreach (var (nid, count) in refCounts)
            {
                if (!rawNodes.TryGetValue(nid, out var raw)) continue;
                bool isSignal = raw.tags.TryGetValue("highway", out var hwTag) && hwTag == "traffic_signals";
                nodes[nid] = new StreetNode
                {
                    OsmId = nid,
                    WorldPosition = GeoProjection.ToWorldPoint(raw.lon, raw.lat),
                    IsIntersection = count >= 2,
                    TrafficControl = count >= 2
                        ? (isSignal ? IntersectionType.TrafficSignals : IntersectionType.StopSign)
                        : (IntersectionType?)null,
                };
            }
            return nodes;
        }

        static List<StreetEdge> BuildEdges(
            List<(long id, long[] nodeIds, Dictionary<string, string> tags)> highwayWays,
            Dictionary<long, StreetNode> streetNodes)
        {
            var edges = new List<StreetEdge>();
            foreach (var (wayId, nodeIds, tags) in highwayWays)
            {
                if (nodeIds.Length < 2) continue;
                tags.TryGetValue("highway", out var hwStr);
                var hwType = ToHighwayType(hwStr ?? "unclassified");
                bool oneWay = tags.TryGetValue("oneway", out var ow) && ow is "yes" or "1" or "true";

                foreach (var segment in SplitAtIntersections(nodeIds, streetNodes))
                {
                    if (segment.Count < 2) continue;
                    var centerline = new Vector3[segment.Count];
                    bool valid = true;
                    for (int i = 0; i < segment.Count; i++)
                    {
                        if (!streetNodes.TryGetValue(segment[i], out var n)) { valid = false; break; }
                        centerline[i] = n.WorldPosition;
                    }
                    if (!valid) continue;

                    edges.Add(new StreetEdge
                    {
                        OsmWayId = wayId,
                        From = streetNodes[segment[0]],
                        To = streetNodes[segment[^1]],
                        Type = hwType,
                        IsOneWay = oneWay,
                        Centerline = centerline,
                    });
                }
            }
            return edges;
        }

        static IReadOnlyDictionary<StreetNode, IReadOnlyList<StreetEdge>> BuildAdjacency(
            List<StreetEdge> edges)
        {
            var adj = new Dictionary<StreetNode, List<StreetEdge>>();
            foreach (var edge in edges)
            {
                if (!adj.TryGetValue(edge.From, out var fl)) adj[edge.From] = fl = new List<StreetEdge>();
                fl.Add(edge);
                if (!adj.TryGetValue(edge.To, out var tl)) adj[edge.To] = tl = new List<StreetEdge>();
                tl.Add(edge);
            }
            var result = new Dictionary<StreetNode, IReadOnlyList<StreetEdge>>(adj.Count);
            foreach (var kvp in adj) result[kvp.Key] = kvp.Value;
            return result;
        }

        static List<BuildingWay> BuildBuildings(
            List<(long id, long[] nodeIds, Dictionary<string, string> tags)> buildingWays,
            Dictionary<long, (double lat, double lon, Dictionary<string, string> tags)> rawNodes)
        {
            var buildings = new List<BuildingWay>();
            foreach (var (wayId, nodeIds, tags) in buildingWays)
            {
                if (nodeIds.Length < 3) continue;
                var footprint = nodeIds
                    .Where(rawNodes.ContainsKey)
                    .Select(id => GeoProjection.ToWorldPoint(rawNodes[id].lon, rawNodes[id].lat))
                    .ToArray();
                if (footprint.Length < 3) continue;

                float height = 0f;
                if (tags.TryGetValue("building:levels", out var lvlStr) &&
                    float.TryParse(lvlStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float lvl))
                    height = lvl * 3.5f;
                else if (tags.TryGetValue("height", out var hStr) &&
                         float.TryParse(hStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float h))
                    height = h;

                buildings.Add(new BuildingWay { OsmId = wayId, Footprint = footprint, Height = height });
            }
            return buildings;
        }

        // Splits a way's node sequence into segments at every intersection node.
        // Each segment starts and ends at a boundary node (way endpoint or intersection).
        static List<List<long>> SplitAtIntersections(
            long[] nodeIds, Dictionary<long, StreetNode> nodes)
        {
            var segments = new List<List<long>>();
            var current = new List<long> { nodeIds[0] };

            for (int i = 1; i < nodeIds.Length; i++)
            {
                long nid = nodeIds[i];
                current.Add(nid);
                bool isLast = i == nodeIds.Length - 1;
                if (!isLast && nodes.TryGetValue(nid, out var node) && node.IsIntersection)
                {
                    segments.Add(current);
                    current = new List<long> { nid };
                }
            }
            if (current.Count >= 2)
                segments.Add(current);

            return segments;
        }

        static bool IsHighwayRoad(Dictionary<string, string> tags) =>
            tags.TryGetValue("highway", out var v) && RoadHighwayValues.Contains(v);

        static HighwayType ToHighwayType(string value) => value switch
        {
            "primary" or "primary_link"          => HighwayType.Primary,
            "secondary" or "secondary_link"      => HighwayType.Secondary,
            "tertiary" or "tertiary_link"        => HighwayType.Tertiary,
            "residential" or "living_street"     => HighwayType.Residential,
            "service"                            => HighwayType.Service,
            "footway" or "path" or "pedestrian"  => HighwayType.Footway,
            _                                    => HighwayType.Unclassified,
        };

        static Dictionary<string, string> ReadTags(XElement el)
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in el.Elements("tag"))
                dict[tag.Attribute("k").Value] = tag.Attribute("v").Value;
            return dict;
        }
    }
}
