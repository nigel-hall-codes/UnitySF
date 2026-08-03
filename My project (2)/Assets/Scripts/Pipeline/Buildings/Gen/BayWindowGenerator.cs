using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>The shape the bay's plan traces, wall → return → face → return → wall. It selects
    /// <b>the plan polyline and nothing else</b>: every facet, every window, the skirt and the cap
    /// are derived from that polyline, so <see cref="Squared"/> is exactly <see cref="Slanted"/>
    /// with <c>widthAtFace == widthAtWall</c> and there is no per-plan branch below
    /// <see cref="BayWindowGenerator.PlanPoints"/> (design #452 design.md §3 names the Noe
    /// slanted bay and the North Beach squared bay as the contrast this enum carries).</summary>
    public enum BayPlan { Squared, Slanted, Curved }

    /// <summary>
    /// The projecting bay (#473, design #452 design.md §2 BayWindow row, §3 Noe Valley / North
    /// Beach). A bay is <b>a volume with windows on it</b>, so this generator builds the volume —
    /// a prism swept along a plan polyline, closed top and bottom, with a skirt under it and a cap
    /// over it — and then asks <b>the window family</b> for the glazing on each facet
    /// (design #452 D3). There is no glazing code in this file.
    ///
    /// <para><b>Part-local frame</b> (as <see cref="IPartGenerator"/> requires): +X along the
    /// facade, +Y up, +Z outward toward the street, origin at the bay's bottom centre on the wall
    /// plane (the <c>BottomCenter</c> anchor <c>BuildingAssembler.PlacePart</c> establishes). The
    /// whole part lives at <c>z ≥ 0</c>: unlike a window, a bay has nothing behind the wall plane,
    /// which is why its <c>mountDepth_m</c> is 0 and its <c>bounds.max.z</c> alone is the
    /// projection the #459 corner rule reasons about.</para>
    ///
    /// <para><b>Profile-axis convention for a plan sweep.</b> generators.md §3 fixes profile space
    /// as <c>(across, up)</c> with <c>+across</c> = outward from the wall — that is stated for a
    /// band running <i>along a wall</i>, whose sweep frame has a horizontal tangent and a vertical
    /// lateral axis. The skirt, cap and shell here run around the bay's <i>plan</i>: the tangent is
    /// still horizontal, but the run turns in the horizontal plane, so the sweep is taken with
    /// <c>upHint = +Y</c> and the profile's two axes become <b>(vertical, inward)</b>. Sweeping a
    /// plan with the facade default <c>upHint = +Z</c> is not merely rotated but <i>degenerate</i>
    /// for a squared bay, whose returns run along ±Z. <see cref="PlanBand"/> is the single place
    /// that conversion happens, and it takes its arguments the way the rest of the codebase reads
    /// them: <c>(rise up, run outward)</c>.</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>),
    /// so every name is listed here with its fallback; a misspelling silently takes that fallback.
    /// Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>plan</term><description><see cref="BayPlan"/> (Slanted)</description></item>
    /// <item><term>projection</term><description>how far the bay jumps the wall plane, m (0.6)</description></item>
    /// <item><term>chamferAngle</term><description>Slanted only — the returns' angle off the wall, degrees (45)</description></item>
    /// <item><term>widthAtWall</term><description>plan width where it meets the wall, m (3.0)</description></item>
    /// <item><term>widthAtFace</term><description>plan width of the front facet (derived from
    /// <c>chamferAngle</c> for Slanted, <c>widthAtWall</c> for Squared)</description></item>
    /// <item><term>curveSegments</term><description>Curved only — facets across the bow (6)</description></item>
    /// <item><term>floorsSpanned</term><description>1..3; a bay usually runs the full front (2)</description></item>
    /// <item><term>floorHeight</term><description>m per floor spanned (3.1)</description></item>
    /// <item><term>sillHeight</term><description>face window opening bottom above each floor line (0.95)</description></item>
    /// <item><term>revealDepth</term><description>default reveal for the face windows; a
    /// <c>face.revealDepth</c> wins over it (0.08)</description></item>
    /// <item><term>skirtProfile</term><description><see cref="ProfileId"/>; None = no skirt (Cove)</description></item>
    /// <item><term>skirtDepth, skirtRun</term><description>the sloped underside: drop, and how far
    /// it tucks back under (0.35, falls back to <c>projection</c>)</description></item>
    /// <item><term>capProfile</term><description><see cref="ProfileId"/>; None = no cap (Ogee)</description></item>
    /// <item><term>capDepth, capProjection</term><description>the roofline over it: rise, and how far
    /// it stands proud of the facets (0.30, 0.12)</description></item>
    /// <item><term>shellRole</term><description><see cref="MaterialRole"/> of the bay's own walls (Base)</description></item>
    /// <item><term>trimRole</term><description><see cref="MaterialRole"/> of skirt and cap (Accent2)</description></item>
    /// <item><term>faceWindowGenerator</term><description>generator id used on each facet
    /// ("window.double_hung"); resolved through <see cref="PartGenerators"/>, so a bay is not tied
    /// to one window family</description></item>
    /// <item><term>faceWindowPreset</term><description>the part id the <c>face.*</c> block was
    /// copied from. <b>Provenance and cache-key material only</b> — see the remark below ("")</description></item>
    /// <item><term>face.*</term><description>the face window's own parameter block, prefix-stripped
    /// and handed to <c>faceWindowGenerator</c>. <c>face.w</c> is a <i>maximum</i>: it shrinks to
    /// fit a narrow facet (empty → the window family's own defaults)</description></item>
    /// <item><term>faceMargin</term><description>clear facet each side of the glazing, m (0.08)</description></item>
    /// <item><term>faceGap</term><description>clear facet between two windows on one facet, m (0.15)</description></item>
    /// <item><term>faceWindowsPerFacet</term><description>upper bound on windows per facet; the
    /// actual count is what fits (1)</description></item>
    /// <item><term>faceWindowMount</term><description>how far the window is lifted proud of its
    /// facet; &lt; 0 derives it from the window's own <c>bounds.min.z</c> (-1)</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/>, read through
    /// <see cref="PartParams.Detail"/> (<see cref="DetailBudget.Default"/>)</description></item>
    /// </list>
    ///
    /// <para><b>Why <c>faceWindowPreset</c> does not resolve a part.</b> <see cref="IPartGenerator"/>
    /// hands a generator one flat bag and nothing else — no part table, no library, no way to turn
    /// the id <c>window_noe_2over2</c> into that part's parameter block. So the face window's
    /// numbers are carried inline under the <c>face.</c> prefix and <c>faceWindowPreset</c> records
    /// where they came from (and lands in the cache key, so two bays differing only in provenance
    /// do not silently share a mesh). Expanding a named preset into the prefix belongs to the
    /// authoring/import side (#475); it is data movement, not geometry. This is a real limit of the
    /// seam and is reported as one rather than worked around silently.</para>
    /// </summary>
    public sealed class BayWindowGenerator : IPartGenerator
    {
        public const string GeneratorId = "bay.projecting";

        /// <summary>The prefix a face window's own parameters are authored under.</summary>
        public const string FacePrefix = "face.";

        /// <summary>Below this the facet has no room for glazing worth generating, and the window
        /// is dropped rather than emitted as a slit.</summary>
        const float MinOpeningWidth = 0.30f;

        public string Id => GeneratorId;

        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            var plan = p.GetEnum("plan", BayPlan.Slanted);
            float projection = Mathf.Max(p.GetFloat("projection", 0.6f), 0.05f);
            float widthAtWall = Mathf.Max(p.GetFloat("widthAtWall", 3.0f), 0.5f);
            float widthAtFace = FaceWidth(p, plan, widthAtWall, projection);
            int floors = Mathf.Clamp(p.GetInt("floorsSpanned", 2), 1, 3);
            float floorHeight = Mathf.Max(p.GetFloat("floorHeight", 3.1f), 0.5f);
            float height = floors * floorHeight;
            DetailLevel detail = p.Detail;

            // uv1 normalises against the bay's own extent so the facade-decal remap (#280/#281)
            // keeps landing on generated geometry (design #452 D4).
            mb.SetLocalRect(widthAtWall, height, new Vector2(-widthAtWall * 0.5f, 0f));

            Vector3[] planPts = PlanPoints(plan, widthAtWall, widthAtFace, projection,
                                           PlanSegments(p, plan));
            var shellRole = p.GetEnum("shellRole", MaterialRole.Base);

            // ---- 1. the volume — one sweep of a bare vertical section along the plan ----------
            // A 2-point profile standing from y = 0 to y = height. The mitre displaces nothing
            // (the section has no horizontal extent), so the facets meet exactly on the plan
            // corners, and dropping to Faces-style back removal is unnecessary: an open path
            // sweeps no back wall in the first place.
            Kernels.ProfileSweep(mb, new[] { new Vector2(0f, 0f), new Vector2(height, 0f) },
                                 planPts, shellRole, closedPath: false, capEnds: false,
                                 smoothAlong: plan == BayPlan.Curved, upHint: Vector3.up);

            // ---- 2. close it top and bottom ---------------------------------------------------
            // The plan is an open polyline from wall to wall, so the polygon it bounds with the
            // wall line is convex and a fan over it is exact. Without these the bay is a hollow
            // shell you can see up into from the pavement.
            Fan(mb, planPts, 0f, Vector3.down, shellRole);
            Fan(mb, planPts, height, Vector3.up, shellRole);

            if (detail == DetailLevel.Flat)
                // The floor for this family is the bare prism, NOT the single quad
                // generators.md §5.2 gives the window family. A bay's whole contribution to a
                // street is its silhouette — flatten it to a quad and the artifact stops being a
                // bay at all, which is a new failure mode rather than the safe one D6 asks for.
                // It is still ~an order of magnitude under Reduced, which is what the floor is for.
                return mb.Finish(GeneratorId);

            var trimRole = p.GetEnum("trimRole", MaterialRole.Accent2);

            // ---- 3. skirt — the sloped underside ----------------------------------------------
            var skirtProfile = Sectioned(p.GetEnum("skirtProfile", ProfileId.Cove), detail);
            if (skirtProfile != ProfileId.None)
            {
                float drop = Mathf.Max(p.GetFloat("skirtDepth", 0.35f), 1e-3f);
                float run = Mathf.Max(p.GetFloat("skirtRun", projection), 0f);
                // Down and inward: the underside falls away from the bay's bottom edge back toward
                // the wall, which is what makes a bay look carried rather than stuck on.
                Kernels.ProfileSweep(mb, PlanBand(skirtProfile, -drop, -run), planPts, trimRole,
                                     closedPath: false, capEnds: true,
                                     smoothAlong: plan == BayPlan.Curved, upHint: Vector3.up);
            }

            // ---- 4. cap — the roofline over it -------------------------------------------------
            var capProfile = Sectioned(p.GetEnum("capProfile", ProfileId.Ogee), detail);
            if (capProfile != ProfileId.None)
            {
                float rise = Mathf.Max(p.GetFloat("capDepth", 0.30f), 1e-3f);
                float run = Mathf.Max(p.GetFloat("capProjection", 0.12f), 0f);
                Kernels.ProfileSweep(mb, PlanBand(capProfile, rise, run), Raised(planPts, height),
                                     trimRole, closedPath: false, capEnds: true,
                                     smoothAlong: plan == BayPlan.Curved, upHint: Vector3.up);
            }

            // ---- 5. the windows — from the window family, not from here ------------------------
            PlaceFaceWindows(p, mb, planPts, floors, floorHeight, detail);

            return mb.Finish(GeneratorId);
        }

        // ---- the plan ------------------------------------------------------------------------

        /// <summary>The front facet's width. Authored wins; otherwise Slanted derives it from the
        /// returns' angle (<c>widthAtWall - 2·projection/tan θ</c>) and every other plan is as wide
        /// at the face as at the wall. Clamped so a steep angle cannot invert the plan.</summary>
        static float FaceWidth(PartParams p, BayPlan plan, float widthAtWall, float projection)
        {
            if (p.Has("widthAtFace"))
                return Mathf.Clamp(p.GetFloat("widthAtFace", widthAtWall), 0.2f, widthAtWall);
            if (plan != BayPlan.Slanted) return widthAtWall;

            float theta = Mathf.Clamp(p.GetFloat("chamferAngle", 45f), 5f, 89f) * Mathf.Deg2Rad;
            float inset = projection / Mathf.Tan(theta);
            return Mathf.Clamp(widthAtWall - 2f * inset, 0.2f, widthAtWall);
        }

        /// <summary>
        /// Facets across the bow. Only Curved has more than the three every other plan has.
        ///
        /// <para><b>Deliberately not a function of <see cref="DetailLevel"/>.</b> Halving the bow at
        /// <c>Reduced</c> is the obvious saving and it is wrong: the facets are what the face
        /// windows are laid out on, so halving them <i>doubles each facet's width</i> and a bow whose
        /// facets were too narrow to glaze at <c>Full</c> sprouts windows at <c>Reduced</c> — a
        /// Reduced curved bay measured heavier than the Full one it is supposed to cheapen. The
        /// budget knob may change how finely an element is built; it must not change which elements
        /// exist (design #452 D6, and the rule generators.md §5.2's own table follows).</para>
        /// </summary>
        static int PlanSegments(PartParams p, BayPlan plan)
            => plan == BayPlan.Curved ? Mathf.Clamp(p.GetInt("curveSegments", 6), 2, 24) : 3;

        /// <summary>
        /// The plan polyline, left to right, at y = 0, from the wall plane out and back.
        /// <see cref="BayPlan.Squared"/> and <see cref="BayPlan.Slanted"/> are the same four points
        /// with a different face width — a squared bay is a slanted bay whose returns happen to run
        /// straight out — and <see cref="BayPlan.Curved"/> is <see cref="Paths.Arc"/> laid into the
        /// horizontal plane, so a zero projection degenerates to the wall line there exactly as a
        /// zero-rise arch degenerates to a lintel in the window family.
        /// </summary>
        public static Vector3[] PlanPoints(BayPlan plan, float widthAtWall, float widthAtFace,
                                           float projection, int segments)
        {
            if (plan == BayPlan.Curved)
            {
                // Arc is authored in XY (chord along X, sagitta along +Y); a bay's bow is the same
                // curve lying down, so the sagitta becomes the projection along +Z.
                Vector3[] arc = Paths.Arc(widthAtWall, projection, Mathf.Max(segments, 2));
                var pts = new Vector3[arc.Length];
                for (int i = 0; i < arc.Length; i++) pts[i] = new Vector3(arc[i].x, 0f, arc[i].y);
                return pts;
            }

            float hw = widthAtWall * 0.5f, hf = widthAtFace * 0.5f;
            return Paths.Polyline(new Vector3(-hw, 0f, 0f),
                                  new Vector3(-hf, 0f, projection),
                                  new Vector3(hf, 0f, projection),
                                  new Vector3(hw, 0f, 0f));
        }

        static Vector3[] Raised(Vector3[] pts, float y)
        {
            var up = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++) up[i] = new Vector3(pts[i].x, y, pts[i].z);
            return up;
        }

        /// <summary>
        /// A moulding that runs around the bay's plan, expressed the way everything else in this
        /// codebase reads a profile: <paramref name="rise"/> is its vertical extent (+up),
        /// <paramref name="run"/> its horizontal extent (+outward from the facet). See the
        /// class remarks for why a plan sweep's profile axes are (vertical, inward) and not the
        /// (outward, lateral) generators.md §3 states for a band on a wall.
        /// </summary>
        static Vector2[] PlanBand(ProfileId id, float rise, float run)
        {
            // Scaled(..., -run) puts +run on the profile's -y, which is the sweep frame's outward;
            // the shift then moves the section's first point onto the facet plane, so the band
            // starts flush with the wall it decorates instead of straddling it.
            var s = Profiles.Scaled(id, rise, -run);
            if (s == null) return null;
            for (int i = 0; i < s.Length; i++) s[i] = new Vector2(s[i].x, s[i].y - run * 0.5f);
            return s;
        }

        /// <summary>Close the prism with a triangle fan over the plan polygon. The polygon is
        /// convex for every <see cref="BayPlan"/>, so a fan from the first point is exact.</summary>
        static void Fan(MeshBuilder mb, Vector3[] plan, float y, Vector3 normal, MaterialRole role)
        {
            if (plan == null || plan.Length < 3) return;
            mb.BeginRole(role);
            var idx = new int[plan.Length];
            for (int i = 0; i < plan.Length; i++)
                idx[i] = mb.Vert(new Vector3(plan[i].x, y, plan[i].z), normal);
            for (int i = 1; i < plan.Length - 1; i++)
                mb.TriFacing(idx[0], idx[i], idx[i + 1], normal);
        }

        // ---- the windows ---------------------------------------------------------------------

        /// <summary>
        /// One window family, run once per distinct facet width and stamped onto every facet and
        /// every floor. The face generator is resolved through <see cref="PartGenerators"/> by id,
        /// so this composes with whatever window family a preset names and knows nothing about
        /// glazing itself (design #452 D3).
        /// </summary>
        void PlaceFaceWindows(PartParams p, MeshBuilder mb, Vector3[] planPts,
                              int floors, float floorHeight, DetailLevel detail)
        {
            string generatorId = ParamText(p, "faceWindowGenerator", DoubleHungWindowGenerator.GeneratorId);
            if (string.IsNullOrEmpty(generatorId)) return;
            if (string.Equals(generatorId, GeneratorId, System.StringComparison.Ordinal)) return;  // no bays on bays
            if (!PartGenerators.TryResolve(generatorId, out var window))
            {
                Debug.LogWarning($"[{GeneratorId}] unknown faceWindowGenerator '{generatorId}'; " +
                                 "the bay is generated without glazing.");
                return;
            }

            float margin = Mathf.Max(p.GetFloat("faceMargin", 0.08f), 0f);
            float gap = Mathf.Max(p.GetFloat("faceGap", 0.15f), 0f);
            int maxPerFacet = Mathf.Clamp(p.GetInt("faceWindowsPerFacet", 1), 1, 6);
            float authoredMount = p.GetFloat("faceWindowMount", -1f);
            float sillHeight = p.GetFloat("sillHeight", 0.95f);
            float reveal = Mathf.Max(p.GetFloat("revealDepth", 0.08f), 0f);
            float authoredW = Mathf.Max(FaceFloat(p, "w", 1.0f), MinOpeningWidth);

            var scratch = new MeshBuilder();
            var built = new Dictionary<int, PartMesh>();          // quantised opening width → mesh
            var owned = new List<Mesh>();

            try
            {
                for (int i = 0; i < planPts.Length - 1; i++)
                {
                    Vector3 a = planPts[i], b = planPts[i + 1];
                    Vector3 span = b - a;
                    float len = span.magnitude;
                    if (len < 1e-4f) continue;

                    Vector3 alongX = span / len;                              // the facet's own +X
                    Vector3 outZ = Vector3.Cross(alongX, Vector3.up).normalized;   // its own +Z
                    float available = len - 2f * margin;
                    if (available < MinOpeningWidth) continue;                // a blank cheek

                    // First run at the authored width tells us the trimmed footprint — casing,
                    // head overhang and sill run all stand outside the opening, and only the mesh
                    // knows by how much. Same principle as #459: ask the geometry, not the author.
                    if (!TryBuild(window, p, reveal, detail, authoredW, scratch, built, owned,
                                  out PartMesh probe)) continue;
                    float trimmed = probe.mesh.bounds.size.x;
                    if (trimmed <= 1e-4f) continue;

                    int count = Mathf.Clamp(Mathf.FloorToInt((available + gap) / (trimmed + gap)),
                                            1, maxPerFacet);
                    float slot = available / count;

                    PartMesh face = probe;
                    float overflow = trimmed - (slot - gap * (count > 1 ? 1f : 0f));
                    if (overflow > 1e-3f)
                    {
                        float shrunk = authoredW - overflow;
                        if (shrunk < MinOpeningWidth) continue;               // facet too narrow
                        if (!TryBuild(window, p, reveal, detail, shrunk, scratch, built, owned,
                                      out face)) continue;
                    }

                    // The bay's own wall is solid — no hole is cut in it, exactly as none is cut in
                    // the building mass — so the window is lifted proud by its own recessed depth
                    // or the assembly's back half is swallowed by the facet.
                    float mount = authoredMount >= 0f ? authoredMount
                                                      : Mathf.Max(-face.mesh.bounds.min.z, 0f);
                    Vector3 facetMid = (a + b) * 0.5f + outZ * mount;

                    for (int f = 0; f < floors; f++)
                    {
                        float y = f * floorHeight + sillHeight;
                        for (int k = 0; k < count; k++)
                        {
                            float offset = -available * 0.5f + slot * (k + 0.5f);
                            Vector3 origin = facetMid + alongX * offset + new Vector3(0f, y, 0f);
                            Append(mb, face, origin, alongX, Vector3.up, outZ);
                        }
                    }
                }
            }
            finally
            {
                foreach (var m in owned) DestroyScratch(m);
            }
        }

        /// <summary>Generate (or reuse) the face window at <paramref name="openingWidth"/>. Widths
        /// are quantised to <see cref="PartKey.QuantumMeters"/> so the two identical returns of a
        /// slanted bay cost one generation, not two.</summary>
        static bool TryBuild(IPartGenerator window, PartParams p, float reveal, DetailLevel detail,
                             float openingWidth, MeshBuilder scratch,
                             Dictionary<int, PartMesh> built, List<Mesh> owned, out PartMesh result)
        {
            int q = Mathf.RoundToInt(openingWidth / PartKey.QuantumMeters);
            if (built.TryGetValue(q, out result)) return result.IsValid;

            scratch.Clear();
            result = window.Generate(FaceBag(p, q * PartKey.QuantumMeters, reveal, detail), scratch);
            built[q] = result;
            if (result.mesh != null) owned.Add(result.mesh);
            return result.IsValid;
        }

        /// <summary>
        /// The <c>face.</c> block, prefix-stripped, as the window family's own parameter bag. Three
        /// entries are decided here rather than by the author: <c>w</c> (the facet decides what
        /// fits), <c>detail</c> (a bay's parts degrade with the bay, not independently of it), and
        /// <c>revealDepth</c> when the block does not carry one (the bay's own reveal is the
        /// sensible default for glazing set into it).
        /// </summary>
        static PartParams FaceBag(PartParams p, float openingWidth, float reveal, DetailLevel detail)
        {
            var vals = new List<PartParam>(p.Count);
            bool hasReveal = false;
            var src = p.values;
            for (int i = 0; src != null && i < src.Length; i++)
            {
                string name = src[i].name;
                if (string.IsNullOrEmpty(name) || !name.StartsWith(FacePrefix, System.StringComparison.Ordinal))
                    continue;
                string stripped = name.Substring(FacePrefix.Length);
                if (stripped.Length == 0) continue;
                if (stripped == "w" || stripped == "detail") continue;        // decided below
                if (stripped == "revealDepth") hasReveal = true;
                vals.Add(new PartParam { name = stripped, value = src[i].value, text = src[i].text });
            }

            vals.Add(new PartParam { name = "w", value = openingWidth });
            vals.Add(new PartParam { name = "detail", text = detail.ToString() });
            if (!hasReveal) vals.Add(new PartParam { name = "revealDepth", value = reveal });
            return new PartParams { values = vals.ToArray() };
        }

        static float FaceFloat(PartParams p, string name, float fallback)
            => p.GetFloat(FacePrefix + name, fallback);

        static string ParamText(PartParams p, string name, string fallback)
        {
            if (!p.Has(name)) return fallback;
            var bag = p.values;
            for (int i = 0; bag != null && i < bag.Length; i++)
                if (string.Equals(bag[i].name, name, System.StringComparison.Ordinal))
                    return string.IsNullOrEmpty(bag[i].text) ? fallback : bag[i].text;
            return fallback;
        }

        // ---- composing one generator's output into another's ----------------------------------

        /// <summary>
        /// Append <paramref name="src"/> into <paramref name="dst"/>, rotated into the facet frame
        /// (<paramref name="axisX"/>, <paramref name="axisY"/>, <paramref name="axisZ"/>) and moved
        /// to <paramref name="origin"/>.
        ///
        /// <para><b>This is the composition seam, and it costs a round trip.</b>
        /// <see cref="IPartGenerator"/>'s unit of output is a finished <see cref="Mesh"/> and
        /// <see cref="MeshBuilder"/> has no transform stack, so the only way one family can carry
        /// another's geometry is to bake it and read it back. The frame is orthonormal and
        /// right-handed (<c>X × Y = Z</c>), so winding is preserved and no re-facing is needed; UVs
        /// are re-derived from the transformed position, which is what puts the window's
        /// <c>uv1</c> into the <i>bay's</i> rect — the thing the facade-decal remap wants.</para>
        /// </summary>
        static void Append(MeshBuilder dst, PartMesh src, Vector3 origin,
                           Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            if (!src.IsValid) return;
            Vector3[] verts = src.mesh.vertices;
            Vector3[] norms = src.mesh.normals;
            if (verts == null || verts.Length == 0) return;

            var map = new int[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                Vector3 n = norms != null && i < norms.Length ? norms[i] : Vector3.forward;
                map[i] = dst.Vert(origin + axisX * v.x + axisY * v.y + axisZ * v.z,
                                  (axisX * n.x + axisY * n.y + axisZ * n.z).normalized);
            }

            int subs = Mathf.Min(src.mesh.subMeshCount, src.submeshRoles.Length);
            for (int s = 0; s < subs; s++)
            {
                dst.BeginRole(src.submeshRoles[s]);
                int[] tris = src.mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    dst.Tri(map[tris[t]], map[tris[t + 1]], map[tris[t + 2]]);
            }
        }

        /// <summary>The scratch meshes exist only to be read back; nothing outside this call ever
        /// sees them, so they are destroyed rather than leaked into the import's object soup.</summary>
        static void DestroyScratch(Mesh m)
        {
            if (m == null) return;
            if (Application.isPlaying) Object.Destroy(m);
            else Object.DestroyImmediate(m);
        }

        // ---- DetailLevel degradation ------------------------------------------------------------

        /// <summary>
        /// The cross-section a moulding is swept with at this budget — the window family's rule:
        /// <see cref="DetailLevel.Full"/> keeps the authored moulding, anything below bevels it to a
        /// 3-point <see cref="ProfileId.Chamfer"/>. Degrading the profile rather than the kernel
        /// keeps one code path and is what actually gets cheaper.
        /// <para><b>With one correction.</b> <see cref="ProfileId.Flat"/> is two points and
        /// <see cref="ProfileId.Chamfer"/> is three, so "degrading" a Flat band to a Chamfer makes
        /// it 50% <i>more</i> expensive. The section is only ever cheapened here.
        /// <c>DoubleHungWindowGenerator.Sectioned</c> still has the unguarded form, which is why a
        /// preset whose trim is entirely Flat measures the same at Full and Reduced — worth a
        /// one-line fix in that family, out of scope for this one.</para>
        /// </summary>
        static ProfileId Sectioned(ProfileId id, DetailLevel detail)
            => detail == DetailLevel.Full || id == ProfileId.None ||
               id == ProfileId.Flat || id == ProfileId.Chamfer
                ? id : ProfileId.Chamfer;
    }
}
