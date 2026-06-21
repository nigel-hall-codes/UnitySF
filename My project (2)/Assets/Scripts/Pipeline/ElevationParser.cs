using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Stage 3: reads the elevation contour CSV and produces a normalized heightmap.
    // Requires GeoProjection.Initialize() to have been called first (done by OsmParser.Parse).
    public static class ElevationParser
    {
        const float FeetToMeters = 0.3048f;
        const float ClipBufferMeters = 200f;

        public static HeightmapData Parse(string csvFilePath, OsmBounds osmBounds, int resolution = 513)
        {
            var worldRect = GeoProjection.WorldBounds(osmBounds);
            var clipRect = Expand(worldRect, ClipBufferMeters);

            var points = new List<Vector2>();
            var elevations = new List<float>();
            CollectContourPoints(csvFilePath, clipRect, points, elevations);

            if (points.Count < 10)
                Debug.LogWarning($"[ElevationParser] Only {points.Count} contour vertices within bounds — sparse coverage");

            float minElev = float.MaxValue, maxElev = float.MinValue;
            foreach (float e in elevations)
            {
                if (e < minElev) minElev = e;
                if (e > maxElev) maxElev = e;
            }
            if (maxElev - minElev < 0.01f) maxElev = minElev + 1f;

            var tris = Triangulate(points);

            var values = new float[resolution, resolution];
            FillHeightmap(values, resolution, worldRect, points, elevations, tris, minElev, maxElev);

            return new HeightmapData
            {
                Values             = values,
                Resolution         = resolution,
                WorldRect          = worldRect,
                MinElevationMeters = minElev,
                MaxElevationMeters = maxElev,
            };
        }

        static Rect Expand(Rect r, float margin) =>
            new Rect(r.x - margin, r.y - margin, r.width + margin * 2, r.height + margin * 2);

        static void CollectContourPoints(string csvPath, Rect clipRect,
            List<Vector2> points, List<float> elevations)
        {
            // CSV has ~1m point spacing; heightmap cells are ~10m — thin to avoid O(n²) triangulation cost.
            const float minSpacingSq = 8f * 8f;

            using var reader = new StreamReader(csvPath);
            reader.ReadLine(); // header

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var fields = SplitCsvLine(line);
                if (fields.Count < 3) continue;

                if (!int.TryParse(fields[1], out int elevFeet)) continue;
                float elevM = elevFeet * FeetToMeters;

                var lastAdded = new Vector2(float.MaxValue, float.MaxValue);
                foreach (var (lon, lat) in ParseLinestring(fields[2]))
                {
                    var wp = GeoProjection.ToWorldPoint(lon, lat);
                    var xz = new Vector2(wp.x, wp.z);
                    if (!clipRect.Contains(xz)) continue;
                    if ((xz - lastAdded).sqrMagnitude < minSpacingSq) continue;
                    lastAdded = xz;
                    points.Add(xz);
                    elevations.Add(elevM);
                }
            }
        }

        // Minimal quoted-CSV parser. Handles fields like "LINESTRING (..., ...)" correctly.
        static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    i++;
                    int start = i;
                    while (i < line.Length)
                    {
                        if (line[i] == '"' && (i + 1 >= line.Length || line[i + 1] != '"'))
                            break;
                        if (line[i] == '"') i++; // escaped quote
                        i++;
                    }
                    fields.Add(line[start..i]);
                    i++; // closing quote
                    if (i < line.Length && line[i] == ',') i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    fields.Add(line[start..i]);
                    if (i < line.Length) i++;
                }
            }
            return fields;
        }

        // Parses "LINESTRING (lon1 lat1, lon2 lat2, ...)" and yields (lon, lat) pairs.
        static IEnumerable<(double lon, double lat)> ParseLinestring(string wkt)
        {
            int open = wkt.IndexOf('(');
            int close = wkt.LastIndexOf(')');
            if (open < 0 || close < 0) yield break;
            string inner = wkt.Substring(open + 1, close - open - 1);

            foreach (string rawPair in inner.Split(','))
            {
                string pair = rawPair.Trim();
                int space = pair.IndexOf(' ');
                if (space <= 0) continue;
                if (double.TryParse(pair.Substring(0, space), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                    double.TryParse(pair.Substring(space + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                    yield return (lon, lat);
            }
        }

        // ---- Bowyer-Watson Delaunay triangulation ----

        struct Tri
        {
            public int A, B, C;
            public Tri(int a, int b, int c) { A = a; B = b; C = c; }
        }

        static List<Tri> Triangulate(List<Vector2> pts)
        {
            int n = pts.Count;
            if (n < 3) return new List<Tri>();

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
            }
            float margin = Mathf.Max(maxX - minX, maxY - minY) * 3f + 1f;
            float midX = (minX + maxX) * 0.5f;

            // Extend point list with super-triangle vertices (CCW: s0, s2, s1)
            var all = new List<Vector2>(pts);
            int s0 = all.Count; all.Add(new Vector2(minX - margin, minY - margin));
            int s1 = all.Count; all.Add(new Vector2(midX, maxY + margin));
            int s2 = all.Count; all.Add(new Vector2(maxX + margin, minY - margin));

            var tris = new List<Tri> { new Tri(s0, s2, s1) };
            var badIdx = new List<int>();
            var edges = new List<(int, int)>();

            for (int pi = 0; pi < n; pi++)
            {
                Vector2 p = pts[pi];
                badIdx.Clear();

                for (int ti = 0; ti < tris.Count; ti++)
                {
                    Tri t = tris[ti];
                    if (InCircumcircle(p, all[t.A], all[t.B], all[t.C]))
                        badIdx.Add(ti);
                }

                // Find hole boundary: edges not shared between bad triangles
                edges.Clear();
                foreach (int bi in badIdx)
                {
                    Tri t = tris[bi];
                    TryAddBoundaryEdge(edges, badIdx, tris, bi, t.A, t.B);
                    TryAddBoundaryEdge(edges, badIdx, tris, bi, t.B, t.C);
                    TryAddBoundaryEdge(edges, badIdx, tris, bi, t.C, t.A);
                }

                // Remove bad triangles (descending order preserves indices)
                for (int i = badIdx.Count - 1; i >= 0; i--)
                    tris.RemoveAt(badIdx[i]);

                // Re-triangulate hole with the new point (ensure CCW winding)
                foreach (var (ea, eb) in edges)
                {
                    if (IsCCW(all[ea], all[eb], p))
                        tris.Add(new Tri(ea, eb, pi));
                    else
                        tris.Add(new Tri(eb, ea, pi));
                }
            }

            // Remove triangles that touch the super-triangle
            tris.RemoveAll(t => t.A >= n || t.B >= n || t.C >= n);
            return tris;
        }

        static void TryAddBoundaryEdge(List<(int, int)> edges,
            List<int> badIdx, List<Tri> tris, int selfIdx, int a, int b)
        {
            foreach (int bj in badIdx)
            {
                if (bj == selfIdx) continue;
                Tri u = tris[bj];
                if (SharesEdge(u, a, b)) return; // shared — not a boundary edge
            }
            edges.Add((a, b));
        }

        static bool SharesEdge(Tri t, int a, int b)
        {
            bool hasA = t.A == a || t.B == a || t.C == a;
            bool hasB = t.A == b || t.B == b || t.C == b;
            return hasA && hasB;
        }

        // Returns true if point p is inside the circumcircle of CCW triangle (a, b, c).
        static bool InCircumcircle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float ax = a.x - p.x, ay = a.y - p.y;
            float bx = b.x - p.x, by = b.y - p.y;
            float cx = c.x - p.x, cy = c.y - p.y;
            float det =
                ax * (by * (cx * cx + cy * cy) - cy * (bx * bx + by * by)) -
                ay * (bx * (cx * cx + cy * cy) - cx * (bx * bx + by * by)) +
                (ax * ax + ay * ay) * (bx * cy - by * cx);
            return det > 0f;
        }

        static bool IsCCW(Vector2 a, Vector2 b, Vector2 c) =>
            (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) > 0f;

        // ---- Heightmap filling ----

        static void FillHeightmap(float[,] values, int res, Rect worldRect,
            List<Vector2> pts, List<float> elevs, List<Tri> tris,
            float minElev, float maxElev)
        {
            float elevRange = maxElev - minElev;
            float cellW = worldRect.width / (res - 1);
            float cellH = worldRect.height / (res - 1);

            foreach (var tri in tris)
            {
                Vector2 a = pts[tri.A], b = pts[tri.B], c = pts[tri.C];
                float ea = elevs[tri.A], eb = elevs[tri.B], ec = elevs[tri.C];

                // Grid cell range covered by this triangle's bounding box
                int colMin = Mathf.Max(0,       Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - worldRect.x) / cellW));
                int colMax = Mathf.Min(res - 1, Mathf.CeilToInt ((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - worldRect.x) / cellW));
                int rowMin = Mathf.Max(0,       Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - worldRect.y) / cellH));
                int rowMax = Mathf.Min(res - 1, Mathf.CeilToInt ((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - worldRect.y) / cellH));

                for (int row = rowMin; row <= rowMax; row++)
                {
                    float wz = worldRect.y + row * cellH;
                    for (int col = colMin; col <= colMax; col++)
                    {
                        float wx = worldRect.x + col * cellW;
                        var p = new Vector2(wx, wz);
                        if (!BarycentricInTriangle(p, a, b, c, out float u, out float v, out float w))
                            continue;
                        values[row, col] = (u * ea + v * eb + w * ec - minElev) / elevRange;
                    }
                }
            }
        }

        // Computes barycentric weights (u, v, w) for point p in triangle (a, b, c).
        // Returns false if p is outside the triangle.
        static bool BarycentricInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
            out float u, out float v, out float w)
        {
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector2.Dot(v0, v0);
            float d01 = Vector2.Dot(v0, v1);
            float d11 = Vector2.Dot(v1, v1);
            float d20 = Vector2.Dot(v2, v0);
            float d21 = Vector2.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-10f) { u = v = w = 0f; return false; }
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1f - v - w;
            return u >= 0f && v >= 0f && w >= 0f;
        }
    }
}
