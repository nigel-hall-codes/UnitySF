using System.IO;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;
using SFMap.Pipeline.Editor;

namespace SFMap.Tests
{
    /// <summary>
    /// Ground-floor (and every other floor's) occupancy (#491). The exclusion used to be a point
    /// mark written only by <c>PlaceExact</c>, keyed <c>(edge_index, floor)</c>, tested against a
    /// radius that could not exceed the rule's own repeat pitch. Three defects followed, all of
    /// them measured by #475: two rules were blind to each other, an artifact spanning two floors
    /// marked only its base, and a 2.20 m pitch could never clear a 3.80 m bay.
    ///
    /// <para>These exercise the replacement — a shared table of real extents — twice over: as a
    /// data structure, and against the <b>real Noe Valley presets</b>, generating their meshes and
    /// measuring them exactly as <c>BuildingAssembler.PlacePart</c> does. The second half is what
    /// makes the numbers here the same numbers the importer will see.</para>
    /// </summary>
    public class FacadeOccupancyTests
    {
        const float FloorHeightMeters = 3.0f;   // BuildingAssembler.FloorHeightMeters

        // ---- 1. the table itself --------------------------------------------------------

        [Test]
        public void TwoPartsOverlappingInBothPlanAndHeightCollide()
        {
            var t = new FacadeOccupancy();
            t.Add(edgeIndex: 0, sourceId: FacadeOccupancy.NotARule,
                  minMeters: 2f, maxMeters: 4f, minY: 3f, maxY: 9f);

            Assert.IsTrue(t.Occupied(0, 1, 3f, 5f, 4f, 6f), "overlaps in both axes");
            Assert.IsFalse(t.Occupied(0, 1, 4.5f, 6f, 4f, 6f), "clear along the facade");
            Assert.IsFalse(t.Occupied(0, 1, 3f, 5f, 9.5f, 11.5f), "clear above it");
        }

        [Test]
        public void APartOnAnotherFacadeEdgeIsNeverInTheWay()
        {
            var t = new FacadeOccupancy();
            t.Add(0, FacadeOccupancy.NotARule, 2f, 4f, 3f, 9f);

            Assert.IsFalse(t.Occupied(1, 0, 3f, 5f, 4f, 6f),
                           "the same metres on a different edge are a different wall");
        }

        [Test]
        public void PartsAuthoredToTouchExactlyDoNotExcludeEachOther()
        {
            var t = new FacadeOccupancy();
            t.Add(0, FacadeOccupancy.NotARule, 2f, 4f, 3f, 9f);

            Assert.IsFalse(t.Occupied(0, 1, 4f, 6f, 3f, 9f), "edge-to-edge is contact, not overlap");
            Assert.IsTrue(t.Occupied(0, 1, 3.9f, 6f, 3f, 9f), "a real overlap still collides");
        }

        [Test]
        public void ARuleNeverExcludesAgainstItsOwnSlots()
        {
            // Keeping one family's parts apart is the repeat pitch's job. If the table did it too,
            // a rule whose part is wider than its pitch would silently thin its own rhythm.
            var t = new FacadeOccupancy();
            t.Add(0, sourceId: 3, minMeters: 2f, maxMeters: 4f, minY: 3f, maxY: 6f);

            Assert.IsFalse(t.Occupied(0, 3, 3f, 5f, 3f, 6f), "same rule — its own business");
            Assert.IsTrue(t.Occupied(0, 4, 3f, 5f, 3f, 6f), "a different rule must dodge it");
            Assert.IsTrue(t.Occupied(0, FacadeOccupancy.NotARule, 3f, 5f, 3f, 6f),
                          "and so must an exact or an override, if it ever consulted the table");
        }

        [Test]
        public void TheVerdictDoesNotDependOnTheOrderSpansWereAdded()
        {
            // Determinism: the assembler's placement order is fixed, but the table must not smuggle
            // in an order dependency of its own.
            var a = new FacadeOccupancy();
            a.Add(0, FacadeOccupancy.NotARule, 2f, 4f, 3f, 9f);
            a.Add(0, 1, 6f, 8f, 3f, 6f);

            var b = new FacadeOccupancy();
            b.Add(0, 1, 6f, 8f, 3f, 6f);
            b.Add(0, FacadeOccupancy.NotARule, 2f, 4f, 3f, 9f);

            for (int i = 0; i <= 200; i++)
            {
                float x = i * 0.05f;
                Assert.AreEqual(a.Occupied(0, 2, x, x + 0.9f, 4f, 6f),
                                b.Occupied(0, 2, x, x + 0.9f, 4f, 6f), $"x = {x}");
            }
        }

        // ---- 2. the real Noe Valley presets ---------------------------------------------

        static PartDefJson LoadPreset(string id)
        {
            string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts",
                                       id + ".part.json");
            Assert.IsTrue(File.Exists(path), $"missing preset {path}");
            return JsonUtility.FromJson<PartDefJson>(File.ReadAllText(path));
        }

        /// <summary>The part's generated mesh bounds — the figures <c>PlacePart</c> measures a
        /// placement by, rather than the <c>size_m</c> a preset claims for itself.</summary>
        static Bounds Built(string id)
        {
            var def = LoadPreset(id);
            Assert.IsTrue(PartGenerators.TryResolve(def.generatorId, out var gen), def.generatorId);
            var mb = new MeshBuilder();
            var pm = gen.Generate(PartParams.From(def.parameters), mb);
            Assert.IsTrue(pm.IsValid, id);
            var bounds = pm.mesh.bounds;
            Object.DestroyImmediate(pm.mesh);
            return bounds;
        }

        /// <summary>Record a placement the way <c>PlacePart</c> does: centre in metres along the
        /// facade, extents from the mesh, world Y from the floor band.</summary>
        static void Place(FacadeOccupancy t, int sourceId, Bounds b, float centerMeters, float y)
            => t.Add(0, sourceId, centerMeters + b.min.x, centerMeters + b.max.x,
                     y + b.min.y, y + b.max.y);

        static bool Blocks(FacadeOccupancy t, int sourceId, Bounds b, float centerMeters, float y)
            => t.Occupied(0, sourceId, centerMeters + b.min.x, centerMeters + b.max.x,
                          y + b.min.y, y + b.max.y);

        [Test]
        public void ABaySpanningTwoFloorsSuppressesWindowSlotsOnBothOfThem()
        {
            // noe_valley_victorian: bay_noe_slanted is an exact on floor 1 with floorsSpanned 2,
            // and window_noe_2over2 repeats over floors 1..8 mid-floor. The old mark was keyed
            // (edge, floor) and carried no span, so floor 2 — the floor the bay rises through —
            // got no mark at all and the window rule placed straight into the shell.
            var bay = Built("bay_noe_slanted");
            var window = Built("window_noe_2over2");

            Assert.Greater(bay.size.y, FloorHeightMeters * 1.5f,
                           "the bay is measured as more than one floor tall by its geometry alone — " +
                           "no floorsSpanned parameter is read anywhere");

            var t = new FacadeOccupancy();
            Place(t, FacadeOccupancy.NotARule, bay, centerMeters: 5f, y: 1 * FloorHeightMeters);

            // A window directly over the bay, mid-floor, on each floor the bay reaches.
            Assert.IsTrue(Blocks(t, 0, window, 5f, (1 + 0.5f) * FloorHeightMeters), "floor 1");
            Assert.IsTrue(Blocks(t, 0, window, 5f, (2 + 0.5f) * FloorHeightMeters),
                          "floor 2 — the floor the bay rises through, which used to carry no mark");
            Assert.IsFalse(Blocks(t, 0, window, 5f, (3 + 0.5f) * FloorHeightMeters),
                           "floor 3 is above the bay and must still be dressed");
            Assert.IsFalse(Blocks(t, 0, window, 5f, 0f),
                           "a ground-floor opening on the floor line is clear below it");

            // Measured while writing this: a MID-floor window on floor 0 is not clear. The bay's
            // skirt drops 0.40 m below its own floor line to 2.60 m, and a 2.32 m window centred in
            // a 3.00 m floor reaches 3.72 m, so the two really do meet. The Victorian template puts
            // no windows on floor 0 (its ground floor is the garage and the entry), so this costs
            // nothing today — but the table reports it, rather than a floor index pretending the
            // two live on different storeys.
            Assert.IsTrue(Blocks(t, 0, window, 5f, 0.5f * FloorHeightMeters),
                          "a mid-floor-0 window reaches up into the bay's skirt");
        }

        [Test]
        public void ClearingTheBayIsNoLongerCappedByTheWindowRulesRepeatPitch()
        {
            // The arithmetic #475 recorded and could not fix: window_noe_2over2 repeats at 2.20 m,
            // and PlaceProcedural derived spacing = max(spacingMeters, minSpacingMeters), so raising
            // the exclusion past 2.20 m would have changed the slot count instead of the exclusion.
            // Clearing the bay needs half of each part, which is more than that.
            const float WindowRepeatPitchMeters = 2.20f;

            var bay = Built("bay_noe_slanted");
            var window = Built("window_noe_2over2");
            float needed = bay.extents.x + window.extents.x;

            Assert.Greater(needed, WindowRepeatPitchMeters,
                           "if a preset resize made the bay clearable inside the old pitch this test " +
                           "no longer describes anything — check the presets, not the engine");

            var t = new FacadeOccupancy();
            Place(t, FacadeOccupancy.NotARule, bay, 5f, FloorHeightMeters);

            float y = 1.5f * FloorHeightMeters;
            Assert.IsTrue(Blocks(t, 0, window, 5f + WindowRepeatPitchMeters, y),
                          $"a window {WindowRepeatPitchMeters} m from the bay's centre is still " +
                          $"inside its {2f * bay.extents.x:F2} m shell");
            Assert.IsFalse(Blocks(t, 0, window, 5f + needed + 0.01f, y),
                           "and one clear of both half-widths is admitted");
        }

        [Test]
        public void TwoProceduralRulesOnOneFloorDoNotInterpenetrate()
        {
            // The limitation TemplateWiringTests.AtMostOneRulePerTemplateClaimsAGivenFloor used to
            // guard: _exactMarks was written by PlaceExact only, so two rules on one floor were
            // authored interpenetration. Now every procedural placement both joins and consults the
            // table, so rule 1 sees rule 0.
            var garage = Built("garage_noe_flush");
            var door = Built("door_noe_victorian");

            var t = new FacadeOccupancy();
            Place(t, sourceId: 0, b: garage, centerMeters: 4f, y: 0f);

            Assert.IsTrue(Blocks(t, 1, door, 4f, 0f), "rule 1 lands on rule 0's garage → refused");
            Assert.IsFalse(Blocks(t, 1, door, 4f + garage.extents.x + door.extents.x + 0.01f, 0f),
                           "clear of it → placed");
        }

        [Test]
        public void AnExactStillClaimsItsSpaceWithoutEverYieldingIt()
        {
            // Precedence Exact > Procedural, expressed as: exacts record but never consult. The
            // table cannot displace a template's fixed bones, only keep rules off them.
            var door = Built("door_noe_victorian");
            var t = new FacadeOccupancy();
            Place(t, FacadeOccupancy.NotARule, door, 6f, 0f);

            Assert.IsTrue(Blocks(t, 0, door, 6f, 0f),
                          "a rule would be refused here — which is the whole point of recording it");
            Assert.AreEqual(1, t.Count, "and the exact itself was recorded, not rejected");
        }
    }
}
