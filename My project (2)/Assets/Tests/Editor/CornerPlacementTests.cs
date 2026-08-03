using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;
using SFMap.Pipeline.Editor;

namespace SFMap.Tests
{
    /// <summary>
    /// Corner-crossing artifact resolution (#459). Two street facades of one building meet at a
    /// corner and, until this, nothing reconciled them: a projecting artifact near <c>nx ≈ 1</c> on
    /// one and near <c>nx ≈ 0</c> on the next claim the same corner volume, and a banding artifact
    /// that ought to wrap the corner as one mitred run instead stopped dead at each facade's end.
    ///
    /// <para>These exercise the placement layer — corner detection, the exclusion rule, the chained
    /// run — against synthetic footprints and a synthetic path-aware generator. No bay or cornice
    /// family exists yet, so nothing here can assert how a real one <i>looks</i>; what it asserts is
    /// that the mechanism computes the right corner, rejects the right slots, and hands a
    /// path-aware generator one run across both facades instead of two.</para>
    /// </summary>
    public class CornerPlacementTests
    {
        readonly List<Object> _spawned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
            PartGenerators.Reset();
        }

        // ---- synthetic sidecar geometry ------------------------------------------------

        // Facade bearings are the sidecar's own convention: degrees CW from +Z, naming the OUTWARD
        // normal, so 180 faces -Z and 90 faces +X (FacadeFrame.OutwardNormal).
        const float FacesSouth = 180f, FacesEast = 90f, FacesNorth = 0f, FacesWest = 270f;

        static StreetFacadeJson Edge(int index, float bearing, float x0, float z0, float x1, float z1)
            => new StreetFacadeJson
            {
                edge_index = index,
                bearing_deg = bearing,
                score = 1f,
                edge = new[] { x0, z0, x1, z1 },
            };

        static BuildingFactsJson Facts(params StreetFacadeJson[] facades)
            => new BuildingFactsJson
            {
                osm_id = 424242,
                floor_count = 3,
                base_y = 0f,
                facade_height_m = 9f,
                street_facades = facades,
            };

        /// <summary>A corner building on the SW corner of a block: footprint x∈[0,10], z∈[0,8],
        /// with the south and east edges both facing a street. They share (10, 0).</summary>
        static BuildingFactsJson CornerBuilding()
            => Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f),
                     Edge(1, FacesEast, 10f, 0f, 10f, 8f));

        /// <summary>A mid-block building: one street facade, therefore no corners at all.</summary>
        static BuildingFactsJson MidBlockBuilding()
            => Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f));

        /// <summary>The reflex (inside) corner of an L-plan: the big square minus its NW quadrant.
        /// The two facades meeting at (5, 5) face into each other.</summary>
        static BuildingFactsJson LPlanNotch()
            => Facts(Edge(0, FacesWest, 5f, 10f, 5f, 5f),
                     Edge(1, FacesNorth, 5f, 5f, 0f, 5f));

        /// <summary>A free-standing block with all four edges on a street — the run closes.</summary>
        static BuildingFactsJson IslandBuilding()
            => Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f),
                     Edge(1, FacesEast, 10f, 0f, 10f, 8f),
                     Edge(2, FacesNorth, 10f, 8f, 0f, 8f),
                     Edge(3, FacesWest, 0f, 8f, 0f, 0f));

        // ---- 1. corner detection --------------------------------------------------------

        [Test]
        public void TwoStreetFacadesSharingAFootprintVertexAreOneConvexCorner()
        {
            var table = FacadeCornerTable.Build(CornerBuilding());

            Assert.AreEqual(1, table.Corners.Count, "the two street edges meet exactly once");
            var c = table.Corners[0];
            Assert.AreEqual(0, c.facadeA);
            Assert.AreEqual(1, c.facadeB);
            Assert.IsTrue(c.farEndA, "the south facade meets it at its nx=1 end");
            Assert.IsFalse(c.farEndB, "the east facade meets it at its nx=0 end");
            Assert.IsTrue(c.convex, "a building juts out at a street corner");
            Assert.Less((c.point - new Vector3(10f, 0f, 0f)).magnitude, 1e-4f);
        }

        [Test]
        public void AnLPlanNotchIsDetectedAsAReflexCorner()
        {
            var table = FacadeCornerTable.Build(LPlanNotch());

            Assert.AreEqual(1, table.Corners.Count);
            Assert.IsFalse(table.Corners[0].convex,
                "the two walls of an inside corner face each other, not away from each other");
        }

        [Test]
        public void FacadesThatDoNotTouchAreNotACorner()
        {
            // Opposite sides of the same block: both street-facing, nowhere near each other.
            var facts = Facts(Edge(0, FacesSouth, 0f, 0f, 10f, 0f),
                              Edge(2, FacesNorth, 10f, 8f, 0f, 8f));

            Assert.AreEqual(0, FacadeCornerTable.Build(facts).Corners.Count);
        }

        [Test]
        public void AnIslandBlockClosesIntoFourCorners()
        {
            var table = FacadeCornerTable.Build(IslandBuilding());

            Assert.AreEqual(4, table.Corners.Count);
            foreach (var c in table.Corners) Assert.IsTrue(c.convex);
        }

        // ---- 2. the exclusion rule ------------------------------------------------------
        //
        // A 1.2 m wide bay standing 0.9 m proud of the wall, i.e. bounds.x ∈ [-0.6, +0.6] and
        // 0.9 m of projection — the shape the collision half of #459 is about.

        const float BayMinX = -0.6f, BayMaxX = 0.6f, BayProjection = 0.9f;

        static bool BayBlocked(FacadeCornerTable table, StreetFacadeJson f, float nx)
            => table.Blocked(f, nx, BayMinX, BayMaxX, BayProjection);

        [Test]
        public void ABayThatWouldOverhangTheCornerIsSuppressed()
        {
            var facts = CornerBuilding();
            var table = FacadeCornerTable.Build(facts);

            // nx = 0.97 on a 10 m facade puts the bay's outer edge at 9.7 + 0.6 = 10.3 m — 30 cm
            // past the corner, into the volume the east facade's own bay occupies.
            Assert.IsTrue(BayBlocked(table, facts.street_facades[0], 0.97f));
            // And symmetrically from the other side of the same corner.
            Assert.IsTrue(BayBlocked(table, facts.street_facades[1], 0.03f));
        }

        [Test]
        public void ABayThatClearsTheCornerIsKept()
        {
            var facts = CornerBuilding();
            var table = FacadeCornerTable.Build(facts);

            // 9.3 + 0.6 = 9.9 m: still on its own facade, so the two corner volumes are disjoint.
            Assert.IsFalse(BayBlocked(table, facts.street_facades[0], 0.93f));
            Assert.IsFalse(BayBlocked(table, facts.street_facades[0], 0.5f));
        }

        [Test]
        public void TheEndOfAFacadeThatIsNotACornerIsNotExcluded()
        {
            var facts = CornerBuilding();
            var table = FacadeCornerTable.Build(facts);

            // The south facade's nx=0 end is a party wall with the neighbouring building — nothing
            // of this building's is placed round it, so the rule must not touch it.
            Assert.IsFalse(BayBlocked(table, facts.street_facades[0], 0.0f));
            Assert.IsFalse(BayBlocked(table, facts.street_facades[0], 0.02f));
        }

        [Test]
        public void AReflexCornerAlsoExcludesTheNeighboursProjection()
        {
            // Facade 0 of the notch is 5 m long and meets the inside corner at its far end. A bay
            // centred at 3.6 m has its outer edge at 4.2 m — comfortably on its own facade, which is
            // all a convex corner asks for…
            var convexFacts = CornerBuilding();
            var convexTable = FacadeCornerTable.Build(convexFacts);
            Assert.IsFalse(BayBlocked(convexTable, convexFacts.street_facades[0], 4.2f / 10f),
                           "the equivalent placement at an outside corner is kept");

            // …but at an inside corner the other wall faces back across this one, so its bay
            // projects 0.9 m along this facade from the shared vertex and anything past
            // 5 - 0.9 = 4.1 m has to go.
            var facts = LPlanNotch();
            var table = FacadeCornerTable.Build(facts);
            Assert.IsTrue(BayBlocked(table, facts.street_facades[0], 3.6f / 5f));
            // Clear of the neighbour's projection as well → kept.
            Assert.IsFalse(BayBlocked(table, facts.street_facades[0], 3.0f / 5f));
        }

        [Test]
        public void AFlushArtifactIsNeverExcludedByAReflexCorner()
        {
            var facts = LPlanNotch();
            var table = FacadeCornerTable.Build(facts);

            // A flush window (no projection, 1.2 m wide) three-quarters along: nothing to collide
            // with, so the rule leaves it alone — the exclusion follows the part, not the corner.
            Assert.IsFalse(table.Blocked(facts.street_facades[0], 0.75f, -0.6f, 0.6f, 0f));
        }

        // ---- 3. non-corner buildings are untouched --------------------------------------

        [Test]
        public void AMidBlockBuildingHasNoCornersAndNothingIsEverExcluded()
        {
            var facts = MidBlockBuilding();
            var table = FacadeCornerTable.Build(facts);

            Assert.IsFalse(table.HasCorners);
            for (int i = 0; i <= 100; i++)
                Assert.IsFalse(BayBlocked(table, facts.street_facades[0], i / 100f),
                               $"nx = {i / 100f} on a building with one street facade");
        }

        [Test]
        public void AFacadeThisBuildingDoesNotOwnIsNeverExcluded()
        {
            var table = FacadeCornerTable.Build(CornerBuilding());
            var stranger = Edge(7, FacesNorth, 100f, 100f, 110f, 100f);

            Assert.IsFalse(BayBlocked(table, stranger, 0.99f));
        }

        // ---- 4. determinism -------------------------------------------------------------

        [Test]
        public void TheCornerRuleIsAPureFunctionOfTheSidecarFacts()
        {
            // The determinism contract is hash(osm_id, ruleIndex, slotIndex) → identical assembly on
            // re-import. The corner rule draws no random numbers, so all it has to hold up is that
            // the same facts always produce the same verdicts. Two independently built tables over
            // two independently built fact sets, swept across every facade and every nx.
            var factsA = IslandBuilding();
            var factsB = IslandBuilding();
            var a = FacadeCornerTable.Build(factsA);
            var b = FacadeCornerTable.Build(factsB);

            Assert.AreEqual(a.Corners.Count, b.Corners.Count);
            for (int c = 0; c < a.Corners.Count; c++)
            {
                Assert.AreEqual(a.Corners[c].facadeA, b.Corners[c].facadeA);
                Assert.AreEqual(a.Corners[c].facadeB, b.Corners[c].facadeB);
                Assert.AreEqual(a.Corners[c].farEndA, b.Corners[c].farEndA);
                Assert.AreEqual(a.Corners[c].farEndB, b.Corners[c].farEndB);
                Assert.AreEqual(a.Corners[c].convex, b.Corners[c].convex);
            }

            for (int f = 0; f < factsA.street_facades.Length; f++)
                for (int i = 0; i <= 200; i++)
                {
                    float nx = i / 200f;
                    Assert.AreEqual(BayBlocked(a, factsA.street_facades[f], nx),
                                    BayBlocked(b, factsB.street_facades[f], nx),
                                    $"facade {f}, nx {nx}");
                }
        }

        [Test]
        public void TheWrappedRunIsIdenticalAcrossRuns()
        {
            FacadeCornerTable.Build(IslandBuilding()).TryBuildRun(9f, 0.25f, out var first);
            FacadeCornerTable.Build(IslandBuilding()).TryBuildRun(9f, 0.25f, out var second);

            Assert.AreEqual(first.closed, second.closed);
            Assert.AreEqual(first.points.Length, second.points.Length);
            for (int i = 0; i < first.points.Length; i++)
            {
                Assert.AreEqual(first.points[i].x, second.points[i].x, 0f);
                Assert.AreEqual(first.points[i].y, second.points[i].y, 0f);
                Assert.AreEqual(first.points[i].z, second.points[i].z, 0f);
            }
        }

        // ---- 5. the wrapped run ---------------------------------------------------------

        [Test]
        public void TheRunChainsBothFacadesOfACornerInOrder()
        {
            var table = FacadeCornerTable.Build(CornerBuilding());

            Assert.IsTrue(table.TryBuildRun(9f, 0f, out var run));
            Assert.IsFalse(run.closed);
            Assert.AreEqual(3, run.points.Length, "start of the south wall, the corner, end of the east wall");
            Assert.AreEqual(1, run.CornerCount, "one turn for ProfileSweep to mitre");
            AssertPoint(new Vector3(0f, 9f, 0f), run.points[0]);
            AssertPoint(new Vector3(10f, 9f, 0f), run.points[1]);
            AssertPoint(new Vector3(10f, 9f, 8f), run.points[2]);
            Assert.AreEqual(18f, run.Length, 1e-3f, "10 m of south wall plus 8 m of east wall");
        }

        [Test]
        public void TheRunIsMitredOutwardSoTheBandStaysOffBothWalls()
        {
            const float Depth = 0.3f;
            var table = FacadeCornerTable.Build(CornerBuilding());
            Assert.IsTrue(table.TryBuildRun(9f, Depth, out var run));

            // The corner point must sit `Depth` off BOTH wall planes — the mitre. Offsetting each
            // edge on its own would leave it `Depth` off one and flush with the other, i.e. a notch.
            Vector3 corner = run.points[1];
            Assert.AreEqual(Depth, -corner.z, 1e-4f, "distance out from the south wall (z = 0)");
            Assert.AreEqual(Depth, corner.x - 10f, 1e-4f, "distance out from the east wall (x = 10)");

            // The free ends move straight out along their own wall's normal.
            Assert.AreEqual(-Depth, run.points[0].z, 1e-4f);
            Assert.AreEqual(8f, run.points[2].z, 1e-4f);

            // The declared outward at the turn bisects the two walls' normals.
            Vector3 bisector = (new Vector3(0f, 0f, -1f) + new Vector3(1f, 0f, 0f)).normalized;
            AssertPoint(bisector, run.outward[1]);
        }

        [Test]
        public void AnIslandBlockProducesOneClosedRun()
        {
            var table = FacadeCornerTable.Build(IslandBuilding());

            Assert.IsTrue(table.TryBuildRun(9f, 0.2f, out var run));
            Assert.IsTrue(run.closed, "four street facades chain back to the first");
            Assert.AreEqual(4, run.points.Length, "the closing point is not repeated");
            Assert.AreEqual(4, run.SegmentCount);
            Assert.AreEqual(4, run.CornerCount);
        }

        [Test]
        public void AMidBlockRunIsOneStraightSegmentWithNoCorners()
        {
            var facts = MidBlockBuilding();
            var table = FacadeCornerTable.Build(facts);

            Assert.IsTrue(table.TryBuildRun(9f, 0.3f, out var run));
            Assert.IsFalse(run.closed);
            Assert.AreEqual(2, run.points.Length);
            Assert.AreEqual(0, run.CornerCount, "nothing to wrap — the geometry is what it always was");
            Assert.AreEqual(10f, run.Length, 1e-3f);
        }

        [Test]
        public void RebasingMovesThePointsButNotTheOutwardDirections()
        {
            var table = FacadeCornerTable.Build(CornerBuilding());
            Assert.IsTrue(table.TryBuildRun(9f, 0.3f, out var world));

            var local = world.Rebased(world.points[0]);
            AssertPoint(Vector3.zero, local.points[0]);
            for (int i = 0; i < world.points.Length; i++)
            {
                AssertPoint(world.points[i] - world.points[0], local.points[i]);
                AssertPoint(world.outward[i], local.outward[i]);
            }
            Assert.AreEqual(world.Length, local.Length, 1e-3f);
        }

        // ---- 6. the run feeds ProfileSweep as one continuous, mitred band ---------------

        // A horizontal band's sweep frame wants `up` = world +Y for every heading along the run, so
        // the run is swept with upHint = Vector3.up rather than ProfileSweep's facade-part default
        // of +Z. That default is degenerate for a segment running along Z, which is precisely why
        // FacadeRun carries a per-point outward direction: one up-hint cannot say "outward" for a
        // path that turns. Settling the profile-axis convention for a turning band belongs to the
        // cornice family, not here.
        static readonly Vector2[] BandProfile = Profiles.Scaled(Profiles.Cove, 0.12f, 0.2f);

        static PartMesh SweepBand(MeshBuilder mb, Vector3[] path, bool closed)
        {
            Kernels.ProfileSweep(mb, BandProfile, path, MaterialRole.Accent1,
                                 closedPath: closed, capEnds: true, smoothAlong: false,
                                 upHint: Vector3.up);
            return mb.Finish("band");
        }

        [Test]
        public void SweepingTheRunGivesOneBandAcrossBothFacadesInsteadOfTwo()
        {
            var table = FacadeCornerTable.Build(CornerBuilding());
            Assert.IsTrue(table.TryBuildRun(9f, 0.2f, out var world));
            var run = world.Rebased(world.points[0]);

            var mb = new MeshBuilder();
            var band = SweepBand(mb, run.points, run.closed);
            _spawned.Add(band.mesh);

            Assert.IsTrue(band.IsValid);
            // One mesh spanning both walls: ~10 m of south run in X and ~8 m of east run in Z.
            Assert.Greater(band.mesh.bounds.size.x, 9.5f);
            Assert.Greater(band.mesh.bounds.size.z, 7.5f);
            Assert.AreEqual(1, band.submeshRoles.Length);
            Assert.AreEqual(MaterialRole.Accent1, band.submeshRoles[0]);

            // And it is genuinely ONE run, not two butted together. Sweeping each facade separately
            // costs an extra pair of end caps — the open ends that today read as a cornice stopping
            // dead at the corner. The chained run has no such ends, so it is exactly one cap pair
            // (Cove has 5 profile points → 3 triangles per cap) lighter.
            var southMb = new MeshBuilder();
            var south = SweepBand(southMb, new[] { run.points[0], run.points[1] }, false);
            var eastMb = new MeshBuilder();
            var east = SweepBand(eastMb, new[] { run.points[1], run.points[2] }, false);
            _spawned.Add(south.mesh);
            _spawned.Add(east.mesh);

            int chained = TriangleCount(band.mesh);
            int separate = TriangleCount(south.mesh) + TriangleCount(east.mesh);
            Assert.AreEqual(separate - 2 * (BandProfile.Length - 2), chained,
                            "the wrapped run replaces the two caps at the corner with a mitred join");
        }

        static int TriangleCount(Mesh m)
        {
            int tris = 0;
            for (int s = 0; s < m.subMeshCount; s++) tris += m.GetTriangles(s).Length / 3;
            return tris;
        }

        // ---- 7. the path-aware generator seam -------------------------------------------

        sealed class RunRecordingGenerator : IPartGenerator, IPathPartGenerator
        {
            public string Id => "test.band";
            public int PerFacadeCalls;
            public int RunCalls;
            public FacadeRun LastRun;

            public PartMesh Generate(PartParams p, MeshBuilder mb)
            {
                PerFacadeCalls++;
                return SweepBand(mb, Paths.Line(Vector3.zero, new Vector3(4f, 0f, 0f)), false);
            }

            public PartMesh GenerateAlong(PartParams p, FacadeRun run, MeshBuilder mb)
            {
                RunCalls++;
                LastRun = run;
                return SweepBand(mb, run.points, run.closed);
            }
        }

        [Test]
        public void APathAwareGeneratorIsHandedOneRunSpanningEveryStreetFacade()
        {
            // The seam as the assembler uses it: resolve the part's generatorId, notice it is
            // path-aware, and sweep once along the chained run instead of once per facade.
            // Resolution — not the local instance — is what the assembler holds, and a discovered
            // generator wins over a registered one, so the assertions below read the resolved one.
            PartGenerators.Reset();
            Assert.IsTrue(PartGenerators.TryResolve("test.band", out var resolved));
            Assert.IsInstanceOf<IPathPartGenerator>(resolved,
                "a banding family opts in by implementing IPathPartGenerator — nothing else changes");
            var generator = (RunRecordingGenerator)resolved;

            var facts = CornerBuilding();
            var table = FacadeCornerTable.Build(facts);
            Assert.IsTrue(table.TryBuildRun(9f, 0.2f, out var world));

            var mb = new MeshBuilder();
            var mesh = ((IPathPartGenerator)resolved).GenerateAlong(
                PartParams.Empty, world.Rebased(world.points[0]), mb);
            _spawned.Add(mesh.mesh);

            Assert.AreEqual(1, generator.RunCalls, "one sweep for the whole building…");
            Assert.AreEqual(0, generator.PerFacadeCalls, "…not one per street facade");
            Assert.AreEqual(3, generator.LastRun.points.Length);
            Assert.AreEqual(1, generator.LastRun.CornerCount);
            Assert.IsTrue(mesh.IsValid);
        }

        [Test]
        public void APlainGeneratorIsNotPathAwareAndKeepsThePerFacadePath()
        {
            // The fallback the assembler relies on: an ordinary family is not an IPathPartGenerator,
            // so TryPlaceWrapped declines and the roofline is placed exactly as it was before #459.
            PartGenerators.Reset();

            Assert.IsTrue(PartGenerators.TryResolve("test.plain", out var resolved));
            Assert.IsFalse(resolved is IPathPartGenerator);
        }

        sealed class PlainGenerator : IPartGenerator
        {
            public string Id => "test.plain";
            public PartMesh Generate(PartParams p, MeshBuilder mb)
            {
                Kernels.Box(mb, new Vector3(1f, 1f, 0.2f), Vector3.zero, MaterialRole.Base);
                return mb.Finish("plain");
            }
        }

        static void AssertPoint(Vector3 expected, Vector3 actual)
            => Assert.Less((expected - actual).magnitude, 1e-4f, $"expected {expected}, was {actual}");
    }
}
