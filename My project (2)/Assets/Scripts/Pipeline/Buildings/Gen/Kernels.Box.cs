using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// The three geometry kernels every procedural building artifact is composed from (#453,
    /// design #452 D3). Split across files by kernel; call them as <c>Kernels.Box(…)</c>, or
    /// <c>using static SFMap.Pipeline.Buildings.Gen.Kernels;</c> for the bare names the design uses.
    /// </summary>
    public static partial class Kernels
    {
        /// <summary>
        /// An axis-aligned box: slabs, panels, bars, blocking (design #452 D3).
        /// 24 verts / 12 tris at <see cref="Faces.All"/>, 20/10 with the back dropped —
        /// and roughly a third of a facade's box triangles are that never-visible back face.
        /// </summary>
        /// <param name="bevel">Chamfer width on every edge between two emitted faces. This is how
        /// <see cref="DetailLevel.Reduced"/> degrades a swept profile to something an order of
        /// magnitude cheaper (design #452 generators.md §2). 0 leaves the box sharp and emits
        /// nothing beyond the six face quads.</param>
        public static void Box(MeshBuilder mb, Vector3 size, Vector3 center, MaterialRole role,
                               Faces faces = Faces.All, float bevel = 0f)
        {
            if (mb == null || faces == Faces.None) return;
            Vector3 h = size * 0.5f;
            float maxBevel = Mathf.Min(Mathf.Abs(h.x), Mathf.Min(Mathf.Abs(h.y), Mathf.Abs(h.z)));
            bevel = Mathf.Clamp(bevel, 0f, maxBevel * 0.99f);

            mb.BeginRole(role);

            // Faces are indexed (axis, sign): 0 = +X, 1 = -X, 2 = +Y, 3 = -Y, 4 = +Z, 5 = -Z, which
            // is the bit order of Faces.
            for (int f = 0; f < 6; f++)
            {
                if (!Has(faces, f)) continue;
                int axis = f >> 1;
                int sign = (f & 1) == 0 ? 1 : -1;
                Vector3 nrm = Axis(axis) * sign;

                // Corners 0..3 are a proper loop round the face; QuadFacing settles the winding
                // against the outward normal, so no handedness case analysis per face.
                var idx = new int[4];
                for (int c = 0; c < 4; c++)
                    idx[c] = mb.Vert(FaceCorner(center, h, bevel, axis, sign, c), nrm);
                mb.QuadFacing(idx[0], idx[1], idx[2], idx[3], nrm);
            }

            if (bevel <= 0f) return;

            // ---- chamfer bands, one per edge shared by two emitted faces -------------------
            for (int f1 = 0; f1 < 6; f1++)
            {
                if (!Has(faces, f1)) continue;
                for (int f2 = f1 + 1; f2 < 6; f2++)
                {
                    if (!Has(faces, f2)) continue;
                    int a1 = f1 >> 1, a2 = f2 >> 1;
                    if (a1 == a2) continue;                       // opposite faces share no edge
                    int s1 = (f1 & 1) == 0 ? 1 : -1, s2 = (f2 & 1) == 0 ? 1 : -1;
                    int a3 = 3 - a1 - a2;                         // the axis the shared edge runs along
                    Vector3 nrm = (Axis(a1) * s1 + Axis(a2) * s2).normalized;

                    int pA0 = mb.Vert(EdgePoint(center, h, bevel, a1, s1, a2, s2, a3, -1), nrm);
                    int pB0 = mb.Vert(EdgePoint(center, h, bevel, a2, s2, a1, s1, a3, -1), nrm);
                    int pB1 = mb.Vert(EdgePoint(center, h, bevel, a2, s2, a1, s1, a3, 1), nrm);
                    int pA1 = mb.Vert(EdgePoint(center, h, bevel, a1, s1, a2, s2, a3, 1), nrm);
                    mb.QuadFacing(pA0, pB0, pB1, pA1, nrm);
                }
            }

            // ---- corner triangles, where three emitted faces meet -------------------------
            for (int cx = -1; cx <= 1; cx += 2)
                for (int cy = -1; cy <= 1; cy += 2)
                    for (int cz = -1; cz <= 1; cz += 2)
                    {
                        int fx = cx > 0 ? 0 : 1, fy = cy > 0 ? 2 : 3, fz = cz > 0 ? 4 : 5;
                        if (!Has(faces, fx) || !Has(faces, fy) || !Has(faces, fz)) continue;
                        Vector3 nrm = (new Vector3(cx, cy, cz)).normalized;
                        int px = mb.Vert(CornerPoint(center, h, bevel, 0, cx, cy, cz), nrm);
                        int py = mb.Vert(CornerPoint(center, h, bevel, 1, cx, cy, cz), nrm);
                        int pz = mb.Vert(CornerPoint(center, h, bevel, 2, cx, cy, cz), nrm);
                        mb.TriFacing(px, py, pz, nrm);
                    }
        }

        static bool Has(Faces faces, int faceIndex) => (faces & (Faces)(1 << faceIndex)) != 0;

        static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        // Corner `corner` (0..3, counter-clockwise seen from +axis) of face (axis, sign). The face
        // stays on its own plane; only its in-plane extents pull in by `bevel`.
        static Vector3 FaceCorner(Vector3 center, Vector3 h, float bevel, int axis, int sign, int corner)
        {
            int b = (axis + 1) % 3, c = (axis + 2) % 3;
            int sb = (corner == 0 || corner == 3) ? -1 : 1;
            int sc = (corner == 0 || corner == 1) ? -1 : 1;
            return center + Axis(axis) * (h[axis] * sign)
                          + Axis(b) * ((h[b] - bevel) * sb)
                          + Axis(c) * ((h[c] - bevel) * sc);
        }

        // A point on face (axisA, signA)'s boundary toward face (axisB, signB), at end `end` of the
        // shared edge along axisRun.
        static Vector3 EdgePoint(Vector3 center, Vector3 h, float bevel,
                                 int axisA, int signA, int axisB, int signB, int axisRun, int end)
            => center + Axis(axisA) * (h[axisA] * signA)
                      + Axis(axisB) * ((h[axisB] - bevel) * signB)
                      + Axis(axisRun) * ((h[axisRun] - bevel) * end);

        // The corner-triangle vertex contributed by the face whose normal is `axis`.
        static Vector3 CornerPoint(Vector3 center, Vector3 h, float bevel, int axis, int cx, int cy, int cz)
        {
            var s = new Vector3(cx, cy, cz);
            Vector3 p = center;
            for (int a = 0; a < 3; a++)
                p += Axis(a) * ((a == axis ? h[a] : h[a] - bevel) * s[a]);
            return p;
        }
    }
}
