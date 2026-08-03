using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>Which cornice this is. It selects the <b>crown cross-section</b> and, for
    /// <see cref="Parapet"/>, whether a wall rises above the roofline — everything else that
    /// distinguishes a Noe bracketed cornice from a SoMa corbel one is a number in the parameter
    /// bag (design #452 design.md §2 Cornice row, §3).</summary>
    public enum CorniceFamily { Cove, Ogee, Bracketed, Corbel, Parapet }

    /// <summary>A parapet's top edge. Like <see cref="HeadType"/> in the window family, it selects
    /// the <b>scallop rise and nothing else</b>: <see cref="Flat"/> is rise 0, and a zero-rise
    /// scallop is <c>Paths.Arc(rise: 0)</c> which <i>is</i> <c>Paths.Line</c>. There is no
    /// flat-parapet branch anywhere in this file.</summary>
    public enum ParapetShape { Flat, Scalloped }

    /// <summary>
    /// The cornice family (#474, design #452 design.md §2/§3) — and the library's <b>first
    /// <see cref="IPathPartGenerator"/></b>, so it is what makes <c>BuildingAssembler</c>'s #459
    /// wrapped-run path live. A cornice on a corner building is not two cornices: it is one mitred
    /// sweep along the polyline that chains every street facade through its corners.
    ///
    /// <para><b>Almost pure <see cref="Kernels.ProfileSweep"/>.</b> The crown, the dentils, the
    /// brackets and the parapet are all the same kernel with a different cross-section and a
    /// different path. The only per-family code here is the parameter reading and the vertical
    /// stack-up.</para>
    ///
    /// <para><b>Frame — the #459 question, settled.</b> <c>ProfileSweep</c> takes one
    /// <c>upHint</c> for the whole sweep, so a cornice <i>cannot</i> build its profile axis from
    /// <see cref="FacadeRun.outward"/>: outward changes at every turn, and feeding the first
    /// point's outward into a run that turns 90° makes the frame exactly degenerate
    /// (<c>across</c> collapses to zero and the guard swings the profile vertical). The convention
    /// that works for a band that turns is <c>upHint = Vector3.up</c>, which for a horizontal
    /// tangent gives <c>across = +Y</c> and <c>up = cross(+Y, tangent)</c> — i.e. the sweep frame's
    /// two axes are the <b>transpose</b> of the (outward, height) frame the <see cref="Profiles"/>
    /// table is authored in. <see cref="Banded"/> transposes each profile point back onto the right
    /// axes. <c>outward</c> is still load-bearing, but as a <i>handedness oracle</i>
    /// (see <see cref="Oriented"/>) and for the end returns — not as an up-hint.</para>
    ///
    /// <para><b>Anchor.</b> <c>y = 0</c> in the run's frame is the <c>PlacementY</c> roofline, and
    /// that is the right anchor: the crown's <b>top</b> sits on it and the moulding hangs below,
    /// so the band always lands on real wall and nothing floats over the roof. A
    /// <see cref="CorniceFamily.Parapet"/> is the one thing that deliberately rises above it —
    /// which is what a parapet is.</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>),
    /// so every name read here is listed with its fallback; a misspelling silently takes that
    /// fallback. Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>profileFamily</term><description><see cref="CorniceFamily"/> (Cove)</description></item>
    /// <item><term>projection, heightM</term><description>crown projection / band height, m (0.30, 0.35)</description></item>
    /// <item><term>returnEnds</term><description>die the moulding back into the wall at a free end (true)</description></item>
    /// <item><term>returnDepth</term><description>how far it returns, m (falls back to <c>projection</c>)</description></item>
    /// <item><term>dentilPitch</term><description>repeat pitch, m; <b>0 = no dentil band</b> (0)</description></item>
    /// <item><term>dentilW, dentilH</term><description>one block's width / height, m (0.06, 0.10)</description></item>
    /// <item><term>dentilProjection</term><description>how far a block stands proud (0.5 × projection)</description></item>
    /// <item><term>bracketSpacing</term><description>bracket pitch, m; <b>0 = no brackets</b> (1.5 for Bracketed, else 0)</description></item>
    /// <item><term>bracketW, bracketD, bracketH</term><description>width along the run / projection / drop (0.10, 1.2 × projection, 1.6 × heightM)</description></item>
    /// <item><term>bracketProfile</term><description><see cref="ProfileId"/> of the scroll section (Ogee)</description></item>
    /// <item><term>parapetRise</term><description>how far the parapet wall rises above the roofline, m (0.9)</description></item>
    /// <item><term>parapetShape</term><description><see cref="ParapetShape"/> (Flat)</description></item>
    /// <item><term>parapetThickness</term><description>wall thickness of the parapet slab, m (0.18)</description></item>
    /// <item><term>scallopWidth, scallopRise</term><description>one scallop's chord / sagitta (1.2, 0.35 × parapetRise)</description></item>
    /// <item><term>corniceRole</term><description><see cref="MaterialRole"/> of crown + dentils (Accent2)</description></item>
    /// <item><term>bracketRole</term><description>brackets (falls back to <c>corniceRole</c>)</description></item>
    /// <item><term>parapetRole</term><description>the parapet slab — stucco reads as the wall (Base)</description></item>
    /// <item><term>runLengthM</term><description>the straight run the single-facade fallback builds, m (8)</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/>, via <see cref="PartParams.Detail"/></description></item>
    /// </list>
    /// </summary>
    public sealed class CorniceGenerator : IPathPartGenerator
    {
        public const string GeneratorId = "cornice.banded";

        /// <summary>Points per scallop arc. Halved below <see cref="DetailLevel.Full"/>: the
        /// silhouette survives being coarser far better than it survives being flattened, which is
        /// why <c>Reduced</c> keeps the scallops and only <c>Flat</c> drops them.</summary>
        const int ScallopArcSegments = 6;

        /// <summary>How deep a scallop may get relative to its own chord — see the cusp note in
        /// <see cref="ScallopedTop"/>. 0.18 keeps the mitre widening at a cusp under ~15%.</summary>
        const float MaxScallopRiseRatio = 0.18f;

        public string Id => GeneratorId;

        // ---- the two entry points --------------------------------------------------------

        /// <summary>The single-facade fallback <see cref="IPartGenerator"/> requires, used when the
        /// sidecar carried no facade geometry to chain. It is not a second implementation: it
        /// synthesises the straight run a one-facade building would have produced — origin-centred,
        /// +X along the facade, +Z outward, exactly the part-local frame
        /// <c>BuildingAssembler.PlacePart</c> establishes — and hands it to
        /// <see cref="GenerateAlong"/>.</summary>
        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            float len = Mathf.Max(p.GetFloat("runLengthM", 8f), 0.5f);
            var run = new FacadeRun(
                new[] { new Vector3(-len * 0.5f, 0f, 0f), new Vector3(len * 0.5f, 0f, 0f) },
                new[] { Vector3.forward, Vector3.forward },
                closed: false);
            return GenerateAlong(p, run, mb);
        }

        public PartMesh GenerateAlong(PartParams p, FacadeRun run, MeshBuilder mb)
        {
            if (mb == null || !run.IsValid) return default;

            var family = p.GetEnum("profileFamily", CorniceFamily.Cove);
            float projection = Mathf.Max(p.GetFloat("projection", 0.30f), 1e-3f);
            float heightM = Mathf.Max(p.GetFloat("heightM", 0.35f), 1e-3f);
            DetailLevel detail = p.Detail;

            // The run may arrive traversed either way round; the sweep frame's handedness depends
            // on that, so settle it once before anything is built.
            FacadeRun r = Oriented(run);

            bool parapet = family == CorniceFamily.Parapet;
            float parapetRise = parapet ? Mathf.Max(p.GetFloat("parapetRise", 0.9f), 0f) : 0f;
            // Scallops are the one element DetailLevel.Flat drops outright: at the floor the whole
            // point is one face per segment, and a scalloped path multiplies the segment count.
            float scallopRise = parapet && detail != DetailLevel.Flat &&
                                p.GetEnum("parapetShape", ParapetShape.Flat) == ParapetShape.Scalloped
                ? Mathf.Clamp(p.GetFloat("scallopRise", parapetRise * 0.35f), 0f, parapetRise)
                : 0f;
            bool curved = scallopRise >= Paths.FlatRiseEpsilon;

            float returnDepth = r.closed || !p.GetBool("returnEnds", true)
                ? 0f : Mathf.Max(p.GetFloat("returnDepth", projection), 0f);

            // Two paths, both continuous through every corner of the run: the wall line itself, and
            // the band's top edge. rise = 0 makes the second the first, lifted — so a flat roofline
            // and a scalloped parapet are one code path with one parameter changed.
            Vector3[] runPath = WithReturns(Shifted(r.points, 0f), r, returnDepth);
            Vector3[] bandPath = curved
                ? WithReturns(ScallopedTop(r, parapetRise, scallopRise,
                                           Mathf.Max(p.GetFloat("scallopWidth", 1.2f), 0.1f),
                                           detail == DetailLevel.Full ? ScallopArcSegments : ScallopArcSegments / 2),
                              r, returnDepth)
                : Shifted(runPath, parapetRise);

            SetRunRect(mb, bandPath, parapetRise + heightM);

            var corniceRole = p.GetEnum("corniceRole", MaterialRole.Accent2);

            // ---- the DetailLevel.Flat floor ------------------------------------------------
            // A run has no single facade rect, so the floor is the run's own flat face: one quad
            // per segment, no caps (a 2-point profile bounds no area). That is 2 triangles per
            // straight facade — exactly the placeholder band this pipeline replaced.
            if (detail == DetailLevel.Flat)
            {
                float faceH = parapet ? Mathf.Max(parapetRise, heightM) : heightM;
                Kernels.ProfileSweep(mb, Banded(Profiles.Flat, projection, faceH),
                                     Shifted(runPath, (parapet ? parapetRise : 0f) - faceH * 0.5f),
                                     corniceRole, closedPath: r.closed, capEnds: false,
                                     smoothAlong: false, upHint: Vector3.up);
                return mb.Finish(GeneratorId);
            }

            // ---- 1. the parapet wall -------------------------------------------------------
            // Straight and horizontal, up to the scallops' TROUGH line — deliberately not swept
            // along the scalloped path. ProfileSweep mitres a turn by widening the section
            // 1/cos(θ/2) about the path normal, which is right for a moulding and wrong for a tall
            // slab following a cusped line: at each scallop cusp the wall's top edge would spike
            // above the crests. Keeping the wall on the straight run leaves the cusp to the band,
            // whose section is short enough for the widening to read as the cusp detail it is.
            float wallTop = parapetRise - scallopRise;
            if (parapet && wallTop > 1e-3f)
                Kernels.ProfileSweep(mb, Banded(Profiles.Dentil,
                                                Mathf.Max(p.GetFloat("parapetThickness", 0.18f), 1e-3f), wallTop),
                                     Shifted(runPath, wallTop * 0.5f),
                                     p.GetEnum("parapetRole", MaterialRole.Base),
                                     closedPath: r.closed, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.up);

            // ---- 2. the crown — mitred through every corner --------------------------------
            // Its TOP sits on the band path, so a plain cornice hangs off the roofline and a
            // parapet's coping caps it. The band is a scallopRise taller than the authored height
            // precisely so it reaches down past the trough line at a crest and closes the lobe;
            // at rise 0 that term vanishes and the band is the authored moulding.
            float bandH = heightM + scallopRise;
            Kernels.ProfileSweep(mb, Banded(Sectioned(family, detail), projection, bandH),
                                 Shifted(bandPath, -bandH * 0.5f), corniceRole,
                                 closedPath: r.closed, capEnds: true, smoothAlong: curved,
                                 upHint: Vector3.up);

            // Everything below hangs off the roofline, not off the parapet, so it is laid along the
            // run rather than the (possibly scalloped, possibly lifted) band path.
            float below = parapet ? 0f : -heightM;

            // ---- 3. dentil band ------------------------------------------------------------
            float dentilPitch = Mathf.Max(p.GetFloat("dentilPitch", 0f), 0f);
            float dentilH = Mathf.Max(p.GetFloat("dentilH", 0.10f), 0f);
            if (dentilPitch > 0f && dentilH > 0f)
            {
                // Reduced halves the density rather than deleting the band. Deleting it is far
                // cheaper and wrong: the stepped course IS the SoMa cornice, so a roofline without
                // it reads as the wrong building rather than as a coarser one. This is the same
                // trade the window family makes when it halves the muntin count.
                float pitch = detail == DetailLevel.Full ? dentilPitch : dentilPitch * 2f;
                var block = Banded(Profiles.Dentil,
                                   Mathf.Max(p.GetFloat("dentilProjection", projection * 0.5f), 1e-3f), dentilH);
                float dentilW = Mathf.Clamp(p.GetFloat("dentilW", 0.06f), 1e-3f, pitch);
                RepeatAlong(r, pitch, dentilW, below - dentilH * 0.5f,
                            (a, b, y) => Sweep(mb, block, a, b, y, corniceRole));
                below -= dentilH;
            }

            // ---- 4. brackets ---------------------------------------------------------------
            float bracketSpacing = Mathf.Max(
                p.GetFloat("bracketSpacing", family == CorniceFamily.Bracketed ? 1.5f : 0f), 0f);
            float bracketH = Mathf.Max(p.GetFloat("bracketH", heightM * 1.6f), 0f);
            if (bracketSpacing > 0f && bracketH > 0f)
            {
                // Halved at Reduced, for the same reason the dentil band is.
                float spacing = detail == DetailLevel.Full ? bracketSpacing : bracketSpacing * 2f;
                var scroll = Banded(Sectioned(p.GetEnum("bracketProfile", ProfileId.Ogee), detail),
                                    Mathf.Max(p.GetFloat("bracketD", projection * 1.2f), 1e-3f), bracketH);
                float bracketW = Mathf.Clamp(p.GetFloat("bracketW", 0.10f), 1e-3f, spacing);
                var bracketRole = p.GetEnum("bracketRole", corniceRole);
                RepeatAlong(r, spacing, bracketW, below - bracketH * 0.5f,
                            (a, b, y) => Sweep(mb, scroll, a, b, y, bracketRole));
            }

            return mb.Finish(GeneratorId);
        }

        // ---- frame ------------------------------------------------------------------------

        /// <summary>
        /// The run, traversed so that <c>ProfileSweep</c>'s <c>up</c> axis lands on the outward side
        /// of the wall. With <c>upHint = +Y</c> that axis is <c>cross(+Y, tangent)</c>, whose sign
        /// depends purely on which way round the placement layer happened to chain the facades — and
        /// getting it wrong builds the whole cornice <i>inside</i> the building. The handedness is
        /// global along a run (every <see cref="FacadeRun.outward"/> points away from the same
        /// footprint), so testing the first segment settles all of them.
        /// <para>This is what <see cref="FacadeRun.outward"/> is actually for in this family; see
        /// the frame remarks on the class.</para>
        /// </summary>
        static FacadeRun Oriented(FacadeRun run)
        {
            Vector3 t = run.points[1] - run.points[0];
            t.y = 0f;
            if (t.sqrMagnitude < 1e-10f) return run;
            if (Vector3.Dot(Vector3.Cross(Vector3.up, t.normalized), run.outward[0]) >= 0f) return run;

            int n = run.points.Length;
            var pts = new Vector3[n];
            var outs = new Vector3[n];
            for (int i = 0; i < n; i++) { pts[i] = run.points[n - 1 - i]; outs[i] = run.outward[n - 1 - i]; }
            return new FacadeRun(pts, outs, run.closed);
        }

        /// <summary>
        /// A <see cref="Profiles"/> cross-section put onto a horizontal run's sweep axes.
        /// <para>The table is authored as <c>(across = outward from the wall, up = the band's own
        /// height)</c>. A run swept with <c>upHint = +Y</c> gets those two the other way round —
        /// <c>across</c> is world up and <c>up</c> is the wall's outward normal — so each point's
        /// components swap. Swapping alone is a reflection, which would invert every surface normal
        /// the winding rule derives; reversing the point order restores it, and the swept surface is
        /// identical because the point <i>set</i> is unchanged.</para>
        /// </summary>
        static Vector2[] Banded(Vector2[] profile, float projection, float height)
        {
            if (profile == null) return null;
            int m = profile.Length;
            var s = new Vector2[m];
            for (int i = 0; i < m; i++)
            {
                Vector2 q = profile[m - 1 - i];
                s[i] = new Vector2(q.y * height, q.x * projection);
            }
            return s;
        }

        static Vector2[] Banded(ProfileId id, float projection, float height)
            => Banded(Profiles.Get(id), projection, height);

        // ---- paths -------------------------------------------------------------------------

        /// <summary>
        /// The line the band is swept along: the run lifted to <paramref name="baseY"/>, with each
        /// segment's top edge broken into scallop arcs of rise <paramref name="rise"/>.
        ///
        /// <para>Each segment carries a whole number of scallops, so every scallop chain starts and
        /// ends at a <i>trough</i> — which is exactly the run's own corner vertex. That is what lets
        /// a scalloped parapet stay ONE polyline across a corner instead of one per facade, and it
        /// is why the corner still gets mitred.</para>
        ///
        /// <para><c>rise &lt; </c><see cref="Paths.FlatRiseEpsilon"/> returns the run unchanged
        /// rather than a subdivided copy of it: <c>Paths.Arc</c> already degenerates to
        /// <c>Paths.Line</c> there, so this is the same geometry with fewer rings.</para>
        /// </summary>
        static Vector3[] ScallopedTop(FacadeRun r, float baseY, float rise, float width, int arcSegments)
        {
            if (rise < Paths.FlatRiseEpsilon) return Shifted(r.points, baseY);

            arcSegments = Mathf.Max(arcSegments, 2);
            int n = r.points.Length;
            var pts = new List<Vector3>(n * 4);

            for (int k = 0; k < r.SegmentCount; k++)
            {
                Vector3 a = r.points[k];
                Vector3 d = r.points[(k + 1) % n] - a;
                d.y = 0f;
                float len = d.magnitude;
                if (len < 1e-4f) continue;
                Vector3 dir = d / len;

                int count = Mathf.Max(Mathf.RoundToInt(len / width), 1);
                float w = len / count;
                // A scallop's end tangent leaves the chord at 2·atan(2·rise/w), so the cusp between
                // two of them turns through twice that — and ProfileSweep widens the swept section
                // by 1/cos of half the turn. Past this ratio that widening stops reading as a cusp
                // and starts reading as a spike, so the rise is capped rather than the sweep hacked.
                float lobe = Mathf.Min(rise, w * MaxScallopRiseRatio);
                var arc = Paths.Arc(w, lobe, arcSegments);     // x ∈ [-w/2, w/2], y ∈ [0, lobe]

                for (int i = 0; i < count; i++)
                    for (int j = 0; j <= arcSegments; j++)
                    {
                        // Every scallop's first point is the previous one's last — and at a segment
                        // boundary it is the corner vertex itself. Emitted once.
                        if (j == 0 && pts.Count > 0) continue;
                        // Troughs sit at baseY − rise (the AUTHORED rise, not the capped lobe) so
                        // every segment's chain — and therefore every corner vertex — is at one
                        // height, which is what keeps the scalloped top a single polyline.
                        pts.Add(a + dir * (i * w + w * 0.5f + arc[j].x)
                                  + new Vector3(0f, baseY - rise + arc[j].y, 0f));
                    }
            }

            // A closed run's last scallop lands back on points[0]; ProfileSweep closes the loop
            // itself, so the repeat has to go or the seam is a zero-length segment.
            if (r.closed && pts.Count > 1 && (pts[pts.Count - 1] - pts[0]).sqrMagnitude < 1e-6f)
                pts.RemoveAt(pts.Count - 1);

            return pts.ToArray();
        }

        /// <summary>The path with a short leg at each free end running back into the wall, so the
        /// moulding dies into the masonry instead of ending in mid-air. The leg's own sweep frame is
        /// the mitre of the turn, which is what a real mitred return is.</summary>
        static Vector3[] WithReturns(Vector3[] path, FacadeRun r, float depth)
        {
            if (depth <= 0f || path == null || path.Length < 2) return path;
            var s = new Vector3[path.Length + 2];
            s[0] = path[0] - r.outward[0] * depth;
            System.Array.Copy(path, 0, s, 1, path.Length);
            s[s.Length - 1] = path[path.Length - 1] - r.outward[r.outward.Length - 1] * depth;
            return s;
        }

        static Vector3[] Shifted(Vector3[] pts, float dy)
        {
            var s = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++) s[i] = new Vector3(pts[i].x, pts[i].y + dy, pts[i].z);
            return s;
        }

        // ---- repeated elements --------------------------------------------------------------

        /// <summary>Lay a repeated element along every segment of the run at
        /// <paramref name="pitch"/>, centred on each segment so a facade reads as symmetrical and a
        /// corner is never straddled by half a block. The callback receives the element's two end
        /// points on the wall line and the height its centre sits at.</summary>
        static void RepeatAlong(FacadeRun r, float pitch, float itemLen, float y,
                                System.Action<Vector3, Vector3, float> emit)
        {
            int n = r.points.Length;
            for (int k = 0; k < r.SegmentCount; k++)
            {
                Vector3 a = r.points[k];
                Vector3 d = r.points[(k + 1) % n] - a;
                d.y = 0f;
                float len = d.magnitude;
                if (len < itemLen) continue;
                Vector3 dir = d / len;

                int count = Mathf.FloorToInt(len / pitch);
                if (count < 1) continue;
                float lead = (len - count * pitch) * 0.5f + (pitch - itemLen) * 0.5f;
                for (int i = 0; i < count; i++)
                {
                    float s = lead + i * pitch;
                    emit(a + dir * s, a + dir * (s + itemLen), y);
                }
            }
        }

        /// <summary>One repeated element: the section swept the short distance from
        /// <paramref name="a"/> to <paramref name="b"/> along the run. A dentil block and a scroll
        /// bracket differ only in which section and how far — neither needs a kernel of its own, and
        /// a 4-point block costs the same 10 triangles a back-dropped <see cref="Kernels.Box"/>
        /// would while following a facade that runs along any bearing.</summary>
        static void Sweep(MeshBuilder mb, Vector2[] section, Vector3 a, Vector3 b, float y, MaterialRole role)
            => Kernels.ProfileSweep(mb, section,
                                    Paths.Line(new Vector3(a.x, a.y + y, a.z), new Vector3(b.x, b.y + y, b.z)),
                                    role, closedPath: false, capEnds: true, smoothAlong: false,
                                    upHint: Vector3.up);

        // ---- DetailLevel degradation, local to this family -----------------------------------

        /// <summary>The crown section for a family. <see cref="CorniceFamily.Bracketed"/> takes an
        /// Ogee crown — the brackets, not the moulding, are what make it bracketed — and
        /// <see cref="CorniceFamily.Parapet"/> a Bullnose, which is what a stucco coping reads
        /// as.</summary>
        static Vector2[] Sectioned(CorniceFamily family, DetailLevel detail)
        {
            switch (family)
            {
                case CorniceFamily.Ogee:
                case CorniceFamily.Bracketed: return Sectioned(ProfileId.Ogee, detail);
                case CorniceFamily.Corbel: return Sectioned(ProfileId.Corbel, detail);
                case CorniceFamily.Parapet: return Sectioned(ProfileId.Bullnose, detail);
                default: return Sectioned(ProfileId.Cove, detail);
            }
        }

        /// <summary>As the window family's <c>Sectioned</c>: below <see cref="DetailLevel.Full"/> a
        /// moulding is swept as a 3-point <see cref="ProfileId.Chamfer"/> instead of its authored
        /// 5–7 point section. Degrading the profile rather than the kernel keeps one code path and
        /// is what actually gets cheaper (design #452 generators.md §5.2 and the note on it in
        /// <c>DoubleHungWindowGenerator</c>).</summary>
        static Vector2[] Sectioned(ProfileId id, DetailLevel detail)
            => Profiles.Get(detail == DetailLevel.Full || id == ProfileId.None ? id : ProfileId.Chamfer);

        // ---- UVs ------------------------------------------------------------------------------

        /// <summary>
        /// <c>uv1</c> normalises against the part's own rect (design #452 D4). A wrapped run has no
        /// single along-facade axis — that is the whole point of it — so the honest rect is the
        /// run's horizontal bounding extent by the band's vertical extent. Decals (#280/#281) land
        /// consistently on a straight run and are approximate around a corner; stated rather than
        /// silently wrong.
        /// </summary>
        static void SetRunRect(MeshBuilder mb, Vector3[] path, float height)
        {
            if (path == null || path.Length == 0) return;
            float minX = path[0].x, maxX = path[0].x, minZ = path[0].z, maxZ = path[0].z;
            for (int i = 1; i < path.Length; i++)
            {
                minX = Mathf.Min(minX, path[i].x); maxX = Mathf.Max(maxX, path[i].x);
                minZ = Mathf.Min(minZ, path[i].z); maxZ = Mathf.Max(maxZ, path[i].z);
            }
            mb.SetLocalRect(Mathf.Max(maxX - minX, maxZ - minZ), Mathf.Max(height, 1e-3f),
                            new Vector2(minX, -height));
        }
    }
}
