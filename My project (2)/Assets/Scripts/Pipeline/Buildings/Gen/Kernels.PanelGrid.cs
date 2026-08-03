using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// Parameters for <see cref="Kernels.PanelGrid"/> — every glazed or panelled opening: window
    /// sash, door leaf, garage door, storefront bay, transom (#453, design #452 generators.md §4).
    /// The opening spans <c>x ∈ [-w/2, w/2]</c>, <c>y ∈ [0, h]</c> (BottomCenter anchor, matching
    /// <c>BuildingAssembler.PlacePart</c>'s frame), with the wall plane at <c>z = 0</c>.
    /// </summary>
    public struct PanelGridParams
    {
        public float w, h;              // opening size (m)
        public float[] cols, rows;      // relative weights; {1,1,1} = thirds, {2,1} = 2:1. Length 1 = undivided
        public float barW, barD;        // muntin width / depth
        public float frameW, frameD;    // surrounding frame (0 = no frame; storefront bays share mullions)
        public float infillInset;       // how far the infill sits behind the frame front
        public float panelBevel;        // >0 → raised solid panel (doors, garage), 0 → flat (glass)
        public MaterialRole frameRole;  // Accent1 (painted wood) | Metal (steel sash, aluminium slider)
        public MaterialRole infillRole; // Glass | Base | Accent2

        /// <summary>Even weights for <paramref name="n"/> lights — the common case, and what a
        /// division like <c>{6,4}</c> in a neighborhood preset expands to.</summary>
        public static float[] Even(int n)
        {
            n = Mathf.Max(n, 1);
            var wts = new float[n];
            for (int i = 0; i < n; i++) wts[i] = 1f;
            return wts;
        }
    }

    public static partial class Kernels
    {
        /// <summary>
        /// Frame sweep + interior bars + per-cell infill + reveal jambs (#453, design #452
        /// generators.md §4). Composes the other two kernels; only step 4 does its own vertex math.
        /// Bars sit on interior cell edges only, so <c>cols.Length == 1</c> correctly yields a
        /// single undivided light with no bar — the Sunset aluminium slider.
        /// </summary>
        /// <param name="offsetY">Lifts the whole grid, so stacked sashes share a meeting rail.</param>
        /// <param name="offsetZ">Pushes it back into the reveal.</param>
        public static void PanelGrid(MeshBuilder mb, in PanelGridParams p,
                                     float offsetY = 0f, float offsetZ = 0f)
        {
            if (mb == null || p.w <= 0f || p.h <= 0f) return;

            Vector3 off = new Vector3(0f, offsetY, offsetZ);
            float fw = Mathf.Max(p.frameW, 0f);
            float innerW = p.w - 2f * fw;
            float innerH = p.h - 2f * fw;
            if (innerW <= 1e-4f || innerH <= 1e-4f) return;

            var cols = Sanitize(p.cols);
            var rows = Sanitize(p.rows);
            float[] xs = EdgePositions(cols, -innerW * 0.5f, innerW);
            float[] ys = EdgePositions(rows, fw, innerH);

            float infillZ = -Mathf.Max(p.infillInset, 0f);

            // 1. frame — one mitred closed sweep round the opening, on the band's centreline.
            if (fw > 0f)
            {
                ProfileSweep(mb, Profiles.Scaled(Profiles.Flat, Mathf.Max(p.frameD, 0f), fw),
                             Paths.Rect(p.w - fw, p.h - fw, off + new Vector3(0f, p.h * 0.5f, 0f)),
                             p.frameRole, closedPath: true, capEnds: false, smoothAlong: false);
            }

            // 2/3. muntins — a box per interior cell edge, back face dropped (it faces the glass).
            float barZ = infillZ + Mathf.Max(p.barD, 0f) * 0.5f;
            for (int k = 1; k < xs.Length - 1; k++)
                Box(mb, new Vector3(p.barW, innerH, p.barD),
                    off + new Vector3(xs[k], fw + innerH * 0.5f, barZ), p.frameRole, Faces.NoBack);
            for (int k = 1; k < ys.Length - 1; k++)
                Box(mb, new Vector3(innerW, p.barW, p.barD),
                    off + new Vector3(0f, ys[k], barZ), p.frameRole, Faces.NoBack);

            // 4. infill — one quad per cell. Per-cell (rather than one quad behind all cells) is
            //    what lets panelBevel promote cells to raised panels for doors and garage leaves
            //    with no branch in the caller; the vertex cost is 4 verts × cells.
            mb.BeginRole(p.infillRole);
            for (int ci = 0; ci < xs.Length - 1; ci++)
                for (int ri = 0; ri < ys.Length - 1; ri++)
                    Cell(mb, off, xs[ci], xs[ci + 1], ys[ri], ys[ri + 1], infillZ, p.panelBevel);

            // 5. reveal — four jamb quads from the wall plane back to the infill plane. This is
            //    what makes an opening read as a hole rather than a sticker.
            if (infillZ < 0f) Reveal(mb, p.w, p.h, -infillZ, MaterialRole.Base, off);
        }

        /// <summary>The four jamb quads of an opening, wall plane → sash plane. Public because the
        /// window family calls it directly as step 1 of its generation (generators.md §5.2), before
        /// any sash exists.</summary>
        public static void Reveal(MeshBuilder mb, float w, float h, float depth,
                                  MaterialRole role, Vector3 origin = default)
        {
            if (mb == null || depth <= 0f) return;
            mb.BeginRole(role);
            float hw = w * 0.5f;
            float z0 = 0f, z1 = -depth;

            // Normals point in toward the opening's axis — you see the jambs from outside, looking
            // into the hole.
            Jamb(mb, origin, new Vector3(-hw, 0f, z0), new Vector3(-hw, h, z0),
                             new Vector3(-hw, h, z1), new Vector3(-hw, 0f, z1), Vector3.right);
            Jamb(mb, origin, new Vector3(hw, 0f, z0), new Vector3(hw, h, z0),
                             new Vector3(hw, h, z1), new Vector3(hw, 0f, z1), Vector3.left);
            Jamb(mb, origin, new Vector3(-hw, 0f, z0), new Vector3(hw, 0f, z0),
                             new Vector3(hw, 0f, z1), new Vector3(-hw, 0f, z1), Vector3.up);
            Jamb(mb, origin, new Vector3(-hw, h, z0), new Vector3(hw, h, z0),
                             new Vector3(hw, h, z1), new Vector3(-hw, h, z1), Vector3.down);
        }

        static void Jamb(MeshBuilder mb, Vector3 o, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            int ia = mb.Vert(o + a, n), ib = mb.Vert(o + b, n);
            int ic = mb.Vert(o + c, n), id = mb.Vert(o + d, n);
            mb.QuadFacing(ia, ib, ic, id, n);
        }

        // One infill cell: a flat quad for glass, a five-face frustum for a raised panel.
        static void Cell(MeshBuilder mb, Vector3 o, float x0, float x1, float y0, float y1,
                         float z, float bevel)
        {
            Vector3 n = Vector3.forward;
            if (bevel <= 0f)
            {
                int a = mb.Vert(o + new Vector3(x0, y0, z), n);
                int b = mb.Vert(o + new Vector3(x1, y0, z), n);
                int c = mb.Vert(o + new Vector3(x1, y1, z), n);
                int d = mb.Vert(o + new Vector3(x0, y1, z), n);
                mb.QuadFacing(a, b, c, d, n);
                return;
            }

            // Raised panel: the field stands proud by `bevel`, inset by `bevel` on every side, with
            // four sloped returns down to the cell edge.
            float bx = Mathf.Min(bevel, (x1 - x0) * 0.45f);
            float by = Mathf.Min(bevel, (y1 - y0) * 0.45f);
            float zf = z + bevel;
            int f0 = mb.Vert(o + new Vector3(x0 + bx, y0 + by, zf), n);
            int f1 = mb.Vert(o + new Vector3(x1 - bx, y0 + by, zf), n);
            int f2 = mb.Vert(o + new Vector3(x1 - bx, y1 - by, zf), n);
            int f3 = mb.Vert(o + new Vector3(x0 + bx, y1 - by, zf), n);
            mb.QuadFacing(f0, f1, f2, f3, n);

            Return(mb, o, new Vector3(x0, y0, z), new Vector3(x1, y0, z),
                          new Vector3(x1 - bx, y0 + by, zf), new Vector3(x0 + bx, y0 + by, zf));
            Return(mb, o, new Vector3(x1, y0, z), new Vector3(x1, y1, z),
                          new Vector3(x1 - bx, y1 - by, zf), new Vector3(x1 - bx, y0 + by, zf));
            Return(mb, o, new Vector3(x1, y1, z), new Vector3(x0, y1, z),
                          new Vector3(x0 + bx, y1 - by, zf), new Vector3(x1 - bx, y1 - by, zf));
            Return(mb, o, new Vector3(x0, y1, z), new Vector3(x0, y0, z),
                          new Vector3(x0 + bx, y0 + by, zf), new Vector3(x0 + bx, y1 - by, zf));
        }

        static void Return(MeshBuilder mb, Vector3 o, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            if (n.sqrMagnitude < 1e-8f) return;
            if (n.z < 0f) n = -n;                       // returns always face out of the opening
            int ia = mb.Vert(o + a, n), ib = mb.Vert(o + b, n);
            int ic = mb.Vert(o + c, n), id = mb.Vert(o + d, n);
            mb.QuadFacing(ia, ib, ic, id, n);
        }

        // Weights → cell edge positions across [start, start + span]. Length 1 gives two edges and
        // therefore one undivided cell with no interior bar.
        static float[] EdgePositions(float[] weights, float start, float span)
        {
            float total = 0f;
            for (int i = 0; i < weights.Length; i++) total += weights[i];
            var edges = new float[weights.Length + 1];
            edges[0] = start;
            float acc = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                acc += weights[i];
                edges[i + 1] = start + span * (acc / total);
            }
            return edges;
        }

        static float[] Sanitize(float[] weights)
        {
            if (weights == null || weights.Length == 0) return PanelGridParams.Even(1);
            float total = 0f;
            for (int i = 0; i < weights.Length; i++) total += Mathf.Max(weights[i], 0f);
            if (total <= 0f) return PanelGridParams.Even(weights.Length);
            var w = new float[weights.Length];
            for (int i = 0; i < weights.Length; i++) w[i] = Mathf.Max(weights[i], 0f);
            return w;
        }
    }
}
