using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>What shape the head (the trim over the opening) runs along. It selects the
    /// <b>rise of the head path and nothing else</b> — every other difference between a Sunset
    /// lintel and a Marina arch is a number in the parameter bag. <see cref="Flat"/> and
    /// <see cref="Hooded"/> both have zero rise, and <c>Paths.Arc(rise: 0)</c> <i>is</i>
    /// <c>Paths.Line</c>, so there is no flat-head branch anywhere in this file (design #452
    /// generators.md §3/§5.2 — the property #453 acceptance 2 exists to protect).</summary>
    public enum HeadType { Flat, Segmental, Round, Hooded, Pedimented }

    /// <summary>
    /// The first real artifact family (#457, design #452 generators.md §5): a double-hung /
    /// slider / fixed window, composed entirely from the #453 kernels — no vertex math of its own
    /// beyond the <see cref="DetailLevel.Flat"/> floor quad.
    ///
    /// <para><b>Part-local frame</b> (as <see cref="IPartGenerator"/> requires): +X along the
    /// facade, +Y up, +Z outward toward the street. The opening spans <c>x ∈ [-w/2, w/2]</c>,
    /// <c>y ∈ [0, h]</c> — the <c>BottomCenter</c> anchor <c>BuildingAssembler.PlacePart</c>
    /// establishes. The reveal, sash and glass sit at <b>negative</b> z (into the wall) and the
    /// casing, head and sill project at positive z. The building mass has no hole cut in it, so a
    /// part's <c>mountDepth_m</c> must lift the assembly proud by at least
    /// <c>revealDepth + glassInset</c> or the recessed half is swallowed by the wall; the shipped
    /// presets are authored that way.</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>),
    /// so every name this generator reads is listed here with its fallback, and a misspelling
    /// silently takes that fallback. Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>w, h</term><description>opening size, m (1.0 × 1.6)</description></item>
    /// <item><term>revealDepth</term><description>how deep the assembly sets back (0.08)</description></item>
    /// <item><term>sashCount</term><description>1 = fixed/slider, 2 = stacked double-hung (2)</description></item>
    /// <item><term>lowerCols, lowerRows</term><description>lights across/up the lower sash (1, 1)</description></item>
    /// <item><term>upperCols, upperRows</term><description>same for the upper sash (falls back to the lower pair)</description></item>
    /// <item><term>meetingRailH</term><description>rail between stacked sashes, m (0.06)</description></item>
    /// <item><term>frameW, frameD</term><description>sash frame width / projection (0.05, 0.035)</description></item>
    /// <item><term>muntinW, muntinD</term><description>glazing bar width / depth (0.025, 0.02)</description></item>
    /// <item><term>glassInset</term><description>glass behind the sash face (0.03)</description></item>
    /// <item><term>casingProfile</term><description><see cref="ProfileId"/>; None = untrimmed (None)</description></item>
    /// <item><term>casingW, casingD</term><description>casing band width / projection (0.09, 0.03)</description></item>
    /// <item><term>sillProfile</term><description><see cref="ProfileId"/>; None = no sill (None)</description></item>
    /// <item><term>sillProjection, sillH, sillOverhang</term><description>0.08 / 0.08 / 0.05</description></item>
    /// <item><term>head</term><description><see cref="HeadType"/> (Flat)</description></item>
    /// <item><term>headProfile</term><description><see cref="ProfileId"/>; None = no head (Ogee)</description></item>
    /// <item><term>headRise</term><description>sagitta for Segmental/Pedimented; Round derives w/2 (0.12)</description></item>
    /// <item><term>headProjection, headH, headOverhang</term><description>0.06 / 0.09 / 0</description></item>
    /// <item><term>frameRole</term><description><see cref="MaterialRole"/> — Accent1 painted, Metal steel/aluminium (Accent1)</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/>, read through <see cref="PartParams.Detail"/> (<see cref="DetailBudget.Default"/>)</description></item>
    /// </list>
    /// </summary>
    public sealed class DoubleHungWindowGenerator : IPartGenerator
    {
        public const string GeneratorId = "window.double_hung";

        /// <summary>Semicircular and segmental heads are curved runs; the segment count follows the
        /// <i>rise</i>, not the head type, so a zero rise is one straight span — which is exactly
        /// <c>Paths.Line</c>. A semicircle lands on this many segments.</summary>
        const int RoundHeadSegments = 12;

        public string Id => GeneratorId;

        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            float w = Mathf.Max(p.GetFloat("w", 1.0f), 0.05f);
            float h = Mathf.Max(p.GetFloat("h", 1.6f), 0.05f);
            float revealDepth = Mathf.Max(p.GetFloat("revealDepth", 0.08f), 0f);
            float glassInset = Mathf.Max(p.GetFloat("glassInset", 0.03f), 0f);
            DetailLevel detail = p.Detail;

            // uv1 normalises against the opening itself, so the facade-decal remap (#280/#281)
            // keeps landing on generated geometry (design #452 D4).
            mb.SetLocalRect(w, h, new Vector2(-w * 0.5f, 0f));

            if (detail == DetailLevel.Flat)
            {
                // The floor, not a new failure mode: one role-coloured quad on the glass plane —
                // exactly the placeholder this pipeline replaced (design #452 D6, §6 mitigation 2).
                FlatQuad(mb, w, h, -(revealDepth + glassInset));
                return mb.Finish(GeneratorId);
            }

            var frameRole = p.GetEnum("frameRole", MaterialRole.Accent1);

            // ---- 1. reveal — the hole ------------------------------------------------------
            // Role Base so it takes the building's own wall colour and reads as depth rather than
            // decoration (generators.md §5.2 step 1).
            Kernels.Reveal(mb, w, h, revealDepth, MaterialRole.Base);

            // ---- 2. sash — one PanelGrid each, stacked, sharing the meeting rail -----------
            int sashCount = Mathf.Clamp(p.GetInt("sashCount", 2), 1, 2);
            float meetingRailH = sashCount == 2 ? Mathf.Max(p.GetFloat("meetingRailH", 0.06f), 0f) : 0f;
            float sashH = sashCount == 2 ? (h - meetingRailH) * 0.5f : h;
            float frameW = Mathf.Max(p.GetFloat("frameW", 0.05f), 0f);
            float frameD = Mathf.Max(p.GetFloat("frameD", 0.035f), 0f);

            int lowerCols = Mathf.Max(p.GetInt("lowerCols", 1), 1);
            int lowerRows = Mathf.Max(p.GetInt("lowerRows", 1), 1);
            int upperCols = Mathf.Max(p.GetInt("upperCols", lowerCols), 1);
            int upperRows = Mathf.Max(p.GetInt("upperRows", lowerRows), 1);

            var sash = new PanelGridParams
            {
                w = w,
                h = sashH,
                barW = Mathf.Max(p.GetFloat("muntinW", 0.025f), 0f),
                barD = Mathf.Max(p.GetFloat("muntinD", 0.02f), 0f),
                frameW = frameW,
                frameD = frameD,
                infillInset = glassInset,
                panelBevel = 0f,                       // glazed, not panelled
                frameRole = frameRole,
                infillRole = MaterialRole.Glass,
            };

            for (int s = 0; s < sashCount; s++)
            {
                // s == 0 is the lower sash; a 2-over-2 may divide its upper light differently.
                int cols = s == 0 ? lowerCols : upperCols;
                int rows = s == 0 ? lowerRows : upperRows;
                sash.cols = PanelGridParams.Even(Lights(cols, detail));
                sash.rows = PanelGridParams.Even(Lights(rows, detail));
                Kernels.PanelGrid(mb, sash,
                                  offsetY: s * (sashH + meetingRailH),
                                  offsetZ: -revealDepth);
            }

            if (sashCount == 2 && meetingRailH > 0f)
                Kernels.Box(mb, new Vector3(w, meetingRailH, frameD),
                            new Vector3(0f, sashH + meetingRailH * 0.5f, -revealDepth + frameD * 0.5f),
                            frameRole, Faces.NoBack);

            // ---- 3. casing — one mitred closed sweep round the opening ---------------------
            // The §3 mitre is what stops the corners pinching. The band's centreline sits half a
            // band outside the opening, so its inner edge butts the reveal exactly (the same
            // convention Kernels.PanelGrid uses for its own frame, one band inside).
            var casingProfile = p.GetEnum("casingProfile", ProfileId.None);
            float casingW = Mathf.Max(p.GetFloat("casingW", 0.09f), 0f);
            float casingD = Mathf.Max(p.GetFloat("casingD", 0.03f), 0f);
            float casingBand = casingProfile != ProfileId.None ? casingW : 0f;

            if (casingBand > 0f)
                Kernels.ProfileSweep(mb, Profiles.Scaled(Sectioned(casingProfile, detail), casingD, casingW),
                                     Paths.Rect(w + casingW, h + casingW, new Vector3(0f, h * 0.5f, 0f)),
                                     frameRole, closedPath: true, capEnds: false, smoothAlong: false);

            // ---- 4. head — flat and arched are the SAME sweep ------------------------------
            var head = p.GetEnum("head", HeadType.Flat);
            var headProfile = p.GetEnum("headProfile", ProfileId.Ogee);
            if (headProfile != ProfileId.None)
            {
                float headH = Mathf.Max(p.GetFloat("headH", 0.09f), 1e-3f);
                float headWidth = w + 2f * casingBand + 2f * Mathf.Max(p.GetFloat("headOverhang", 0f), 0f);
                float rise = HeadRise(head, headWidth, p.GetFloat("headRise", 0.12f));
                bool curved = Mathf.Abs(rise) >= Paths.FlatRiseEpsilon;
                var center = new Vector3(0f, h + casingBand + headH * 0.5f, 0f);

                // Only Pedimented leaves the arc family — its two straight rakes are a polyline,
                // not a circle. Flat, Hooded, Segmental and Round all go through Arc, which
                // degenerates to Line at rise 0.
                Vector3[] headPath = head == HeadType.Pedimented
                    ? Paths.Polyline(center + new Vector3(-headWidth * 0.5f, 0f, 0f),
                                     center + new Vector3(0f, rise, 0f),
                                     center + new Vector3(headWidth * 0.5f, 0f, 0f))
                    : Paths.Arc(headWidth, rise, HeadSegments(headWidth, rise, detail), center);

                Kernels.ProfileSweep(mb, Profiles.Scaled(Sectioned(headProfile, detail),
                                                         Mathf.Max(p.GetFloat("headProjection", 0.06f), 0f), headH),
                                     headPath, MaterialRole.Accent2,
                                     closedPath: false, capEnds: true, smoothAlong: curved);
            }

            // ---- 5. sill — runs past the opening on both sides ----------------------------
            var sillProfile = p.GetEnum("sillProfile", ProfileId.None);
            if (sillProfile != ProfileId.None)
            {
                float sillH = Mathf.Max(p.GetFloat("sillH", 0.08f), 1e-3f);
                float halfRun = w * 0.5f + casingBand + Mathf.Max(p.GetFloat("sillOverhang", 0.05f), 0f);
                float y = -sillH * 0.5f;               // sill top flush with the opening's bottom
                Kernels.ProfileSweep(mb, Profiles.Scaled(Sectioned(sillProfile, detail),
                                                         Mathf.Max(p.GetFloat("sillProjection", 0.08f), 0f), sillH),
                                     Paths.Line(new Vector3(-halfRun, y, 0f), new Vector3(halfRun, y, 0f)),
                                     MaterialRole.Accent2, closedPath: false, capEnds: true, smoothAlong: false);
            }

            return mb.Finish(GeneratorId);
        }

        // ---- DetailLevel degradation, local to this family (generators.md §5.2) ------------

        /// <summary>Lights across one sash axis. <see cref="DetailLevel.Reduced"/> halves the
        /// muntin count, which is the same thing as halving the light count: <c>n</c> lights carry
        /// <c>n-1</c> bars.</summary>
        static int Lights(int n, DetailLevel detail)
            => detail == DetailLevel.Full ? Mathf.Max(n, 1) : Mathf.Max((n + 1) / 2, 1);

        /// <summary>
        /// The cross-section a moulding is swept with at this budget. <see cref="DetailLevel.Full"/>
        /// keeps the authored moulding; <c>Reduced</c> bevels it — a 3-point
        /// <see cref="ProfileId.Chamfer"/> instead of a 5–7 point Ogee/Sill/Bullnose.
        /// <para><b>Deviation from generators.md §5.2</b>, which degrades swept profiles to "Box
        /// with bevel". Against the kernels as they actually merged that is a <i>regression</i>: a
        /// bevelled <see cref="Kernels.Box"/> emits its chamfer bands and corner triangles, so one
        /// costs ~30 triangles where the whole Ogee casing sweep costs 48 and a Chamfer sweep of
        /// the same run costs 16. Degrading the profile rather than the kernel keeps one code path
        /// and is what actually gets cheaper.</para>
        /// </summary>
        static ProfileId Sectioned(ProfileId id, DetailLevel detail)
            => detail == DetailLevel.Full || id == ProfileId.None ? id : ProfileId.Chamfer;

        // ---- head geometry -----------------------------------------------------------------

        /// <summary>The head path's sagitta. This table is the <i>only</i> place
        /// <see cref="HeadType"/> is read: Flat and Hooded are zero-rise, so they take the same
        /// <c>Paths.Arc</c> call as Segmental and Round and reach <c>Paths.Line</c> through its own
        /// degenerate guard. A hood differs from a flat head only in <c>headProjection</c> and
        /// <c>headOverhang</c> — numbers in the preset, not code here.</summary>
        static float HeadRise(HeadType head, float width, float authoredRise)
        {
            switch (head)
            {
                case HeadType.Round: return width * 0.5f;
                case HeadType.Segmental:
                case HeadType.Pedimented: return authoredRise;
                default: return 0f;                    // Flat, Hooded
            }
        }

        /// <summary>Segments for the head sweep, derived from the rise so nothing branches on the
        /// head type: no rise is one span (a straight lintel), a semicircle is
        /// <see cref="RoundHeadSegments"/>. Below <see cref="DetailLevel.Full"/> the arc is halved
        /// as well — without it an arched preset barely gets cheaper at <c>Reduced</c>, because a
        /// 12-segment sweep dominates a window that has already dropped its muntins.</summary>
        static int HeadSegments(float width, float rise, DetailLevel detail)
        {
            if (width <= 0f || Mathf.Abs(rise) < Paths.FlatRiseEpsilon) return 1;
            int cap = detail == DetailLevel.Full ? RoundHeadSegments : RoundHeadSegments / 2;
            return Mathf.Clamp(Mathf.CeilToInt(2f * RoundHeadSegments * Mathf.Abs(rise) / width), 2, cap);
        }

        // ---- the Flat floor ------------------------------------------------------------------

        static void FlatQuad(MeshBuilder mb, float w, float h, float z)
        {
            mb.BeginRole(MaterialRole.Glass);
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
