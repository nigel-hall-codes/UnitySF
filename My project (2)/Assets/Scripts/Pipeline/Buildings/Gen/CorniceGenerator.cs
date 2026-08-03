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
    /// <para><b>Frame — the #459 question, settled by #483.</b> A band that turns needs a frame
    /// that turns with it, so every sweep here goes through <c>ProfileSweep</c>'s <b>per-point</b>
    /// overload and hands it the run's own <see cref="FacadeRun.outward"/> array. The sections are
    /// therefore scaled with plain <see cref="Profiles.Scaled"/> in the authored <c>(across, up)</c>
    /// convention — <c>+across</c> = outward from the wall, <c>+up</c> = the band's height —
    /// exactly as a slot artifact's are, and generators.md §3's convention holds here too.
    /// <c>outward</c> remains load-bearing as a <i>handedness oracle</i> as well
    /// (see <see cref="Oriented"/>) and for the end returns.</para>
    ///
    /// <para>Before #483 the kernel took one <c>upHint</c> for a whole sweep. Feeding it the first
    /// point's outward made <c>across</c> collapse to zero at a 90° turn (the guard then swung the
    /// moulding vertical), so the family passed <c>Vector3.up</c> and transposed every section back
    /// onto the resulting mirrored axes. That transpose is gone; every derived path below now
    /// carries an outward direction per point instead.</para>
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

            // Two paths, both continuous through every corner of the run and both carrying an
            // outward direction per point (#483): the wall line itself, and the band's top edge.
            // rise = 0 makes the second the first, lifted — so a flat roofline and a scalloped
            // parapet are one code path with one parameter changed.
            FacadeRun runPath = WithReturns(r, r, returnDepth);
            FacadeRun bandPath = curved
                ? WithReturns(ScallopedTop(r, parapetRise, scallopRise,
                                           Mathf.Max(p.GetFloat("scallopWidth", 1.2f), 0.1f),
                                           detail == DetailLevel.Full ? ScallopArcSegments : ScallopArcSegments / 2),
                              r, returnDepth)
                : Shifted(runPath, parapetRise);

            SetRunRect(mb, bandPath.points, parapetRise + heightM);

            var corniceRole = p.GetEnum("corniceRole", MaterialRole.Accent2);

            // ---- the DetailLevel.Flat floor ------------------------------------------------
            // A run has no single facade rect, so the floor is the run's own flat face: one quad
            // per segment, no caps (a 2-point profile bounds no area). That is 2 triangles per
            // straight facade — exactly the placeholder band this pipeline replaced.
            if (detail == DetailLevel.Flat)
            {
                float faceH = parapet ? Mathf.Max(parapetRise, heightM) : heightM;
                SweepRun(mb, Profiles.Scaled(Profiles.Flat, projection, faceH),
                         Shifted(runPath, (parapet ? parapetRise : 0f) - faceH * 0.5f),
                         corniceRole, capEnds: false, smoothAlong: false);
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
                SweepRun(mb, Profiles.Scaled(Profiles.Dentil,
                                             Mathf.Max(p.GetFloat("parapetThickness", 0.18f), 1e-3f), wallTop),
                         Shifted(runPath, wallTop * 0.5f),
                         p.GetEnum("parapetRole", MaterialRole.Base),
                         capEnds: true, smoothAlong: false);

            // ---- 2. the crown — mitred through every corner --------------------------------
            // Its TOP sits on the band path, so a plain cornice hangs off the roofline and a
            // parapet's coping caps it. The band is a scallopRise taller than the authored height
            // precisely so it reaches down past the trough line at a crest and closes the lobe;
            // at rise 0 that term vanishes and the band is the authored moulding.
            float bandH = heightM + scallopRise;
            SweepRun(mb, Profiles.Scaled(Sectioned(family, detail), projection, bandH),
                     Shifted(bandPath, -bandH * 0.5f), corniceRole,
                     capEnds: true, smoothAlong: curved);

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
                var block = Profiles.Scaled(Profiles.Dentil,
                                            Mathf.Max(p.GetFloat("dentilProjection", projection * 0.5f), 1e-3f), dentilH);
                float dentilW = Mathf.Clamp(p.GetFloat("dentilW", 0.06f), 1e-3f, pitch);
                RepeatAlong(r, pitch, dentilW, below - dentilH * 0.5f,
                            (a, b, y, o) => Sweep(mb, block, a, b, y, o, corniceRole));
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
                var scroll = Profiles.Scaled(Detail.Section(p.GetEnum("bracketProfile", ProfileId.Ogee), detail),
                                             Mathf.Max(p.GetFloat("bracketD", projection * 1.2f), 1e-3f), bracketH);
                float bracketW = Mathf.Clamp(p.GetFloat("bracketW", 0.10f), 1e-3f, spacing);
                var bracketRole = p.GetEnum("bracketRole", corniceRole);
                RepeatAlong(r, spacing, bracketW, below - bracketH * 0.5f,
                            (a, b, y, o) => Sweep(mb, scroll, a, b, y, o, bracketRole));
            }

            return mb.Finish(GeneratorId);
        }

        // ---- frame ------------------------------------------------------------------------

        /// <summary>
        /// The run, traversed so that <c>ProfileSweep</c>'s <c>up</c> axis lands on the sky side of
        /// the band. With a per-point outward frame that axis is <c>cross(outward, tangent)</c>,
        /// whose sign depends purely on which way round the placement layer happened to chain the
        /// facades — and getting it wrong builds the whole cornice upside down and inside the
        /// building. The handedness is global along a run (every <see cref="FacadeRun.outward"/>
        /// points away from the same footprint), so testing the first segment settles all of them.
        /// <para>This is one of the two things <see cref="FacadeRun.outward"/> is for in this
        /// family; see the frame remarks on the class.</para>
        /// </summary>
        static FacadeRun Oriented(FacadeRun run)
        {
            Vector3 t = run.points[1] - run.points[0];
            t.y = 0f;
            if (t.sqrMagnitude < 1e-10f) return run;
            if (Vector3.Dot(Vector3.Cross(run.outward[0], t.normalized), Vector3.up) >= 0f) return run;

            int n = run.points.Length;
            var pts = new Vector3[n];
            var outs = new Vector3[n];
            for (int i = 0; i < n; i++) { pts[i] = run.points[n - 1 - i]; outs[i] = run.outward[n - 1 - i]; }
            return new FacadeRun(pts, outs, run.closed);
        }

        /// <summary>One sweep along a derived run: the section in the authored
        /// <c>(across = outward, up = height)</c> convention, the path and its per-point outward
        /// straight off the <see cref="FacadeRun"/> (#483).</summary>
        static void SweepRun(MeshBuilder mb, Vector2[] section, FacadeRun path, MaterialRole role,
                             bool capEnds, bool smoothAlong)
            => Kernels.ProfileSweep(mb, section, path.points, path.outward, role,
                                    closedPath: path.closed, capEnds: capEnds, smoothAlong: smoothAlong);

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
        ///
        /// <para>Every point generated for segment <c>k</c> carries that segment's own
        /// <c>r.outward[k]</c> as its frame hint. That is correct for the shared corner vertex too:
        /// <c>ProfileSweep</c> makes the hint perpendicular to the tangent it is framing, and the
        /// bisector at a corner resolves to the outward normal of whichever segment is asking.</para>
        /// </summary>
        static FacadeRun ScallopedTop(FacadeRun r, float baseY, float rise, float width, int arcSegments)
        {
            if (rise < Paths.FlatRiseEpsilon) return Shifted(r, baseY);

            arcSegments = Mathf.Max(arcSegments, 2);
            int n = r.points.Length;
            var pts = new List<Vector3>(n * 4);
            var outs = new List<Vector3>(n * 4);

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
                        outs.Add(r.outward[k]);
                    }
            }

            // A closed run's last scallop lands back on points[0]; ProfileSweep closes the loop
            // itself, so the repeat has to go or the seam is a zero-length segment.
            if (r.closed && pts.Count > 1 && (pts[pts.Count - 1] - pts[0]).sqrMagnitude < 1e-6f)
            {
                pts.RemoveAt(pts.Count - 1);
                outs.RemoveAt(outs.Count - 1);
            }

            return new FacadeRun(pts.ToArray(), outs.ToArray(), r.closed);
        }

        /// <summary>
        /// The path with a short leg at each free end running back into the wall, so the moulding
        /// dies into the masonry instead of ending in mid-air.
        ///
        /// <para>A return is a corner like any other, so it needs outward directions like any other
        /// (#483). The leg's own outward is the direction the run <i>came from</i> — turn the band
        /// into the wall and its face turns to look back along the facade — and the junction takes
        /// the bisector of the two, which is exactly what makes the turn mitre. The along-run
        /// directions are taken from <paramref name="r"/> rather than from
        /// <paramref name="path"/> so a scalloped band's arc slope cannot tilt the leg.</para>
        /// </summary>
        static FacadeRun WithReturns(FacadeRun path, FacadeRun r, float depth)
        {
            if (depth <= 0f || path.points == null || path.points.Length < 2) return path;

            int n = path.points.Length, m = r.points.Length;
            Vector3 oStart = r.outward[0], oEnd = r.outward[m - 1];
            Vector3 tStart = Along(r.points[1] - r.points[0]);
            Vector3 tEnd = Along(r.points[m - 1] - r.points[m - 2]);

            var pts = new Vector3[n + 2];
            var outs = new Vector3[n + 2];
            System.Array.Copy(path.points, 0, pts, 1, n);
            System.Array.Copy(path.outward, 0, outs, 1, n);

            pts[0] = path.points[0] - oStart * depth;
            outs[0] = -tStart;
            outs[1] = (oStart - tStart).normalized;

            pts[n + 1] = path.points[n - 1] - oEnd * depth;
            outs[n + 1] = tEnd;
            outs[n] = (oEnd + tEnd).normalized;

            return new FacadeRun(pts, outs, false);
        }

        /// <summary>The horizontal unit direction of a run segment — the along-facade axis, with any
        /// slope a scalloped band introduced dropped.</summary>
        static Vector3 Along(Vector3 d)
        {
            d.y = 0f;
            return d.sqrMagnitude < 1e-10f ? Vector3.right : d.normalized;
        }

        static FacadeRun Shifted(FacadeRun run, float dy)
        {
            var s = new Vector3[run.points.Length];
            for (int i = 0; i < s.Length; i++)
                s[i] = new Vector3(run.points[i].x, run.points[i].y + dy, run.points[i].z);
            return new FacadeRun(s, run.outward, run.closed);
        }

        // ---- repeated elements --------------------------------------------------------------

        /// <summary>Lay a repeated element along every segment of the run at
        /// <paramref name="pitch"/>, centred on each segment so a facade reads as symmetrical and a
        /// corner is never straddled by half a block. The callback receives the element's two end
        /// points on the wall line, the height its centre sits at, and the outward direction of the
        /// segment it lies on — an element is a one-segment sweep, so one direction frames it.</summary>
        static void RepeatAlong(FacadeRun r, float pitch, float itemLen, float y,
                                System.Action<Vector3, Vector3, float, Vector3> emit)
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
                    emit(a + dir * s, a + dir * (s + itemLen), y, r.outward[k]);
                }
            }
        }

        /// <summary>One repeated element: the section swept the short distance from
        /// <paramref name="a"/> to <paramref name="b"/> along the run. A dentil block and a scroll
        /// bracket differ only in which section and how far — neither needs a kernel of its own, and
        /// a 4-point block costs the same 10 triangles a back-dropped <see cref="Kernels.Box"/>
        /// would while following a facade that runs along any bearing.</summary>
        static void Sweep(MeshBuilder mb, Vector2[] section, Vector3 a, Vector3 b, float y,
                          Vector3 outward, MaterialRole role)
            => Kernels.ProfileSweep(mb, section,
                                    Paths.Line(new Vector3(a.x, a.y + y, a.z), new Vector3(b.x, b.y + y, b.z)),
                                    new[] { outward, outward }, role,
                                    closedPath: false, capEnds: true, smoothAlong: false);

        // ---- DetailLevel degradation, local to this family -----------------------------------

        /// <summary>The crown section for a family — the one piece of section choice that is
        /// genuinely this family's own, so it stays here while the degradation rule itself lives in
        /// <see cref="Detail.Section"/>. <see cref="CorniceFamily.Bracketed"/> takes an
        /// Ogee crown — the brackets, not the moulding, are what make it bracketed — and
        /// <see cref="CorniceFamily.Parapet"/> a Bullnose, which is what a stucco coping reads
        /// as.</summary>
        static Vector2[] Sectioned(CorniceFamily family, DetailLevel detail)
        {
            switch (family)
            {
                case CorniceFamily.Ogee:
                case CorniceFamily.Bracketed: return Detail.Section(ProfileId.Ogee, detail);
                case CorniceFamily.Corbel: return Detail.Section(ProfileId.Corbel, detail);
                case CorniceFamily.Parapet: return Detail.Section(ProfileId.Bullnose, detail);
                default: return Detail.Section(ProfileId.Cove, detail);
            }
        }

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
