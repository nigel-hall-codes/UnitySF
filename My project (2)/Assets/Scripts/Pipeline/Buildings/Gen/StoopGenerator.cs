using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>What stands on the flight's edge. Like <c>HeadType</c> in the window family it
    /// selects <b>which repeated member and at what pitch</b> and nothing else: posts and balusters
    /// are the same box laid along the same rake, differing in width and spacing, so there is no
    /// per-style branch below beyond picking those two numbers.</summary>
    public enum StoopRailing { None, Posts, Balustrade }

    /// <summary>
    /// The stoop family (#495, design #452 design.md §2 "Stoop" row) — the raised entry that makes
    /// an SF row house read as one, and the <b>first user of <see cref="Paths.Stair"/></b>, which
    /// shipped in #453 and had never executed.
    ///
    /// <para><b>Composition.</b> One <see cref="Kernels.ProfileSweep"/> along the stair polyline is
    /// the entire flight — every tread, every riser, both flanks and both ends. The landing is one
    /// <see cref="Kernels.Box"/>, a cheek wall is one box per step, the coping is the same sweep
    /// along the same polyline shifted onto the cheek, and the handrail is that sweep again along a
    /// three-point rake. Nothing here does vertex math of its own.</para>
    ///
    /// <para><b>The balustrade is deliberately NOT the deferred <c>Lattice</c> kernel.</b> A
    /// balustrade has no diagonals — it is a repeated vertical member between two rails, which is a
    /// box laid at a pitch and two sweeps. Nothing in this family is waiting on <c>Lattice</c>;
    /// the kernel is still owed to the Mission scissor gate and the Marina balconette, not to
    /// this.</para>
    ///
    /// <para><b>Using <see cref="Paths.Stair"/>.</b> The kernel ascends toward <c>+Z</c> — outward,
    /// toward the street. A stoop ascends the other way: you climb <i>toward</i> the facade. Passing
    /// a <b>negative run</b> turns the flight round without a mirroring pass, because the kernel
    /// applies <c>run</c> as a signed displacement and clamps only <c>steps</c>. The path then needs
    /// one translation to put its top at the landing, which it would have needed anyway (the kernel
    /// takes no origin). See the remark on <see cref="Flight"/> for what that says about the
    /// kernel.</para>
    ///
    /// <para><b>Part-local frame and anchor</b> (as <see cref="IPartGenerator"/> requires): +X along
    /// the facade, +Y up, +Z outward toward the street. The origin is <b>grade, at the wall plane,
    /// centred on the door's own <c>nx</c></b> — so the part occupies
    /// <c>x ∈ [-(width/2 + cheekWall), +(width/2 + cheekWall)]</c>, <c>y ∈ [0, stepCount·rise]</c>
    /// (plus the railing above it) and <c>z ∈ [0, landingDepth + stepCount·run + noseProjection]</c>.
    /// Everything the stoop is made of therefore sits <i>below</i> the door and <i>in front of</i>
    /// the wall. <b>The door it serves has to be raised by <c>stepCount·rise</c></b> — that is the
    /// number a wiring pass needs, and it is what <c>LandingY</c> reports.</para>
    ///
    /// <para><b>Parameters.</b> The bag has no compile-time schema (see <see cref="PartParams"/>), so
    /// every name read here is listed with its fallback; a misspelling silently takes that fallback.
    /// Names are matched case-sensitively.</para>
    ///
    /// <list type="table">
    /// <item><term>stepCount</term><description>steps in the flight, 3–8 typical (4)</description></item>
    /// <item><term>rise, run</term><description>per step, m (0.17, 0.28)</description></item>
    /// <item><term>width</term><description>clear width of the flight, m (1.40)</description></item>
    /// <item><term>landingDepth</term><description>the flat platform at the door, m (0.90)</description></item>
    /// <item><term>treadThickness</term><description>how thick the swept step slab is (0.12)</description></item>
    /// <item><term>noseProjection</term><description>tread overhang; 0 = flush risers (0.03)</description></item>
    /// <item><term>cheekWall</term><description>solid side wall thickness, m; <b>0 = open sides</b> (0)</description></item>
    /// <item><term>cheekCapProfile</term><description><see cref="ProfileId"/> of the coping; None = bare cheek (Bullnose)</description></item>
    /// <item><term>cheekCapH, cheekCapOverhang</term><description>coping thickness / how far it laps the cheek each side (0.09, 0.025)</description></item>
    /// <item><term>railingStyle</term><description><see cref="StoopRailing"/> (None)</description></item>
    /// <item><term>postPitch, postW</term><description>Posts: pitch and square section, m (0.90, 0.09)</description></item>
    /// <item><term>balusterPitch, balusterW</term><description>Balustrade: pitch and square section, m (0.16, 0.05)</description></item>
    /// <item><term>handrailProfile</term><description><see cref="ProfileId"/>; None = no top rail (Bullnose)</description></item>
    /// <item><term>handrailH</term><description>rail height above whatever it stands on, m (0.95)</description></item>
    /// <item><term>handrailW, handrailD</term><description>rail section across / thick, m (0.11, 0.07)</description></item>
    /// <item><term>bottomRailY, bottomRailProfile</term><description>Balustrade only; 0 = none (0.09, Flat)</description></item>
    /// <item><term>newelW</term><description>the post that terminates the rail at the bottom nose; 0 = none (derived)</description></item>
    /// <item><term>stepRole</term><description><see cref="MaterialRole"/> of flight + landing — masonry reads as the wall (Base)</description></item>
    /// <item><term>copingRole</term><description>the cheek coping (Accent2)</description></item>
    /// <item><term>railRole</term><description>handrail, balusters, newels (Accent1)</description></item>
    /// <item><term>detail</term><description><see cref="DetailLevel"/>, via <see cref="PartParams.Detail"/></description></item>
    /// </list>
    /// </summary>
    public sealed class StoopGenerator : IPartGenerator
    {
        public const string GeneratorId = "stoop.flight";

        public string Id => GeneratorId;

        /// <summary>Balusters and posts are laid at this many multiples of their authored pitch below
        /// <see cref="DetailLevel.Full"/>. Halving the count is the same trade the cornice family
        /// makes with its dentils: a thinner balustrade still reads as a balustrade, an absent one
        /// reads as a different building.</summary>
        const float ReducedPitchFactor = 2f;

        public PartMesh Generate(PartParams p, MeshBuilder mb)
        {
            if (mb == null) return default;

            int steps = Mathf.Clamp(p.GetInt("stepCount", 4), 1, 32);
            float rise = Mathf.Max(p.GetFloat("rise", 0.17f), 1e-3f);
            float run = Mathf.Max(p.GetFloat("run", 0.28f), 1e-3f);
            float width = Mathf.Max(p.GetFloat("width", 1.40f), 0.05f);
            float landing = Mathf.Max(p.GetFloat("landingDepth", 0.90f), 0f);
            // The overhang is capped at half a run: past that a "nosing" is a cantilever, and the
            // raked riser it is expressed as (see Flight) would lean past the step below it.
            float nose = Mathf.Clamp(p.GetFloat("noseProjection", 0.03f), 0f, run * 0.5f);
            float slab = Mathf.Max(p.GetFloat("treadThickness", 0.12f), 1e-3f);
            float cheek = Mathf.Max(p.GetFloat("cheekWall", 0f), 0f);
            var stepRole = p.GetEnum("stepRole", MaterialRole.Base);
            DetailLevel detail = p.Detail;

            float landingY = steps * rise;                 // the door's threshold
            float flightRun = steps * run;
            float bottomNose = landing + flightRun + nose; // the outermost point of the whole part
            float halfW = width * 0.5f;

            // uv1 normalises against the stoop's own front elevation, so the facade-decal remap
            // (#280/#281) keeps landing on generated geometry (design #452 D4).
            mb.SetLocalRect(width + 2f * cheek, landingY, new Vector2(-(halfW + cheek), 0f));

            // ---- the DetailLevel.Flat floor -------------------------------------------------
            // One role-coloured quad standing in for the whole mass, at the outermost plane so the
            // silhouette a distant stoop contributes is the right one. Two triangles — the same
            // floor every family lands on (design #452 D6).
            if (detail == DetailLevel.Flat)
            {
                Detail.FlatQuad(mb, width + 2f * cheek, landingY, bottomNose, stepRole);
                return mb.Finish(GeneratorId);
            }

            // ---- 1. the flight — one stair polyline, every tread and riser -------------------
            Vector3[] flight = Flight(steps, rise, run, landing, nose);
            foreach (var face in StepSlab(halfW, slab))
                Kernels.ProfileSweep(mb, face, flight, stepRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);

            // ---- 2. the landing block -------------------------------------------------------
            // A solid podium from grade to the threshold, not a cantilevered slab: an SF stoop's
            // landing stands on the same masonry the flight does. Its back face is the wall and its
            // bottom is below grade, so neither is emitted.
            if (landing > 1e-3f)
                Kernels.Box(mb, new Vector3(width, landingY, landing),
                            new Vector3(0f, landingY * 0.5f, landing * 0.5f), stepRole,
                            Faces.All & ~Faces.Back & ~Faces.Bottom);

            // ---- 3. cheek walls, one box per step -------------------------------------------
            // The stepped side wall. A Box cannot be a wedge, and a sweep cannot reach the ground
            // from a constant section, so the honest expression of "solid cheek" is one column per
            // step standing on grade — which is also exactly how the masonry is laid.
            float cheekX = halfW + cheek * 0.5f;
            if (cheek > 1e-3f)
                for (int i = 0; i < steps; i++)
                {
                    float top = (i + 1) * rise;
                    float zFront = landing + flightRun - i * run + nose;
                    // The topmost cheek runs all the way back to the wall, absorbing the landing's
                    // own cheek: one box instead of two, and no buried face between them.
                    float zBack = i == steps - 1 ? 0f : landing + flightRun - (i + 1) * run;
                    var size = new Vector3(cheek, top, zFront - zBack);
                    var at = new Vector3(0f, top * 0.5f, (zFront + zBack) * 0.5f);
                    // Back face butts the next cheek up (or the wall); bottom is below grade.
                    var faces = Faces.All & ~Faces.Back & ~Faces.Bottom;
                    Kernels.Box(mb, size, at + new Vector3(-cheekX, 0f, 0f), stepRole, faces);
                    Kernels.Box(mb, size, at + new Vector3(cheekX, 0f, 0f), stepRole, faces);
                }

            // ---- 4. the coping — the flight's own polyline, moved onto the cheek --------------
            var capProfile = cheek > 1e-3f ? p.GetEnum("cheekCapProfile", ProfileId.Bullnose) : ProfileId.None;
            float capH = Mathf.Max(p.GetFloat("cheekCapH", 0.09f), 0f);
            if (capProfile != ProfileId.None && capH > 1e-4f)
            {
                float capW = cheek + 2f * Mathf.Max(p.GetFloat("cheekCapOverhang", 0.025f), 0f);
                var cap = Raked(Detail.Section(capProfile, detail), capH, capW, 0f);
                var copingRole = p.GetEnum("copingRole", MaterialRole.Accent2);
                // Continued back to the wall so the coping caps the landing's cheek too. The extra
                // point is collinear with the top tread, so it costs one ring and no corner — and it
                // is only added when there IS a landing, because a repeated path point makes
                // ProfileSweep bail out entirely rather than emit a zero-length ring.
                Vector3[] over = landing > 1e-3f ? Append(flight, new Vector3(0f, landingY, 0f)) : flight;
                Kernels.ProfileSweep(mb, cap, Shifted(over, -cheekX), copingRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);
                Kernels.ProfileSweep(mb, cap, Shifted(over, cheekX), copingRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);
            }

            // ---- 5. the railing --------------------------------------------------------------
            var style = p.GetEnum("railingStyle", StoopRailing.None);
            if (style == StoopRailing.None) return mb.Finish(GeneratorId);

            bool balustrade = style == StoopRailing.Balustrade;
            float memberW = Mathf.Max(balustrade ? p.GetFloat("balusterW", 0.05f)
                                                 : p.GetFloat("postW", 0.09f), 1e-3f);
            float pitch = Mathf.Max(balustrade ? p.GetFloat("balusterPitch", 0.16f)
                                               : p.GetFloat("postPitch", 0.90f), memberW);
            if (detail != DetailLevel.Full) pitch *= ReducedPitchFactor;

            // On a cheek wall the railing stands on the coping; on an open-sided stoop it stands on
            // the tread itself, set in far enough that its outer face is flush with the flank.
            float railX = cheek > 1e-3f ? cheekX : Mathf.Max(halfW - memberW * 0.5f, 0f);
            float standOff = cheek > 1e-3f ? capH : 0f;
            float handrailH = Mathf.Max(p.GetFloat("handrailH", 0.95f), 0f);
            float lift = standOff + handrailH;
            var railRole = p.GetEnum("railRole", MaterialRole.Accent1);

            // The rake: level over the landing and the top tread, then parallel to the nosing line
            // down to the bottom nose. Three points, so ProfileSweep mitres the one bend for free —
            // except on a one-step stoop, where the knee and the bottom nose are the same point and
            // the rail is simply level. A repeated path point makes ProfileSweep bail out, so the
            // degenerate case drops the point rather than relying on it being harmless.
            float topTreadFront = landing + run + nose;
            Vector3[] rake = steps > 1
                ? new[]
                {
                    new Vector3(0f, rise + lift, bottomNose),
                    new Vector3(0f, landingY + lift, topTreadFront),
                    new Vector3(0f, landingY + lift, 0f),
                }
                : new[]
                {
                    new Vector3(0f, landingY + lift, topTreadFront),
                    new Vector3(0f, landingY + lift, 0f),
                };

            var handrailProfile = p.GetEnum("handrailProfile", ProfileId.Bullnose);
            float railD = Mathf.Max(p.GetFloat("handrailD", 0.07f), 1e-3f);
            if (handrailProfile != ProfileId.None)
            {
                var section = Raked(Detail.Section(handrailProfile, detail), railD,
                                    Mathf.Max(p.GetFloat("handrailW", 0.11f), 1e-3f), -railD * 0.5f);
                Kernels.ProfileSweep(mb, section, Shifted(rake, -railX), railRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);
                Kernels.ProfileSweep(mb, section, Shifted(rake, railX), railRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);
            }

            // A bottom rail is what turns a row of sticks into a balustrade; Posts do without one.
            float bottomRailY = balustrade ? Mathf.Clamp(p.GetFloat("bottomRailY", 0.09f), 0f, handrailH) : 0f;
            var bottomRailProfile = p.GetEnum("bottomRailProfile", ProfileId.Flat);
            if (bottomRailY > 1e-4f && bottomRailProfile != ProfileId.None)
            {
                var section = Raked(Detail.Section(bottomRailProfile, detail), railD * 0.6f,
                                    Mathf.Max(p.GetFloat("handrailW", 0.11f), 1e-3f) * 0.8f, 0f);
                Vector3[] low = Lowered(rake, handrailH - bottomRailY);
                Kernels.ProfileSweep(mb, section, Shifted(low, -railX), railRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);
                Kernels.ProfileSweep(mb, section, Shifted(low, railX), railRole,
                                     closedPath: false, capEnds: true, smoothAlong: false,
                                     upHint: Vector3.right);
            }

            // ---- 6. the repeated member ------------------------------------------------------
            // Evenly divided across the whole rail run rather than laid from one end, so a stoop is
            // symmetrical about its own middle and never ends on a half gap. Each member spans from
            // whatever surface is under it up to the rail — which on a rake is a different length for
            // every one of them, and is the whole reason this is not a PanelGrid.
            int count = Mathf.Max(Mathf.FloorToInt(bottomNose / pitch), 1);
            float spacing = bottomNose / count;
            for (int k = 0; k < count; k++)
            {
                float z = (k + 0.5f) * spacing;
                float bottom = SurfaceY(z, steps, rise, run, landing, nose) + standOff;
                float h = RailY(z, steps, rise, run, landing, nose, lift) - railD * 0.5f - bottom;
                if (h <= 1e-3f) continue;
                var size = new Vector3(memberW, h, memberW);
                var at = new Vector3(0f, bottom + h * 0.5f, z);
                // Top and bottom are buried in the rail and the tread; the four sides are the member.
                var faces = Faces.All & ~Faces.Top & ~Faces.Bottom;
                Kernels.Box(mb, size, at + new Vector3(-railX, 0f, 0f), railRole, faces);
                Kernels.Box(mb, size, at + new Vector3(railX, 0f, 0f), railRole, faces);
            }

            // ---- 7. the newel ----------------------------------------------------------------
            // Without it the handrail ends in mid-air over the bottom nose, which is the one defect
            // a railing can have that reads as broken rather than as coarse.
            float newelW = Mathf.Max(p.GetFloat("newelW", balustrade ? memberW * 2.2f : memberW * 1.4f), 0f);
            if (newelW > 1e-3f)
            {
                float z = bottomNose - newelW * 0.5f;
                float top = RailY(z, steps, rise, run, landing, nose, lift) + railD * 0.5f;
                var size = new Vector3(newelW, top, newelW);
                var at = new Vector3(0f, top * 0.5f, z);
                Kernels.Box(mb, size, at + new Vector3(-railX, 0f, 0f), railRole, Faces.All & ~Faces.Bottom);
                Kernels.Box(mb, size, at + new Vector3(railX, 0f, 0f), railRole, Faces.All & ~Faces.Bottom);
            }

            return mb.Finish(GeneratorId);
        }

        // ---- the flight polyline ---------------------------------------------------------------

        /// <summary>
        /// The stair polyline in part-local space, top at the landing and bottom at the street.
        ///
        /// <para><b>What using <see cref="Paths.Stair"/> for real actually took</b> — #495 exists
        /// partly to find this out. Three things:</para>
        /// <list type="number">
        /// <item><description><b>The sign is backwards for the only family that wants it.</b> The
        /// kernel ascends toward <c>+Z</c>, which in the part-local frame every generator shares is
        /// <i>outward, toward the street</i>; a stoop ascends toward the facade. Its doc comment
        /// offers mirroring "for a descending flight", but the ascending case needs turning round
        /// too. Passing a <b>negative run</b> does it exactly, with no mirroring pass and no lost
        /// winding, because the kernel applies <c>run</c> as a signed displacement and clamps only
        /// <c>steps</c>. That it composes this cleanly is the kernel being right; that the default
        /// direction is the one no caller wants is worth a follow-up.</description></item>
        /// <item><description><b>It has no origin.</b> The flight always starts at <c>(0,0,0)</c>, so
        /// a caller always translates. Cheap, but every future caller pays it.</description></item>
        /// <item><description><b>It has no nosing.</b> A tread overhang is expressed here by pushing
        /// each riser's <i>top</i> outward, which rakes the riser back instead of cantilevering the
        /// tread — the same silhouette, no hairpin in the path, and <c>nose = 0</c> returns the
        /// kernel's own points untouched. A hairpin would have been the literal reading and it is
        /// the one thing <see cref="Kernels.ProfileSweep"/>'s mitre has to clamp.</description></item>
        /// </list>
        /// </summary>
        static Vector3[] Flight(int steps, float rise, float run, float landing, float nose)
        {
            // Negative run: up and INWARD, toward the wall. Then one translation puts the top of the
            // flight at the back of the landing.
            var pts = Paths.Stair(steps, rise, -run);
            float z0 = landing + steps * run;
            for (int i = 0; i < pts.Length; i++)
            {
                // Odd indices are riser tops, which are exactly the tread front edges.
                float overhang = (i & 1) == 1 ? nose : 0f;
                pts[i] = new Vector3(pts[i].x, pts[i].y, pts[i].z + z0 + overhang);
            }
            return pts;
        }

        /// <summary>The tread top under <paramref name="z"/>: the landing and the top tread are one
        /// level, and below them each run of horizontal distance drops one rise.</summary>
        static float SurfaceY(float z, int steps, float rise, float run, float landing, float nose)
        {
            if (z <= landing + run + nose) return steps * rise;
            int i = Mathf.Clamp(Mathf.FloorToInt((landing + steps * run + nose - z) / run), 0, steps - 1);
            return (i + 1) * rise;
        }

        /// <summary>The rail centreline at <paramref name="z"/> — level over the landing, then
        /// parallel to the nosing line. The same three-point rake the sweep follows, sampled.</summary>
        static float RailY(float z, int steps, float rise, float run, float landing, float nose, float lift)
        {
            float top = steps * rise + lift;
            float knee = landing + run + nose;
            return z <= knee ? top : top - (z - knee) * (rise / run);
        }

        // ---- sections and paths -----------------------------------------------------------------

        /// <summary>
        /// The step slab, as <b>three two-point sections</b> — the walking surface and the two
        /// flanks — swept along the same polyline rather than as one four-point box section.
        ///
        /// <para><b>Why, and it is not an optimisation.</b> <see cref="Kernels.ProfileSweep"/>
        /// averages its normals <i>across</i> the section, one per profile point, which is exactly
        /// right for the curved sections it was written for and which its own remarks scope to "the
        /// scale a 10 cm moulding occupies". A four-point box section has no interior curvature to
        /// average, so the averaging invents some: measured, every corner vertex of the slab came out
        /// carrying <c>(0.71, 0.71, 0)</c> instead of a clean <c>+Y</c> tread and <c>+Z</c> riser, and
        /// a 1.4 m tread shaded as a rounded bar. A two-point section has no interior point to
        /// average, so each of these carries its face's true normal and the tread reads flat.</para>
        ///
        /// <para>It costs nothing: the same three quads per path segment, from the same mitred ring
        /// positions (they depend on the profile point and the path, not on which call emitted them,
        /// so the three surfaces meet without a crack). It in fact saves the two fan caps, and a
        /// 2-point section bounds no area to cap anyway — both ends of a flight are buried, one in
        /// the pavement and one behind the landing block.</para>
        /// </summary>
        static Vector2[][] StepSlab(float halfW, float thickness) => new[]
        {
            new[] { new Vector2(halfW, 0f), new Vector2(-halfW, 0f) },              // walking surface
            new[] { new Vector2(halfW, -thickness), new Vector2(halfW, 0f) },       // +X flank
            new[] { new Vector2(-halfW, 0f), new Vector2(-halfW, -thickness) },     // −X flank
        };

        /// <summary>
        /// A <see cref="Profiles"/> cross-section put onto the sweep axes a <i>raked</i> run has.
        ///
        /// <para>The table is authored as <c>(across = outward from the wall, up = the member's own
        /// size)</c>. Everything in this family is swept with <c>upHint = +X</c> — the only hint that
        /// stays well-conditioned for a path whose tangent alternates between vertical and horizontal
        /// — which makes the sweep frame's <c>across</c> the along-facade axis and its <c>up</c> the
        /// surface's own outward normal (world <c>+Z</c> on a riser, <c>+Y</c> on a tread). So the
        /// two components swap, exactly as <c>CorniceGenerator.Banded</c> swaps them for a horizontal
        /// band. Swapping alone is a reflection, which would invert every normal the winding rule
        /// derives; reversing the point order restores it, and the swept surface is unchanged because
        /// the point <i>set</i> is.</para>
        ///
        /// <para><paramref name="lift"/> slides the finished section along its outward axis:
        /// <c>-thickness</c> hangs it below the path (a step slab whose top surface IS the path),
        /// <c>0</c> stands it on the path (a coping), <c>-thickness/2</c> centres it (a rail).</para>
        /// </summary>
        static Vector2[] Raked(Vector2[] profile, float thickness, float width, float lift)
        {
            if (profile == null) return null;
            int m = profile.Length;
            var s = new Vector2[m];
            for (int i = 0; i < m; i++)
            {
                Vector2 q = profile[m - 1 - i];
                s[i] = new Vector2(q.y * width, q.x * thickness + lift);
            }
            return s;
        }

        static Vector3[] Shifted(Vector3[] pts, float dx)
        {
            var s = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++) s[i] = new Vector3(pts[i].x + dx, pts[i].y, pts[i].z);
            return s;
        }

        static Vector3[] Lowered(Vector3[] pts, float dy)
        {
            var s = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++) s[i] = new Vector3(pts[i].x, pts[i].y - dy, pts[i].z);
            return s;
        }

        static Vector3[] Append(Vector3[] pts, Vector3 last)
        {
            var s = new Vector3[pts.Length + 1];
            System.Array.Copy(pts, s, pts.Length);
            s[pts.Length] = last;
            return s;
        }
    }
}
