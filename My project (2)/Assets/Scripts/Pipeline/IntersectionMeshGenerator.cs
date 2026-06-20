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
    public static class IntersectionMeshGenerator
    {
        const float BevelThreshold = 5f; // meters; miter points beyond this become two-vertex bevels

        public static IReadOnlyList<Mesh> Generate(
            StreetGraph graph,
            HeightmapData heightmap,
            Rect worldRect,
            ChunkCoord coord)
        {
            var meshes = new List<Mesh>();

#if UNITY_EDITOR
            AssetDatabase.StartAssetEditing();
            try
            {
#endif
                foreach (var node in graph.IntersectionNodes)
                {
                    Mesh mesh = BuildIntersectionMesh(node, graph, heightmap, worldRect);
                    if (mesh == null) continue;
#if UNITY_EDITOR
                    SaveMesh(mesh, coord, node.OsmId);
#endif
                    meshes.Add(mesh);
                }
#if UNITY_EDITOR
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.SaveAssets();
#endif
            return meshes;
        }

        // One arm = one road radiating away from the intersection node.
        // Dir is in XZ space: Dir.x = world X (East), Dir.y = world Z (North).
        struct Arm
        {
            public Vector2 Dir;
            public float   HalfWidth;
            public float   Angle; // atan2(z, x) for CCW sort
        }

        static Mesh BuildIntersectionMesh(
            StreetNode node, StreetGraph graph,
            HeightmapData heightmap, Rect worldRect)
        {
            var arms = CollectArms(node, graph);
            if (arms.Count < 2) return null;

            // CCW sort by angle in the XZ plane (viewed from +Y)
            arms.Sort((a, b) => a.Angle.CompareTo(b.Angle));

            float y = SampleElevation(node.WorldPosition.x, node.WorldPosition.z, heightmap, worldRect);
            var center = new Vector3(node.WorldPosition.x, y, node.WorldPosition.z);

            // Compute polygon vertices (XZ offsets from center) via miter/bevel joins
            var poly = new List<Vector2>();
            int n = arms.Count;
            for (int i = 0; i < n; i++)
                ComputeJoin(arms[i], arms[(i + 1) % n], poly);

            if (poly.Count < 3) return null;
            return TriangulateFan(node.OsmId, center, poly);
        }

        static List<Arm> CollectArms(StreetNode node, StreetGraph graph)
        {
            var arms = new List<Arm>();
            foreach (var edge in graph.Edges)
            {
                if (edge.Width <= 0f) continue;

                Vector3[] cl = edge.Centerline;
                Vector2 dir;

                if (edge.From == node && cl.Length >= 2)
                {
                    Vector3 d = cl[1] - cl[0];
                    dir = new Vector2(d.x, d.z).normalized;
                }
                else if (edge.To == node && cl.Length >= 2)
                {
                    Vector3 d = cl[cl.Length - 2] - cl[cl.Length - 1];
                    dir = new Vector2(d.x, d.z).normalized;
                }
                else
                {
                    continue;
                }

                if (dir.sqrMagnitude < 0.0001f) continue;

                arms.Add(new Arm
                {
                    Dir       = dir,
                    HalfWidth = edge.Width * 0.5f,
                    Angle     = Mathf.Atan2(dir.y, dir.x),
                });
            }
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

            // Solve pa + t*da = pb + s*db  (2D linear system in t and s)
            float dax = a.Dir.x, daz = a.Dir.y;
            float dbx = b.Dir.x, dbz = b.Dir.y;

            float det = -dax * dbz + daz * dbx;

            Vector2 miter;
            if (Mathf.Abs(det) < 1e-6f)
            {
                // Parallel boundary lines — use midpoint as fallback
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
                // Bevel: place two vertices on the respective boundary rays.
                // Each is the point on the ray at world distance BevelThreshold from center:
                //   distance^2 = halfWidth^2 + t^2  →  t = sqrt(threshold^2 - halfWidth^2)
                float tA = Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - a.HalfWidth * a.HalfWidth));
                float tB = Mathf.Sqrt(Mathf.Max(0f, BevelThreshold * BevelThreshold - b.HalfWidth * b.HalfWidth));
                poly.Add(pa + a.Dir * tA);
                poly.Add(pb + b.Dir * tB);
            }
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
                // Reversed winding (j before i) → normals point up for a CCW polygon
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

        // Bilinear elevation sample — identical to RoadMeshGenerator.SampleElevation.
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
