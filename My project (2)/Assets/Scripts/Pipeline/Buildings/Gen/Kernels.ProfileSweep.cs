using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    public static partial class Kernels
    {
        /// <summary>At a hairpin the mitre scale <c>1/cos(θ/2)</c> runs away; clamping the cosine
        /// at 0.25 caps the widening at 4× so a bad path produces a fat corner, not a spike
        /// (design #452 generators.md §3).</summary>
        public const float MitreCosClamp = 0.25f;

        /// <summary>
        /// Extrude a 2D cross-section along a 3D path — the workhorse kernel (#453, design #452
        /// generators.md §3). Cornices, sills, lintels, casings, mullions, rails, brackets, belt
        /// courses and water tables are all this, differing only in profile and path.
        /// </summary>
        /// <param name="profile">Cross-section in (across, up); see <see cref="Profiles"/> for the
        /// space and winding convention. Already scaled to metres.</param>
        /// <param name="path">Polyline in part-local space.</param>
        /// <param name="closedPath">True for <see cref="Paths.Rect"/>: stitch the last ring back to
        /// the first and mitre the seam instead of capping.</param>
        /// <param name="capEnds">Fan-cap the first and last ring (ignored when closed).</param>
        /// <param name="smoothAlong">Share one ring per path vertex, so normals blend along the
        /// run — what an arc wants. False duplicates rings per segment, giving each segment its own
        /// flat-shaded normals and a crisp corner, at the cost of double the vertices.</param>
        /// <param name="upHint">Which world direction the profile's <c>+across</c> axis leans
        /// toward. Default <c>+Z</c> — outward from the wall, the convention every facade part
        /// wants. One hint can only describe a path that stays in one plane; a path that turns in
        /// plan needs the per-point overload below (#483).</param>
        public static void ProfileSweep(MeshBuilder mb, Vector2[] profile, Vector3[] path,
                                        MaterialRole role, bool closedPath = false,
                                        bool capEnds = true, bool smoothAlong = false,
                                        Vector3 upHint = default)
            => Sweep(mb, profile, path, null, upHint, role, closedPath, capEnds, smoothAlong);

        /// <summary>
        /// <see cref="ProfileSweep(MeshBuilder, Vector2[], Vector3[], MaterialRole, bool, bool, bool, Vector3)"/>
        /// with a <b>per-point</b> frame instead of one hint for the whole run (#483).
        ///
        /// <para>One <c>upHint</c> is enough only while the path stays in a plane. A band that
        /// wraps a corner building turns in plan, and "which way is out of the wall" turns with it:
        /// feeding the run's first outward in as the single hint makes <c>across</c> collapse to
        /// zero at the turn and the degenerate guard swings the moulding vertical. Feeding
        /// <c>Vector3.up</c> instead is well-conditioned but transposes the frame, so the family has
        /// to transpose every section back — the trick #474 shipped and this overload removes.</para>
        ///
        /// <para><paramref name="outward"/> is exactly <c>FacadeRun.outward</c>: index-aligned to
        /// <paramref name="path"/>, one unit direction per point, the bisector of the two adjacent
        /// faces' outward normals at an interior vertex. The kernel makes it perpendicular to the
        /// tangent itself, so a bisector at a corner correctly resolves to the outward normal of
        /// whichever segment is being framed. With it the <c>(across, up)</c> convention
        /// <see cref="Profiles"/> is authored in — <c>+across</c> = outward from the wall, <c>+up</c>
        /// = the band's own height — holds for a turning path exactly as it does for a straight one
        /// (design #452 generators.md §3).</para>
        ///
        /// <para><b>Handedness still matters.</b> <c>up</c> is <c>cross(across, tangent)</c>, so
        /// traversing the same run the other way round flips it and builds the band upside down.
        /// A caller whose path direction is not its own choice settles it once, before sweeping —
        /// see <c>CorniceGenerator.Oriented</c>.</para>
        ///
        /// <para>A null or wrongly-sized <paramref name="outward"/> falls back to the default
        /// <c>+Z</c> hint, i.e. to the planar overload's behaviour.</para>
        /// </summary>
        public static void ProfileSweep(MeshBuilder mb, Vector2[] profile, Vector3[] path,
                                        Vector3[] outward, MaterialRole role,
                                        bool closedPath = false, bool capEnds = true,
                                        bool smoothAlong = false)
            => Sweep(mb, profile, path,
                     path != null && outward != null && outward.Length == path.Length ? outward : null,
                     Vector3.forward, role, closedPath, capEnds, smoothAlong);

        static void Sweep(MeshBuilder mb, Vector2[] profile, Vector3[] path, Vector3[] outward,
                          Vector3 upHint, MaterialRole role,
                          bool closedPath, bool capEnds, bool smoothAlong)
        {
            if (mb == null || profile == null || profile.Length < 2 || path == null) return;
            if (upHint == Vector3.zero) upHint = Vector3.forward;

            // The frame hint at path point i — the per-point outward when there is one, otherwise
            // the single hint. Every Frame() call below indexes by the point whose tangent it is
            // framing, which is why one accessor covers ring positions, per-segment normals and the
            // smoothed-along case alike.
            Vector3 Hint(int i) => outward != null ? outward[i] : upHint;

            int n = path.Length;
            if (closedPath && n < 3) closedPath = false;
            if (n < 2) return;

            int segCount = closedPath ? n : n - 1;
            int m = profile.Length;

            // ---- per-segment directions ------------------------------------------------
            var dir = new Vector3[segCount];
            for (int k = 0; k < segCount; k++)
            {
                Vector3 d = path[(k + 1) % n] - path[k];
                float len = d.magnitude;
                if (len < 1e-6f) return;              // duplicated path point: nothing sane to sweep
                dir[k] = d / len;
            }

            // ---- ring positions, mitred ------------------------------------------------
            // The mitre is done by *projection*, not by scaling an axis: take the offset the point
            // would have on the incoming segment and slide it along that segment until it meets the
            // mitre plane (through path[i], normal = the tangent bisector). That reproduces the
            // 1/cos(θ/2) widening exactly where the turn is, leaves the axis perpendicular to the
            // turn untouched, and needs no case analysis about which axis the corner bends in.
            // Without it, a rectangular casing pinches at every corner — the single most visible
            // defect this kernel can have.
            var ring = new Vector3[n][];
            var bisector = new Vector3[n];
            var inDir = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 dIn = closedPath ? dir[(i - 1 + segCount) % segCount]
                                         : dir[Mathf.Max(i - 1, 0)];
                Vector3 dOut = closedPath ? dir[i % segCount]
                                          : dir[Mathf.Min(i, segCount - 1)];
                Vector3 m0 = dIn + dOut;
                Vector3 mitreN = m0.sqrMagnitude < 1e-10f ? dIn : m0.normalized;   // 180° hairpin
                inDir[i] = dIn;
                bisector[i] = mitreN;

                Frame(dIn, Hint(i), out Vector3 across, out Vector3 up);
                float denom = Mathf.Max(Vector3.Dot(dIn, mitreN), MitreCosClamp);

                ring[i] = new Vector3[m];
                for (int j = 0; j < m; j++)
                {
                    Vector3 v = across * profile[j].x + up * profile[j].y;
                    float s = -Vector3.Dot(v, mitreN) / denom;
                    ring[i][j] = path[i] + v + dIn * s;
                }
            }

            // ---- profile normals, smoothed across the section --------------------------
            var pn = ProfileNormals(profile);

            mb.BeginRole(role);

            if (smoothAlong)
            {
                // One ring of vertices per path vertex — the vertex count generators.md §3 quotes.
                var idx = new int[n][];
                for (int i = 0; i < n; i++)
                {
                    Frame(bisector[i], Hint(i), out Vector3 across, out Vector3 up);
                    idx[i] = new int[m];
                    for (int j = 0; j < m; j++)
                    {
                        Vector3 nrm = (across * pn[j].x + up * pn[j].y).normalized;
                        idx[i][j] = mb.Vert(ring[i][j], nrm);
                    }
                }
                for (int k = 0; k < segCount; k++)
                    Stitch(mb, idx[k], idx[(k + 1) % n], pn, dir[k], Hint(k));
            }
            else
            {
                // A fresh ring pair per segment: same positions (so no cracks), but normals taken
                // from that segment's own frame, which is what keeps a mitred corner crisp.
                for (int k = 0; k < segCount; k++)
                {
                    int i0 = k, i1 = (k + 1) % n;
                    Frame(dir[k], Hint(k), out Vector3 across, out Vector3 up);
                    var a = new int[m];
                    var b = new int[m];
                    for (int j = 0; j < m; j++)
                    {
                        Vector3 nrm = (across * pn[j].x + up * pn[j].y).normalized;
                        a[j] = mb.Vert(ring[i0][j], nrm);
                        b[j] = mb.Vert(ring[i1][j], nrm);
                    }
                    Stitch(mb, a, b, pn, dir[k], Hint(k));
                }
            }

            // Caps get their own vertices either way — they face along the run, not along the
            // profile, so they can share nothing with the ring.
            if (!closedPath && capEnds)
            {
                Cap(mb, ring[0], -inDir[0]);
                Cap(mb, ring[n - 1], inDir[n - 1]);
            }
        }

        /// <summary>Orthonormal sweep frame for a tangent: <c>across</c> is <paramref name="hint"/>
        /// made perpendicular to the tangent (so it stays "outward from the wall" for every facade
        /// run), <c>up</c> completes the frame. Falls back to a world axis when the tangent runs
        /// parallel to the hint — the degenerate guard generators.md §3 calls for. The hint is one
        /// constant for a planar sweep and this point's own outward for a turning one (#483).</summary>
        static void Frame(Vector3 tangent, Vector3 hint, out Vector3 across, out Vector3 up)
        {
            across = hint - tangent * Vector3.Dot(hint, tangent);
            if (across.sqrMagnitude < 1e-8f)
            {
                Vector3 alt = Mathf.Abs(tangent.y) > 0.99f ? Vector3.forward : Vector3.up;
                across = alt - tangent * Vector3.Dot(alt, tangent);
            }
            across.Normalize();
            up = Vector3.Cross(across, tangent).normalized;
        }

        /// <summary>Normal per profile point, averaged across the two adjacent profile segments.
        /// Curved sections (cove, ogee, bullnose) want that; the stepped ones read fine with it at
        /// the scale a 10 cm moulding occupies.</summary>
        static Vector2[] ProfileNormals(Vector2[] profile)
        {
            int m = profile.Length;
            var segN = new Vector2[m - 1];
            for (int k = 0; k < m - 1; k++)
            {
                Vector2 t = (profile[k + 1] - profile[k]).normalized;
                segN[k] = new Vector2(t.y, -t.x);
            }
            var pn = new Vector2[m];
            pn[0] = segN[0];
            pn[m - 1] = segN[m - 2];
            for (int j = 1; j < m - 1; j++)
            {
                Vector2 sum = segN[j - 1] + segN[j];
                pn[j] = sum.sqrMagnitude < 1e-8f ? segN[j] : sum.normalized;
            }
            return pn;
        }

        // Stitch two rings into a quad strip, oriented by the profile's outward normal so the
        // surface faces the way the profile table says it should.
        static void Stitch(MeshBuilder mb, int[] a, int[] b,
                           Vector2[] pn, Vector3 tangent, Vector3 hint)
        {
            Frame(tangent, hint, out Vector3 across, out Vector3 up);
            for (int j = 0; j < a.Length - 1; j++)
            {
                Vector2 f = (pn[j] + pn[j + 1]);
                Vector3 outward = across * f.x + up * f.y;
                mb.QuadFacing(a[j], a[j + 1], b[j + 1], b[j], outward);
            }
        }

        // Fan-cap an open end from the ring's first point. A profile is an open outline, so the cap
        // is the polygon it bounds together with the segment closing it back to the start. A
        // two-point profile (Flat) has no area to cap and correctly produces nothing.
        static void Cap(MeshBuilder mb, Vector3[] ringPos, Vector3 outward)
        {
            if (ringPos == null || ringPos.Length < 3) return;
            var v = new int[ringPos.Length];
            for (int j = 0; j < ringPos.Length; j++) v[j] = mb.Vert(ringPos[j], outward);
            for (int j = 1; j < v.Length - 1; j++) mb.TriFacing(v[0], v[j], v[j + 1], outward);
        }
    }
}
