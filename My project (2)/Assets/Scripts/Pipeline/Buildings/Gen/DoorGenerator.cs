using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// The door family (#470, design #452 §2 "Door" row): one or two leaves of raised panels under
    /// an optional glazed head, a bottom rail wide enough that the thing reads as a door rather than
    /// a small window, an optional transom light, a mitred surround, a threshold and a header band.
    ///
    /// <para><b>A door is a window with solid infill and a wide bottom rail.</b> #470 predicted this
    /// file would therefore come out substantially shorter than
    /// <see cref="DoubleHungWindowGenerator"/>. Measured, it does not — it is about 7% <i>longer</i>,
    /// and the reason is not duplicated code but arithmetic: the door family carries ten elements
    /// (reveal, transom, panelled field, glazed head, bottom rail, meeting stiles, surround,
    /// threshold, header, hardware) against the window's six, and 34 parameters against 29. Per
    /// element it is a third cheaper to write, which is the kernel leverage the prediction was really
    /// about. It does <i>no</i> vertex math of its own beyond the <see cref="DetailLevel.Flat"/>
    /// floor quad: <see cref="Kernels.PanelGrid"/> already promotes a cell to a raised panel when
    /// <c>panelBevel &gt; 0</c>, so "panelled" versus "glazed" is a number rather than a branch, all
    /// three glazed/panelled bands are one local function, and the threshold and the steel header are
    /// the same <see cref="Kernels.ProfileSweep"/> call at two heights (see <see cref="Course"/>).</para>
    ///
    /// <para><b>Part-local frame</b> (as <see cref="IPartGenerator"/> requires): +X along the facade,
    /// +Y up, +Z outward toward the street; the opening spans <c>x ∈ [-w/2, w/2]</c>,
    /// <c>y ∈ [0, h]</c> — the <c>BottomCenter</c> anchor <c>BuildingAssembler.PlacePart</c>
    /// establishes. Leaves and glass sit at negative z (into the wall), surround / header /
    /// threshold / hardware project at positive z. The building mass has no hole cut in it, so a
    /// preset's <c>mountDepth_m</c> must be at least <c>revealDepth + infillInset</c> or the
    /// recessed half is swallowed by the wall; the shipped presets are authored that way.</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>), so
    /// every name read here is listed with its fallback and a misspelling silently takes that
    /// fallback. Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>w, h</term><description>opening size, m (0.95 × 2.10)</description></item>
    /// <item><term>revealDepth</term><description>how deep the assembly sets back (0.10)</description></item>
    /// <item><term>infillInset</term><description>panel/glass behind the leaf face (0.03)</description></item>
    /// <item><term>leafCount</term><description>1, or 2 for a double door (1)</description></item>
    /// <item><term>panelCols, panelRows</term><description>panel grid <i>per leaf</i> (1, 2)</description></item>
    /// <item><term>glazedFraction</term><description>0 = solid, 1 = fully glazed; between, the upper
    /// rows are Glass — see the remark on <see cref="Generate"/> (0)</description></item>
    /// <item><term>panelBevel</term><description>&gt;0 → raised panels (0.02)</description></item>
    /// <item><term>railW</term><description>the wide bottom rail (0.22)</description></item>
    /// <item><term>stileW</term><description>leaf frame width — stiles, top rail, lock rail (0.065)</description></item>
    /// <item><term>barW, barD</term><description>bars between panels, width / depth (0.05, 0.03)</description></item>
    /// <item><term>frameD</term><description>how far the leaf face stands proud of its grid plane (0.045)</description></item>
    /// <item><term>transomH</term><description>&gt;0 → a fixed light above the door, m (0)</description></item>
    /// <item><term>transomCols</term><description>lights across the transom (1)</description></item>
    /// <item><term>surroundProfile</term><description><see cref="ProfileId"/>; None = untrimmed (None)</description></item>
    /// <item><term>surroundW, surroundD</term><description>surround band width / projection (0.11, 0.04)</description></item>
    /// <item><term>thresholdProfile</term><description><see cref="ProfileId"/> (Bullnose)</description></item>
    /// <item><term>thresholdH, thresholdProjection, thresholdOverhang</term><description>0 (= no threshold) / 0.07 / 0.04</description></item>
    /// <item><term>headerProfile</term><description><see cref="ProfileId"/>; the SoMa steel header (None)</description></item>
    /// <item><term>headerH, headerProjection, headerOverhang</term><description>0 (= no header) / 0.05 / 0.06</description></item>
    /// <item><term>hardware</term><description>0 = none, else the pull/knob box's height, m (0)</description></item>
    /// <item><term>hardwareW, hardwareD, hardwareY</term><description>0.045 / 0.05 / 1.02</description></item>
    /// <item><term>frameRole</term><description><see cref="MaterialRole"/> — Accent1 painted, Metal steel (Accent1)</description></item>
    /// <item><term>panelRole</term><description>solid infill role; Base takes the wall colour (Base)</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/>, read through <see cref="PartParams.Detail"/> (<see cref="DetailBudget.Default"/>)</description></item>
    /// </list>
    /// </summary>
    public sealed class PanelDoorGenerator : IPartGenerator
    {
        public const string GeneratorId = "door.panel";

        public string Id => GeneratorId;

        /// <summary>
        /// <para><b>How <c>glazedFraction</c> is read.</b> #470 specifies "fractional → upper N rows
        /// are Glass". Taken literally that quantises the glazing to <c>1/panelRows</c> of the leaf,
        /// which makes a small vision light impossible without inventing panel rows that then have
        /// to be suppressed. So the fraction sets the glazed band's <i>height</i> directly and the
        /// row split only decides how many lights and how many panels that band and its remainder
        /// are divided into: <c>glazedRows = round(panelRows · glazedFraction)</c>, at least one
        /// whenever the band exists. At the values the presets use the two readings coincide —
        /// <c>panelRows = 3, glazedFraction = 1/3</c> is one light over two panels either way — and
        /// the continuous one additionally expresses "flush leaf, small square light".</para>
        /// </summary>
        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            float w = Mathf.Max(p.GetFloat("w", 0.95f), 0.05f);
            float h = Mathf.Max(p.GetFloat("h", 2.10f), 0.05f);
            float revealDepth = Mathf.Max(p.GetFloat("revealDepth", 0.10f), 0f);
            float infillInset = Mathf.Max(p.GetFloat("infillInset", 0.03f), 0f);
            float glazed = Mathf.Clamp01(p.GetFloat("glazedFraction", 0f));
            var frameRole = p.GetEnum("frameRole", MaterialRole.Accent1);
            DetailLevel detail = p.Detail;

            // uv1 normalises against the opening itself, so the facade-decal remap (#280/#281) keeps
            // landing on generated geometry (design #452 D4).
            mb.SetLocalRect(w, h, new Vector2(-w * 0.5f, 0f));

            if (detail == DetailLevel.Flat)
            {
                // The floor, not a new failure mode (design #452 D6): one role-coloured quad. A door
                // is mostly solid, so the role follows the infill that dominates it — a glazed shop
                // door still degrades to Glass, a panelled one to its own paint.
                FlatQuad(mb, w, h, -(revealDepth + infillInset),
                         glazed >= 0.5f ? MaterialRole.Glass : frameRole);
                return mb.Finish(GeneratorId);
            }

            // ---- 1. reveal — the hole -----------------------------------------------------
            // Role Base so it takes the building's own wall colour and reads as depth, exactly as
            // the window family's step 1 does.
            Kernels.Reveal(mb, w, h, revealDepth, MaterialRole.Base);

            float stileW = Mathf.Max(p.GetFloat("stileW", 0.065f), 0f);
            float frameD = Mathf.Max(p.GetFloat("frameD", 0.045f), 0f);
            float leafZ = -revealDepth;

            var grid = new PanelGridParams
            {
                w = w,
                barW = Mathf.Max(p.GetFloat("barW", 0.05f), 0f),
                barD = Mathf.Max(p.GetFloat("barD", 0.03f), 0f),
                frameW = stileW,
                frameD = frameD,
                infillInset = infillInset,
                frameRole = frameRole,
            };

            // Every glazed or panelled band of this door is one call with different numbers: the
            // transom light, the panelled field, the glazed head. That *is* the family's claim — a
            // door is a window with solid infill — written as a local function rather than as three
            // copies of the same six lines. A zero-height band emits nothing, which is how
            // "no transom" and "fully glazed" cost no branch at the call sites.
            void Band(float bandH, float atY, int cols, int rows, float bevel, MaterialRole infill)
            {
                if (bandH <= 1e-3f) return;
                grid.h = bandH;
                grid.cols = PanelGridParams.Even(cols);
                grid.rows = PanelGridParams.Even(Divisions(rows, detail));
                grid.panelBevel = bevel;
                grid.infillRole = infill;
                Kernels.PanelGrid(mb, grid, offsetY: atY, offsetZ: leafZ);
            }

            // ---- 2. transom, leaves, bottom rail, meeting stiles --------------------------
            // A transom's own frame band and the leaves' top rail meet to form the transom bar, so
            // there is no bar element: two adjacent PanelGrid frames already are one.
            float transomH = Mathf.Clamp(p.GetFloat("transomH", 0f), 0f, h * 0.5f);
            float leafH = h - transomH;
            float railW = Mathf.Clamp(p.GetFloat("railW", 0.22f), 0f, leafH * 0.5f);
            float fieldH = leafH - railW;
            int leafCount = Mathf.Clamp(p.GetInt("leafCount", 1), 1, 2);
            int panelRows = Mathf.Max(p.GetInt("panelRows", 2), 1);
            int glazedRows = GlazedRows(panelRows, glazed);

            // Both leaves are one grid: PanelGrid divides across the whole opening, and at
            // leafCount 2 the division at x = 0 is exactly the leaf boundary. Detail halves the
            // divisions *within* a leaf, never the leaf count — a double door stays a double door.
            int leafCols = Divisions(Mathf.Max(p.GetInt("panelCols", 1), 1), detail) * leafCount;
            float glazedH = fieldH * glazed;
            float solidH = fieldH - glazedH;

            // A raised panel costs 10 triangles per cell against a flat one's 2, and on a 9-slat
            // dock door the cells are the whole budget — so flattening the field is what actually
            // makes Reduced cheap. The element set is unchanged; only the relief goes.
            float bevel = detail == DetailLevel.Full ? Mathf.Max(p.GetFloat("panelBevel", 0.02f), 0f) : 0f;

            Band(transomH, h - transomH, Divisions(p.GetInt("transomCols", 1), detail), 1,
                 0f, MaterialRole.Glass);
            Band(solidH, railW, leafCols, Mathf.Max(panelRows - glazedRows, 1),
                 bevel, p.GetEnum("panelRole", MaterialRole.Base));
            Band(glazedH, railW + solidH, leafCols, glazedRows, 0f, MaterialRole.Glass);

            // The one element that separates a door from a window: a rail deep enough to kick.
            if (railW > 0f)
                Kernels.Box(mb, new Vector3(w, railW, frameD),
                            new Vector3(0f, railW * 0.5f, leafZ + frameD * 0.5f),
                            frameRole, Faces.NoBack);

            // …and the one thing a shared grid cannot say: a pair of meeting stiles is twice as wide
            // as a bar between panels.
            if (leafCount == 2 && stileW > 0f)
                Kernels.Box(mb, new Vector3(stileW * 2f, fieldH, frameD),
                            new Vector3(0f, railW + fieldH * 0.5f, leafZ + frameD * 0.5f),
                            frameRole, Faces.NoBack);

            // ---- 3. surround — one mitred closed sweep round the opening -------------------
            // Same convention as the window casing: the band's centreline sits half a band outside
            // the opening, so its inner edge butts the reveal.
            var surroundProfile = p.GetEnum("surroundProfile", ProfileId.None);
            float band = surroundProfile != ProfileId.None ? Mathf.Max(p.GetFloat("surroundW", 0.11f), 0f) : 0f;
            if (band > 0f)
                Kernels.ProfileSweep(mb, Profiles.Scaled(Sectioned(surroundProfile, detail),
                                                         Mathf.Max(p.GetFloat("surroundD", 0.04f), 0f), band),
                                     Paths.Rect(w + band, h + band, new Vector3(0f, h * 0.5f, 0f)),
                                     frameRole, closedPath: true, capEnds: false, smoothAlong: false);

            // ---- 4. threshold and header — one call, two heights ---------------------------
            // A stone threshold and a SoMa steel header differ in profile, height and y. Nothing
            // else, which is why there is one helper and not two elements.
            Course(mb, p.GetEnum("thresholdProfile", ProfileId.Bullnose),
                   Mathf.Max(p.GetFloat("thresholdProjection", 0.07f), 0f),
                   Mathf.Max(p.GetFloat("thresholdH", 0f), 0f),
                   w * 0.5f + band + Mathf.Max(p.GetFloat("thresholdOverhang", 0.04f), 0f),
                   y => -y * 0.5f, detail);                   // top flush with the opening's bottom

            Course(mb, p.GetEnum("headerProfile", ProfileId.None),
                   Mathf.Max(p.GetFloat("headerProjection", 0.05f), 0f),
                   Mathf.Max(p.GetFloat("headerH", 0f), 0f),
                   w * 0.5f + band + Mathf.Max(p.GetFloat("headerOverhang", 0.06f), 0f),
                   y => h + band + y * 0.5f, detail);         // sits on top of the surround

            // ---- 5. hardware — a Metal box on each leaf's lock stile ----------------------
            float hardware = Mathf.Max(p.GetFloat("hardware", 0f), 0f);
            if (hardware > 0f)
            {
                float hw = Mathf.Max(p.GetFloat("hardwareW", 0.045f), 1e-3f);
                float hd = Mathf.Max(p.GetFloat("hardwareD", 0.05f), 1e-3f);
                float hy = Mathf.Clamp(p.GetFloat("hardwareY", 1.02f), hardware * 0.5f, leafH);
                float leafW = w / leafCount;
                // Leaf 0 hinges on the left so its lock stile is its right edge, and leaf 1
                // mirrors it — which on a pair is the same line, the meeting stile, and on a single
                // leaf is the opening's own right jamb. One expression covers both.
                float lockEdge = -w * 0.5f + leafW;
                for (int s = 0; s < leafCount; s++)
                    Kernels.Box(mb, new Vector3(hw, hardware, hd),
                                new Vector3(lockEdge + (s == 0 ? -1f : 1f) * (stileW + hw * 0.5f), hy,
                                            leafZ + frameD + hd * 0.5f),
                                MaterialRole.Metal, Faces.NoBack);
            }

            return mb.Finish(GeneratorId);
        }

        // ---- composition helpers -----------------------------------------------------------

        /// <summary>A horizontal moulding run across the opening — threshold, header, water table.
        /// <paramref name="atY"/> receives the band's thickness and returns its centreline height, so
        /// the two call sites differ by one lambda instead of by a duplicated sweep.</summary>
        static void Course(MeshBuilder mb, ProfileId id, float projection, float thickness,
                           float halfRun, System.Func<float, float> atY, DetailLevel detail)
        {
            if (id == ProfileId.None || thickness <= 1e-4f) return;
            float y = atY(thickness);
            Kernels.ProfileSweep(mb, Profiles.Scaled(Sectioned(id, detail), projection, thickness),
                                 Paths.Line(new Vector3(-halfRun, y, 0f), new Vector3(halfRun, y, 0f)),
                                 MaterialRole.Accent2, closedPath: false, capEnds: true, smoothAlong: false);
        }

        /// <summary>How many of the leaf's rows are glass. At least one whenever any glazing was
        /// asked for, and never all of them unless the whole leaf is glazed — see the remark on
        /// <see cref="Generate"/>.</summary>
        static int GlazedRows(int panelRows, float glazedFraction)
        {
            if (glazedFraction <= 0f) return 0;
            return Mathf.Clamp(Mathf.RoundToInt(panelRows * glazedFraction), 1, panelRows);
        }

        // ---- DetailLevel degradation, local to this family (design #452 D6) -----------------

        /// <summary>Cells across one axis of a grid. <see cref="DetailLevel.Reduced"/> halves them,
        /// which halves the bars between them: <c>n</c> cells carry <c>n-1</c> bars.</summary>
        static int Divisions(int n, DetailLevel detail)
            => detail == DetailLevel.Full ? Mathf.Max(n, 1) : Mathf.Max((n + 1) / 2, 1);

        /// <summary>The cross-section a moulding is swept with at this budget — the authored profile
        /// at <see cref="DetailLevel.Full"/>, a 3-point <see cref="ProfileId.Chamfer"/> below it.
        /// Same reasoning as the window family: degrading the <i>profile</i> rather than swapping the
        /// kernel for a bevelled <see cref="Kernels.Box"/> keeps one code path and is what actually
        /// gets cheaper.
        /// <para><b>A budget must never make something more expensive.</b> The window family's
        /// version of this substitutes Chamfer unconditionally, which <i>upgrades</i> a 2-point
        /// <see cref="ProfileId.Flat"/> band to 3 points — measured, that turned the flush Sunset
        /// door's steel threshold from 2 triangles into 6 and made its whole <c>Reduced</c> mesh
        /// larger than its <c>Full</c> one. So the substitution is guarded by point count: a section
        /// already at or below Chamfer's cost is left alone.</para></summary>
        static ProfileId Sectioned(ProfileId id, DetailLevel detail)
        {
            if (detail == DetailLevel.Full || id == ProfileId.None) return id;
            var authored = Profiles.Get(id);
            return authored != null && authored.Length <= Profiles.Chamfer.Length ? id : ProfileId.Chamfer;
        }

        // ---- the Flat floor ------------------------------------------------------------------

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
