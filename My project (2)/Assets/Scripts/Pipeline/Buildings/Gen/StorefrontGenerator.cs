using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>How the display bays divide the run. <see cref="Even"/> gives every bay the same
    /// width; <see cref="WideNarrowWide"/> alternates 2:1 <i>by bay index</i>, so the rhythm stays
    /// symmetric about a centred entry (wide · narrow · entry · narrow · wide).</summary>
    public enum BayRhythm { Even, WideNarrowWide }

    /// <summary>Which bay the recessed entry takes. <see cref="None"/> is an unbroken glass front —
    /// also what a facade too narrow to hold both an entry and a display bay degrades to.</summary>
    public enum EntrySide { None, Left, Center, Right }

    /// <summary>The plan condition at the storefront's outer end. <see cref="Chamfered"/> is the
    /// North Beach corner-entry condition: the projecting shopfront's corner is cut back at an
    /// angle instead of returning square to the wall.</summary>
    public enum CornerCondition { Square, Chamfered }

    /// <summary>
    /// The storefront family (#472, design #452 design.md §2 "Storefront" row and §3's Mission
    /// tiled-bulkhead piece): a bulkhead / display-glazing / transom row of bays with a recessed
    /// entry, composed from the #453 kernels.
    ///
    /// <para><b>Not a slot artifact.</b> Every family shipped so far occupies a slot the placement
    /// engine repeats every N metres. A storefront occupies a <i>whole ground-floor facade</i>, so
    /// its width is the facade's width, and the generator is written to be driven by <c>w</c>
    /// rather than to assume one: bay count is derived from <c>w</c> when <c>bayCount</c> is left
    /// at 0, and every element degrades rather than inverting as <c>w</c> shrinks. See the PR for
    /// the template rule shape #475 needs.</para>
    ///
    /// <para><b>Part-local frame</b> (as <see cref="IPartGenerator"/> requires): +X along the
    /// facade, +Y up, +Z outward toward the street; the opening spans <c>x ∈ [-w/2, w/2]</c>,
    /// <c>y ∈ [0, h]</c> from the <c>BottomCenter</c> anchor.</para>
    ///
    /// <para><b>Depth model — why a storefront projects.</b> The building mass is a solid box with
    /// no hole cut in it, so anything authored behind the wall plane is swallowed by it. A recessed
    /// entry is <i>defined</i> by being behind the glazing, so the only way to make it visible is
    /// to stand the glazing proud of the wall by the recess depth. This generator therefore builds
    /// the storefront as a <b>projecting shopfront box</b> — a ground floor that bumps out to the
    /// property line while the facade above sets back, which is an ordinary San Francisco condition
    /// and not a workaround:</para>
    /// <list type="bullet">
    /// <item><description><c>z = 0</c> — the surround frame's face.</description></item>
    /// <item><description><c>z = -revealDepth</c> — mullion faces and the bulkhead.</description></item>
    /// <item><description><c>z = -revealDepth - glassInset</c> — the glazing plane.</description></item>
    /// <item><description><c>z = -revealDepth - glassInset - entryRecessDepth</c> — the recess back,
    /// which is where the real wall must be. A preset's <c>mountDepth_m</c> must therefore equal
    /// <c>revealDepth + glassInset + entryRecessDepth</c>, or the box will not meet the wall.</description></item>
    /// </list>
    /// <para>The box's own left/right/top/bottom returns close the gap between the glazing plane and
    /// the wall; the entry's four jamb quads are the recess's side walls, soffit and floor.</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>), so
    /// every name read here is listed with its fallback; a misspelling silently takes that fallback.
    /// Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>w, h</term><description>the flat storefront run, m (7.0 × 3.6). A
    /// <see cref="CornerCondition.Chamfered"/> return is added <i>beyond</i> this.</description></item>
    /// <item><term>revealDepth</term><description>frame face → mullion face (0.10)</description></item>
    /// <item><term>glassInset</term><description>mullion face → glazing plane (0.02)</description></item>
    /// <item><term>bayCount</term><description>0 = derive from <c>bayPitch</c> (0)</description></item>
    /// <item><term>bayPitch</term><description>target metres per bay when deriving (3.0)</description></item>
    /// <item><term>bayRhythm</term><description><see cref="BayRhythm"/> (Even)</description></item>
    /// <item><term>bulkheadH</term><description>the solid base below the glass (0.60)</description></item>
    /// <item><term>bulkheadPanelRows</term><description>tile rows in the bulkhead (1)</description></item>
    /// <item><term>bulkheadPanelCols</term><description>tile columns per bay; 0 = roughly square (0)</description></item>
    /// <item><term>bulkheadBevel</term><description>&gt;0 → proud tiles — the Mission grid (0)</description></item>
    /// <item><term>bulkheadRole</term><description><see cref="MaterialRole"/> (Accent2)</description></item>
    /// <item><term>glazingH</term><description>display glass band; 0 = whatever is left (0)</description></item>
    /// <item><term>transomH</term><description>the band of small lights above the glass (0.45)</description></item>
    /// <item><term>transomLights</term><description>lights per bay in that band (3)</description></item>
    /// <item><term>mullionW, mullionD</term><description>0.09 / 0.06</description></item>
    /// <item><term>entrySide</term><description><see cref="EntrySide"/> (Center)</description></item>
    /// <item><term>entryWidth</term><description>the entry bay's width, m (1.80)</description></item>
    /// <item><term>entryRecessDepth</term><description>how far the entry sets back — and therefore
    /// how far the whole shopfront projects (0.85)</description></item>
    /// <item><term>entryLeaves</term><description>door leaves across the entry (2)</description></item>
    /// <item><term>entryKickH</term><description>solid kick panel at the door's foot (0.30)</description></item>
    /// <item><term>corner, cornerChamfer</term><description><see cref="CornerCondition"/> (Square) /
    /// the chamfer's run in X (0.90)</description></item>
    /// <item><term>frameProfile</term><description><see cref="ProfileId"/>; None = no surround (Flat)</description></item>
    /// <item><term>frameW, frameD</term><description>surround band width / projection (0.14, 0.06)</description></item>
    /// <item><term>awningProjection</term><description>0 = no awning (0)</description></item>
    /// <item><term>awningDrop, awningValance, awningOverhang</term><description>0.35 / 0.22 / 0.12</description></item>
    /// <item><term>awningY</term><description>where it hangs; &lt;0 = the head (-1)</description></item>
    /// <item><term>frameRole</term><description>mullions + surround (Metal)</description></item>
    /// <item><term>glassRole, awningRole</term><description>Glass / Accent1</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/> via <see cref="PartParams.Detail"/></description></item>
    /// </list>
    /// </summary>
    public sealed class StorefrontGenerator : IPartGenerator
    {
        public const string GeneratorId = "storefront.bay_row";

        /// <summary>Hard ceiling on derived bays. A 60 m warehouse frontage must not become 20 bays
        /// of glazing; past this the bays simply get wider, which is what a real long frontage does.</summary>
        const int MaxBays = 10;

        /// <summary>Narrower than this and a bay is a slot, not a bay — the derived count is capped
        /// so <c>w</c> can fall to anything without producing slivers.</summary>
        const float MinBayWidth = 0.45f;

        /// <summary>Below this the entry is dropped and the storefront becomes an unbroken glass
        /// front. A doorway needs its own width plus at least one display bay beside it; a facade
        /// that cannot hold both should not get a 20 cm door.</summary>
        const float MinEntryWidth = 0.85f;

        /// <summary>Tiles per bay ceiling — the bulkhead is the one element whose cell count is
        /// derived from a length, so it is the one that can run away on a wide facade.</summary>
        const int MaxBulkheadCols = 10;

        public string Id => GeneratorId;

        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            float w = Mathf.Max(p.GetFloat("w", 7.0f), 0.30f);
            float h = Mathf.Max(p.GetFloat("h", 3.6f), 0.50f);
            float revealDepth = Mathf.Max(p.GetFloat("revealDepth", 0.10f), 0f);
            float glassInset = Mathf.Max(p.GetFloat("glassInset", 0.02f), 0f);
            DetailLevel detail = p.Detail;

            // uv1 normalises against the storefront's own rect, so the facade-decal remap
            // (#280/#281) keeps landing on generated geometry (design #452 D4). It matters more here
            // than anywhere else: a storefront is where signs and painted lettering go.
            mb.SetLocalRect(w, h, new Vector2(-w * 0.5f, 0f));

            if (detail == DetailLevel.Flat)
            {
                // The floor, not a new failure mode: one role-coloured quad on the glazing plane —
                // exactly the placeholder this pipeline replaced (design #452 D6).
                FlatQuad(mb, w, h, -(revealDepth + glassInset),
                         p.GetEnum("glassRole", MaterialRole.Glass));
                return mb.Finish(GeneratorId);
            }

            var frameRole = p.GetEnum("frameRole", MaterialRole.Metal);
            var glassRole = p.GetEnum("glassRole", MaterialRole.Glass);
            var bulkheadRole = p.GetEnum("bulkheadRole", MaterialRole.Accent2);

            // ---- 1. vertical bands ---------------------------------------------------------
            // h is authoritative — it is the shared "opening" parameter every family takes, and the
            // facade's ground-floor height is what actually constrains a storefront. bulkhead and
            // transom are honoured in full when they fit and shrunk proportionally when they do not,
            // so no authoring mistake can produce a negative glazing band.
            float bulkheadH = Mathf.Max(p.GetFloat("bulkheadH", 0.60f), 0f);
            float transomH = Mathf.Max(p.GetFloat("transomH", 0.45f), 0f);
            float authoredGlazing = p.GetFloat("glazingH", 0f);
            if (authoredGlazing > 0f) transomH = Mathf.Max(h - bulkheadH - authoredGlazing, 0f);

            float minGlazing = Mathf.Min(0.35f, h * 0.4f);
            float solidBudget = Mathf.Max(h - minGlazing, 0f);
            if (bulkheadH + transomH > solidBudget && bulkheadH + transomH > 1e-4f)
            {
                float k = solidBudget / (bulkheadH + transomH);
                bulkheadH *= k;
                transomH *= k;
            }
            float glazingH = h - bulkheadH - transomH;

            // ---- 2. bay layout -------------------------------------------------------------
            var side = p.GetEnum("entrySide", EntrySide.Center);
            float entryWidth = Mathf.Max(p.GetFloat("entryWidth", 1.80f), 0f);
            float recess = Mathf.Max(p.GetFloat("entryRecessDepth", 0.85f), 0f);

            bool hasEntry = side != EntrySide.None && entryWidth > 0f && recess > 0f &&
                            w >= MinEntryWidth + MinBayWidth;
            if (hasEntry) entryWidth = Mathf.Clamp(entryWidth, MinEntryWidth, w - MinBayWidth);

            int bayCount = BayCount(p, w, hasEntry);
            int entryIndex = hasEntry ? EntryIndex(side, bayCount) : -1;
            float[] bayW = BayWidths(bayCount, w, entryIndex, entryWidth,
                                     p.GetEnum("bayRhythm", BayRhythm.Even));

            var xs = new float[bayCount + 1];
            xs[0] = -w * 0.5f;
            for (int i = 0; i < bayCount; i++) xs[i + 1] = xs[i] + bayW[i];

            // Depth of the projecting box: the recess is what pushes the shopfront out, so with no
            // entry there is nothing to push it out and the storefront sits flat on the wall.
            float shellDepth = hasEntry ? recess : 0f;
            float glazZ = -(revealDepth + glassInset);
            float wallZ = glazZ - shellDepth;

            // ---- 3. the box: surround reveal, projecting returns, chamfered corner ---------
            Kernels.Reveal(mb, w, h, revealDepth, MaterialRole.Base);

            float chamferX = 0f;
            if (p.GetEnum("corner", CornerCondition.Square) == CornerCondition.Chamfered && shellDepth > 0f)
                chamferX = Mathf.Clamp(p.GetFloat("cornerChamfer", 0.90f), 0f, 2.5f);

            // The chamfer replaces the box's return on the side it cuts. Left/Center/Right pick which
            // corner is the street corner: an entry on the left implies the corner is on the left.
            bool chamferAtMinX = side == EntrySide.Left;
            if (shellDepth > 0f)
            {
                var shellFaces = Faces.Left | Faces.Right | Faces.Top | Faces.Bottom;
                if (chamferX > 0f) shellFaces &= ~(chamferAtMinX ? Faces.Left : Faces.Right);
                if (detail != DetailLevel.Full) shellFaces &= ~Faces.Bottom;   // at grade, never seen
                Kernels.Box(mb, new Vector3(w, h, shellDepth),
                            new Vector3(0f, h * 0.5f, glazZ - shellDepth * 0.5f),
                            MaterialRole.Base, shellFaces);
            }

            if (chamferX > 0f)
                ChamferReturn(mb, chamferAtMinX ? -w * 0.5f : w * 0.5f, chamferAtMinX ? -chamferX : chamferX,
                              glazZ, shellDepth, bulkheadH, glazingH, transomH,
                              bulkheadRole, glassRole);

            // ---- 4. the bays ---------------------------------------------------------------
            float bulkheadBevel = detail == DetailLevel.Full
                ? Mathf.Max(p.GetFloat("bulkheadBevel", 0f), 0f) : 0f;
            int bulkRows = detail == DetailLevel.Full
                ? Mathf.Max(p.GetInt("bulkheadPanelRows", 1), 1) : 1;
            int authoredBulkCols = Mathf.Max(p.GetInt("bulkheadPanelCols", 0), 0);
            int transomLights = Mathf.Max(p.GetInt("transomLights", 3), 1);
            if (detail != DetailLevel.Full) transomLights = Mathf.Max((transomLights + 1) / 2, 1);

            float mullionW = Mathf.Max(p.GetFloat("mullionW", 0.09f), 0f);
            float mullionD = Mathf.Max(p.GetFloat("mullionD", 0.06f), 0f);
            float mullionZ = -revealDepth - mullionD * 0.5f;    // mullion face flush with the reveal
            float barW = mullionW * 0.6f, barD = mullionD * 0.6f;
            float barZ = -revealDepth - barD * 0.5f;            // transom bars flush with the mullions

            for (int i = 0; i < bayCount; i++)
            {
                if (i == entryIndex) continue;                   // the entry bay is a hole, not a bay
                float bx0 = xs[i] + mullionW * 0.5f;
                float bx1 = xs[i + 1] - mullionW * 0.5f;
                if (bx1 - bx0 < 1e-3f) continue;                 // mullions ate the bay: emit nothing

                // bulkhead — the low solid base. Tiles are roughly square unless authored, capped so
                // a wide facade widens its tiles rather than multiplying them.
                if (bulkheadH > 1e-3f)
                {
                    int cols = authoredBulkCols > 0
                        ? authoredBulkCols
                        : Mathf.Clamp(Mathf.RoundToInt((bx1 - bx0) / Mathf.Max(bulkheadH / bulkRows, 0.05f)),
                                      1, MaxBulkheadCols);
                    if (detail != DetailLevel.Full) cols = Mathf.Max((cols + 1) / 2, 1);
                    Cells(mb, bx0, bx1, 0f, bulkheadH, -revealDepth, cols, bulkRows,
                          bulkheadBevel, bulkheadRole);
                }

                // display glazing — one undivided light per bay; that is what a shopfront is.
                if (glazingH > 1e-3f)
                    Cells(mb, bx0, bx1, bulkheadH, bulkheadH + glazingH, glazZ, 1, 1, 0f, glassRole);

                // transom — its own narrow grid of small lights above the display glass.
                if (transomH > 1e-3f)
                {
                    float ty0 = bulkheadH + glazingH;
                    Cells(mb, bx0, bx1, ty0, ty0 + transomH, glazZ, transomLights, 1, 0f, glassRole);
                    Bars(mb, bx0, bx1, ty0, ty0 + transomH, barZ, transomLights, 1,
                         barW, barD, frameRole);
                }
            }

            // ---- 5. mullions and rails -----------------------------------------------------
            // Vertical members run the full glazed height at every bay edge, including the two ends
            // (they are the jamb posts) and both sides of the entry (they are the door posts).
            if (mullionW > 1e-3f && mullionD > 1e-3f)
            {
                float postH = glazingH + transomH;
                if (postH > 1e-3f)
                    for (int k = 0; k <= bayCount; k++)
                        Kernels.Box(mb, new Vector3(mullionW, postH, mullionD),
                                    new Vector3(xs[k], bulkheadH + postH * 0.5f, mullionZ),
                                    frameRole, Faces.NoBack);

                // The transom rail is the storefront's defining horizontal and survives Reduced; the
                // bulkhead cap rail is trim and does not.
                if (transomH > 1e-3f)
                    Kernels.Box(mb, new Vector3(w, mullionW, mullionD),
                                new Vector3(0f, bulkheadH + glazingH, mullionZ), frameRole, Faces.NoBack);
                if (detail == DetailLevel.Full && bulkheadH > 1e-3f)
                    Kernels.Box(mb, new Vector3(w, mullionW * 0.8f, mullionD * 0.8f),
                                new Vector3(0f, bulkheadH, mullionZ), frameRole, Faces.NoBack);
            }

            // ---- 6. the recessed entry -----------------------------------------------------
            // A real inset box with its own side jambs, soffit and floor — not a texture. This is
            // what makes a storefront read as enterable rather than as a glass wall.
            if (hasEntry)
            {
                float ec = (xs[entryIndex] + xs[entryIndex + 1]) * 0.5f;
                float ew = Mathf.Max(bayW[entryIndex] - mullionW, 0.1f);
                float eh = bulkheadH + glazingH;                 // door height: up to the transom line

                Kernels.Reveal(mb, ew, eh, shellDepth, MaterialRole.Base,
                               new Vector3(ec, 0f, glazZ));

                float doorW = ew - mullionW;
                if (doorW > 0.2f)
                {
                    int leaves = Mathf.Clamp(p.GetInt("entryLeaves", 2), 1, 4);
                    float kickH = Mathf.Clamp(p.GetFloat("entryKickH", 0.30f), 0f, eh * 0.5f);
                    float dx0 = ec - doorW * 0.5f, dx1 = ec + doorW * 0.5f;

                    if (kickH > 1e-3f)
                        Cells(mb, dx0, dx1, 0f, kickH, wallZ, leaves, 1, bulkheadBevel, bulkheadRole);
                    Cells(mb, dx0, dx1, kickH, eh, wallZ, leaves, 1, 0f, glassRole);
                    Bars(mb, dx0, dx1, kickH, eh, wallZ + mullionD * 0.5f, leaves, 1,
                         mullionW, mullionD, frameRole);

                    // Door surround: one mitred closed sweep on the band's centreline (half a band
                    // inside the opening, the convention Kernels.PanelGrid's own frame uses), so the
                    // leaves read as doors rather than as a glazed panel at the back of a hole.
                    Kernels.ProfileSweep(mb, Profiles.Scaled(ProfileId.Flat, mullionD, mullionW),
                                         Paths.Rect(ew - mullionW, eh - mullionW,
                                                    new Vector3(ec, eh * 0.5f, wallZ)),
                                         frameRole, closedPath: true, capEnds: false, smoothAlong: false);
                }
            }

            // ---- 7. surround frame ---------------------------------------------------------
            var frameProfile = Sectioned(p.GetEnum("frameProfile", ProfileId.Flat), detail);
            float frameW = Mathf.Max(p.GetFloat("frameW", 0.14f), 0f);
            float frameD = Mathf.Max(p.GetFloat("frameD", 0.06f), 0f);
            if (frameProfile != ProfileId.None && frameW > 1e-3f)
                Kernels.ProfileSweep(mb, Profiles.Scaled(frameProfile, frameD, frameW),
                                     Paths.Rect(w + frameW, h + frameW, new Vector3(0f, h * 0.5f, 0f)),
                                     frameRole, closedPath: true, capEnds: false, smoothAlong: false);

            // ---- 8. awning -----------------------------------------------------------------
            float awProjection = Mathf.Max(p.GetFloat("awningProjection", 0f), 0f);
            if (awProjection > 1e-3f)
            {
                float drop = Mathf.Max(p.GetFloat("awningDrop", 0.35f), 0f);
                float valance = Mathf.Max(p.GetFloat("awningValance", 0.22f), 0f);
                float overhang = Mathf.Max(p.GetFloat("awningOverhang", 0.12f), 0f);
                float awY = p.GetFloat("awningY", -1f);
                if (awY < 0f) awY = h;

                // Profile in (across, up) with +across outward from the wall — the convention
                // generators.md §3 is explicit about. Authored bottom-of-valance first so the
                // winding rule (n = tangent rotated −90°) puts the top surface facing out and up.
                var section = new[]
                {
                    new Vector2(awProjection, -(drop + valance)),
                    new Vector2(awProjection, -drop),
                    new Vector2(0f, 0f),
                };
                Kernels.ProfileSweep(mb, section,
                                     Paths.Line(new Vector3(-w * 0.5f - overhang, awY, 0f),
                                                new Vector3(w * 0.5f + overhang, awY, 0f)),
                                     p.GetEnum("awningRole", MaterialRole.Accent1),
                                     closedPath: false, capEnds: true, smoothAlong: false);
            }

            return mb.Finish(GeneratorId);
        }

        // ---- bay layout --------------------------------------------------------------------

        /// <summary>How many bays the run divides into. An authored <c>bayCount</c> wins; otherwise
        /// it follows <c>bayPitch</c>, which is what makes the family width-driven rather than
        /// slot-driven. Both are then clamped so no bay can fall below
        /// <see cref="MinBayWidth"/> — that clamp is the whole of the narrow-facade story.</summary>
        static int BayCount(PartParams p, float run, bool hasEntry)
        {
            int authored = Mathf.Max(p.GetInt("bayCount", 0), 0);
            float pitch = Mathf.Max(p.GetFloat("bayPitch", 3.0f), 0.5f);
            int n = authored > 0 ? authored : Mathf.Max(Mathf.RoundToInt(run / pitch), 1);

            // An entry needs a display bay beside it, so two is the floor once there is one.
            if (hasEntry) n = Mathf.Max(n, 2);
            n = Mathf.Min(n, Mathf.Max(Mathf.FloorToInt(run / MinBayWidth), hasEntry ? 2 : 1));
            return Mathf.Clamp(n, 1, MaxBays);
        }

        static int EntryIndex(EntrySide side, int bayCount)
        {
            switch (side)
            {
                case EntrySide.Left: return 0;
                case EntrySide.Right: return bayCount - 1;
                default: return bayCount / 2;
            }
        }

        /// <summary>Bay widths across the run. The entry takes its authored width and the display
        /// bays share what is left, weighted by the rhythm. Weighting by <i>bay index</i> rather
        /// than by display-bay ordinal is what keeps <see cref="BayRhythm.WideNarrowWide"/>
        /// symmetric about a centred entry.</summary>
        static float[] BayWidths(int bayCount, float run, int entryIndex, float entryWidth,
                                 BayRhythm rhythm)
        {
            var widths = new float[bayCount];
            bool hasEntry = entryIndex >= 0 && entryIndex < bayCount && bayCount >= 2;
            float displayTotal = run - (hasEntry ? entryWidth : 0f);

            float sum = 0f;
            for (int i = 0; i < bayCount; i++)
            {
                if (hasEntry && i == entryIndex) continue;
                float wt = rhythm == BayRhythm.WideNarrowWide && (i % 2) != 0 ? 1f : 2f;
                widths[i] = wt;
                sum += wt;
            }

            for (int i = 0; i < bayCount; i++)
            {
                if (hasEntry && i == entryIndex) widths[i] = entryWidth;
                else widths[i] = sum > 0f ? displayTotal * widths[i] / sum : run;
            }
            return widths;
        }

        // ---- the chamfered corner ----------------------------------------------------------

        /// <summary>
        /// The North Beach corner-entry condition: the projecting shopfront's corner is cut back to
        /// the wall on the diagonal instead of returning square. The three bands are
        /// <see cref="Kernels.ProfileSweep"/>s of a zero-projection <see cref="Profiles.Flat"/>
        /// section along that diagonal — the sweep frame turns the section into a vertical band on
        /// the angled plane with the right outward normal, so no vertex math is needed for a plane
        /// that is not axis-aligned.
        /// </summary>
        static void ChamferReturn(MeshBuilder mb, float xEdge, float dx, float glazZ, float depth,
                                  float bulkheadH, float glazingH, float transomH,
                                  MaterialRole bulkheadRole, MaterialRole glassRole)
        {
            void Band(float y0, float y1, MaterialRole role)
            {
                float band = y1 - y0;
                if (band <= 1e-3f) return;
                Kernels.ProfileSweep(mb, Profiles.Scaled(ProfileId.Flat, 0f, band),
                                     Paths.Line(new Vector3(xEdge, (y0 + y1) * 0.5f, glazZ),
                                                new Vector3(xEdge + dx, (y0 + y1) * 0.5f, glazZ - depth)),
                                     role, closedPath: false, capEnds: false, smoothAlong: false);
            }

            Band(0f, bulkheadH, bulkheadRole);
            Band(bulkheadH, bulkheadH + glazingH, glassRole);
            Band(bulkheadH + glazingH, bulkheadH + glazingH + transomH, glassRole);
        }

        // ---- panel fields ------------------------------------------------------------------

        /// <summary>
        /// A grid of infill cells over an explicit x-range: <see cref="Kernels.PanelGrid"/>'s steps
        /// 2–4, but positioned rather than centred.
        /// <para><b>Why not <c>Kernels.PanelGrid</c> itself.</b> It centres its grid on <c>x = 0</c>
        /// and offsets only in Y and Z, which is right for every family that occupies one opening.
        /// A storefront's bays are side by side along X and the entry bay must be <i>skipped</i>, so
        /// a single centred call cannot express the field. The kernel wants an <c>offsetX</c>
        /// alongside its <c>offsetY</c>/<c>offsetZ</c>; adding one is a follow-up (noted in the PR)
        /// rather than something this family should have changed under four sibling families landing
        /// on the same file.</para>
        /// </summary>
        static void Cells(MeshBuilder mb, float x0, float x1, float y0, float y1, float z,
                          int cols, int rows, float bevel, MaterialRole role)
        {
            if (x1 - x0 <= 1e-4f || y1 - y0 <= 1e-4f) return;
            cols = Mathf.Max(cols, 1);
            rows = Mathf.Max(rows, 1);
            mb.BeginRole(role);

            float cw = (x1 - x0) / cols, ch = (y1 - y0) / rows;
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                {
                    float cx0 = x0 + c * cw, cx1 = cx0 + cw;
                    float cy0 = y0 + r * ch, cy1 = cy0 + ch;
                    if (bevel > 1e-4f) Tile(mb, cx0, cx1, cy0, cy1, z, bevel);
                    else Quad(mb, cx0, cx1, cy0, cy1, z);
                }
        }

        /// <summary>Interior cell-edge bars, as boxes with the wall-facing face dropped — the same
        /// construction and the same <see cref="Faces.NoBack"/> saving <c>PanelGrid</c> makes for
        /// its muntins.</summary>
        static void Bars(MeshBuilder mb, float x0, float x1, float y0, float y1, float z,
                         int cols, int rows, float barW, float barD, MaterialRole role)
        {
            if (barW <= 1e-4f || barD <= 1e-4f) return;
            float span = x1 - x0, height = y1 - y0;
            if (span <= 1e-4f || height <= 1e-4f) return;

            for (int c = 1; c < Mathf.Max(cols, 1); c++)
                Kernels.Box(mb, new Vector3(barW, height, barD),
                            new Vector3(x0 + span * c / cols, (y0 + y1) * 0.5f, z), role, Faces.NoBack);
            for (int r = 1; r < Mathf.Max(rows, 1); r++)
                Kernels.Box(mb, new Vector3(span, barW, barD),
                            new Vector3((x0 + x1) * 0.5f, y0 + height * r / rows, z), role, Faces.NoBack);
        }

        static void Quad(MeshBuilder mb, float x0, float x1, float y0, float y1, float z)
        {
            Vector3 n = Vector3.forward;
            int a = mb.Vert(new Vector3(x0, y0, z), n);
            int b = mb.Vert(new Vector3(x1, y0, z), n);
            int c = mb.Vert(new Vector3(x1, y1, z), n);
            int d = mb.Vert(new Vector3(x0, y1, z), n);
            mb.QuadFacing(a, b, c, d, n);
        }

        /// <summary>A proud tile: the field stands off by <paramref name="bevel"/>, inset by the
        /// same on every side, with four sloped returns down to the cell edge. This is the Mission
        /// grid-tiled bulkhead (design.md §3), and it is <see cref="Kernels.PanelGrid"/>'s raised
        /// panel with the sign of the offset flipped outward.</summary>
        static void Tile(MeshBuilder mb, float x0, float x1, float y0, float y1, float z, float bevel)
        {
            float bx = Mathf.Min(bevel, (x1 - x0) * 0.45f);
            float by = Mathf.Min(bevel, (y1 - y0) * 0.45f);
            float zf = z + bevel;

            Quad(mb, x0 + bx, x1 - bx, y0 + by, y1 - by, zf);
            Return(mb, new Vector3(x0, y0, z), new Vector3(x1, y0, z),
                       new Vector3(x1 - bx, y0 + by, zf), new Vector3(x0 + bx, y0 + by, zf));
            Return(mb, new Vector3(x1, y0, z), new Vector3(x1, y1, z),
                       new Vector3(x1 - bx, y1 - by, zf), new Vector3(x1 - bx, y0 + by, zf));
            Return(mb, new Vector3(x1, y1, z), new Vector3(x0, y1, z),
                       new Vector3(x0 + bx, y1 - by, zf), new Vector3(x1 - bx, y1 - by, zf));
            Return(mb, new Vector3(x0, y1, z), new Vector3(x0, y0, z),
                       new Vector3(x0 + bx, y0 + by, zf), new Vector3(x0 + bx, y1 - by, zf));
        }

        static void Return(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            if (n.sqrMagnitude < 1e-8f) return;
            if (n.z < 0f) n = -n;                              // returns always face out of the wall
            int ia = mb.Vert(a, n), ib = mb.Vert(b, n);
            int ic = mb.Vert(c, n), id = mb.Vert(d, n);
            mb.QuadFacing(ia, ib, ic, id, n);
        }

        // ---- DetailLevel degradation, local to this family (design #452 D6) -----------------

        /// <summary>The cross-section a moulding is swept with at this budget — the same rule the
        /// window family settled on in #457: degrade the <i>profile</i>, not the kernel, because a
        /// bevelled <see cref="Kernels.Box"/> stand-in costs more than the sweep it replaces.</summary>
        static ProfileId Sectioned(ProfileId id, DetailLevel detail)
            => detail == DetailLevel.Full || id == ProfileId.None ? id : ProfileId.Chamfer;

        static void FlatQuad(MeshBuilder mb, float w, float h, float z, MaterialRole role)
        {
            mb.BeginRole(role);
            Quad(mb, -w * 0.5f, w * 0.5f, 0f, h, z);
        }
    }
}
