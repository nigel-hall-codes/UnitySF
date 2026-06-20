#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Stage 7: generates flat quad-strip sidewalk meshes alongside each road segment.
    // For each driveable edge, produces one mesh containing both a left and right sidewalk
    // strip, each 1.5m wide and raised 0.05m above the road surface elevation.
    public static class SidewalkMeshGenerator
    {
        const float Width = 1.5f;
        const float Raise = 0.10f;

        public static IReadOnlyList<Mesh> Generate(
            StreetGraph graph,
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
                foreach (var edge in graph.Edges)
                {
                    if (edge.Width <= 0f) continue;

                    Vector3[] centerline = SampledCenterline(edge, heightmap, worldRect);
                    Mesh mesh = BuildMesh(edge, centerline);

#if UNITY_EDITOR
                    SaveMesh(mesh, coord, edge.OsmWayId);
                    meshPaths.Add(GeneratedAssets.SidewalkMesh(coord, edge.OsmWayId));
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
            EnsureMaterial();
            AssetDatabase.SaveAssets();
            foreach (var path in meshPaths)
                meshes.Add(AssetDatabase.LoadAssetAtPath<Mesh>(path));
#endif

            return meshes;
        }

        // Samples terrain elevation along the edge centerline (road terrain is already stamped flat).
        static Vector3[] SampledCenterline(StreetEdge edge, HeightmapData heightmap, Rect worldRect)
        {
            var cl = edge.Centerline;
            var out_ = new Vector3[cl.Length];
            for (int i = 0; i < cl.Length; i++)
                out_[i] = new Vector3(cl[i].x, SampleElevation(cl[i].x, cl[i].z, heightmap, worldRect), cl[i].z);
            return out_;
        }

        // Builds one mesh with a left and right quad-strip sidewalk.
        // Vertex layout per cross-section (4 verts, indices i*4+0..3):
        //   0 = left-outer, 1 = left-inner, 2 = right-inner, 3 = right-outer
        static Mesh BuildMesh(StreetEdge edge, Vector3[] centerline)
        {
            int n = centerline.Length;
            if (n < 2) return new Mesh { name = $"sidewalk_{edge.OsmWayId}" };

            float halfW      = edge.Width * 0.5f;
            float outerOffset = halfW + Width;

            var arcLen = new float[n];
            for (int i = 1; i < n; i++)
                arcLen[i] = arcLen[i - 1] + Vector3.Distance(centerline[i - 1], centerline[i]);
            float totalLen = arcLen[n - 1] < 0.001f ? 1f : arcLen[n - 1];

            var verts = new Vector3[n * 4];
            var uvs   = new Vector2[n * 4];

            for (int i = 0; i < n; i++)
            {
                Vector3 fwd;
                if      (i == 0)     fwd = (centerline[1]     - centerline[0]).normalized;
                else if (i == n - 1) fwd = (centerline[n - 1] - centerline[n - 2]).normalized;
                else                 fwd = (centerline[i + 1]  - centerline[i - 1]).normalized;

                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                float y = centerline[i].y + Raise;

                verts[i * 4]     = centerline[i] - right * outerOffset;  // left outer
                verts[i * 4 + 1] = centerline[i] - right * halfW;        // left inner
                verts[i * 4 + 2] = centerline[i] + right * halfW;        // right inner
                verts[i * 4 + 3] = centerline[i] + right * outerOffset;  // right outer

                // Snap Y after offsetting so the XZ offset doesn't pull the Y (right is horizontal).
                verts[i * 4].y     = y;
                verts[i * 4 + 1].y = y;
                verts[i * 4 + 2].y = y;
                verts[i * 4 + 3].y = y;

                float v = arcLen[i] / totalLen;
                uvs[i * 4]     = new Vector2(1f, v);  // left outer
                uvs[i * 4 + 1] = new Vector2(0f, v);  // left inner
                uvs[i * 4 + 2] = new Vector2(0f, v);  // right inner
                uvs[i * 4 + 3] = new Vector2(1f, v);  // right outer
            }

            // 2 strips × (n-1) quads × 2 triangles × 3 indices = (n-1) * 12
            var tris = new int[(n - 1) * 12];
            for (int i = 0; i < n - 1; i++)
            {
                int b  = i * 4;
                int t  = b + 4;
                int ti = i * 12;

                // Left strip: outer→inner, CW from above → normal up
                tris[ti]     = b;     tris[ti + 1] = t;     tris[ti + 2] = b + 1;
                tris[ti + 3] = t;     tris[ti + 4] = t + 1; tris[ti + 5] = b + 1;

                // Right strip: inner→outer, CW from above → normal up
                tris[ti + 6]  = b + 2; tris[ti + 7]  = t + 2; tris[ti + 8]  = b + 3;
                tris[ti + 9]  = t + 2; tris[ti + 10] = t + 3; tris[ti + 11] = b + 3;
            }

            var mesh = new Mesh { name = $"sidewalk_{edge.OsmWayId}" };
            mesh.vertices  = verts;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Bilinear-interpolated elevation at world (wx, wz) from the heightmap.
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
            float normalized = Mathf.Lerp(Mathf.Lerp(h00, h10, tc), Mathf.Lerp(h01, h11, tc), tr);

            return heightmap.MinElevationMeters + normalized * (heightmap.MaxElevationMeters - heightmap.MinElevationMeters);
        }

#if UNITY_EDITOR
        static void SaveMesh(Mesh mesh, ChunkCoord coord, long osmWayId)
        {
            string dir = $"{GeneratedAssets.ChunkDir(coord)}/Sidewalks";
            EnsureFolder(dir);
            AssetDatabase.CreateAsset(mesh, GeneratedAssets.SidewalkMesh(coord, osmWayId));
            Debug.Log($"[SidewalkMeshGenerator] Saved Sidewalks/sidewalk_{osmWayId}.mesh");
        }

        static void EnsureMaterial()
        {
            string path = GeneratedAssets.SidewalkMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            var mat = new Material(Shader.Find("Standard")) { name = "SidewalkSurface", color = new Color(0.8f, 0.8f, 0.8f) };
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log($"[SidewalkMeshGenerator] Created {path}");
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
