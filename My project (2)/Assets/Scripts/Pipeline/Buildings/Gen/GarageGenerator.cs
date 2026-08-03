using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>How the leaf is divided. This selects <b>cell counts and bevel, nothing else</b> —
    /// a roll-up shutter is the same <see cref="Kernels.PanelGrid"/> as a sectional door with many
    /// thin rows and no vertical bars, and a flush door is that grid undivided (design #452
    /// design.md §2 Garage row, issue #471 "Composition"). There is no per-style geometry.</summary>
    public enum GarageDoorStyle { Sectional, Slat, Flush }

    /// <summary>
    /// The garage family (#471, design #452 design.md §2 Garage row / §3 Sunset row): a sectional,
    /// roll-up or flush door in a recessed opening, under a header that may be a flat lintel or a
    /// stucco arch, over an optional driveway apron.
    ///
    /// <para>The garage-under-flat is the defining ground-floor condition of the Sunset and much of
    /// Glen Park, so this family carries more of the city's read than its parameter count suggests.
    /// It is a floor-0 artifact: the opening starts at the floor line, which is what the placement
    /// rule's <c>alignToFloorLine</c> expresses (see #475).</para>
    ///
    /// <para><b>Part-local frame</b> (as <see cref="IPartGenerator"/> requires): +X along the
    /// facade, +Y up, +Z outward toward the street. The opening spans <c>x ∈ [-leafW/2, leafW/2]</c>,
    /// <c>y ∈ [0, leafH]</c> — the <c>BottomCenter</c> anchor <c>BuildingAssembler.PlacePart</c>
    /// establishes, and here <c>y = 0</c> really is the ground. The leaf and reveal sit at
    /// <b>negative</b> z (into the wall); the header and apron project at positive z. The building
    /// mass has no hole cut in it, so a part's <c>mountDepth_m</c> must lift the assembly proud by
    /// at least <c>trackReveal + panelInset</c> or the recessed half is swallowed by the wall; the
    /// shipped presets are authored that way.</para>
    ///
    /// <para><b>The head is one code path.</b> The header sweeps along
    /// <see cref="Paths.Arc"/>, and <c>Arc(rise: 0)</c> <i>is</i> <c>Paths.Line</c>, so the Sunset
    /// stucco arch and a flat SoMa steel lintel are the same call with one number changed — the
    /// property #453 acceptance 2 exists to protect and the reason the arch is part of the opening
    /// surround here rather than a separate hooded element (issue #471, generators.md §3/§5.2).</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>),
    /// so every name this generator reads is listed here with its fallback, and a misspelling
    /// silently takes that fallback. Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>leafW, leafH</term><description>opening size, m — 2.4–4.8 × 2.1–2.6 typical (2.8 × 2.3)</description></item>
    /// <item><term>revealDepth</term><description>how deep the opening surround recesses (0.10)</description></item>
    /// <item><term>trackReveal</term><description>how far the leaf sits back from the wall plane (0.12)</description></item>
    /// <item><term>doorStyle</term><description><see cref="GarageDoorStyle"/> (Sectional)</description></item>
    /// <item><term>panelCols, panelRows</term><description>the sectional grid (3, 4)</description></item>
    /// <item><term>slatRows</term><description>rows a <see cref="GarageDoorStyle.Slat"/> leaf uses instead (14)</description></item>
    /// <item><term>panelBevel</term><description>raised-panel depth; 0 = flat cells (0.03)</description></item>
    /// <item><term>panelInset</term><description>panel face behind the leaf frame (0.03)</description></item>
    /// <item><term>railW, railD</term><description>the rail/stile bars between panels (0.05, 0.03)</description></item>
    /// <item><term>frameW, frameD</term><description>the leaf's own perimeter frame (0.06, 0.04)</description></item>
    /// <item><term>head</term><description><see cref="HeadType"/> — shared with the window family (Flat)</description></item>
    /// <item><term>headerProfile</term><description><see cref="ProfileId"/>; None = no header (Flat)</description></item>
    /// <item><term>headerRise</term><description>sagitta for Segmental/Pedimented; Round derives width/2 (0.15)</description></item>
    /// <item><term>headerProjection, headerH, headerOverhang</term><description>0.06 / 0.14 / 0</description></item>
    /// <item><term>apronProjection</term><description>how far the driveway apron runs out; <b>0 = no apron</b> (0)</description></item>
    /// <item><term>apronSlope</term><description>the apron's fall as a fraction of its thickness; 0 = level slab (0)</description></item>
    /// <item><term>apronH, apronOverhang</term><description>slab thickness / run past the opening per side (0.10, 0.10)</description></item>
    /// <item><term>leafRole</term><description><see cref="MaterialRole"/> — Accent1 painted, Metal roll-up (Accent1)</description></item>
    /// <item><term>panelRole</term><description>role of the panel infill (falls back to <c>leafRole</c>)</description></item>
    /// <item><term>headerRole</term><description>Base for a stucco surround, Accent2 for painted trim, Metal for steel (Accent2)</description></item>
    /// <item><term>apronRole</term><description>role of the apron slab (Base — it is concrete, not trim)</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/>, read through <see cref="PartParams.Detail"/> (<see cref="DetailBudget.Default"/>)</description></item>
    /// </list>
    /// </summary>
    public sealed class SectionalGarageGenerator : IPartGenerator
    {
        public const string GeneratorId = "garage.sectional";

        /// <summary>A semicircular head lands on this many segments; a segmental one takes fewer,
        /// scaled by its rise, and a zero rise takes one — which is a straight lintel. Same table as
        /// the window family, deliberately: an arch over a garage and an arch over a window are the
        /// same arch.</summary>
        const int RoundHeadSegments = 12;

        public string Id => GeneratorId;

        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            float w = Mathf.Max(p.GetFloat("leafW", 2.8f), 0.05f);
            float h = Mathf.Max(p.GetFloat("leafH", 2.3f), 0.05f);
            float revealDepth = Mathf.Max(p.GetFloat("revealDepth", 0.10f), 0f);
            float trackReveal = Mathf.Max(p.GetFloat("trackReveal", 0.12f), 0f);
            var leafRole = p.GetEnum("leafRole", MaterialRole.Accent1);
            DetailLevel detail = p.Detail;

            // uv1 normalises against the opening itself, so the facade-decal remap (#280/#281) keeps
            // landing on generated geometry (design #452 D4).
            mb.SetLocalRect(w, h, new Vector2(-w * 0.5f, 0f));

            if (detail == DetailLevel.Flat)
            {
                // The floor, not a new failure mode: one role-coloured quad on the leaf plane —
                // exactly the placeholder this pipeline replaced (design #452 D6, §6 mitigation 2).
                FlatQuad(mb, w, h, -trackReveal, leafRole);
                return mb.Finish(GeneratorId);
            }

            // ---- 1. reveal — the hole -------------------------------------------------------
            // Role Base so it takes the building's own wall colour and reads as depth rather than
            // decoration. The jambs always run at least as deep as the leaf sits back, so a door
            // authored further back than its surround can never appear to float in mid-air.
            Kernels.Reveal(mb, w, h, Mathf.Max(revealDepth, trackReveal), MaterialRole.Base);

            // ---- 2. leaf — one PanelGrid, which is what that kernel was built for -------------
            var style = p.GetEnum("doorStyle", GarageDoorStyle.Sectional);
            Division(style, p, out int cols, out int rows, out bool bevelled);

            var leaf = new PanelGridParams
            {
                w = w,
                h = h,
                cols = PanelGridParams.Even(Cells(cols, detail)),
                rows = PanelGridParams.Even(Cells(rows, detail)),
                barW = Mathf.Max(p.GetFloat("railW", 0.05f), 0f),
                barD = Mathf.Max(p.GetFloat("railD", 0.03f), 0f),
                frameW = Mathf.Max(p.GetFloat("frameW", 0.06f), 0f),
                frameD = Mathf.Max(p.GetFloat("frameD", 0.04f), 0f),
                infillInset = Mathf.Max(p.GetFloat("panelInset", 0.03f), 0f),
                // Raised panels are five faces a cell instead of one, so they are the first thing a
                // Reduced budget gives up — see Cells' remarks. A Flush leaf never had any.
                panelBevel = bevelled && detail == DetailLevel.Full
                             ? Mathf.Max(p.GetFloat("panelBevel", 0.03f), 0f) : 0f,
                frameRole = leafRole,
                infillRole = p.GetEnum("panelRole", leafRole),
            };
            Kernels.PanelGrid(mb, leaf, offsetY: 0f, offsetZ: -trackReveal);

            // ---- 3. header — flat lintel and stucco arch are the SAME sweep -------------------
            var headerProfile = p.GetEnum("headerProfile", ProfileId.Flat);
            if (headerProfile != ProfileId.None)
            {
                float headerH = Mathf.Max(p.GetFloat("headerH", 0.14f), 1e-3f);
                float headerW = w + 2f * Mathf.Max(p.GetFloat("headerOverhang", 0f), 0f);
                var head = p.GetEnum("head", HeadType.Flat);
                float rise = HeadRise(head, headerW, p.GetFloat("headerRise", 0.15f));
                bool curved = Mathf.Abs(rise) >= Paths.FlatRiseEpsilon;
                var center = new Vector3(0f, h + headerH * 0.5f, 0f);

                // Only Pedimented leaves the arc family — its two straight rakes are a polyline, not
                // a circle. Flat, Hooded, Segmental and Round all go through Arc, which degenerates
                // to Line at rise 0.
                Vector3[] path = head == HeadType.Pedimented
                    ? Paths.Polyline(center + new Vector3(-headerW * 0.5f, 0f, 0f),
                                     center + new Vector3(0f, rise, 0f),
                                     center + new Vector3(headerW * 0.5f, 0f, 0f))
                    : Paths.Arc(headerW, rise, HeadSegments(headerW, rise, detail), center);

                Kernels.ProfileSweep(mb, Profiles.Scaled(Sectioned(headerProfile, detail),
                                                         Mathf.Max(p.GetFloat("headerProjection", 0.06f), 0f),
                                                         headerH),
                                     path, p.GetEnum("headerRole", MaterialRole.Accent2),
                                     closedPath: false, capEnds: true, smoothAlong: curved);
            }

            // ---- 4. apron — the driveway slab at the base ------------------------------------
            float apronProjection = Mathf.Max(p.GetFloat("apronProjection", 0f), 0f);
            if (apronProjection > 0f)
            {
                float apronH = Mathf.Max(p.GetFloat("apronH", 0.10f), 1e-3f);
                float halfRun = w * 0.5f + Mathf.Max(p.GetFloat("apronOverhang", 0.10f), 0f);
                float y = -apronH * 0.5f;                  // slab top flush with the opening's floor
                Kernels.ProfileSweep(mb, Profiles.Scaled(ApronSection(p.GetFloat("apronSlope", 0f)),
                                                         apronProjection, apronH),
                                     Paths.Line(new Vector3(-halfRun, y, 0f), new Vector3(halfRun, y, 0f)),
                                     p.GetEnum("apronRole", MaterialRole.Base),
                                     closedPath: false, capEnds: true, smoothAlong: false);
            }

            return mb.Finish(GeneratorId);
        }

        // ---- leaf division, the only thing doorStyle selects ---------------------------------

        /// <summary>Cell counts and whether the cells are raised. <c>Slat</c> is the sectional grid
        /// with many thin rows and <b>no</b> vertical bars (<c>cols = 1</c>, so
        /// <see cref="Kernels.PanelGrid"/> places no interior column edge at all); <c>Flush</c> is
        /// one undivided cell with no bevel. This table is the whole of the style handling — there
        /// is no geometry behind any of the three names.</summary>
        static void Division(GarageDoorStyle style, PartParams p, out int cols, out int rows, out bool bevelled)
        {
            switch (style)
            {
                case GarageDoorStyle.Slat:
                    cols = 1;
                    rows = Mathf.Max(p.GetInt("slatRows", 14), 1);
                    bevelled = true;
                    return;
                case GarageDoorStyle.Flush:
                    cols = 1;
                    rows = 1;
                    bevelled = false;
                    return;
                default:
                    cols = Mathf.Max(p.GetInt("panelCols", 3), 1);
                    rows = Mathf.Max(p.GetInt("panelRows", 4), 1);
                    bevelled = true;
                    return;
            }
        }

        // ---- DetailLevel degradation, local to this family (generators.md §5.2) ---------------

        /// <summary>Cells along one leaf axis. <see cref="DetailLevel.Reduced"/> halves them, which
        /// halves the rail/stile boxes with them: <c>n</c> cells carry <c>n-1</c> bars. A 16-row
        /// roll-up is where this matters — its slats, not its trim, are its whole cost.</summary>
        static int Cells(int n, DetailLevel detail)
            => detail == DetailLevel.Full ? Mathf.Max(n, 1) : Mathf.Max((n + 1) / 2, 1);

        /// <summary>The cross-section a moulding is swept with at this budget: <c>Full</c> keeps the
        /// authored profile, <c>Reduced</c> bevels it to a 3-point <see cref="ProfileId.Chamfer"/>.
        /// Same choice the window family made and for the same reason — degrading the profile keeps
        /// one code path and is what actually gets cheaper, where degrading the <i>kernel</i> to a
        /// bevelled <see cref="Kernels.Box"/> would emit more triangles, not fewer.</summary>
        static ProfileId Sectioned(ProfileId id, DetailLevel detail)
            => detail == DetailLevel.Full || id == ProfileId.None ? id : ProfileId.Chamfer;

        // ---- head geometry --------------------------------------------------------------------

        /// <summary>The header path's sagitta. This table is the <i>only</i> place
        /// <see cref="HeadType"/> is read: Flat and Hooded are zero-rise, so they take the same
        /// <see cref="Paths.Arc"/> call as Segmental and Round and reach <see cref="Paths.Line"/>
        /// through its own degenerate guard. A Sunset stucco arch and a SoMa steel lintel differ by
        /// this number and nothing else.</summary>
        static float HeadRise(HeadType head, float width, float authoredRise)
        {
            switch (head)
            {
                case HeadType.Round: return width * 0.5f;
                case HeadType.Segmental:
                case HeadType.Pedimented: return authoredRise;
                default: return 0f;                        // Flat, Hooded
            }
        }

        /// <summary>Segments for the header sweep, derived from the rise so nothing branches on the
        /// head type: no rise is one span (a straight lintel), a semicircle is
        /// <see cref="RoundHeadSegments"/>. Below <see cref="DetailLevel.Full"/> the arc halves too,
        /// or an arched preset barely gets cheaper once its panels have already gone flat.</summary>
        static int HeadSegments(float width, float rise, DetailLevel detail)
        {
            if (width <= 0f || Mathf.Abs(rise) < Paths.FlatRiseEpsilon) return 1;
            int cap = detail == DetailLevel.Full ? RoundHeadSegments : RoundHeadSegments / 2;
            return Mathf.Clamp(Mathf.CeilToInt(2f * RoundHeadSegments * Mathf.Abs(rise) / width), 2, cap);
        }

        // ---- the apron cross-section ------------------------------------------------------------

        /// <summary>
        /// The apron's cross-section in <see cref="Profiles"/> space — <c>x</c> across (0 = wall
        /// plane, 1 = full projection), <c>y</c> the lateral axis, here vertical because the run is
        /// horizontal. Four points: underside, nose, sloping top, back at the wall.
        /// <para><b>Deviation from #471's "Box apron", stated plainly.</b> A driveway apron falls
        /// toward the street and <see cref="Kernels.Box"/> is axis-aligned, so it cannot slope; a
        /// box would have to be a wedge to express <c>apronSlope</c> at all. Sweeping this
        /// four-point section along <see cref="Paths.Line"/> costs the same order of triangles
        /// (3 strips + 2 caps vs. a 5-face box) and <b>at <c>apronSlope = 0</c> is exactly the
        /// rectangular slab the issue names</b> — the back face, against the wall, is the one the
        /// open outline omits, which is the same face <see cref="Faces.NoBack"/> would have
        /// dropped.</para>
        /// </summary>
        static Vector2[] ApronSection(float slope)
        {
            float s = Mathf.Clamp01(slope);
            // Wound so the outward normal is the profile tangent rotated −90° — the rule every
            // table in Profiles is authored to.
            return new[]
            {
                new Vector2(0f, -0.5f),        // back of the underside
                new Vector2(1f, -0.5f),        // nose, underside
                new Vector2(1f,  0.5f - s),    // nose, top — dropped by the fall
                new Vector2(0f,  0.5f),        // top, at the wall
            };
        }

        // ---- the Flat floor ---------------------------------------------------------------------

        static void FlatQuad(MeshBuilder mb, float w, float h, float z, MaterialRole role)
        {
            mb.BeginRole(role);
            Vector3 n = Vector3.forward;
            float hw = w * 0.5f;
            int a = mb.Vert(new Vector3(-hw, 0f, z), n);
            int b = mb.Vert(new Vector3(hw, 0f, z), n);
            int c = mb.Vert(new Vector3(hw, h, z), n);
            int d = mb.Vert(new Vector3(-hw, h, z), n);
            mb.QuadFacing(a, b, c, d, n);
        }
    }
}
