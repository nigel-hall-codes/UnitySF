#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Stage 6: for each intersection node, computes a convex polygon via miter/bevel joins
    // (ported from A/B Street) and triangulates it into a flat Unity mesh.
    // Saves to Assets/Generated/Intersections/.
    //
    // Intersection-first pipeline:
    //   1. ComputePolygons  — build polygon shapes for all intersections
    //   2. ComputeBoundaries — derive world-space road endpoint positions from those polygons
    //   3. Generate          — stamp terrain + triangulate meshes from precomputed polygons
    public static class IntersectionMeshGenerator
    {
        const float BevelThreshold = 5f; // meters; miter points beyond this become two-vertex bevels
        const float Raise = 0.05f;       // match RoadMeshGenerator — prevents z-fighting with terrain

        // One arm = one road radiating away from the intersection node.
        // Dir is in XZ space: Dir.x = world X (East), Dir.y = world Z (North).
        struct Arm
        {
            public Vector2 Dir;
            public float   HalfWidth;
            public float   Angle; // atan2(z, x) for CCW sort
        }

        struct EdgeArm
        {
            public Arm        Arm;
            public StreetEdge Edge;
            public bool       IsFrom;
        }

        // Phase 1 — compute intersection polygon shapes (XZ offsets from node center).
        // Must be called before ComputeBoundaries and Generate.
        public static Dictionary<StreetNode, List<Vector2>> ComputePolygons(StreetGraph graph)
        {
            var result = new Dictionary<StreetNode, List<Vector2>>();
            foreach (var node in graph.IntersectionNodes)
            {
                var arms = CollectArms(node, graph);
                if (arms.Count < 2) continue;
                arms.Sort((a, b) => a.Angle.CompareTo(b.Angle));

                var poly = new List<Vector2>();
                int n = arms.Count;
                for (int i = 0; i < n; i++)
                    ComputeJoin(arms[i], arms[(i + 1) % n], poly);

                if (poly.Count >= 3)
                    result[node] = poly;
            }
            return result;
        }

        // Phase 2 — for each road edge endpoint at an intersection, compute the exact
        // world-space boundary point where the road mesh should start or end.
        // Non-intersection endpoints (dead-ends) are absent from the result — road runs to node.
        public static Dictionary<StreetEdge, (Vector3? from, Vector3? to)> ComputeBoundaries(
            StreetGraph graph, Dictionary<StreetNode, List<Vector2>> polygons)
        {
            var result = new Dictionary<StreetEdge, (Vector3? from, Vector3? to)>();

            foreach (var node in graph.IntersectionNodes)
            {
                if (!polygons.ContainsKey(node)) continue;

                var edgeArms = CollectEdgeArms(node, graph);
                if (edgeArms.Count < 2) continue;
                edgeArms.Sort((a, b) => a.Arm.Angle.CompareTo(b.Arm.Angle));

                int n = edgeArms.Count;
                var perArm = new float[n];

                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    (float tA, float tB) = JoinSetbacks(edgeArms[i].Arm, edgeArms[j].Arm);
                    perArm[i] = Mathf.Max(perArm[i], tA);
                    perArm[j] = Mathf.Max(perArm[j], tB);
                }

                for (int i = 0; i < n; i++)
                {
                    var ea = edgeArms[i];
                    Vector2 dir = ea.Arm.Dir;
                    float t = perArm[i];
                    // Y=0 here; generators elevate to terrain height before use.
                    Vector3 boundaryPt = node.WorldPosition + new Vector3(dir.x, 0f, dir.y) * t;

                    result.TryGetValue(ea.Edge, out var pair);
                    result[ea.Edge] = ea.IsFrom
                        ? (boundaryPt, pair.to)
                        : (pair.from, boundaryPt);
                }
            }

            return result;
        }

        // Phase 3 — stamp terrain + build meshes from precomputed polygons.
        public static IReadOnlyList<Mesh> Generate(
            StreetGraph graph,
            Dictionary<StreetNode, List<Vector2>> polygons,
            HeightmapData heightmap,
            Rect worldRect,
            ChunkCoord coord)
        {
            var meshes = new List<Mesh>();
#if UNITY_EDITOR
            var meshPaths = new List<string>();
#endif

#if UNITY_EDITOR
            AssetDatabase.StartAssetEditing();
            try
            {
#endif
                foreach (var node in graph.IntersectionNodes)
                {
                    if (!polygons.TryGetValue(node, out var poly)) continue;

                    float y = SampleElevation(node.WorldPosition.x, node.WorldPosition.z, heightmap, worldRect) + Raise;
                    var center = new Vector3(node.WorldPosition.x, y, node.WorldPosition.z);

                    float nodeElev = y - Raise;
                    StampCircle(node.WorldPosition.x, node.WorldPosition.z, poly, nodeElev, heightmap, worldRect);

                    Mesh mesh = TriangulateFan(node.OsmId, center, poly);
#if UNITY_EDITOR
                    SaveMesh(mesh, coord, node.OsmId);
                    meshPaths.Add(GeneratedAssets.IntersectionMesh(coord, node.OsmId));
#else
                    meshes.Add(mesh);
#endif
                }
#if UNITY_EDITOR
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            foreach (var path in meshPaths)
                meshes.Add(AssetDatabase.LoadAssetAtPath<Mesh>(path));
#endif
            return meshes;
        }

        // Uses adjacency list for O(degree) lookup instead of O(E) scan.
        static List<EdgeArm> CollectEdgeArms(StreetNode node, StreetGraph graph)
        {
            var arms = new List<EdgeArm>();
            if (!graph.Adjacency.TryGetValue(node, out var nodeEdges)) return arms;

            foreach (var edge in nodeEdges)
            {
                if (edge.Width <= 0f) continue;
                Vector3[] cl = edge.Centerline;
                Vector2 dir;
                bool isFrom;

                if (edge.From == node && cl.Length >= 2)
                {
                    Vector3 d = cl[1] - cl[0];
                    dir    = new Vector2(d.x, d.z).normalized;
                    isFrom = true;
                }
                else if (edge.To == node && cl.Length >= 2)
                {
                    Vector3 d = cl[cl.Length - 2] - cl[cl.Length - 1];
                    dir    = new Vector2(d.x, d.z).normalized;
                    isFrom = false;
                }
                else continue;

                if (dir.sqrMagnitude < 0.0001f) continue;

                arms.Add(new EdgeArm
                {
                    Arm    = new Arm { Dir = dir, HalfWidth = edge.Width * 0.5f, Angle = Mathf.Atan2(dir.y, dir.x) },
                    Edge   = edge,
                    IsFrom = isFrom,
                });
            }
            return arms;
        }

        static List<Arm> CollectArms(StreetNode node, StreetGraph graph)
        {
            var edgeArms = CollectEdgeArms(node, graph);
            var arms = new List<Arm>(edgeArms.Count);
            foreach (var ea in edgeArms) arms.Add(ea.Arm);
            return arms;
        }

        // Computes the join vertex/vertices between arm A (the arm just before the gap in CCW order)
        // and arm B (the arm just after). Appends 1 miter vertex or 2 bevel vertices to poly.
        //
        // The join is at the intersection of:
        //   - the LEFT boundary ray of arm A  (the CCW side of A, facing toward B)
        //   - the RIGHT boundary ray of arm B (the CW side of B, facing toward A)
        //
        // In XZ space: perpLeft(dir) = (-dir.z, dir.x) = Vector2(-dir.y, dir.x)
        //              perpRight(dir)= ( dir.z,-dir.x) = Vector2( dir.y,-dir.x)
        static void ComputeJoin(Arm a, Arm b, List<Vector2> poly)
        {
            Vector2 perpLeftA  = new Vector2(-a.Dir.y,  a.Dir.x);
            Vector2 perpRightB = new Vector2( b.Dir.y, -b.Dir.x);

            Vector2 pa = perpLeftA  * a.HalfWidth;
            Vector2 pb = perpRightB * b.HalfWidth;

            float dax = a.Dir.x, daz = a.Dir.y;
            float dbx = b.Dir.x, dbz = b.Dir.y;
            float det = -dax * dbz + daz * dbx;

            Vector2 miter;
            if (Mathf.Abs(det) < 1e-6f)
            {
                miter = (pa + pb) * 0.5f;
            }
            else
            {
                float dx = pb.x - pa.x;
                float dz = pb.y - pa.y;
                float t  = (-dx * dbz + dz * dbx) / det;
                miter = pa + t * a.Dir;
            }

            if (miter.magnitude <= BevelThreshold)
            {
                poly.Add(miter);
            }
            else
            {
                float tA = Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - a.HalfWidth * a.HalfWidth));
                float tB = Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - b.HalfWidth * b.HalfWidth));
                poly.Add(pa + a.Dir * tA);
                poly.Add(pb + b.Dir * tB);
            }
        }

        // Returns setback distance (meters) along each arm to the intersection polygon boundary.
        static (float tA, float tB) JoinSetbacks(Arm a, Arm b)
        {
            Vector2 perpLeftA  = new Vector2(-a.Dir.y,  a.Dir.x);
            Vector2 perpRightB = new Vector2( b.Dir.y, -b.Dir.x);
            Vector2 pa = perpLeftA  * a.HalfWidth;
            Vector2 pb = perpRightB * b.HalfWidth;

            float dax = a.Dir.x, daz = a.Dir.y;
            float dbx = b.Dir.x, dbz = b.Dir.y;
            float det = -dax * dbz + daz * dbx;

            if (Mathf.Abs(det) < 1e-6f)
            {
                return (
                    Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - a.HalfWidth * a.HalfWidth)),
                    Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - b.HalfWidth * b.HalfWidth))
                );
            }

            float dx = pb.x - pa.x;
            float dz = pb.y - pa.y;
            float t = (-dx * dbz + dz * dbx) / det;
            float s = ( dax * dz - daz * dx) / det;

            if ((pa + t * a.Dir).magnitude <= BevelThreshold)
                return (Mathf.Max(0f, t), Mathf.Max(0f, s));

            return (
                Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - a.HalfWidth * a.HalfWidth)),
                Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - b.HalfWidth * b.HalfWidth))
            );
        }

        // Fan triangulation from center. Polygon vertices are CCW from above → reverse winding
        // so RecalculateNormals produces upward-facing normals.
        static Mesh TriangulateFan(long osmId, Vector3 center, List<Vector2> poly)
        {
            int n = poly.Count;
            var verts = new Vector3[n + 1];
            var uvs   = new Vector2[n + 1];
            var tris  = new int[n * 3];

            verts[0] = center;
            uvs[0]   = new Vector2(0.5f, 0.5f);

            float maxR = 0f;
            foreach (var p in poly) maxR = Mathf.Max(maxR, p.magnitude);
            if (maxR < 0.001f) maxR = 1f;

            for (int i = 0; i < n; i++)
            {
                verts[i + 1] = center + new Vector3(poly[i].x, 0f, poly[i].y);
                uvs[i + 1]   = new Vector2(poly[i].x / (2f * maxR) + 0.5f, poly[i].y / (2f * maxR) + 0.5f);

                int j = (i + 1) % n;
                tris[i * 3]     = 0;
                tris[i * 3 + 1] = j + 1;
                tris[i * 3 + 2] = i + 1;
            }

            var mesh = new Mesh { name = $"intersection_{osmId}" };
            mesh.vertices  = verts;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Stamps all heightmap cells within the intersection polygon's bounding circle to elevation.
        static void StampCircle(float cx, float cz, List<Vector2> poly, float elevation,
            HeightmapData heightmap, Rect worldRect)
        {
            float maxR = 0f;
            foreach (var p in poly) maxR = Mathf.Max(maxR, p.magnitude);

            int   res   = heightmap.Resolution;
            float cellW = worldRect.width  / (res - 1);
            float cellH = worldRect.height / (res - 1);
            float pad   = Mathf.Sqrt(cellW * cellW + cellH * cellH) * 0.5f;
            float r     = maxR + pad;
            float r2    = r * r;

            float elevRange = heightmap.MaxElevationMeters - heightmap.MinElevationMeters;
            if (elevRange < 0.001f) elevRange = 1f;
            float normalized = (elevation - heightmap.MinElevationMeters) / elevRange;

            int colMin = Mathf.Max(0,       Mathf.FloorToInt((cx - r - worldRect.x) / cellW));
            int colMax = Mathf.Min(res - 1, Mathf.CeilToInt ((cx + r - worldRect.x) / cellW));
            int rowMin = Mathf.Max(0,       Mathf.FloorToInt((cz - r - worldRect.y) / cellH));
            int rowMax = Mathf.Min(res - 1, Mathf.CeilToInt ((cz + r - worldRect.y) / cellH));

            for (int row = rowMin; row <= rowMax; row++)
            {
                float wz = worldRect.y + row * cellH;
                float dz = wz - cz;
                for (int col = colMin; col <= colMax; col++)
                {
                    float wx = worldRect.x + col * cellW;
                    float dx = wx - cx;
                    if (dx * dx + dz * dz > r2) continue;
                    heightmap.Values[row, col] = normalized;
                }
            }
        }

        static float SampleElevation(float wx, float wz, HeightmapData heightmap, Rect worldRect)
        {
            float u = Mathf.Clamp01((wx - worldRect.x) / worldRect.width);
            float v = Mathf.Clamp01((wz - worldRect.y) / worldRect.height);
            int res = heightmap.Resolution;

            float fc = u * (res - 1);
            float fr = v * (res - 1);
            int c0 = Mathf.Clamp(Mathf.FloorToInt(fc), 0, res - 2);
            int r0 = Mathf.Clamp(Mathf.FloorToInt(fr), 0, res - 2);
            float tc = fc - c0;
            float tr = fr - r0;

            float h00 = heightmap.Values[r0,     c0];
            float h10 = heightmap.Values[r0,     c0 + 1];
            float h01 = heightmap.Values[r0 + 1, c0];
            float h11 = heightmap.Values[r0 + 1, c0 + 1];
            float norm = Mathf.Lerp(Mathf.Lerp(h00, h10, tc), Mathf.Lerp(h01, h11, tc), tr);

            return heightmap.MinElevationMeters + norm * (heightmap.MaxElevationMeters - heightmap.MinElevationMeters);
        }

#if UNITY_EDITOR
        static void SaveMesh(Mesh mesh, ChunkCoord coord, long osmId)
        {
            string dir = $"{GeneratedAssets.ChunkDir(coord)}/Intersections";
            EnsureFolder(dir);
            AssetDatabase.CreateAsset(mesh, GeneratedAssets.IntersectionMesh(coord, osmId));
            Debug.Log($"[IntersectionMeshGenerator] Saved Intersections/intersection_{osmId}.mesh");
        }

        static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
#endif
    }
}
