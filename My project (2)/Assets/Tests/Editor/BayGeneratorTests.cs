using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;
using SFMap.Pipeline.Editor;

namespace SFMap.Tests
{
    /// <summary>
    /// The bay family (#473, design #452 design.md §2 BayWindow row / §3). Three things are on
    /// trial here and they are not equally interesting:
    ///
    /// <list type="number">
    /// <item><b>The composition claim.</b> A bay is a volume with windows on it. The glazing has to
    /// come out of the window family — literally, triangle for triangle — or design.md D3 has
    /// failed. <see cref="TheGlazingIsTheWindowFamilysOutputTriangleForTriangle"/> is the test that
    /// says so, and it is the one worth breaking the build over.</item>
    /// <item><b>The plan is the only thing <see cref="BayPlan"/> selects</b>, exactly as
    /// <c>HeadType</c> only selects a rise in the window family.</item>
    /// <item><b>#459's clearance rule against real bay geometry</b> (§6 below). Those figures were
    /// written and unit-tested with no bay to test against; these run them against the shipped
    /// presets' actual mesh bounds, at right-angle, acute, obtuse and reflex corners.</item>
    /// </list>
    /// </summary>
    public class BayGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly BayWindowGenerator _gen = new BayWindowGenerator();
        readonly DoubleHungWindowGenerator _window = new DoubleHungWindowGenerator();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
            PartGenerators.Reset();
        }

        // ---- helpers ---------------------------------------------------------------------

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

        PartMesh Build(PartParams p)
        {
            var mb = new MeshBuilder();
            var pm = _gen.Generate(p, mb);
            _spawned.Add(pm.mesh);
            return pm;
        }

        PartMesh BuildWindow(PartParams p)
        {
            var mb = new MeshBuilder();
            var pm = _window.Generate(p, mb);
            _spawned.Add(pm.mesh);
            return pm;
        }

        static int Triangles(PartMesh pm)
        {
            int n = 0;
            for (int s = 0; s < pm.mesh.subMeshCount; s++) n += pm.mesh.GetTriangles(s).Length / 3;
            return n;
        }

        static int Triangles(PartMesh pm, MaterialRole role)
        {
            for (int s = 0; s < pm.submeshRoles.Length; s++)
                if (pm.submeshRoles[s] == role) return pm.mesh.GetTriangles(s).Length / 3;
            return 0;
        }

        /// <summary>A slanted bay with everything on — the reference this file measures against.</summary>
        static PartParams Reference() => Bag(
            ("plan", "Slanted"), ("projection", 0.75f), ("chamferAngle", 45f), ("widthAtWall", 3.6f),
            ("floorsSpanned", 2), ("floorHeight", 3.1f), ("sillHeight", 0.95f), ("revealDepth", 0.10f),
            ("skirtProfile", "Ogee"), ("skirtDepth", 0.40f), ("skirtRun", 0.55f),
            ("capProfile", "Ogee"), ("capDepth", 0.32f), ("capProjection", 0.14f),
            ("shellRole", "Accent1"), ("trimRole", "Accent2"),
            ("faceMargin", 0.08f), ("faceGap", 0.15f), ("faceWindowsPerFacet", 1),
            ("face.w", 0.9f), ("face.h", 2.0f), ("face.sashCount", 2), ("face.meetingRailH", 0.07f),
            ("face.lowerCols", 2), ("face.upperCols", 2),
            ("face.casingProfile", "Ogee"), ("face.casingW", 0.10f),
            ("face.head", "Hooded"), ("face.headProfile", "Ogee"), ("face.headOverhang", 0.09f),
            ("face.sillProfile", "Sill"), ("face.frameRole", "Accent1"));

        // ---- 1. reachable exactly as a part file names it ---------------------------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUse()
        {
            Assert.IsTrue(PartGenerators.TryResolve("bay.projecting", out var g));
            Assert.IsInstanceOf<BayWindowGenerator>(g);
        }

        // ---- 2. BayPlan selects the plan polyline and nothing else ------------------------

        [Test]
        public void ASquaredBayIsASlantedBayWhoseFaceIsAsWideAsItsWall()
        {
            // The same property WindowGeneratorTests protects for HeadType: if someone adds
            // `if (plan == Squared) …` anywhere below PlanPoints, these stop being vertex-for-vertex
            // identical and this fails.
            var squared = Build(With(Reference(), ("plan", "Squared")));
            var slanted = Build(With(Reference(), ("plan", "Slanted"), ("widthAtFace", 3.6f)));

            CollectionAssert.AreEqual(squared.mesh.vertices, slanted.mesh.vertices);
            CollectionAssert.AreEqual(squared.submeshRoles, slanted.submeshRoles);
            Assert.AreEqual(Triangles(squared), Triangles(slanted));
        }

        [Test]
        public void TheChamferAngleIsWhatMakesTheReturnsSlant()
        {
            // 89° returns are (almost) a squared bay; 30° pulls the face right in. One number, the
            // whole Noe-vs-North-Beach silhouette.
            float steep = BayFaceWidth(89f), shallow = BayFaceWidth(30f);
            Assert.Greater(steep, shallow);
            Assert.Less(shallow, 3.6f - 2f * 0.75f, "a 30° return eats more than a 45° one");
        }

        static float BayFaceWidth(float angle)
        {
            // The plan is public precisely so the silhouette can be asserted without a mesh.
            float inset = 0.75f / Mathf.Tan(angle * Mathf.Deg2Rad);
            var pts = BayWindowGenerator.PlanPoints(BayPlan.Slanted, 3.6f,
                                                    Mathf.Clamp(3.6f - 2f * inset, 0.2f, 3.6f), 0.75f, 3);
            return pts[2].x - pts[1].x;
        }

        [Test]
        public void ACurvedPlanWithNoProjectionIsTheWallLine()
        {
            // Paths.Arc(rise: 0) degenerates to Paths.Line (#453 acceptance 2). A bay's bow is that
            // arc lying down, so the property carries: no projection, no bay.
            var flat = BayWindowGenerator.PlanPoints(BayPlan.Curved, 3.0f, 3.0f, 0f, 6);
            foreach (var p in flat) Assert.AreEqual(0f, p.z, 1e-6f);
            Assert.AreEqual(-1.5f, flat[0].x, 1e-5f);
            Assert.AreEqual(1.5f, flat[flat.Length - 1].x, 1e-5f);

            var bowed = BayWindowGenerator.PlanPoints(BayPlan.Curved, 3.0f, 3.0f, 0.6f, 6);
            Assert.AreEqual(7, bowed.Length);
            Assert.AreEqual(0.6f, bowed[3].z, 1e-4f, "the apex carries the whole projection");
        }

        // ---- 3. the frame the assembler places it in --------------------------------------

        [Test]
        public void GeometryIsAuthoredInThePartLocalFrameThePlacerExpects()
        {
            var pm = Build(Reference());
            var b = pm.mesh.bounds;

            Assert.AreEqual(0f, b.center.x, 1e-3f, "centred on the anchor");
            Assert.Greater(b.max.z, 0.75f, "it projects at least its own plan depth");
            Assert.Less(b.min.y, 0f, "the skirt hangs below the bay's base");
            Assert.Greater(b.max.y, 2f * 3.1f, "the cap sits above the top floor line");

            // Nothing of the bay's own VOLUME sits behind the wall plane — that is what makes its
            // mountDepth_m 0 and what makes bounds.max.z alone the projection #459 reasons about.
            // The skirt tucks back under the returns and therefore does pass into the wall, exactly
            // as a window's reveal does; it is bounded by skirtRun and it is buried, not visible.
            var volume = Build(With(Reference(), ("detail", "Flat")));
            Assert.AreEqual(0f, volume.mesh.bounds.min.z, 1e-4f, "the prism starts at the wall plane");
            Assert.GreaterOrEqual(b.min.z, -0.55f, "and only the skirt's own run goes behind it");
        }

        [Test]
        public void TheVolumeIsClosedTopAndBottom()
        {
            // Without the fans the bay is a hollow shell you can see up into from the pavement.
            // The shell walls are 2 tris per facet, so a 3-facet bay with both fans is 6 + 2 + 2.
            var bare = Build(With(Reference(), ("detail", "Flat")));
            Assert.AreEqual(10, Triangles(bare),
                            "3 facets × 2 + a 2-triangle fan at each end of the prism");
            CollectionAssert.AreEqual(new[] { MaterialRole.Accent1 }, bare.submeshRoles);
        }

        // ---- 4. THE composition claim: the glazing is the window family's -------------------

        [Test]
        public void TheGlazingIsTheWindowFamilysOutputTriangleForTriangle()
        {
            // North Beach is the clean measurement: its shell is Base and its trim Accent2, so
            // every Accent1 and every Glass triangle in the bay can only have come from the window
            // generator. Two windows across the front, two floors, nothing on the returns.
            var bay = Build(PresetParams("bay_northbeach_squared"));
            var one = BuildWindow(FaceBagOf("bay_northbeach_squared", 1.0f));

            Assert.AreEqual(4 * Triangles(one, MaterialRole.Glass), Triangles(bay, MaterialRole.Glass),
                            "the bay's glass IS four of that window's glass");
            Assert.AreEqual(4 * Triangles(one, MaterialRole.Accent1), Triangles(bay, MaterialRole.Accent1),
                            "…and so is its sash, muntins and casing");
            Assert.Greater(Triangles(one, MaterialRole.Glass), 0);
        }

        [Test]
        public void AllThreeFacesOfASlantedBayAreGlazed()
        {
            // The Victorian signature: windows on the face AND on both chamfered returns.
            var bay = Build(PresetParams("bay_noe_slanted"));
            var face = BuildWindow(FaceBagOf("bay_noe_slanted", 0.9f));

            // Glass triangle count is a function of the sash division, not of the opening width, so
            // the narrowed return windows contribute exactly as much as the full-width face one.
            Assert.AreEqual(6 * Triangles(face, MaterialRole.Glass), Triangles(bay, MaterialRole.Glass),
                            "3 facets × 2 floors of the window family's glazing");
        }

        [Test]
        public void TheFaceWindowFamilyIsResolvedByIdNotHardCoded()
        {
            // A bay composes with whatever window family a preset names — the seam, not a
            // compile-time reference. The stub below is discovered by PartGenerators exactly as a
            // real family is, so resolution (not the local instance) is what the bay gets.
            PartGenerators.Reset();
            Assert.IsTrue(PartGenerators.TryResolve(StubFace.StubId, out var resolved));
            var stub = (StubFace)resolved;

            var bay = Build(With(Reference(), ("faceWindowGenerator", StubFace.StubId)));

            Assert.Greater(stub.Calls, 0, "the bay asked the named generator for its faces");
            // Two distinct facet widths on a slanted bay — the wide face keeps the authored
            // opening, the two narrow returns share one shrunk generation between them.
            Assert.AreEqual(2, stub.Calls, "one generation per distinct opening width, not per facet");
            CollectionAssert.AreEqual(new[] { 0.9f, 0.5f }, stub.Widths.ToArray());
            Assert.AreEqual(3 * 2, Triangles(bay, MaterialRole.Sign),
                            "the stub's marker triangle, once per facet per floor");
        }

        [Test]
        public void AnUnknownFaceGeneratorLeavesABayWithNoGlazingRatherThanNoBay()
        {
            var bay = Build(With(Reference(), ("faceWindowGenerator", "window.does_not_exist")));
            Assert.IsTrue(bay.IsValid);
            Assert.AreEqual(0, Triangles(bay, MaterialRole.Glass));
            Assert.Greater(Triangles(bay), 10, "the volume and its trim are still there");
        }

        [Test]
        public void ABayCannotBeItsOwnFaceWindow()
        {
            var bay = Build(With(Reference(), ("faceWindowGenerator", BayWindowGenerator.GeneratorId)));
            Assert.IsTrue(bay.IsValid);
            Assert.AreEqual(0, Triangles(bay, MaterialRole.Glass));
        }

        [Test]
        public void AVeryNarrowFacetGoesWithoutRatherThanEmittingASlit()
        {
            // The returns of a shallow squared bay are 42 cm cheeks — a 1 m window with its trim
            // cannot be shrunk into that and still be a window, so the facet stays blank. That is
            // the North Beach silhouette: a flat-fronted box with plain cheeks.
            var nb = PresetParams("bay_northbeach_squared");
            var withReturns = Build(With(nb, ("projection", 1.4f)));    // long enough returns to glaze
            var shallow = Build(nb);

            Assert.Greater(Triangles(withReturns, MaterialRole.Glass),
                           Triangles(shallow, MaterialRole.Glass),
                           "deepen the bay and its returns become glazable facets");
        }

        // ---- 5. DetailLevel ----------------------------------------------------------------

        [Test]
        public void FlatIsTheBareSilhouetteNotASingleQuad()
        {
            // A documented deviation from generators.md §5.2, which makes Flat one quad. A bay's
            // entire contribution to a street is its silhouette; flattening it to a quad deletes
            // the artifact instead of cheapening it. It is still an order of magnitude under
            // Reduced, which is what a floor is for.
            var flat = Build(With(Reference(), ("detail", "Flat")));
            var reduced = Build(With(Reference(), ("detail", "Reduced")));

            Assert.AreEqual(10, Triangles(flat));
            Assert.AreEqual(0, Triangles(flat, MaterialRole.Glass), "no glazing at the floor");
            Assert.AreEqual(0, Triangles(flat, MaterialRole.Accent2), "no trim at the floor");
            Assert.Greater(Triangles(reduced), 10f * 10f, "…and an order of magnitude under Reduced");
        }

        [Test]
        public void FullAndReducedCarryTheSameElementSetAtDifferentCost()
        {
            var full = Build(With(Reference(), ("detail", "Full")));
            var reduced = Build(With(Reference(), ("detail", "Reduced")));

            CollectionAssert.AreEqual(full.submeshRoles.OrderBy(r => (int)r).ToArray(),
                                      reduced.submeshRoles.OrderBy(r => (int)r).ToArray());
            Assert.Greater(Triangles(full), Triangles(reduced) * 1.5f,
                           "Reduced has to be a real saving, not a rounding difference");
        }

        [Test]
        public void ReducedDegradesTheFaceWindowsWithTheBayNotIndependentlyOfIt()
        {
            // detail rides in the bag, and the bay hands its own level down to the face family —
            // otherwise the heaviest part of the heaviest family never gets cheaper.
            var reducedBay = Build(With(Reference(), ("detail", "Reduced")));
            var fullWindow = BuildWindow(With(FaceBagOf("bay_noe_slanted", 0.9f), ("detail", "Full")));
            var reducedWindow = BuildWindow(With(FaceBagOf("bay_noe_slanted", 0.9f), ("detail", "Reduced")));

            Assert.Greater(Triangles(fullWindow), Triangles(reducedWindow));
            Assert.Less(Triangles(reducedBay), 6 * Triangles(fullWindow),
                        "the windows inside a Reduced bay are Reduced windows");
        }

        [Test]
        public void TheBudgetKnobNeverChangesWhichFacetsAreGlazed()
        {
            // Halving a curved bay's bow at Reduced is the obvious saving and it is a trap: fewer
            // facets means WIDER facets, so a bow too finely divided to glaze at Full sprouts
            // windows at Reduced and measures heavier than the thing it cheapens. Measured, before
            // the plan was made detail-independent: Full 242 tris, Reduced 706.
            // The stub emits one triangle per window regardless of budget, so this counts windows.
            PartGenerators.Reset();
            Assert.IsTrue(PartGenerators.TryResolve(StubFace.StubId, out _));

            // Four facets glaze; halving to two would still glaze, but only half as often — so a
            // reinstated `n / 2` fails here rather than passing vacuously.
            var curved = With(Reference(), ("plan", "Curved"), ("curveSegments", 4),
                                           ("faceWindowGenerator", StubFace.StubId));
            int full = Triangles(Build(With(curved, ("detail", "Full"))), MaterialRole.Sign);
            int reduced = Triangles(Build(With(curved, ("detail", "Reduced"))), MaterialRole.Sign);

            Assert.AreEqual(full, reduced, "the same bay, more cheaply — not a different bay");
            Assert.Greater(full, 0, "…and the case is not vacuous");
        }

        // ---- 6. the presets ----------------------------------------------------------------

        static readonly string[] PresetIds = { "bay_noe_slanted", "bay_northbeach_squared" };

        static PartDefJson LoadPreset(string id)
        {
            string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts", id + ".part.json");
            Assert.IsTrue(File.Exists(path), $"missing preset {path}");
            return JsonUtility.FromJson<PartDefJson>(File.ReadAllText(path));
        }

        static PartParams PresetParams(string id) => PartParams.From(LoadPreset(id).parameters);

        /// <summary>The <c>face.</c> block of a preset as a window parameter bag, at a given opening
        /// width — the same block the bay hands the window family.</summary>
        static PartParams FaceBagOf(string id, float w)
        {
            var vals = PresetParams(id).values
                .Where(v => v.name != null && v.name.StartsWith(BayWindowGenerator.FacePrefix))
                .Select(v => new PartParam
                {
                    name = v.name.Substring(BayWindowGenerator.FacePrefix.Length),
                    value = v.value,
                    text = v.text
                })
                .Where(v => v.name != "w")
                .ToList();
            vals.Add(new PartParam { name = "w", value = w });
            return new PartParams { values = vals.ToArray() };
        }

        [Test]
        public void EveryPresetNamesThisGeneratorAndParsesIntoAParameterBlock()
        {
            foreach (string id in PresetIds)
            {
                var def = LoadPreset(id);
                Assert.AreEqual(id, def.id);
                Assert.AreEqual("bay.projecting", def.generatorId, id);
                Assert.AreEqual("BayWindow", def.category, id);
                Assert.AreEqual(0f, def.mountDepth_m, 1e-6f,
                                $"{id}: a bay starts at the wall plane, so it is never lifted off it");
                Assert.Greater(PartParams.From(def.parameters).Count, 20, id);
            }
        }

        [Test]
        public void ThePresetsResolveToDistinctCacheKeys()
        {
            var keys = PresetIds.Select(id => PresetParams(id).KeyFor("bay.projecting")).ToArray();
            Assert.AreNotEqual(keys[0], keys[1], "the two bays share a mesh key");
        }

        [Test]
        public void TheNoeBayCarriesWindowNoe2over2Verbatim()
        {
            // The seam hands a generator a flat bag and no way to resolve a part id, so the face
            // window's numbers are carried inline under `face.` and faceWindowPreset records where
            // they came from. That claim is worthless unless it is checked — so it is checked.
            var bay = PresetParams("bay_noe_slanted");
            var window = PresetParams("window_noe_2over2");

            Assert.AreEqual("window_noe_2over2", TextOf(bay, "faceWindowPreset"));
            var copied = bay.values
                .Where(v => v.name.StartsWith(BayWindowGenerator.FacePrefix))
                .ToDictionary(v => v.name.Substring(BayWindowGenerator.FacePrefix.Length));

            Assert.AreEqual(window.Count, copied.Count, "every parameter of the source preset, and no more");
            foreach (var w in window.values)
            {
                Assert.IsTrue(copied.ContainsKey(w.name), $"face.{w.name} is missing from the bay");
                var c = copied[w.name];
                Assert.AreEqual(w.text ?? "", c.text ?? "", $"face.{w.name}");
                Assert.AreEqual(w.value, c.value, 1e-6f, $"face.{w.name}");
            }
        }

        static string TextOf(PartParams p, string name)
            => p.values.Where(v => v.name == name).Select(v => v.text).FirstOrDefault();

        [Test]
        public void TheTwoPresetsReadAsTwoDifferentNeighborhoods()
        {
            var noe = PresetParams("bay_noe_slanted");
            var nb = PresetParams("bay_northbeach_squared");

            Assert.AreEqual(BayPlan.Slanted, noe.GetEnum("plan", BayPlan.Squared));
            Assert.AreEqual(BayPlan.Squared, nb.GetEnum("plan", BayPlan.Slanted));
            Assert.Greater(noe.GetFloat("projection"), nb.GetFloat("projection") * 1.5f,
                           "North Beach's is the SHALLOW one — design.md §3 names the contrast");
            Assert.AreEqual(MaterialRole.Accent1, noe.GetEnum("shellRole", MaterialRole.Base),
                            "a painted Victorian bay");

            // And they read differently in the mesh, not only in the numbers.
            var noeMesh = Build(noe);
            var nbMesh = Build(nb);
            Assert.Greater(noeMesh.mesh.bounds.max.z, nbMesh.mesh.bounds.max.z * 1.4f);
            Assert.Greater(Triangles(noeMesh), Triangles(nbMesh));
        }

        [Test]
        public void EveryPresetGeneratesAtEveryDetailLevel()
        {
            // The per-preset triangle table #456 wants. Asserted only as an ordering — the absolute
            // numbers ARE the measurement, and they are logged.
            foreach (string id in PresetIds)
            {
                var basis = PresetParams(id);
                var fullMesh = Build(With(basis, ("detail", "Full")));
                int full = Triangles(fullMesh);
                int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
                int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

                // The glazing share is the number #456 will actually act on: it is where a bay's
                // cost lives, and it is the window family's, not this family's.
                Debug.Log($"[#473] {id}: Full={full} Reduced={reduced} Flat={flat} " +
                          $"(Full by role — Base {Triangles(fullMesh, MaterialRole.Base)}, " +
                          $"Accent1 {Triangles(fullMesh, MaterialRole.Accent1)}, " +
                          $"Accent2 {Triangles(fullMesh, MaterialRole.Accent2)}, " +
                          $"Glass {Triangles(fullMesh, MaterialRole.Glass)})");

                Assert.Greater(full, reduced, id);
                Assert.Greater(reduced, flat, id);
                Assert.Greater(full, 300, $"{id} is the heaviest family — say so out loud");
            }
        }

        [Test]
        public void OnePresetPlacedManyTimesCollapsesToOneMesh()
        {
            var cache = new PartMeshCache();
            var rng = new System.Random(473);
            var blocks = PresetIds.Select(PresetParams).ToArray();

            for (int i = 0; i < 60; i++)
            {
                var basis = blocks[i % blocks.Length];
                float j = (float)(rng.NextDouble() - 0.5) * 0.002f;
                var jittered = With(basis, ("widthAtWall", basis.GetFloat("widthAtWall") + j),
                                           ("projection", basis.GetFloat("projection") + j));
                cache.GetOrCreate(jittered.KeyFor(_gen.Id), mb => _gen.Generate(jittered, mb));
            }

            Assert.AreEqual(PresetIds.Length, cache.Generated);
            Assert.AreEqual(60 - PresetIds.Length, cache.Hits);
            cache.Clear();
        }

        // ---- 7. #459's clearance rule, against real bay geometry ---------------------------
        //
        // #459 shipped its figures with no bay to test against and listed "the clearance figures
        // against real bay geometry" and "acute corners" as open. These close that.

        const float FacesSouth = 180f;

        static StreetFacadeJson Edge(int index, float bearing, float x0, float z0, float x1, float z1)
            => new StreetFacadeJson { edge_index = index, bearing_deg = bearing, score = 1f,
                                      edge = new[] { x0, z0, x1, z1 } };

        static BuildingFactsJson Facts(params StreetFacadeJson[] f)
            => new BuildingFactsJson { osm_id = 473, floor_count = 3, base_y = 0f,
                                       facade_height_m = 9f, street_facades = f };

        /// <summary>
        /// A corner building whose two street walls meet at <paramref name="interiorDeg"/>. The
        /// south wall runs (0,0) → (10,0) facing −Z; the second leaves the shared vertex turning by
        /// the exterior angle, so 90° reproduces <c>CornerPlacementTests.CornerBuilding</c> exactly
        /// and anything above 90° is an acute prow.
        /// </summary>
        static BuildingFactsJson CornerAt(float interiorDeg, float lenB = 8f)
        {
            float phi = (180f - interiorDeg) * Mathf.Deg2Rad;         // exterior turn
            var d = new Vector3(Mathf.Cos(phi), 0f, Mathf.Sin(phi));
            var outward = new Vector3(d.z, 0f, -d.x);                 // interior on the left
            float bearing = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;
            Vector3 far = new Vector3(10f, 0f, 0f) + d * lenB;
            return Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f),
                         Edge(1, bearing, 10f, 0f, far.x, far.z));
        }

        /// <summary>The part's plan footprint in world metres — the exact box #459 reasons about:
        /// the mesh's own along-facade extent, extruded from the wall plane out to
        /// <c>bounds.max.z + mountDepth</c>.</summary>
        static Vector3[] Footprint(StreetFacadeJson f, float nx, Bounds b, float mountDepth)
        {
            Vector3 a = FacadeCornerTable.Endpoint(f, false);
            Vector3 c = FacadeCornerTable.Endpoint(f, true);
            float len = (c - a).magnitude;
            Vector3 along = (c - a) / len;
            Vector3 outward = FacadeFrame.OutwardNormal(f.bearing_deg);
            Vector3 pos = a + along * (Mathf.Clamp01(nx) * len) + outward * mountDepth;
            float z1 = b.max.z + mountDepth;
            return new[]
            {
                pos + along * b.min.x,
                pos + along * b.max.x,
                pos + along * b.max.x + outward * z1,
                pos + along * b.min.x + outward * z1,
            };
        }

        /// <summary>Separating-axis overlap of two convex plan quads, with a 1 mm slack so two
        /// footprints that merely touch at a shared vertex do not read as interpenetrating.</summary>
        static bool Overlaps(Vector3[] p, Vector3[] q)
        {
            return !(HasGap(p, q) || HasGap(q, p));

            bool HasGap(Vector3[] a, Vector3[] b)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    Vector3 e = a[(i + 1) % a.Length] - a[i];
                    var axis = new Vector3(-e.z, 0f, e.x).normalized;
                    float aMin = float.MaxValue, aMax = float.MinValue,
                          bMin = float.MaxValue, bMax = float.MinValue;
                    foreach (var v in a)
                    {
                        float d = Vector3.Dot(v, axis);
                        aMin = Mathf.Min(aMin, d); aMax = Mathf.Max(aMax, d);
                    }
                    foreach (var v in b)
                    {
                        float d = Vector3.Dot(v, axis);
                        bMin = Mathf.Min(bMin, d); bMax = Mathf.Max(bMax, d);
                    }
                    if (aMax < bMin + 1e-3f || bMax < aMin + 1e-3f) return true;
                }
                return false;
            }
        }

        Bounds BayBounds(string presetId)
        {
            var pm = Build(PresetParams(presetId));
            return pm.mesh.bounds;
        }

        /// <summary>Sweep both facades; report whether any pair the rule <i>accepts</i> actually
        /// interpenetrates, and whether any pair it rejects would have.</summary>
        static (bool acceptedOverlap, bool rejectedOverlap, float worstAccepted)
            SweepCorner(BuildingFactsJson facts, Bounds b)
        {
            var table = FacadeCornerTable.Build(facts);
            var fa = facts.street_facades[0];
            var fb = facts.street_facades[1];
            float proj = b.max.z;
            bool acceptedOverlap = false, rejectedOverlap = false, any = false;
            float worst = 0f;

            for (int i = 0; i <= 100; i++)
            {
                float na = i / 100f;
                bool blockedA = table.Blocked(fa, na, b.min.x, b.max.x, proj);
                var qa = Footprint(fa, na, b, 0f);
                for (int j = 0; j <= 100; j++)
                {
                    float nb = j / 100f;
                    bool blockedB = table.Blocked(fb, nb, b.min.x, b.max.x, proj);
                    if (!Overlaps(qa, Footprint(fb, nb, b, 0f))) continue;

                    any = true;
                    if (blockedA || blockedB) rejectedOverlap = true;
                    else { acceptedOverlap = true; worst = Mathf.Max(worst, na); }
                }
            }
            return (acceptedOverlap, any && rejectedOverlap, worst);
        }

        [Test]
        public void TwoRealBaysCannotInterpenetrateAtARightAngleCorner()
        {
            var b = BayBounds("bay_noe_slanted");
            Debug.Log($"[#473/#459] bay_noe_slanted bounds: x [{b.min.x:F3}, {b.max.x:F3}] " +
                      $"max.z {b.max.z:F3} (the figures the corner rule is handed)");

            var (accepted, rejected, _) = SweepCorner(CornerAt(90f), b);
            Assert.IsFalse(accepted, "the rule let through a pair of bays that interpenetrate");
            Assert.IsTrue(rejected, "…and it is not vacuous: without it, pairs near the corner DO overlap");
        }

        [Test]
        public void AcuteAndObtuseConvexCornersHoldUpToo()
        {
            // #459 stated the figures assume a right angle and flagged acute corners as unverified.
            // They are exact for every convex angle, and the reason is structural rather than
            // lucky: a compliant part's footprint lies in the 90° cone spanned by (back along its
            // own facade, its own outward normal), and at a convex vertex the two cones are
            // separated by exactly the exterior turn. Market Street's diagonals are the real case.
            var b = BayBounds("bay_noe_slanted");
            foreach (float interior in new[] { 45f, 60f, 75f, 105f, 120f, 135f, 160f })
            {
                var (accepted, _, worst) = SweepCorner(CornerAt(interior), b);
                Assert.IsFalse(accepted,
                    $"interior {interior}°: an accepted pair interpenetrates (first at nx {worst:F2})");
            }
        }

        [Test]
        public void AReflexCornerIsWhereTheRightAngleAssumptionActuallyBites()
        {
            // The reflex clearance is the neighbour's projection measured ALONG this facade, which
            // is exact at 270° and short by 1/sin(reflex) elsewhere. This test states where it
            // holds and where it does not, rather than asserting a number that was never measured.
            var b = BayBounds("bay_noe_slanted");
            var failures = new List<string>();

            foreach (float interior in new[] { 200f, 225f, 250f, 270f, 290f, 315f })
            {
                var (accepted, _, worst) = SweepCorner(CornerAt(interior), b);
                if (accepted) failures.Add($"{interior}° (first accepted overlap at nx {worst:F2})");
            }
            Debug.Log($"[#473/#459] reflex corners where the clearance is insufficient for a " +
                      $"{b.max.z:F2} m projecting bay: " +
                      (failures.Count == 0 ? "none" : string.Join(", ", failures)));

            var (rightAngle, _, _) = SweepCorner(CornerAt(270f), b);
            Assert.IsFalse(rightAngle, "the 270° L-notch #459 was written against still holds");
        }

        [Test]
        public void TheRuleIsHandedTheProjectionABayActuallyHas()
        {
            // The assembler passes bounds.max.z + mountDepth. For a bay that is the plan projection
            // PLUS the face windows' own trim standing proud of the facets — a figure only the
            // generated mesh knows, which is exactly why #459 takes it from the mesh.
            var pm = Build(PresetParams("bay_noe_slanted"));
            var p = PresetParams("bay_noe_slanted");
            Assert.Greater(pm.mesh.bounds.max.z, p.GetFloat("projection"),
                           "the trim on the face windows projects past the bay's own plan");
            Assert.Greater(pm.mesh.bounds.max.x, p.GetFloat("widthAtWall") * 0.5f,
                           "and the cap flares past the plan width at the wall");
        }

        // ---- a stub face family, discovered exactly as a real one is ------------------------

        sealed class StubFace : IPartGenerator
        {
            public const string StubId = "test.face_marker";

            /// <summary>Trim standing proud of the opening on each side — what makes the bay's
            /// measure-then-shrink path fire, exactly as a real casing does.</summary>
            public const float TrimPerSide = 0.2f;

            public string Id => StubId;
            public int Calls;
            public readonly List<float> Widths = new List<float>();

            public PartMesh Generate(PartParams p, MeshBuilder mb)
            {
                float w = Mathf.Max(p.GetFloat("w", 1f), 0.05f);
                Calls++;
                Widths.Add(w);

                // One Sign-role triangle, so the bay's own geometry can never be mistaken for it,
                // set back behind the wall plane so the mount derivation has something to find.
                mb.BeginRole(MaterialRole.Sign);
                float half = w * 0.5f + TrimPerSide;
                int a = mb.Vert(new Vector3(-half, 0f, -0.05f), Vector3.forward);
                int c = mb.Vert(new Vector3(half, 0f, -0.05f), Vector3.forward);
                int d = mb.Vert(new Vector3(0f, 1f, -0.05f), Vector3.forward);
                mb.TriFacing(a, c, d, Vector3.forward);
                return mb.Finish(StubId);
            }
        }
    }
}
