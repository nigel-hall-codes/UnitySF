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
    /// The cornice family (#474) — and with it the first exercise of the #459 wrapped-run path,
    /// which until now returned false for every part in the library because no
    /// <see cref="IPathPartGenerator"/> existed.
    ///
    /// <para>What these assert, in order: the generator is discovered as path-aware (so
    /// <c>TryPlaceWrapped</c> takes it); a corner building gets ONE continuous mitred run rather
    /// than one run per facade; the profile-axis convention a turning band actually needs; the
    /// roofline anchor; the <see cref="DetailLevel"/> ladder; the three presets; and the cache
    /// behaviour of a wrapped run, which is genuinely different from every slot artifact so far.</para>
    /// </summary>
    public class CorniceGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly CorniceGenerator _gen = new CorniceGenerator();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
            PartGenerators.Reset();
        }

        // ---- helpers ----------------------------------------------------------------------

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

        const float Projection = 0.30f, HeightM = 0.35f;

        /// <summary>A plain cove band with nothing repeated along it and no end returns — the
        /// isolate for every geometric assertion below, so a triangle count is the sweep's and
        /// nothing else's.</summary>
        static PartParams Plain() => Bag(
            ("profileFamily", "Cove"), ("projection", Projection), ("heightM", HeightM),
            ("dentilPitch", 0f), ("bracketSpacing", 0f), ("returnEnds", 0f));

        PartMesh Build(PartParams p, FacadeRun run)
        {
            var mb = new MeshBuilder();
            var pm = _gen.GenerateAlong(p, run, mb);
            _spawned.Add(pm.mesh);
            return pm;
        }

        static int Triangles(PartMesh pm)
        {
            int n = 0;
            for (int s = 0; s < pm.mesh.subMeshCount; s++) n += pm.mesh.GetTriangles(s).Length / 3;
            return n;
        }

        static int CoveProfilePoints => Profiles.Cove.Length;    // 5

        // ---- synthetic runs ----------------------------------------------------------------
        //
        // The same footprints CornerPlacementTests uses, run through the real FacadeCornerTable so
        // these exercise the actual #459 chaining rather than a hand-built polyline.

        const float FacesSouth = 180f, FacesEast = 90f, FacesNorth = 0f, FacesWest = 270f;

        static StreetFacadeJson Edge(int index, float bearing, float x0, float z0, float x1, float z1)
            => new StreetFacadeJson { edge_index = index, bearing_deg = bearing, score = 1f,
                                      edge = new[] { x0, z0, x1, z1 } };

        static BuildingFactsJson Facts(params StreetFacadeJson[] f)
            => new BuildingFactsJson { osm_id = 424242, floor_count = 3, base_y = 0f,
                                       facade_height_m = 9f, street_facades = f };

        static BuildingFactsJson CornerBuilding(float w = 10f, float d = 8f)
            => Facts(Edge(0, FacesSouth, 0f, 0f, w, 0f), Edge(1, FacesEast, w, 0f, w, d));

        static BuildingFactsJson MidBlockBuilding()
            => Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f));

        static BuildingFactsJson IslandBuilding()
            => Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f), Edge(1, FacesEast, 10f, 0f, 10f, 8f),
                     Edge(2, FacesNorth, 10f, 8f, 0f, 8f), Edge(3, FacesWest, 0f, 8f, 0f, 0f));

        /// <summary>The run exactly as <c>BuildingAssembler.TryPlaceWrapped</c> hands it over:
        /// chained at the roofline, offset by the part's mount depth, then rebased so
        /// <c>points[0]</c> is the part-local origin.</summary>
        static FacadeRun Run(BuildingFactsJson facts, float mountDepth = 0f)
        {
            Assert.IsTrue(FacadeCornerTable.Build(facts).TryBuildRun(9f, mountDepth, out var world));
            return world.Rebased(world.points[0]);
        }

        /// <summary>One facade of a run on its own — what a cornice looked like before #459.</summary>
        static FacadeRun SubRun(FacadeRun run, int from)
            => new FacadeRun(new[] { run.points[from], run.points[from + 1] },
                             new[] { run.outward[from], run.outward[from + 1] }, false);

        // ---- 1. the seam: this is the family that makes TryPlaceWrapped live -----------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUseAndIsPathAware()
        {
            Assert.IsTrue(PartGenerators.TryResolve("cornice.banded", out var g));
            Assert.IsInstanceOf<CorniceGenerator>(g);
            Assert.IsInstanceOf<IPathPartGenerator>(g,
                "TryPlaceWrapped declines anything that is not an IPathPartGenerator — this is the " +
                "opt-in, and until this family shipped nothing in the library had it");
        }

        // ---- 2. THE acceptance criterion: one continuous mitred run --------------------------

        [Test]
        public void ACornerBuildingGetsOneContinuousMitredRunNotOnePerFacade()
        {
            var run = Run(CornerBuilding());
            Assert.AreEqual(3, run.points.Length);
            Assert.AreEqual(1, run.CornerCount);

            var band = Build(Plain(), run);
            Assert.IsTrue(band.IsValid);

            // One mesh spanning both walls: 10 m of south run in X, 8 m of east run in Z.
            Assert.Greater(band.mesh.bounds.size.x, 9.5f);
            Assert.Greater(band.mesh.bounds.size.z, 7.5f);

            // Sweeping each facade on its own — the pre-#459 behaviour — costs an extra pair of end
            // caps at the corner, which is exactly the cornice "stopping dead" that #459 was filed
            // for. The chained run has no such ends.
            int separate = Triangles(Build(Plain(), SubRun(run, 0))) +
                           Triangles(Build(Plain(), SubRun(run, 1)));
            Assert.AreEqual(separate - 2 * (CoveProfilePoints - 2), Triangles(band),
                            "the wrapped run replaces the two corner caps with a mitred join");
        }

        [Test]
        public void TheCornerRingStandsOffBOTHWallPlanes()
        {
            // The mitre itself, measured. The corner of the footprint is (10, 0, 0); the band has to
            // clear the south wall (z = 0) and the east wall (x = 10) by its full projection at the
            // same time. Offsetting each facade independently leaves it proud of one and flush with
            // the other — a notch, visible from the street.
            var band = Build(Plain(), Run(CornerBuilding()));

            Assert.AreEqual(10f + Projection, band.mesh.bounds.max.x, 1e-3f);
            Assert.AreEqual(-Projection, band.mesh.bounds.min.z, 1e-3f);

            // The same facade swept alone stops at the wall plane, so this is not a property the
            // per-facade path ever had.
            var south = Build(Plain(), SubRun(Run(CornerBuilding()), 0));
            Assert.AreEqual(10f, south.mesh.bounds.max.x, 1e-3f);
        }

        [Test]
        public void AnIslandBlockIsOneClosedBandWithNoEndCapsAtAll()
        {
            var run = Run(IslandBuilding());
            Assert.IsTrue(run.closed);

            var band = Build(Plain(), run);
            // 4 segments × (5 profile points − 1) quads × 2 triangles, and not one cap: a closed run
            // has no free end for a cornice to stop at.
            Assert.AreEqual(4 * (CoveProfilePoints - 1) * 2, Triangles(band));
        }

        // ---- 3. (a) the profile-axis convention a turning band builds from --------------------

        [Test]
        public void TheBandProjectsOutwardHorizontallyAndItsHeightIsVertical()
        {
            // The convention #459 left open. Its own tests swept with upHint = Vector3.up purely to
            // get a well-conditioned band; taken literally that puts the Profiles table's
            // "+across = outward from the wall" axis on world UP, i.e. a cornice that projects
            // toward the sky and is `projection` tall. CorniceGenerator.Banded transposes the
            // section back, and this is the guard on it: a 0.30 m projection must be 0.30 m of Z on
            // a wall that faces −Z, and 0.35 m of band height must be 0.35 m of Y.
            var band = Build(Plain(), Run(MidBlockBuilding()));
            var b = band.mesh.bounds;

            Assert.AreEqual(Projection, b.size.z, 1e-3f, "projection is horizontal, out of the wall");
            Assert.AreEqual(HeightM, b.size.y, 1e-3f, "the band's height is vertical");
            Assert.AreEqual(10f, b.size.x, 1e-3f, "and the run itself is the along-facade axis");
            Assert.AreEqual(-Projection, b.min.z, 1e-3f, "a south-facing wall projects toward −Z");
            Assert.AreEqual(0f, b.max.z, 1e-3f, "…and nothing pokes back through the wall");
        }

        [Test]
        public void TheSameBandOnAWallOfAnyBearingStillProjectsOutOfThatWall()
        {
            // A run along Z is where a single up-hint taken from FacadeRun.outward would be exactly
            // degenerate. The east facade of the corner building, swept alone.
            var east = Build(Plain(), SubRun(Run(CornerBuilding()), 1));
            Assert.AreEqual(10f + Projection, east.mesh.bounds.max.x, 1e-3f, "east wall projects +X");
            Assert.AreEqual(10f, east.mesh.bounds.min.x, 1e-3f);
            Assert.AreEqual(HeightM, east.mesh.bounds.size.y, 1e-3f);
        }

        [Test]
        public void ARunTraversedTheOtherWayRoundProducesTheSameBand()
        {
            // The sweep frame's outward axis is cross(+Y, tangent), whose sign flips with the
            // traversal direction — and the placement layer picks that direction from whichever
            // facade happened to have a free end. Getting it wrong builds the cornice inside the
            // building, so the generator re-orients the run from FacadeRun.outward.
            var forward = Run(MidBlockBuilding());
            var backward = new FacadeRun(new[] { forward.points[1], forward.points[0] },
                                         new[] { forward.outward[1], forward.outward[0] }, false);

            var a = Build(Plain(), forward);
            var b = Build(Plain(), backward);

            Assert.AreEqual(a.mesh.bounds.min.z, b.mesh.bounds.min.z, 1e-4f);
            Assert.AreEqual(a.mesh.bounds.max.z, b.mesh.bounds.max.z, 1e-4f);
            Assert.AreEqual(Triangles(a), Triangles(b));
        }

        // ---- 4. (b) the roofline anchor -------------------------------------------------------

        [Test]
        public void TheCrownHangsBelowThePlacementYRoofline()
        {
            // y = 0 is the run's height, which TryPlaceWrapped takes from PlacementY — the same
            // roofline the per-facade path clamps to. The band's TOP sits on it and the moulding
            // hangs below, so the whole cornice lands on real wall.
            var b = Build(Plain(), Run(MidBlockBuilding())).mesh.bounds;

            Assert.AreEqual(0f, b.max.y, 1e-4f, "the crown's top edge IS the roofline");
            Assert.AreEqual(-HeightM, b.min.y, 1e-4f);
        }

        [Test]
        public void DentilsAndBracketsStackDownwardsFromTheCrown()
        {
            var p = With(Plain(), ("profileFamily", "Bracketed"),
                         ("dentilPitch", 0.30f), ("dentilW", 0.13f), ("dentilH", 0.11f),
                         ("bracketSpacing", 1.3f), ("bracketW", 0.11f), ("bracketH", 0.55f));
            var b = Build(p, Run(MidBlockBuilding())).mesh.bounds;

            Assert.AreEqual(0f, b.max.y, 1e-4f, "still anchored at the roofline");
            Assert.AreEqual(-(HeightM + 0.11f + 0.55f), b.min.y, 1e-3f,
                            "crown, then the dentil band, then the brackets hanging off it");
        }

        [Test]
        public void AParapetIsTheOneThingThatRisesAboveTheRoofline()
        {
            var p = Bag(("profileFamily", "Parapet"), ("parapetRise", 0.85f), ("heightM", 0.14f),
                        ("projection", 0.16f), ("parapetShape", "Flat"),
                        ("dentilPitch", 0f), ("bracketSpacing", 0f), ("returnEnds", 0f));
            var b = Build(p, Run(MidBlockBuilding())).mesh.bounds;

            Assert.AreEqual(0.85f, b.max.y, 1e-3f, "wall above the roofline, capped at its rise");
            Assert.AreEqual(0f, b.min.y, 1e-3f, "and its foot is the roofline");
        }

        // ---- 5. scallops: one parameter, one code path -----------------------------------------

        [Test]
        public void AFlatParapetAndAZeroRiseScallopAreTheSameGeometry()
        {
            // parapetShape selects the rise and nothing else; Paths.Arc(rise: 0) is Paths.Line. The
            // property #453 acceptance 2 protects, restated for this family.
            var flat = Bag(("profileFamily", "Parapet"), ("parapetShape", "Flat"),
                           ("parapetRise", 0.85f), ("dentilPitch", 0f), ("returnEnds", 0f));
            var zeroRise = With(flat, ("parapetShape", "Scalloped"), ("scallopRise", 0f));

            var a = Build(flat, Run(MidBlockBuilding()));
            var z = Build(zeroRise, Run(MidBlockBuilding()));

            Assert.AreEqual(Triangles(a), Triangles(z));
            Assert.AreEqual(a.mesh.vertexCount, z.mesh.vertexCount);
        }

        [Test]
        public void AScallopedParapetWavesBetweenTroughAndCrestAndStillWrapsTheCorner()
        {
            var p = Bag(("profileFamily", "Parapet"), ("parapetShape", "Scalloped"),
                        ("parapetRise", 0.85f), ("scallopRise", 0.22f), ("scallopWidth", 1.3f),
                        ("heightM", 0.14f), ("projection", 0.24f), ("parapetThickness", 0.18f),
                        ("dentilPitch", 0f), ("bracketSpacing", 0f), ("returnEnds", 0f));

            var corner = Build(p, Run(CornerBuilding()));
            var b = corner.mesh.bounds;

            // Crests reach the parapet's full rise; the wall behind them stops at the trough line,
            // and the band is tall enough to close the lobe between the two.
            Assert.AreEqual(0.85f, b.max.y, 5e-3f, "crests reach the authored rise");
            Assert.AreEqual(0f, b.min.y, 1e-3f, "and the foot of the wall is still the roofline");

            // Still one run: it spans both walls and it is still mitred at the corner.
            Assert.Greater(b.size.x, 9.5f);
            Assert.Greater(b.size.z, 7.5f);
            Assert.Greater(b.max.x, 10f, "the corner ring clears the east wall too");

            // Every scallop chain starts and ends at a trough, which is the corner vertex itself —
            // that is what lets the scalloped top stay one polyline across the turn. If it did not,
            // the two facades' chains would meet at different heights and the mitre would tear.
            var mb = new MeshBuilder();
            var separate = _gen.GenerateAlong(p, SubRun(Run(CornerBuilding()), 0), mb);
            _spawned.Add(separate.mesh);
            Assert.Greater(Triangles(corner), Triangles(separate));
        }

        // ---- 6. end returns --------------------------------------------------------------------

        [Test]
        public void ReturnEndsDieTheMouldingBackIntoTheWall()
        {
            var open = Build(With(Plain(), ("returnEnds", 1f), ("returnDepth", 0.25f)),
                             Run(MidBlockBuilding()));

            // The return runs back through the wall plane, so the band's z extent now straddles it.
            Assert.Greater(open.mesh.bounds.max.z, 0.2f, "the leg turns into the wall");
            Assert.AreEqual(-Projection, open.mesh.bounds.min.z, 1e-3f, "the face still projects");
            Assert.Greater(Triangles(open), Triangles(Build(Plain(), Run(MidBlockBuilding()))));
        }

        [Test]
        public void AClosedRunIgnoresReturnEndsBecauseItHasNoEnds()
        {
            var withReturns = Build(With(Plain(), ("returnEnds", 1f)), Run(IslandBuilding()));
            var without = Build(Plain(), Run(IslandBuilding()));
            Assert.AreEqual(Triangles(without), Triangles(withReturns));
        }

        // ---- 7. the DetailLevel ladder ----------------------------------------------------------

        [Test]
        public void TheFlatFloorIsOneBandFacePerSegment()
        {
            // The safe floor, not a new failure mode: a 2-point profile bounds no area, so it is one
            // quad per straight facade — two triangles, exactly the placeholder band.
            Assert.AreEqual(2, Triangles(Build(With(Plain(), ("detail", "Flat")), Run(MidBlockBuilding()))));
            Assert.AreEqual(4, Triangles(Build(With(Plain(), ("detail", "Flat")), Run(CornerBuilding()))));
        }

        [Test]
        public void EachDetailLevelIsCheaperThanTheOneAboveIt()
        {
            var p = With(Plain(), ("profileFamily", "Bracketed"),
                         ("dentilPitch", 0.30f), ("dentilW", 0.13f), ("dentilH", 0.11f),
                         ("bracketSpacing", 1.3f), ("bracketW", 0.11f), ("bracketH", 0.55f));

            int full = Triangles(Build(With(p, ("detail", "Full")), Run(CornerBuilding())));
            int reduced = Triangles(Build(With(p, ("detail", "Reduced")), Run(CornerBuilding())));
            int flat = Triangles(Build(With(p, ("detail", "Flat")), Run(CornerBuilding())));

            Assert.Greater(full, reduced * 2f, "Reduced has to be worth having…");
            Assert.Greater(reduced, flat * 10f,
                           "…but it still carries the dentils and brackets that make it Noe Valley");
            Assert.AreEqual(4, flat);
        }

        // ---- 8. the single-facade fallback -------------------------------------------------------

        [Test]
        public void TheFallbackBuildsTheSameBandInThePartLocalFrameThePlacerExpects()
        {
            // IPartGenerator's frame: +X along the facade, +Y up, +Z outward toward the street. The
            // fallback is not a second implementation — it synthesises the run a one-facade building
            // would have produced — but it still has to land in that frame.
            var mb = new MeshBuilder();
            var pm = _gen.Generate(With(Plain(), ("runLengthM", 10f)), mb);
            _spawned.Add(pm.mesh);
            var b = pm.mesh.bounds;

            Assert.AreEqual(0f, b.center.x, 1e-3f, "centred on the anchor");
            Assert.AreEqual(10f, b.size.x, 1e-3f);
            Assert.AreEqual(Projection, b.max.z, 1e-3f, "+Z is outward, toward the street");
            Assert.AreEqual(0f, b.min.z, 1e-3f);
            Assert.AreEqual(0f, b.max.y, 1e-3f, "and the crown still hangs off the roofline");

            // Same triangle count as the equivalent straight wrapped run: one composition, two ways in.
            Assert.AreEqual(Triangles(Build(Plain(), Run(MidBlockBuilding()))), Triangles(pm));
        }

        // ---- 9. determinism ----------------------------------------------------------------------

        [Test]
        public void TheSameRunAlwaysProducesTheSameMesh()
        {
            var a = Build(Plain(), Run(CornerBuilding()));
            var b = Build(Plain(), Run(CornerBuilding()));

            Assert.AreEqual(a.mesh.vertexCount, b.mesh.vertexCount);
            var va = a.mesh.vertices;
            var vb = b.mesh.vertices;
            for (int i = 0; i < va.Length; i++)
                Assert.Less((va[i] - vb[i]).magnitude, 1e-6f, $"vertex {i}");
        }

        // ---- 10. the three presets ----------------------------------------------------------------

        static readonly string[] PresetIds =
        {
            "cornice_noe_bracketed", "cornice_sunset_parapet", "cornice_soma_corbel",
        };

        static PartDefJson LoadPreset(string id)
        {
            string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts", id + ".part.json");
            Assert.IsTrue(File.Exists(path), $"missing preset {path}");
            return JsonUtility.FromJson<PartDefJson>(File.ReadAllText(path));
        }

        [Test]
        public void EveryPresetNamesThisGeneratorAndIsARoofPart()
        {
            foreach (string id in PresetIds)
            {
                var def = LoadPreset(id);
                Assert.AreEqual(id, def.id);
                Assert.AreEqual("cornice.banded", def.generatorId, id);
                // A cornice is a TemplateDef.roofParts entry, not a placement rule — #475 wires it.
                Assert.AreEqual("Roof", def.category, id);
                Assert.Greater(def.mountDepth_m, 0f, $"{id} must stand proud of the wall");
                Assert.Greater(PartParams.From(def.parameters).Count, 5, id);
            }
        }

        [Test]
        public void ThePresetsResolveToDistinctParametersAndDistinctCacheKeys()
        {
            var keys = PresetIds.Select(id => PartParams.From(LoadPreset(id).parameters)
                                                        .KeyFor("cornice.banded")).ToList();
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                    Assert.AreNotEqual(keys[i], keys[j], $"{PresetIds[i]} and {PresetIds[j]} share a mesh key");
        }

        [Test]
        public void ThePresetsReadAsThreeDifferentRooflines()
        {
            PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

            var noe = Preset("cornice_noe_bracketed");
            var sunset = Preset("cornice_sunset_parapet");
            var soma = Preset("cornice_soma_corbel");

            // Noe Valley: the bracketed gingerbread cornice — scroll brackets on a dentil band.
            Assert.AreEqual(CorniceFamily.Bracketed, noe.GetEnum("profileFamily", CorniceFamily.Cove));
            Assert.Greater(noe.GetFloat("bracketSpacing"), 0f);
            Assert.Greater(noe.GetFloat("dentilPitch"), 0f, "the brackets sit ON a dentil band");
            Assert.AreEqual(ProfileId.Ogee, noe.GetEnum("bracketProfile", ProfileId.None));

            // Sunset: the scalloped parapet, stucco, arc-segmented.
            Assert.AreEqual(CorniceFamily.Parapet, sunset.GetEnum("profileFamily", CorniceFamily.Cove));
            Assert.AreEqual(ParapetShape.Scalloped, sunset.GetEnum("parapetShape", ParapetShape.Flat));
            Assert.Greater(sunset.GetFloat("scallopRise"), 0f);
            Assert.AreEqual(MaterialRole.Base, sunset.GetEnum("parapetRole", MaterialRole.Accent2),
                            "stucco reads as the wall, not as trim");
            Assert.AreEqual(0f, sunset.GetFloat("bracketSpacing"), 0f);

            // SoMa: the brick corbel cornice — stepped courses, no brackets, wall-coloured.
            Assert.AreEqual(CorniceFamily.Corbel, soma.GetEnum("profileFamily", CorniceFamily.Cove));
            Assert.AreEqual(0f, soma.GetFloat("bracketSpacing"), 0f);
            Assert.Greater(soma.GetFloat("dentilPitch"), 0f, "the stepped brick course");
            Assert.AreEqual(MaterialRole.Base, soma.GetEnum("corniceRole", MaterialRole.Accent2),
                            "brick is the wall material, not applied trim");
            Assert.Greater(soma.GetFloat("heightM"), noe.GetFloat("heightM"), "a deeper masonry band");
        }

        [Test]
        public void EveryPresetGeneratesAtEveryDetailLevelOnACornerBuilding()
        {
            // The measured triangle table #456 wants. Asserted only as an ordering, because the
            // absolute numbers ARE the measurement; they are logged for the issue.
            var run = Run(CornerBuilding());          // 18 m of run, one corner
            var mid = Run(MidBlockBuilding());        // 10 m of run, no corner
            foreach (string id in PresetIds)
            {
                var basis = PartParams.From(LoadPreset(id).parameters);
                int full = Triangles(Build(With(basis, ("detail", "Full")), run));
                int reduced = Triangles(Build(With(basis, ("detail", "Reduced")), run));
                int flat = Triangles(Build(With(basis, ("detail", "Flat")), run));

                Debug.Log($"[#474] {id}: Full={full} Reduced={reduced} Flat={flat}  (18 m run, 1 corner)" +
                          $"  | mid-block 10 m: Full={Triangles(Build(With(basis, ("detail", "Full")), mid))}" +
                          $" Reduced={Triangles(Build(With(basis, ("detail", "Reduced")), mid))}" +
                          $" Flat={Triangles(Build(With(basis, ("detail", "Flat")), mid))}");

                Assert.Greater(full, reduced, id);
                Assert.Greater(reduced, flat, id);
                // Every preset returns its ends, so the floor is 2 facade segments + 2 return legs,
                // one quad each. Scallops are dropped at Flat, so even the parapet lands here.
                Assert.AreEqual(8, flat, id);
            }
        }

        // ---- 11. (c) cache pressure — the part that is NOT like the other families ---------------

        /// <summary>Mirrors <c>BuildingAssembler.RunKeyNumbers</c> (private there): the run flattened
        /// into cache-key material, plus a closed flag well outside the coordinate range.</summary>
        static float[] RunKeyNumbers(FacadeRun run)
        {
            var nums = new float[run.points.Length * 3 + 1];
            for (int i = 0; i < run.points.Length; i++)
            {
                nums[i * 3] = run.points[i].x;
                nums[i * 3 + 1] = run.points[i].y;
                nums[i * 3 + 2] = run.points[i].z;
            }
            nums[nums.Length - 1] = run.closed ? 1e6f : -1e6f;
            return nums;
        }

        [Test]
        public void AWrappedRunIsOneMeshPerBuildingWhereASlotArtifactIsOneMeshPerPreset()
        {
            const int Buildings = 50;
            var cache = new PartMeshCache();
            var preset = PartParams.From(LoadPreset("cornice_noe_bracketed").parameters);

            // 50 corner buildings, same preset, each a different footprint — which is the ordinary
            // case, not a contrived one: no two street corners in SF are the same size.
            for (int i = 0; i < Buildings; i++)
            {
                var run = Run(CornerBuilding(8f + i * 0.37f, 6f + i * 0.21f), 0.05f);
                cache.GetOrCreate(
                    PartKey.From("cornice.banded|wrap|cornice_noe_bracketed", preset.Detail, RunKeyNumbers(run)),
                    mb => _gen.GenerateAlong(preset, run, mb));
            }
            int wrappedMeshes = cache.Generated;
            int wrappedHits = cache.Hits;

            // The same 50 placements of a slot artifact collapse onto ONE mesh, because a slot
            // artifact's key is its parameters and nothing about the building it lands on.
            var slotCache = new PartMeshCache();
            var window = PartParams.From(JsonUtility.FromJson<PartDefJson>(File.ReadAllText(
                Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts",
                             "window_noe_2over2.part.json"))).parameters);
            var windowGen = new DoubleHungWindowGenerator();
            for (int i = 0; i < Buildings; i++)
                slotCache.GetOrCreate(window.KeyFor("window.double_hung"), mb => windowGen.Generate(window, mb));

            Debug.Log($"[#474] cache over {Buildings} buildings — wrapped cornice: {wrappedMeshes} meshes, " +
                      $"{wrappedHits} hits; slot window: {slotCache.Generated} meshes, {slotCache.Hits} hits");

            Assert.AreEqual(Buildings, wrappedMeshes,
                "a wrapped run carries the building's own polyline in its key, so it cannot collapse");
            Assert.AreEqual(0, wrappedHits);
            Assert.AreEqual(1, slotCache.Generated, "a slot artifact collapses onto one mesh…");
            Assert.AreEqual(Buildings - 1, slotCache.Hits, "…and every further placement is a hit");

            cache.Clear();
            slotCache.Clear();
        }

        [Test]
        public void TwoBuildingsWithTheSameFootprintInTheSamePlaceDoStillShare()
        {
            // The sharing that IS available: PartKey's 5 mm quantum, applied to the run's own
            // coordinates. Identical runs — a re-import, or a mirrored pair of identical corner
            // shops — hit one mesh. It is real, it is just not the collapse a slot artifact gets.
            var cache = new PartMeshCache();
            var preset = PartParams.From(LoadPreset("cornice_soma_corbel").parameters);
            for (int i = 0; i < 8; i++)
            {
                var run = Run(CornerBuilding(), 0.04f);
                cache.GetOrCreate(PartKey.From("cornice.banded|wrap|cornice_soma_corbel", preset.Detail,
                                               RunKeyNumbers(run)),
                                  mb => _gen.GenerateAlong(preset, run, mb));
            }

            Assert.AreEqual(1, cache.Generated);
            Assert.AreEqual(7, cache.Hits);
            cache.Clear();
        }

        [Test]
        public void AMillimetreOfFootprintNoiseStillCollapsesOntoOneMesh()
        {
            // PartKey quantises at 5 mm, so the double→float noise in the sidecar's edge coordinates
            // does not multiply the mesh count on top of the per-building cost measured above.
            var cache = new PartMeshCache();
            var preset = PartParams.From(LoadPreset("cornice_soma_corbel").parameters);
            for (int i = 0; i < 4; i++)
            {
                var run = Run(CornerBuilding(10f + i * 0.0005f, 8f), 0.04f);
                cache.GetOrCreate(PartKey.From("cornice.banded|wrap|cornice_soma_corbel", preset.Detail,
                                               RunKeyNumbers(run)),
                                  mb => _gen.GenerateAlong(preset, run, mb));
            }

            Assert.AreEqual(1, cache.Generated, "sub-quantum differences share a mesh");
            cache.Clear();
        }

        // ---- 12. repeated elements follow the whole run, not just one facade ----------------------

        [Test]
        public void DentilsAndBracketsAreLaidAlongEveryFacadeOfTheRun()
        {
            var p = With(Plain(), ("profileFamily", "Bracketed"),
                         ("dentilPitch", 0.30f), ("dentilW", 0.13f), ("dentilH", 0.11f),
                         ("bracketSpacing", 1.3f), ("bracketW", 0.11f), ("bracketH", 0.55f));

            var run = Run(CornerBuilding());
            int corner = Triangles(Build(p, run));
            int southOnly = Triangles(Build(p, SubRun(run, 0)));

            // The east wall is 8 m of the 18 m run, so its dentils and brackets have to be there too.
            Assert.Greater(corner, southOnly * 1.5f,
                           "repeated elements follow the chained run round the corner");
        }
    }
}
