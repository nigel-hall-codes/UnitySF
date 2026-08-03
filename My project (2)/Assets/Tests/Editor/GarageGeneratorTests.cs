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
    /// The properties the garage family has to hold (#471, design #452 design.md §2 Garage row /
    /// §3 Sunset row). Two of them are load-bearing beyond this family:
    /// <list type="number">
    /// <item>the Sunset stucco arch and a flat lintel are <b>one code path</b> —
    /// <c>Paths.Arc(rise: 0)</c> is <c>Paths.Line</c>, so a zero-rise arch is vertex-for-vertex a
    /// flat header (#453 acceptance 2, generators.md §3);</item>
    /// <item><c>doorStyle</c> selects cell counts and nothing else — a roll-up shutter <i>is</i> a
    /// sectional door with one column and many rows, and a flush door is that grid undivided.</item>
    /// </list>
    /// <para>Triangle counts are asserted as ranges and orderings; the absolute numbers are logged,
    /// because they are the measurement #456 wants, not a target this issue invented.</para>
    /// </summary>
    public class GarageGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly SectionalGarageGenerator _gen = new SectionalGarageGenerator();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
            PartGenerators.Reset();   // leave the reflected registry as we found it
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

        static int Triangles(PartMesh pm)
        {
            int n = 0;
            for (int s = 0; s < pm.mesh.subMeshCount; s++) n += pm.mesh.GetTriangles(s).Length / 3;
            return n;
        }

        /// <summary>A 3×4 sectional door in a recessed opening under a flat lintel, over an apron —
        /// the garage-under-flat condition this family exists for.</summary>
        static PartParams Reference() => Bag(
            ("leafW", 2.8f), ("leafH", 2.3f), ("revealDepth", 0.12f), ("trackReveal", 0.16f),
            ("doorStyle", "Sectional"), ("panelCols", 3), ("panelRows", 4),
            ("panelBevel", 0.03f), ("panelInset", 0.03f),
            ("railW", 0.05f), ("railD", 0.03f), ("frameW", 0.06f), ("frameD", 0.04f),
            ("head", "Flat"), ("headerProfile", "Bullnose"),
            ("headerProjection", 0.07f), ("headerH", 0.15f), ("headerOverhang", 0.06f),
            ("headerRise", 0.4f),
            ("apronProjection", 0.3f), ("apronSlope", 0.3f), ("apronH", 0.11f), ("apronOverhang", 0.1f),
            ("leafRole", "Accent1"), ("panelRole", "Accent1"),
            ("headerRole", "Accent2"), ("apronRole", "Base"));

        // ---- 1. The generator is reachable exactly as a part file names it ----------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUse()
        {
            Assert.IsTrue(PartGenerators.TryResolve("garage.sectional", out var g));
            Assert.IsInstanceOf<SectionalGarageGenerator>(g);
        }

        // ---- 2. The stucco arch and the flat lintel are one code path ---------------------

        [Test]
        public void AZeroRiseArcIsExactlyTheFlatHeaderPath()
        {
            // The kernel property the whole family rests on, asserted where it lives: the header
            // path a Flat head produces and the one a Segmental head with rise 0 produces are the
            // *same array*, so no `if (head == Flat)` can ever be needed (#453 acceptance 2).
            const float width = 2.92f;                       // leafW 2.8 + 2 × headerOverhang 0.06
            var center = new Vector3(0f, 2.375f, 0f);
            var flat = Paths.Line(center + new Vector3(-width * 0.5f, 0f, 0f),
                                  center + new Vector3(width * 0.5f, 0f, 0f), 1);
            var zeroRise = Paths.Arc(width, 0f, 1, center);

            CollectionAssert.AreEqual(flat, zeroRise);
        }

        [Test]
        public void AFlatHeaderAndAZeroRiseArchAreTheSameGarage()
        {
            // …and the same property one level up, through the generator. If someone adds a
            // flat-header branch, these two stop being vertex-for-vertex identical.
            var flat = Build(With(Reference(), ("head", "Flat")));
            var zeroRiseArch = Build(With(Reference(), ("head", "Segmental"), ("headerRise", 0f)));

            CollectionAssert.AreEqual(flat.mesh.vertices, zeroRiseArch.mesh.vertices);
            CollectionAssert.AreEqual(flat.submeshRoles, zeroRiseArch.submeshRoles);
            Assert.AreEqual(Triangles(flat), Triangles(zeroRiseArch));
        }

        [Test]
        public void TheStuccoArchIsTheSameElementSetAsTheLintelJustCurved()
        {
            var flat = Build(With(Reference(), ("head", "Flat")));
            var arch = Build(With(Reference(), ("head", "Segmental"), ("headerRise", 0.45f)));

            Assert.Greater(Triangles(arch), Triangles(flat), "a segmental arc sweeps more rings");
            Assert.Greater(arch.mesh.bounds.max.y, flat.mesh.bounds.max.y + 0.4f, "the arch springs");
            // Not a different assembly — the arch is part of the opening surround, not a new hood.
            CollectionAssert.AreEqual(flat.submeshRoles, arch.submeshRoles);
        }

        [Test]
        public void ARoundHeadIsASemicircleOverTheOpening()
        {
            // Round derives its rise from the header width, so it needs no authored headerRise at
            // all — the Marina window's mechanism, unchanged.
            var round = Build(With(Reference(), ("head", "Round"), ("headerRise", 0f)));
            var flat = Build(With(Reference(), ("head", "Flat")));

            const float headerWidth = 2.8f + 2f * 0.06f;
            Assert.AreEqual(flat.mesh.bounds.max.y + headerWidth * 0.5f, round.mesh.bounds.max.y, 0.02f,
                            "apex sits half the header width above the springing line");
        }

        // ---- 3. doorStyle is a division table, not geometry -------------------------------

        [Test]
        public void ARollUpIsASectionalDoorWithOneColumnAndManyRows()
        {
            var basis = With(Reference(), ("slatRows", 16), ("panelRows", 16), ("panelCols", 1));
            var slat = Build(With(basis, ("doorStyle", "Slat")));
            var sectional = Build(With(basis, ("doorStyle", "Sectional")));

            CollectionAssert.AreEqual(sectional.mesh.vertices, slat.mesh.vertices,
                                      "Slat must be the same PanelGrid, not its own code");
        }

        [Test]
        public void ARollUpHasNoVerticalBars()
        {
            // The whole difference between a shutter and a sectional door: interior column edges.
            var slat = Build(With(Reference(), ("doorStyle", "Slat"), ("slatRows", 12)));
            var sectional = Build(With(Reference(), ("doorStyle", "Sectional"),
                                                    ("panelCols", 4), ("panelRows", 12)));
            Assert.Greater(Triangles(sectional), Triangles(slat),
                           "3 interior column edges' worth of bars and cells");
        }

        [Test]
        public void AFlushDoorIsAnUndividedUnbevelledLeaf()
        {
            var flush = Build(With(Reference(), ("doorStyle", "Flush")));
            var equivalent = Build(With(Reference(), ("doorStyle", "Sectional"),
                                                     ("panelCols", 1), ("panelRows", 1),
                                                     ("panelBevel", 0f)));
            CollectionAssert.AreEqual(equivalent.mesh.vertices, flush.mesh.vertices);
            Assert.Less(Triangles(flush), Triangles(Build(Reference())),
                        "flush is the cheapest leaf in the family");
        }

        // ---- 4. DetailLevel degradation is local to the generator -------------------------

        [Test]
        public void FlatDetailIsOneRoleColouredQuad()
        {
            var pm = Build(With(Reference(), ("detail", "Flat")));
            Assert.AreEqual(2, Triangles(pm), "the safe floor is exactly today's placeholder");
            CollectionAssert.AreEqual(new[] { MaterialRole.Accent1 }, pm.submeshRoles,
                                      "and it takes the leaf's own role");
            Assert.AreEqual(4, pm.mesh.vertexCount);
        }

        [Test]
        public void FullAndReducedCarryTheSameElementSetAtDifferentCost()
        {
            var full = Build(With(Reference(), ("detail", "Full")));
            var reduced = Build(With(Reference(), ("detail", "Reduced")));

            // Reduced keeps the reveal, the leaf, the header and the apron; it only cheapens them.
            var expected = new[] { MaterialRole.Base, MaterialRole.Accent1, MaterialRole.Accent2 };
            CollectionAssert.AreEqual(expected.OrderBy(r => (int)r).ToArray(),
                                      full.submeshRoles.OrderBy(r => (int)r).ToArray());
            CollectionAssert.AreEqual(expected.OrderBy(r => (int)r).ToArray(),
                                      reduced.submeshRoles.OrderBy(r => (int)r).ToArray());

            Assert.Greater(Triangles(full), Triangles(reduced) * 1.5f,
                           "Reduced has to be a real saving, not a rounding difference");
            Assert.Greater(Triangles(reduced), 2, "…and still more than the Flat floor");
        }

        [Test]
        public void ReducedDropsTheRaisedPanelsAndHalvesTheSlats()
        {
            // A 16-row roll-up is where the leaf dominates everything else in the part: 16 bevelled
            // cells (5 faces each) + 15 bars become 8 flat cells + 7 bars.
            var rollup = With(Reference(), ("doorStyle", "Slat"), ("slatRows", 16),
                                           ("apronProjection", 0f), ("headerProfile", "Flat"));
            int full = Triangles(Build(With(rollup, ("detail", "Full"))));
            int reduced = Triangles(Build(With(rollup, ("detail", "Reduced"))));
            Assert.Greater(full, reduced * 2f, "raised panels plus slat count is a >2× saving");
        }

        [Test]
        public void EachDetailLevelLandsInTheRightOrderOfMagnitude()
        {
            var basis = Reference();
            int full = Triangles(Build(With(basis, ("detail", "Full"))));
            int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
            int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

            Debug.Log($"[#471] reference 3x4 sectional triangles: Full={full} Reduced={reduced} Flat={flat}");

            Assert.IsTrue(full >= 100 && full <= 700, $"Full was {full}");
            Assert.IsTrue(reduced >= 20 && reduced <= 250, $"Reduced was {reduced}");
            Assert.AreEqual(2, flat);
        }

        // ---- 5. A garage is a floor-0 artifact sitting on the floor line -------------------

        [Test]
        public void TheOpeningStartsExactlyAtTheFloorLine()
        {
            // Why this matters: the placement rule for a garage sets alignToFloorLine (ny = 0), so
            // the part's y = 0 is the floor. Without an apron nothing may hang below it, or every
            // garage in the Sunset sinks into the sidewalk (#475 wires the rule; this is the
            // generator-side contract it depends on).
            var pm = Build(With(Reference(), ("apronProjection", 0f)));
            Assert.AreEqual(0f, pm.mesh.bounds.min.y, 1e-4f);
        }

        [Test]
        public void GeometryIsAuthoredInThePartLocalFrameThePlacerExpects()
        {
            var pm = Build(Reference());
            var b = pm.mesh.bounds;

            Assert.AreEqual(0f, b.center.x, 1e-4f, "centred on the anchor");
            Assert.Greater(b.max.y, 2.3f, "the header sits above the opening");
            Assert.Less(b.min.z, -0.16f, "the leaf and reveal set back into the wall");
            Assert.Greater(b.max.z, 0.05f, "the header and apron stand proud (+Z is toward the street)");
        }

        [Test]
        public void TheApronRunsOutToTheStreetAndFallsWithItsSlope()
        {
            var none = Build(With(Reference(), ("apronProjection", 0f)));
            var level = Build(With(Reference(), ("apronProjection", 0.4f), ("apronSlope", 0f)));
            var sloped = Build(With(Reference(), ("apronProjection", 0.4f), ("apronSlope", 0.6f)));

            Assert.AreEqual(0f, none.mesh.bounds.min.y, 1e-4f, "apronProjection 0 = no apron at all");
            Assert.Greater(level.mesh.bounds.max.z, none.mesh.bounds.max.z + 0.2f, "the slab projects");
            Assert.AreEqual(-0.11f, level.mesh.bounds.min.y, 1e-3f, "a level slab is apronH thick");

            // A level apron is the rectangular slab #471 calls a Box; slope only drops its nose, so
            // the bounding box is unchanged and the geometry is not.
            Assert.AreEqual(level.mesh.bounds.min.y, sloped.mesh.bounds.min.y, 1e-3f);
            Assert.AreEqual(level.mesh.bounds.max.z, sloped.mesh.bounds.max.z, 1e-3f);
            CollectionAssert.AreNotEqual(level.mesh.vertices, sloped.mesh.vertices);
        }

        // ---- 6. The three neighborhood presets --------------------------------------------

        static readonly string[] PresetIds =
        {
            "garage_sunset_arch", "garage_noe_flush", "garage_soma_rollup",
        };

        static PartDefJson LoadPreset(string id)
        {
            string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts", id + ".part.json");
            Assert.IsTrue(File.Exists(path), $"missing preset {path}");
            return JsonUtility.FromJson<PartDefJson>(File.ReadAllText(path));
        }

        [Test]
        public void EveryPresetNamesThisGeneratorAndParsesIntoAParameterBlock()
        {
            foreach (string id in PresetIds)
            {
                var def = LoadPreset(id);
                Assert.AreEqual(id, def.id);
                Assert.AreEqual("Garage", def.category, id);
                Assert.AreEqual("garage.sectional", def.generatorId, id);
                Assert.Greater(PartParams.From(def.parameters).Count, 10, id);
            }
        }

        [Test]
        public void ThePresetsResolveToDistinctParametersAndDistinctCacheKeys()
        {
            var keys = new List<PartKey>();
            var sizes = new List<Vector2>();

            foreach (string id in PresetIds)
            {
                var p = PartParams.From(LoadPreset(id).parameters);
                keys.Add(p.KeyFor("garage.sectional"));
                sizes.Add(new Vector2(p.GetFloat("leafW"), p.GetFloat("leafH")));
            }

            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                {
                    Assert.AreNotEqual(keys[i], keys[j], $"{PresetIds[i]} and {PresetIds[j]} share a mesh key");
                    Assert.AreNotEqual(sizes[i], sizes[j], $"{PresetIds[i]} and {PresetIds[j]} are the same size");
                }
        }

        [Test]
        public void ThePresetsReadAsThreeDifferentGarages()
        {
            PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

            var sunset = Preset("garage_sunset_arch");
            var noe = Preset("garage_noe_flush");
            var soma = Preset("garage_soma_rollup");

            // Sunset garage-under: a sectional door in a deep stucco arch over a driveway apron.
            // The head is part of the surround, so it takes the wall's own role, not a trim role.
            Assert.AreEqual(GarageDoorStyle.Sectional, sunset.GetEnum("doorStyle", GarageDoorStyle.Flush));
            Assert.AreEqual(HeadType.Segmental, sunset.GetEnum("head", HeadType.Flat));
            Assert.Greater(sunset.GetFloat("headerRise"), 0.2f, "a real arch, not a token curve");
            Assert.AreEqual(MaterialRole.Base, sunset.GetEnum("headerRole", MaterialRole.Accent2),
                            "stucco surround, not a painted hood");
            Assert.Greater(sunset.GetFloat("apronProjection"), 0.2f, "the driveway apron");

            // Noe Valley: narrower, flush leaf, flat Ogee-trimmed header, the deepest reveal.
            Assert.AreEqual(GarageDoorStyle.Flush, noe.GetEnum("doorStyle", GarageDoorStyle.Sectional));
            Assert.AreEqual(HeadType.Flat, noe.GetEnum("head", HeadType.Segmental));
            Assert.AreEqual(MaterialRole.Accent1, noe.GetEnum("leafRole", MaterialRole.Base));
            Assert.Less(noe.GetFloat("leafW"), sunset.GetFloat("leafW"), "narrower than the Sunset bay");
            Assert.Greater(noe.GetFloat("trackReveal"), sunset.GetFloat("trackReveal"), "deep reveal");

            // SoMa: the widest opening, a steel roll-up, a steel header, and no apron.
            Assert.AreEqual(GarageDoorStyle.Slat, soma.GetEnum("doorStyle", GarageDoorStyle.Sectional));
            Assert.Greater(soma.GetInt("slatRows"), 10, "many thin slats");
            Assert.AreEqual(MaterialRole.Metal, soma.GetEnum("leafRole", MaterialRole.Base));
            Assert.AreEqual(MaterialRole.Metal, soma.GetEnum("headerRole", MaterialRole.Accent2));
            Assert.Greater(soma.GetFloat("leafW"), sunset.GetFloat("leafW") + 1f, "a loading bay");
            Assert.AreEqual(0f, soma.GetFloat("apronProjection"), 1e-6f, "no apron");
            Assert.Less(soma.GetFloat("trackReveal"), noe.GetFloat("trackReveal"), "flush industrial wall");
        }

        [Test]
        public void EveryPresetGeneratesAtEveryDetailLevel()
        {
            // Also the source of the per-preset triangle table #456 wants; asserted only as an
            // ordering (Full > Reduced > Flat), because the absolute numbers are the measurement.
            foreach (string id in PresetIds)
            {
                var basis = PartParams.From(LoadPreset(id).parameters);
                int full = Triangles(Build(With(basis, ("detail", "Full"))));
                int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
                int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

                Debug.Log($"[#471] {id}: Full={full} Reduced={reduced} Flat={flat}");

                Assert.Greater(full, reduced, id);
                Assert.Greater(reduced, flat, id);
                Assert.AreEqual(2, flat, id);
            }
        }

        [Test]
        public void OnePresetPlacedManyTimesCollapsesToOneMesh()
        {
            // A garage is one placement per building, but the Sunset is thousands of buildings with
            // the same garage — which is exactly the case the 5 mm quantum exists for.
            var cache = new PartMeshCache();
            var rng = new System.Random(471);
            var blocks = PresetIds.Select(id => PartParams.From(LoadPreset(id).parameters)).ToArray();

            for (int i = 0; i < 150; i++)
            {
                var basis = blocks[i % blocks.Length];
                float j = (float)(rng.NextDouble() - 0.5) * 0.002f;
                var jittered = With(basis, ("leafW", basis.GetFloat("leafW") + j),
                                           ("leafH", basis.GetFloat("leafH") + j));
                cache.GetOrCreate(jittered.KeyFor(_gen.Id), mb => _gen.Generate(jittered, mb));
            }

            Assert.AreEqual(PresetIds.Length, cache.Generated);
            Assert.AreEqual(150 - PresetIds.Length, cache.Hits);
            cache.Clear();
        }
    }
}
