using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// The polylines <see cref="Kernels.ProfileSweep"/> extrudes along (#453, design #452
    /// generators.md §3). All of them live in part-local space: <b>+X along the facade, +Y up,
    /// +Z outward toward the street</b>, which is the frame <c>BuildingAssembler.PlacePart</c>
    /// already establishes from <c>f.bearing_deg</c>.
    /// </summary>
    public static class Paths
    {
        /// <summary>Below this rise an <see cref="Arc"/> is a straight line to within float
        /// precision, and the circle math it would otherwise do divides by zero.</summary>
        public const float FlatRiseEpsilon = 1e-6f;

        /// <summary>Straight run from <paramref name="a"/> to <paramref name="b"/>, subdivided
        /// into <paramref name="segments"/> spans (so <c>segments + 1</c> points).</summary>
        public static Vector3[] Line(Vector3 a, Vector3 b, int segments = 1)
        {
            segments = Mathf.Max(segments, 1);
            var pts = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
                pts[i] = Vector3.LerpUnclamped(a, b, (float)i / segments);
            return pts;
        }

        /// <summary>A rectangle in the facade plane (XY), counter-clockwise from the bottom-left.
        /// Four points — sweep it with <c>closedPath: true</c> so the seam and all four corners
        /// are mitred.</summary>
        public static Vector3[] Rect(float w, float h, Vector3 center = default)
        {
            float hw = w * 0.5f, hh = h * 0.5f;
            return new[]
            {
                center + new Vector3(-hw, -hh, 0f),
                center + new Vector3( hw, -hh, 0f),
                center + new Vector3( hw,  hh, 0f),
                center + new Vector3(-hw,  hh, 0f),
            };
        }

        /// <summary>
        /// A circular arc of chord <paramref name="w"/> and sagitta <paramref name="rise"/> in the
        /// facade plane, left to right, apex at <c>+Y</c>. <c>rise = w/2</c> is a semicircle (round
        /// head), <c>rise &lt; w/2</c> is segmental, and <b><c>rise = 0</c> is exactly
        /// <see cref="Line"/></b> — so a flat window head and an arched one are the same code path
        /// with one parameter changed (#453 acceptance 2, design #452 generators.md §3).
        /// <para>The parametrisation is continuous as <c>rise → 0</c>; the explicit guard below is
        /// there to make the degenerate case bit-identical to <see cref="Line"/> rather than
        /// identical-to-within-rounding, and to keep <c>sin φ</c> out of a denominator.</para>
        /// </summary>
        public static Vector3[] Arc(float w, float rise, int segments, Vector3 center = default)
        {
            segments = Mathf.Max(segments, 1);
            float hw = w * 0.5f;
            Vector3 a = center + new Vector3(-hw, 0f, 0f);
            Vector3 b = center + new Vector3(hw, 0f, 0f);
            if (Mathf.Abs(rise) < FlatRiseEpsilon) return Line(a, b, segments);

            // Half the arc's included angle, from the sagitta: tan(phi/2) = 2*rise / w.
            float phi = 2f * Mathf.Atan2(2f * rise, w);
            float sinPhi = Mathf.Sin(phi);
            float cosPhi = Mathf.Cos(phi);

            var pts = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float theta = (2f * t - 1f) * phi;
                // x = R sin(theta), y = R (cos(theta) - cos(phi)), with R = hw / sin(phi) folded in
                // so the ratio stays well-conditioned for small phi.
                float x = hw * Mathf.Sin(theta) / sinPhi;
                float y = hw * (Mathf.Cos(theta) - cosPhi) / sinPhi;
                pts[i] = center + new Vector3(x, y, 0f);
            }
            return pts;
        }

        /// <summary>Stoop/step polyline: from the origin, alternately up <paramref name="rise"/>
        /// and out <paramref name="run"/> along +Z (toward the street). <c>2·steps + 1</c> points;
        /// callers mirror it for a descending flight.</summary>
        public static Vector3[] Stair(int steps, float rise, float run)
        {
            steps = Mathf.Max(steps, 1);
            var pts = new Vector3[steps * 2 + 1];
            Vector3 p = Vector3.zero;
            pts[0] = p;
            for (int i = 0; i < steps; i++)
            {
                p += new Vector3(0f, rise, 0f);
                pts[i * 2 + 1] = p;
                p += new Vector3(0f, 0f, run);
                pts[i * 2 + 2] = p;
            }
            return pts;
        }

        /// <summary>An arbitrary polyline — bay plans, pediments, anything the named generators
        /// do not cover. Copied so the caller can keep mutating its own array.</summary>
        public static Vector3[] Polyline(params Vector3[] pts)
        {
            var copy = new Vector3[pts != null ? pts.Length : 0];
            if (pts != null) System.Array.Copy(pts, copy, pts.Length);
            return copy;
        }
    }
}
