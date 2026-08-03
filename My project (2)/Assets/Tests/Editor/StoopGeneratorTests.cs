using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Tests
{
    /// <summary>
    /// The stoop family (#495) — and the first execution of <see cref="Paths.Stair"/>, which shipped
    /// in #453 and had never been run by anything.
    ///
    /// <para>What these assert, in order: the generator is discovered; the part-local frame and the
    /// <b>bounds a wiring pass needs to align a stoop to its door</b>; that the flight climbs toward
    /// the facade rather than away from it (the one thing <c>Paths.Stair</c>'s own sign convention
    /// gets backwards for this family); that the nosing degenerates cleanly to the kernel's own
    /// points; cheek walls and coping; the railing, expressed as repeated boxes and sweeps rather
    /// than as the deferred <c>Lattice</c> kernel; the <see cref="DetailLevel"/> ladder; and the
    /// three presets.</para>
    /// </summary>
    public class StoopGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly StoopGenerator _gen = new StoopGenerator();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
            PartGenerators.Reset();
        }

        // ---- helpers ------------------------------------------------------------------------

        static PartParams Bag(params (string name, object value)[] entries)
            => new PartParams
            {
                values = entries.Select(e => e.value is string s
                    ? new PartParam { name = e.name, text = s }
                    : new PartParam { name = e.name, value = System.Convert.ToSingle(e.value) }).ToArray()
            };

        static PartParams With(PartParams basis, params (string name, object value)[] overrides)
        {
            var vals = new List<PartParam>(basis.values ?? new PartParam[0]);
            foreach (var o in overrides)
            {
                var np = o.value is string s
                    ? new PartParam { name = o.name, text = s }
                    : new PartParam { name = o.name, value = System.Convert.ToSingle(o.value) };
                int at = vals.FindIndex(v => v.name == o.name);
                if (at >= 0) vals[at] = np; else vals.Add(np);
            }
            return new PartParams { values = vals.ToArray() };
        }

        const int Steps = 4;
        const float Rise = 0.17f, Run = 0.28f, Width = 1.40f, Landing = 0.90f, Slab = 0.12f;

        /// <summary>A bare flight and its landing: no nosing, no cheeks, no railing. The isolate for
        /// every geometric assertion below, so a triangle count is the sweep's and the landing
        /// block's and nothing else's.</summary>
        static PartParams Plain() => Bag(
            ("stepCount", Steps), ("rise", Rise), ("run", Run), ("width", Width),
            ("landingDepth", Landing), ("treadThickness", Slab), ("noseProjection", 0f),
            ("cheekWall", 0f), ("railingStyle", "None"));

        PartMesh Build(PartParams p)
        {
            var mb = new MeshBuilder();
            var pm = _gen.Generate(p, mb);
            _spawned.Add(pm.mesh);
            return pm;
        }

        Bounds BoundsOf(PartParams p) => Build(p).mesh.bounds;

        static int Triangles(PartMesh pm)
        {
            int n = 0;
            for (int s = 0; s < pm.mesh.subMeshCount; s++) n += pm.mesh.GetTriangles(s).Length / 3;
            return n;
        }

        int Tris(PartParams p) => Triangles(Build(p));

        // ---- 1. the seam ----------------------------------------------------------------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUse()
        {
            Assert.IsTrue(PartGenerators.TryResolve("stoop.flight", out var g));
            Assert.IsInstanceOf<StoopGenerator>(g);
        }

        // ---- 2. the part-local frame, and THE number the wiring pass needs ----------------------

        [Test]
        public void TheStoopSitsBelowTheDoorAndInFrontOfTheWall()
        {
            // The contract a wiring pass reads off this family (#495 acceptance): the origin is
            // grade at the wall plane, the mass rises to exactly stepCount·rise — which is the
            // height the door it serves has to be raised by — and it projects forward, never back.
            var b = BoundsOf(Plain());

            Assert.AreEqual(0f, b.min.y, 1e-4f, "the foot of the flight is grade");
            Assert.AreEqual(Steps * Rise, b.max.y, 1e-4f,
                            "the landing IS the door's threshold: raise the door by stepCount*rise");
            Assert.AreEqual(0f, b.min.z, 1e-4f, "nothing pokes back through the wall");
            Assert.AreEqual(Landing + Steps * Run, b.max.z, 1e-4f, "landingDepth + the flight's run");
            Assert.AreEqual(0f, b.center.x, 1e-4f, "centred on the anchor — the door's own nx");
            Assert.AreEqual(Width, b.size.x, 1e-4f);
        }

        [Test]
        public void EveryStepAddsExactlyOneRiseAndOneRun()
        {
            for (int n = 1; n <= 8; n++)
            {
                var b = BoundsOf(With(Plain(), ("stepCount", n)));
                Assert.AreEqual(n * Rise, b.max.y, 1e-4f, $"{n} steps");
                Assert.AreEqual(Landing + n * Run, b.max.z, 1e-4f, $"{n} steps");
            }
        }

        // ---- 3. Paths.Stair, used for the first time --------------------------------------------

        [Test]
        public void TheFlightClimbsTowardTheFacadeNotTowardTheStreet()
        {
            // Paths.Stair ascends toward +Z, which in the part-local frame every generator shares is
            // OUTWARD, toward the street. A stoop climbs the other way. The family turns it round by
            // passing a negative run — so this is the assertion that the sign is right: the highest
            // geometry has to be at the BACK (small z, against the wall) and the lowest at the front.
            var mesh = Build(Plain()).mesh;
            var verts = mesh.vertices;

            float highestZ = float.MaxValue, lowestZ = float.MinValue;
            float top = Steps * Rise, bottom = 0f;
            foreach (var v in verts)
            {
                if (v.y > top - 1e-3f) highestZ = Mathf.Min(highestZ, v.z);
                if (v.y < bottom + 1e-3f) lowestZ = Mathf.Max(lowestZ, v.z);
            }

            Assert.AreEqual(0f, highestZ, 1e-3f, "the top of the flight reaches the wall plane");
            Assert.AreEqual(Landing + Steps * Run, lowestZ, 1e-3f, "and grade is the outermost point");
            Assert.Less(highestZ, lowestZ, "an inverted flight would climb out over the sidewalk");
        }

        [Test]
        public void TheWholeFlightIsOneStairPolylineSweptThreeTimes()
        {
            // 2 segments per step (riser + tread) × 3 quads — the walking surface and the two flanks
            // — × 2 triangles, and then the landing block's four visible faces. A 2-point section
            // bounds no area, so there are no fan caps and both buried ends cost nothing. If any of
            // this were assembled per step out of boxes the number would be different.
            Assert.AreEqual(12 * Steps + 8, Tris(Plain()));
            Assert.AreEqual(12 * 6 + 8, Tris(With(Plain(), ("stepCount", 6))));
        }

        [Test]
        public void TreadsFaceTheSkyAndRisersFaceTheStreetWithFlatNormals()
        {
            // The reason the slab is three 2-point sections rather than one 4-point one (see
            // StoopGenerator.StepSlab). ProfileSweep averages normals across the section, which on a
            // box section blends every corner to (0.71, 0.71, 0) and shades a 1.4 m tread as a
            // rounded bar. Each surface must carry its own face normal exactly.
            var mesh = Build(Plain()).mesh;
            var normals = mesh.normals;

            int up = 0, out_ = 0;
            foreach (var n in normals)
            {
                Assert.AreEqual(1f, n.magnitude, 1e-3f);
                // Every normal is an axis: +Y treads, +Z risers, ±X flanks, and the landing's faces.
                float biggest = Mathf.Max(Mathf.Abs(n.x), Mathf.Max(Mathf.Abs(n.y), Mathf.Abs(n.z)));
                Assert.AreEqual(1f, biggest, 1e-3f, $"blended normal {n}");
                if (n.y > 0.99f) up++;
                if (n.z > 0.99f) out_++;
            }
            Assert.Greater(up, 0, "the treads face the sky");
            Assert.Greater(out_, 0, "the risers face the street");
        }

        [Test]
        public void AZeroNosingIsTheKernelsOwnPolylineUntouched()
        {
            // Same property #453 protects for Arc(rise: 0) == Line: the overhang is one parameter,
            // and at 0 it must not perturb the path at all — not "to within rounding".
            var flush = Build(Plain());
            var stated = Build(With(Plain(), ("noseProjection", 0f)));

            Assert.AreEqual(Triangles(flush), Triangles(stated));
            Assert.AreEqual(flush.mesh.vertexCount, stated.mesh.vertexCount);
            Assert.AreEqual(Landing + Steps * Run, flush.mesh.bounds.max.z, 1e-5f);
        }

        [Test]
        public void ANosingPushesTheTreadEdgesOutByExactlyItself()
        {
            const float Nose = 0.04f;
            var nosed = BoundsOf(With(Plain(), ("noseProjection", Nose)));

            Assert.AreEqual(Landing + Steps * Run + Nose, nosed.max.z, 1e-4f);
            // It rakes the risers rather than cantilevering the treads, so it costs no extra path
            // points and therefore no extra triangles.
            Assert.AreEqual(Tris(Plain()), Tris(With(Plain(), ("noseProjection", Nose))));
        }

        [Test]
        public void TheNosingIsCappedSoARiserCanNeverLeanPastTheStepBelowIt()
        {
            var absurd = BoundsOf(With(Plain(), ("noseProjection", 5f)));
            Assert.AreEqual(Landing + Steps * Run + Run * 0.5f, absurd.max.z, 1e-4f);
        }

        // ---- 4. cheek walls and their coping ------------------------------------------------------

        [Test]
        public void CheekWallsWidenThePartByExactlyTheirThicknessOnEachSide()
        {
            const float Cheek = 0.20f;
            var open = BoundsOf(Plain());
            var walled = BoundsOf(With(Plain(), ("cheekWall", Cheek), ("cheekCapProfile", "None")));

            Assert.AreEqual(Width, open.size.x, 1e-4f, "0 = open sides");
            Assert.AreEqual(Width + 2f * Cheek, walled.size.x, 1e-4f);
            Assert.AreEqual(Steps * Rise, walled.max.y, 1e-4f, "a cheek never rises above its own step");
        }

        [Test]
        public void TheCopingStandsOnTheCheekAndFollowsTheSameSteppedLine()
        {
            const float Cheek = 0.20f, CapH = 0.10f, Overhang = 0.03f;
            var bare = With(Plain(), ("cheekWall", Cheek), ("cheekCapProfile", "None"));
            var capped = With(bare, ("cheekCapProfile", "Bullnose"),
                              ("cheekCapH", CapH), ("cheekCapOverhang", Overhang));

            var b = BoundsOf(capped);
            Assert.AreEqual(Steps * Rise + CapH, b.max.y, 1e-3f, "the coping sits ON the cheek top");
            Assert.AreEqual(Width + 2f * (Cheek + Overhang), b.size.x, 1e-3f,
                            "…and laps the cheek's outer face by the overhang");
            Assert.Greater(Tris(capped), Tris(bare));

            // It is the flight's own polyline moved sideways, so it carries one ring per stair
            // vertex plus one for the landing — not a second, independently authored path.
            Assert.AreEqual(0f, BoundsOf(capped).min.z, 1e-4f, "and it runs back to the wall");
        }

        [Test]
        public void ACopingNeedsACheekToStandOn()
        {
            // cheekWall = 0 is "open sides", and a coping floating in mid-air beside an open flight
            // would be the wrong reading of it.
            Assert.AreEqual(Tris(With(Plain(), ("cheekCapProfile", "None"))),
                            Tris(With(Plain(), ("cheekCapProfile", "Bullnose"), ("cheekCapH", 0.1f))));
        }

        // ---- 5. the railing — repeated members, NOT the deferred Lattice kernel ---------------------

        [Test]
        public void ABalustradeIsRepeatedBoxesAndSweepsWithNoDiagonalInIt()
        {
            // #495 states this deliberately: nothing in this family waits on Lattice. The
            // observable consequence is that halving the baluster pitch buys roughly twice the
            // members and nothing else — a linear repeat, which is exactly what a diagonal infill
            // would NOT give you.
            var basis = With(Plain(), ("railingStyle", "Balustrade"), ("balusterW", 0.05f),
                             ("handrailProfile", "None"), ("bottomRailY", 0f), ("newelW", 0f));

            int bare = Tris(With(basis, ("railingStyle", "None")));
            int coarse = Tris(With(basis, ("balusterPitch", 0.40f))) - bare;
            int fine = Tris(With(basis, ("balusterPitch", 0.20f))) - bare;

            Assert.AreEqual(2f, (float)fine / coarse, 0.35f, "halving the pitch doubles the members");
            Assert.AreEqual(0, coarse % 16, "one baluster is 2 boxes of 4 faces — 8 triangles each");
        }

        [Test]
        public void TheHandrailRidesAboveWhateverItStandsOn()
        {
            const float RailH = 0.90f, RailD = 0.06f;
            var posts = With(Plain(), ("railingStyle", "Posts"), ("handrailProfile", "Bullnose"),
                             ("handrailH", RailH), ("handrailD", RailD));

            Assert.AreEqual(Steps * Rise + RailH + RailD * 0.5f, BoundsOf(posts).max.y, 1e-3f,
                            "on an open-sided stoop the rail stands on the tread");

            const float Cheek = 0.20f, CapH = 0.10f;
            var onCheek = With(posts, ("cheekWall", Cheek), ("cheekCapProfile", "Bullnose"),
                               ("cheekCapH", CapH));
            Assert.AreEqual(Steps * Rise + CapH + RailH + RailD * 0.5f, BoundsOf(onCheek).max.y, 1e-3f,
                            "…and on a cheek wall it stands on the coping");
        }

        [Test]
        public void TheRailingIsTheOnlyThingThatRisesAboveTheThreshold()
        {
            // Everything else in the family is below the door, which is what makes the bounds a
            // usable alignment contract.
            Assert.AreEqual(Steps * Rise, BoundsOf(With(Plain(), ("railingStyle", "None"))).max.y, 1e-4f);
            Assert.Greater(BoundsOf(With(Plain(), ("railingStyle", "Posts"))).max.y, Steps * Rise);
        }

        [Test]
        public void ABalustradeCostsMoreThanPostsAndPostsMoreThanNothing()
        {
            int none = Tris(With(Plain(), ("railingStyle", "None")));
            int posts = Tris(With(Plain(), ("railingStyle", "Posts"), ("postPitch", 0.55f)));
            int balustrade = Tris(With(Plain(), ("railingStyle", "Balustrade"), ("balusterPitch", 0.16f)));

            Assert.Greater(posts, none);
            Assert.Greater(balustrade, posts);
        }

        [Test]
        public void AOneStepStoopStillGetsAWholeRailingRatherThanNone()
        {
            // The rake collapses onto the level run at stepCount 1 — a repeated path point, which
            // ProfileSweep answers by emitting nothing at all. The degenerate case has to drop the
            // point instead of relying on that being harmless.
            var one = With(Plain(), ("stepCount", 1), ("railingStyle", "Posts"),
                           ("handrailProfile", "Bullnose"));
            Assert.Greater(Tris(one), Tris(With(one, ("railingStyle", "None"))));
            Assert.Greater(BoundsOf(one).max.y, Rise, "the rail is above the single step");
        }

        [Test]
        public void ALandinglessStoopStillBuildsItsCoping()
        {
            // The same class of trap on the coping path, which appends a point at the wall: with no
            // landing that point is the flight's own last one.
            var p = With(Plain(), ("landingDepth", 0f), ("cheekWall", 0.2f),
                         ("cheekCapProfile", "Bullnose"), ("cheekCapH", 0.1f));
            Assert.Greater(Tris(p), Tris(With(p, ("cheekCapProfile", "None"))));
        }

        // ---- 6. the DetailLevel ladder ---------------------------------------------------------

        [Test]
        public void TheFlatFloorIsOneRoleColouredQuad()
        {
            var flat = Build(With(Plain(), ("detail", "Flat")));
            Assert.AreEqual(2, Triangles(flat));
            // …standing at the outermost plane, so a distant stoop contributes the right silhouette.
            Assert.AreEqual(Landing + Steps * Run, flat.mesh.bounds.max.z, 1e-4f);
            Assert.AreEqual(Steps * Rise, flat.mesh.bounds.max.y, 1e-4f);
        }

        [Test]
        public void EachDetailLevelIsCheaperThanTheOneAboveIt()
        {
            var p = With(Plain(), ("cheekWall", 0.2f), ("cheekCapProfile", "Bullnose"),
                         ("cheekCapH", 0.1f), ("railingStyle", "Balustrade"),
                         ("balusterPitch", 0.16f), ("handrailProfile", "Ogee"));

            int full = Tris(With(p, ("detail", "Full")));
            int reduced = Tris(With(p, ("detail", "Reduced")));
            int flat = Tris(With(p, ("detail", "Flat")));

            Assert.Greater(full, reduced);
            Assert.Greater(reduced, flat * 10f, "…but Reduced still carries a flight and a balustrade");
            Assert.AreEqual(2, flat);
        }

        [Test]
        public void ReducedKeepsTheFlightIntactAndHalvesTheBalusters()
        {
            // The step count IS the stoop's height, so unlike a muntin field the flight can never be
            // thinned — a cheaper stoop must be the same stair with less railing on it.
            var noRail = With(Plain(), ("railingStyle", "None"));
            Assert.AreEqual(Tris(With(noRail, ("detail", "Full"))),
                            Tris(With(noRail, ("detail", "Reduced"))),
                            "a bare flight has nothing Reduced is allowed to take away");

            var rail = With(Plain(), ("railingStyle", "Balustrade"), ("balusterPitch", 0.16f),
                            ("handrailProfile", "None"), ("bottomRailY", 0f), ("newelW", 0f));
            int full = Tris(With(rail, ("detail", "Full")));
            int reduced = Tris(With(rail, ("detail", "Reduced")));
            int bare = Tris(With(noRail, ("detail", "Full")));
            Assert.AreEqual(2f, (float)(full - bare) / (reduced - bare), 0.35f);
        }

        // ---- 7. determinism ----------------------------------------------------------------------

        [Test]
        public void TheSameParametersAlwaysProduceTheSameMesh()
        {
            var a = Build(Plain());
            var b = Build(Plain());
            Assert.AreEqual(a.mesh.vertexCount, b.mesh.vertexCount);
            var va = a.mesh.vertices;
            var vb = b.mesh.vertices;
            for (int i = 0; i < va.Length; i++)
                Assert.Less((va[i] - vb[i]).magnitude, 1e-6f, $"vertex {i}");
        }

        // ---- 8. the three presets ------------------------------------------------------------------

        static readonly string[] PresetIds =
        {
            "stoop_noe_victorian", "stoop_glenpark_short", "stoop_soma_flush",
        };

        static PartDefJson LoadPreset(string id)
        {
            string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts", id + ".part.json");
            Assert.IsTrue(File.Exists(path), $"missing preset {path}");
            return JsonUtility.FromJson<PartDefJson>(File.ReadAllText(path));
        }

        static PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

        [Test]
        public void EveryPresetNamesThisGeneratorAndParsesIntoAParameterBlock()
        {
            foreach (string id in PresetIds)
            {
                var def = LoadPreset(id);
                Assert.AreEqual(id, def.id);
                Assert.AreEqual("stoop.flight", def.generatorId, id);
                Assert.AreEqual(0f, def.mountDepth_m, 1e-4f,
                                $"{id}: a stoop is anchored ON the wall plane and projects forward");
                Assert.Greater(PartParams.From(def.parameters).Count, 5, id);
            }
        }

        [Test]
        public void ThePresetsResolveToDistinctCacheKeys()
        {
            var keys = PresetIds.Select(id => Preset(id).KeyFor("stoop.flight")).ToList();
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                    Assert.AreNotEqual(keys[i], keys[j], $"{PresetIds[i]} and {PresetIds[j]} share a mesh key");
        }

        [Test]
        public void ThePresetsReadAsThreeDifferentEntries()
        {
            var noe = Preset("stoop_noe_victorian");
            var glen = Preset("stoop_glenpark_short");
            var soma = Preset("stoop_soma_flush");

            // Noe Valley: the tall Victorian flight — solid cheeks with coping, and a balustrade.
            Assert.GreaterOrEqual(noe.GetInt("stepCount"), 5);
            Assert.Greater(noe.GetFloat("cheekWall"), 0f);
            Assert.AreEqual(ProfileId.Bullnose, noe.GetEnum("cheekCapProfile", ProfileId.None));
            Assert.AreEqual(StoopRailing.Balustrade, noe.GetEnum("railingStyle", StoopRailing.None));
            Assert.AreEqual(MaterialRole.Accent1, noe.GetEnum("railRole", MaterialRole.Base),
                            "the railing matches Accent1");

            // Glen Park sits lower to grade: a short open-sided flight with simple posts.
            Assert.AreEqual(3, glen.GetInt("stepCount"));
            Assert.AreEqual(0f, glen.GetFloat("cheekWall"), 1e-5f, "open sides");
            Assert.AreEqual(StoopRailing.Posts, glen.GetEnum("railingStyle", StoopRailing.None));
            Assert.Less(glen.GetInt("stepCount") * glen.GetFloat("rise"),
                        noe.GetInt("stepCount") * noe.GetFloat("rise"), "…lower than Noe");

            // SoMa meets the sidewalk: one step and a threshold slab, nothing else.
            Assert.AreEqual(1, soma.GetInt("stepCount"));
            Assert.AreEqual(StoopRailing.None, soma.GetEnum("railingStyle", StoopRailing.None));
            Assert.AreEqual(0f, soma.GetFloat("cheekWall"), 1e-5f);
            Assert.Greater(soma.GetFloat("width"), noe.GetFloat("width"), "a wide industrial threshold");
        }

        [Test]
        public void EveryPresetGeneratesAtEveryDetailLevelAndStaysBelowItsDoor()
        {
            // The measured table #456 wants, plus the bounds the wiring pass needs. Asserted as an
            // ordering and a frame; the absolute numbers ARE the measurement and are logged.
            foreach (string id in PresetIds)
            {
                var basis = Preset(id);
                int full = Tris(With(basis, ("detail", "Full")));
                int reduced = Tris(With(basis, ("detail", "Reduced")));
                int flat = Tris(With(basis, ("detail", "Flat")));
                var b = BoundsOf(With(basis, ("detail", "Full")));

                Debug.Log($"[#495] {id}: Full={full} Reduced={reduced} Flat={flat} | " +
                          $"landingY={basis.GetInt("stepCount") * basis.GetFloat("rise"):0.###} " +
                          $"bounds x[{b.min.x:0.###},{b.max.x:0.###}] " +
                          $"y[{b.min.y:0.###},{b.max.y:0.###}] z[{b.min.z:0.###},{b.max.z:0.###}]");

                Assert.LessOrEqual(reduced, full, id);
                Assert.LessOrEqual(flat, reduced, id);
                Assert.AreEqual(2, flat, id);

                float landingY = basis.GetInt("stepCount") * basis.GetFloat("rise");
                // Never above grade — a floating stoop is the one placement defect that reads as
                // broken. It may bury a little: a nosing rakes the bottom riser, and a coping run
                // perpendicular to a raked face has its outer edge below that face's foot. Bounded
                // by the coping's own thickness, so it sinks into the sidewalk and nothing else.
                Assert.LessOrEqual(b.min.y, 1e-3f, $"{id} never floats above grade");
                Assert.GreaterOrEqual(b.min.y, -0.05f, $"{id} buries no more than a coping's depth");
                Assert.AreEqual(0f, b.min.z, 1e-3f, $"{id} does not reach back through the wall");
                // The mass is below the door; only a railing is allowed above the threshold.
                bool railed = basis.GetEnum("railingStyle", StoopRailing.None) != StoopRailing.None;
                if (!railed) Assert.AreEqual(landingY, b.max.y, 1e-3f, $"{id} sits entirely below its door");
                else Assert.Greater(b.max.y, landingY, id);
            }
        }

        [Test]
        public void ThePresetsDeclaredSizesMatchTheGeometryTheyProduce()
        {
            // size_m is what a placement reasons about (#487); a preset whose declared box does not
            // contain its own mesh is a placement bug waiting for a wiring pass.
            foreach (string id in PresetIds)
            {
                var def = LoadPreset(id);
                var b = BoundsOf(PartParams.From(def.parameters));
                Assert.AreEqual(def.size_m.w, b.size.x, 0.02f, $"{id} width");
                Assert.AreEqual(def.size_m.h, b.max.y, 0.02f, $"{id} height");
                Assert.AreEqual(def.size_m.d, b.max.z, 0.02f, $"{id} depth");
            }
        }

        [Test]
        public void OnePresetPlacedManyTimesCollapsesToOneMesh()
        {
            var cache = new PartMeshCache();
            var preset = Preset("stoop_noe_victorian");
            for (int i = 0; i < 25; i++)
                cache.GetOrCreate(preset.KeyFor("stoop.flight"), mb => _gen.Generate(preset, mb));

            Assert.AreEqual(1, cache.Generated, "a slot artifact's key is its parameters, nothing else");
            Assert.AreEqual(24, cache.Hits);
            cache.Clear();
        }
    }
}
