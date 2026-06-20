#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Stage 5: extrudes OSM building footprints into meshes with walls + flat roof.
    // Requires HeightmapData (from ElevationParser) for base-Y sampling.
    public static class BuildingGenerator
    {
        public static GameObject Generate(
            IReadOnlyList<BuildingWay> buildings,
            HeightmapData heightmap,
            ChunkBounds chunk,
            ChunkCoord coord,
            float defaultBuildingHeight = 10f)
        {
            var parent = new GameObject("Buildings");

            foreach (var b in buildings)
            {
                var mesh = GenerateMesh(b, heightmap, chunk, coord, defaultBuildingHeight);
                if (mesh == null) continue;

                var go = new GameObject($"building_{b.OsmId}");
                go.transform.SetParent(parent.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>();
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

#if UNITY_EDITOR
            AssetDatabase.SaveAssets();
#endif
            return parent;
        }

        static Mesh GenerateMesh(BuildingWay b, HeightmapData heightmap, ChunkBounds chunk, ChunkCoord coord,
            float defaultBuildingHeight)
        {
            // Trim closing vertex if OSM polygon is closed (first == last node)
            int n = b.Footprint.Length;
            if (n > 1 && b.Footprint[0] == b.Footprint[n - 1]) n--;
            if (n < 3) return null;

            // Normalized index order — CCW in XZ so wall normals point outward
            var poly = new int[n];
            for (int i = 0; i < n; i++) poly[i] = i;
            if (SignedAreaXZ(b.Footprint, poly, n) < 0f)
                System.Array.Reverse(poly);

            float cx = 0f, cz = 0f;
            for (int i = 0; i < n; i++) { cx += b.Footprint[poly[i]].x; cz += b.Footprint[poly[i]].z; }
            cx /= n; cz /= n;

            float baseY  = SampleTerrainHeight(cx, cz, heightmap, chunk);
            float height = b.Height > 0f ? b.Height : defaultBuildingHeight;
            float topY   = baseY + height;

            var verts = new List<Vector3>();
            var tris  = new List<int>();
            BuildWalls(b.Footprint, poly, n, baseY, topY, verts, tris);
            BuildRoof(b.Footprint, poly, n, topY, verts, tris);

            var mesh = new Mesh { name = $"building_{b.OsmId}" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

#if UNITY_EDITOR
            string path = GeneratedAssets.BuildingMesh(coord, b.OsmId);
            EnsureFolder(path[..path.LastIndexOf('/')]);
            AssetDatabase.CreateAsset(mesh, path);
#endif
            return mesh;
        }

        // Nearest-neighbour sample from the heightmap to get actual world-space Y at (wx, wz).
        static float SampleTerrainHeight(float wx, float wz, HeightmapData h, ChunkBounds chunk)
        {
            Rect r = chunk.WorldRect;
            float nx = Mathf.Clamp01((wx - r.x) / r.width);
            float nz = Mathf.Clamp01((wz - r.y) / r.height);
            // Values[row,col]: row=0 is south (min Z), col=0 is west (min X)
            int row = Mathf.RoundToInt(nz * (h.Resolution - 1));
            int col = Mathf.RoundToInt(nx * (h.Resolution - 1));
            float t = h.Values[row, col];
            return h.MinElevationMeters + t * (h.MaxElevationMeters - h.MinElevationMeters);
        }

        // Quad strip around the perimeter. Polygon must be CCW in XZ.
        // CW triangle winding so RecalculateNormals produces outward-facing normals.
        static void BuildWalls(Vector3[] fp, int[] poly, int n, float baseY, float topY,
            List<Vector3> verts, List<int> tris)
        {
            for (int i = 0; i < n; i++)
            {
                int a = poly[i], b = poly[(i + 1) % n];
                int v = verts.Count;
                verts.Add(new Vector3(fp[a].x, baseY, fp[a].z));  // v+0  B0
                verts.Add(new Vector3(fp[b].x, baseY, fp[b].z));  // v+1  B1
                verts.Add(new Vector3(fp[b].x, topY,  fp[b].z));  // v+2  T1
                verts.Add(new Vector3(fp[a].x, topY,  fp[a].z));  // v+3  T0
                tris.Add(v + 0); tris.Add(v + 2); tris.Add(v + 1);
                tris.Add(v + 0); tris.Add(v + 3); tris.Add(v + 2);
            }
        }

        // Flat roof cap via ear-clipping. Polygon must be CCW in XZ.
        // Triangles emitted in reverse (CW) so RecalculateNormals produces +Y normals.
        static void BuildRoof(Vector3[] fp, int[] poly, int n, float topY,
            List<Vector3> verts, List<int> tris)
        {
            int vBase = verts.Count;
            for (int i = 0; i < n; i++)
                verts.Add(new Vector3(fp[poly[i]].x, topY, fp[poly[i]].z));

            var ring = new List<int>(n);
            for (int i = 0; i < n; i++) ring.Add(i);

            while (ring.Count >= 3)
            {
                bool clipped = false;
                for (int i = 0; i < ring.Count; i++)
                {
                    int pi = ring[(i - 1 + ring.Count) % ring.Count];
                    int ci = ring[i];
                    int ni = ring[(i + 1) % ring.Count];

                    Vector3 pa = verts[vBase + pi];
                    Vector3 pb = verts[vBase + ci];
                    Vector3 pc = verts[vBase + ni];

                    // Convex vertex in CCW polygon: local cross product > 0
                    float cross = (pb.x - pa.x) * (pc.z - pa.z) - (pb.z - pa.z) * (pc.x - pa.x);
                    if (cross <= 0f) continue;

                    bool isEar = true;
                    foreach (int ri in ring)
                    {
                        if (ri == pi || ri == ci || ri == ni) continue;
                        if (PointInTriangleXZ(verts[vBase + ri], pa, pb, pc)) { isEar = false; break; }
                    }
                    if (!isEar) continue;

                    // Emit CW (pi, ci, ni reversed) for +Y normal
                    tris.Add(vBase + ni);
                    tris.Add(vBase + ci);
                    tris.Add(vBase + pi);
                    ring.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // degenerate polygon — stop rather than infinite loop
            }
        }

        // Shoelace signed area in XZ. Positive = CCW, negative = CW.
        static float SignedAreaXZ(Vector3[] pts, int[] idx, int n)
        {
            float area = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = pts[idx[i]], b = pts[idx[(i + 1) % n]];
                area += a.x * b.z - b.x * a.z;
            }
            return area * 0.5f;
        }

        // Edge-crossing point-in-triangle test in XZ plane.
        static bool PointInTriangleXZ(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = (p.x - a.x) * (b.z - a.z) - (p.z - a.z) * (b.x - a.x);
            float d2 = (p.x - b.x) * (c.z - b.z) - (p.z - b.z) * (c.x - b.x);
            float d3 = (p.x - c.x) * (a.z - c.z) - (p.z - c.z) * (a.x - c.x);
            return (d1 >= 0f && d2 >= 0f && d3 >= 0f) || (d1 <= 0f && d2 <= 0f && d3 <= 0f);
        }

#if UNITY_EDITOR
        static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
#endif
    }
}
