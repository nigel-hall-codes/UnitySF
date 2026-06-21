#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Stage 5: stamps terrain flat under each road segment, then generates a quad-strip mesh.
    public static class RoadMeshGenerator
    {
        public static IReadOnlyList<Mesh> Generate(
            StreetGraph graph,
            HeightmapData heightmap,
            Rect worldRect,
            ChunkCoord coord,
            IReadOnlyDictionary<StreetEdge, (Vector3? from, Vector3? to)> boundaries = null,
            float widthMultiplier = 1f)
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

                    (Vector3? from, Vector3? to) bd = default;
                    if (boundaries != null) boundaries.TryGetValue(edge, out bd);

                    Vector3[] stamped = StampedCenterline(edge, heightmap, worldRect);
                    // Elevate boundary points to pre-stamp terrain height, matching StampedCenterline.
                    Vector3? fromPt = bd.from.HasValue
                        ? new Vector3(bd.from.Value.x, SampleElevation(bd.from.Value.x, bd.from.Value.z, heightmap, worldRect), bd.from.Value.z)
                        : (Vector3?)null;
                    Vector3? toPt = bd.to.HasValue
                        ? new Vector3(bd.to.Value.x, SampleElevation(bd.to.Value.x, bd.to.Value.z, heightmap, worldRect), bd.to.Value.z)
                        : (Vector3?)null;
                    // Stamp the full centerline so terrain near intersection nodes is flattened —
                    // the intersection mesh overwrites this area later.
                    StampFootprint(edge, stamped, heightmap, worldRect, widthMultiplier);
                    Vector3[] anchored = AnchorCenterline(stamped, fromPt, toPt);
                    Mesh mesh = BuildMesh(edge, anchored, widthMultiplier);

#if UNITY_EDITOR
                    SaveMesh(mesh, coord, edge.OsmWayId);
                    meshPaths.Add(GeneratedAssets.RoadMesh(coord, edge.OsmWayId));
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
            foreach (var path in meshPaths)
                meshes.Add(AssetDatabase.LoadAssetAtPath<Mesh>(path));
#endif

            return meshes;
        }

        // Copies the edge centerline, replacing Y with sampled terrain elevation.
        static Vector3[] StampedCenterline(StreetEdge edge, HeightmapData heightmap, Rect worldRect)
        {
            var cl = edge.Centerline;
            var out_ = new Vector3[cl.Length];
            for (int i = 0; i < cl.Length; i++)
                out_[i] = new Vector3(cl[i].x, SampleElevation(cl[i].x, cl[i].z, heightmap, worldRect), cl[i].z);
            return out_;
        }

        // Mirrors SidewalkMeshGenerator.Width; stamp must cover the full sidewalk footprint.
        const float SidewalkWidth = 1.5f;

        // Flattens heightmap cells under the road to the interpolated road elevation.
        static void StampFootprint(StreetEdge edge, Vector3[] centerline,
            HeightmapData heightmap, Rect worldRect, float widthMultiplier = 1f)
        {
            float halfW = edge.Width * widthMultiplier * 0.5f;
            int res = heightmap.Resolution;
            float cellW = worldRect.width  / (res - 1);
            float cellH = worldRect.height / (res - 1);
            float elevRange = heightmap.MaxElevationMeters - heightmap.MinElevationMeters;
            if (elevRange < 0.001f) elevRange = 1f;
            // Pad by sidewalk width + half-cell diagonal so terrain is flat under the full
            // road-and-sidewalk footprint, preventing terrain bleed at the outer sidewalk edge.
            float pad = Mathf.Sqrt(cellW * cellW + cellH * cellH) * 0.5f;
            float stampW = halfW + SidewalkWidth + pad;

            for (int seg = 0; seg < centerline.Length - 1; seg++)
            {
                Vector3 p0 = centerline[seg];
                Vector3 p1 = centerline[seg + 1];

                Vector2 dir2 = new Vector2(p1.x - p0.x, p1.z - p0.z);
                float segLen = dir2.magnitude;
                if (segLen < 0.001f) continue;
                Vector2 dirN = dir2 / segLen;
                Vector2 perp = new Vector2(-dirN.y, dirN.x);

                int colMin = Mathf.Max(0,       Mathf.FloorToInt((Mathf.Min(p0.x, p1.x) - stampW - worldRect.x) / cellW));
                int colMax = Mathf.Min(res - 1, Mathf.CeilToInt ((Mathf.Max(p0.x, p1.x) + stampW - worldRect.x) / cellW));
                int rowMin = Mathf.Max(0,       Mathf.FloorToInt((Mathf.Min(p0.z, p1.z) - stampW - worldRect.y) / cellH));
                int rowMax = Mathf.Min(res - 1, Mathf.CeilToInt ((Mathf.Max(p0.z, p1.z) + stampW - worldRect.y) / cellH));

                for (int row = rowMin; row <= rowMax; row++)
                {
                    float wz = worldRect.y + row * cellH;
                    for (int col = colMin; col <= colMax; col++)
                    {
                        float wx = worldRect.x + col * cellW;
                        var toP = new Vector2(wx - p0.x, wz - p0.z);
                        float along   = Vector2.Dot(toP, dirN);
                        float lateral = Mathf.Abs(Vector2.Dot(toP, perp));
                        if (along < -pad || along > segLen + pad || lateral > stampW) continue;

                        float t = along / segLen;
                        float elev = Mathf.Lerp(p0.y, p1.y, t);
                        heightmap.Values[row, col] = (elev - heightmap.MinElevationMeters) / elevRange;
                    }
                }
            }
        }

        // Anchors the centerline to exact intersection boundary points, dropping any interior
        // vertices that fall between the two anchors (handles short roads between close intersections).
        static Vector3[] AnchorCenterline(Vector3[] cl, Vector3? fromPt, Vector3? toPt)
        {
            if (!fromPt.HasValue && !toPt.HasValue) return cl;

            int n = cl.Length;
            var arc = new float[n];
            for (int i = 1; i < n; i++)
                arc[i] = arc[i - 1] + Vector3.Distance(cl[i - 1], cl[i]);
            float total = arc[n - 1];

            float startArc = fromPt.HasValue ? Vector3.Distance(cl[0], fromPt.Value) : 0f;
            float endArc   = toPt.HasValue   ? total - Vector3.Distance(cl[n - 1], toPt.Value) : total;

            if (endArc - startArc < 0.01f) return cl; // degenerate: road fully inside intersections

            var result = new List<Vector3> { fromPt ?? cl[0] };
            for (int i = 1; i < n - 1; i++)
                if (arc[i] > startArc && arc[i] < endArc)
                    result.Add(cl[i]);
            result.Add(toPt ?? cl[n - 1]);
            return result.ToArray();
        }

        const float Raise = 0.05f;

        // Builds a quad-strip mesh along the stamped centerline.
        static Mesh BuildMesh(StreetEdge edge, Vector3[] centerline, float widthMultiplier = 1f)
        {
            int n = centerline.Length;
            if (n < 2) return new Mesh { name = $"road_{edge.OsmWayId}" };

            float halfW = edge.Width * widthMultiplier * 0.5f;

            // Accumulate arc-lengths for UV.v
            var arcLen = new float[n];
            for (int i = 1; i < n; i++)
                arcLen[i] = arcLen[i - 1] + Vector3.Distance(centerline[i - 1], centerline[i]);
            float totalLen = arcLen[n - 1] < 0.001f ? 1f : arcLen[n - 1];

            var verts = new Vector3[n * 2];
            var uvs   = new Vector2[n * 2];

            for (int i = 0; i < n; i++)
            {
                Vector3 fwd;
                if      (i == 0)     fwd = (centerline[1]     - centerline[0]).normalized;
                else if (i == n - 1) fwd = (centerline[n - 1] - centerline[n - 2]).normalized;
                else                 fwd = (centerline[i + 1]  - centerline[i - 1]).normalized;

                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 center = centerline[i] + Vector3.up * Raise;
                verts[i * 2]     = center - right * halfW;
                verts[i * 2 + 1] = center + right * halfW;

                float v = arcLen[i] / totalLen;
                uvs[i * 2]     = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(1f, v);
            }

            var tris = new int[(n - 1) * 6];
            for (int i = 0; i < n - 1; i++)
            {
                int bl = i * 2,  br = i * 2 + 1;
                int tl = bl + 2, tr = br + 2;
                int ti = i * 6;
                tris[ti]     = bl; tris[ti + 1] = tl; tris[ti + 2] = br;
                tris[ti + 3] = tl; tris[ti + 4] = tr; tris[ti + 5] = br;
            }

            var mesh = new Mesh { name = $"road_{edge.OsmWayId}" };
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
            string dir = $"{GeneratedAssets.ChunkDir(coord)}/Roads";
            EnsureFolder(dir);
            AssetDatabase.CreateAsset(mesh, GeneratedAssets.RoadMesh(coord, osmWayId));
            Debug.Log($"[RoadMeshGenerator] Saved Roads/road_{osmWayId}.mesh");
        }

        static void EnsureMaterial()
        {
            string path = GeneratedAssets.RoadMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            var mat = new Material(Shader.Find("Standard")) { name = "RoadSurface", color = new Color(0.5f, 0.5f, 0.5f) };
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log($"[RoadMeshGenerator] Created {path}");
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
