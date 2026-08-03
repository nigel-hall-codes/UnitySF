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
    /// The properties the first artifact family has to hold (#457, design #452 generators.md §5):
    /// flat and arched heads are one code path, each <see cref="DetailLevel"/> produces the element
    /// set §5.2 promises at a triangle count in the right order of magnitude, and the four
    /// neighborhood presets are genuinely four different windows off one generator.
    /// <para>The triangle figures in generators.md §5.3 are an estimate from a vertex table, not a
    /// measurement, so these assert <i>ranges</i>. The measured counts are logged for #456.</para>
    /// </summary>
    public class WindowGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly DoubleHungWindowGenerator _gen = new DoubleHungWindowGenerator();

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

        /// <summary>A 2-over-2 with casing, sill and a head — the configuration generators.md §5.3
        /// accounts triangles for.</summary>
        static PartParams Reference() => Bag(
            ("w", 0.9f), ("h", 2.0f), ("revealDepth", 0.10f),
            ("sashCount", 2), ("meetingRailH", 0.07f),
            ("lowerCols", 2), ("lowerRows", 1), ("upperCols", 2), ("upperRows", 1),
            ("frameW", 0.07f), ("frameD", 0.045f), ("muntinW", 0.035f), ("muntinD", 0.025f),
            ("glassInset", 0.035f),
            ("casingProfile", "Ogee"), ("casingW", 0.10f), ("casingD", 0.035f),
            ("head", "Flat"), ("headProfile", "Ogee"), ("headProjection", 0.16f), ("headH", 0.12f),
            ("sillProfile", "Sill"), ("sillProjection", 0.12f), ("sillH", 0.10f), ("sillOverhang", 0.07f),
            ("frameRole", "Accent1"));

        // ---- 1. The generator is reachable exactly as a part file names it ----------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUse()
        {
            Assert.IsTrue(PartGenerators.TryResolve("window.double_hung", out var g));
            Assert.IsInstanceOf<DoubleHungWindowGenerator>(g);
        }

        // ---- 2. Flat and arched heads are one code path -----------------------------------
        // generators.md §3/§5.2 and #453 acceptance 2: Arc(rise: 0) degenerates to Line, so a flat
        // head needs no special case. If someone adds `if (head == Flat) …`, these two windows stop
        // being vertex-for-vertex identical and this fails.

        [Test]
        public void AFlatHeadAndAZeroRiseArchAreTheSameGeometry()
        {
            var flat = Build(With(Reference(), ("head", "Flat")));
            var zeroRiseArch = Build(With(Reference(), ("head", "Segmental"), ("headRise", 0f)));

            CollectionAssert.AreEqual(flat.mesh.vertices, zeroRiseArch.mesh.vertices);
            CollectionAssert.AreEqual(flat.submeshRoles, zeroRiseArch.submeshRoles);
            Assert.AreEqual(Triangles(flat), Triangles(zeroRiseArch));
        }

        [Test]
        public void AHoodIsAFlatHeadWithDifferentNumbersNotDifferentCode()
        {
            // Hooded has zero rise like Flat, so at identical projection/overhang it is the *same*
            // sweep — the design's claim that neighborhood identity is numbers, not branches.
            var flat = Build(With(Reference(), ("head", "Flat"), ("headOverhang", 0.09f),
                                               ("headProjection", 0.16f)));
            var hooded = Build(With(Reference(), ("head", "Hooded"), ("headOverhang", 0.09f),
                                                 ("headProjection", 0.16f)));
            CollectionAssert.AreEqual(flat.mesh.vertices, hooded.mesh.vertices);
        }

        [Test]
        public void AnArchedHeadCurvesAndCostsMoreThanAFlatOne()
        {
            var flat = Build(With(Reference(), ("head", "Flat")));
            var round = Build(With(Reference(), ("head", "Round")));

            Assert.Greater(Triangles(round), Triangles(flat), "a 12-segment arc sweeps more rings");
            // The apex of a semicircle over the casing sits half the head's width above the head
            // line, so the part gets meaningfully taller — the visible difference.
            Assert.Greater(round.mesh.bounds.max.y, flat.mesh.bounds.max.y + 0.3f);
            // Same element set: the arch is not a different assembly, only a different path.
            CollectionAssert.AreEqual(flat.submeshRoles, round.submeshRoles);
        }

        // ---- 3. DetailLevel degradation is local to the generator (§5.2) -------------------

        [Test]
        public void FlatDetailIsOneRoleColouredQuad()
        {
            var pm = Build(With(Reference(), ("detail", "Flat")));
            Assert.AreEqual(2, Triangles(pm), "the safe floor is exactly today's placeholder");
            CollectionAssert.AreEqual(new[] { MaterialRole.Glass }, pm.submeshRoles);
            Assert.AreEqual(4, pm.mesh.vertexCount);
        }

        [Test]
        public void FullAndReducedCarryTheSameElementSetAtDifferentCost()
        {
            var full = Build(With(Reference(), ("detail", "Full")));
            var reduced = Build(With(Reference(), ("detail", "Reduced")));

            // §5.2: Reduced keeps the reveal, the sash and the trim; it only cheapens them.
            var expected = new[] { MaterialRole.Base, MaterialRole.Accent1, MaterialRole.Accent2, MaterialRole.Glass };
            CollectionAssert.AreEqual(expected.OrderBy(r => (int)r).ToArray(),
                                      full.submeshRoles.OrderBy(r => (int)r).ToArray());
            CollectionAssert.AreEqual(expected.OrderBy(r => (int)r).ToArray(),
                                      reduced.submeshRoles.OrderBy(r => (int)r).ToArray());

            Assert.Greater(Triangles(full), Triangles(reduced) * 1.5f,
                           "Reduced has to be a real saving, not a rounding difference");
            Assert.Greater(Triangles(reduced), 2, "…and still more than the Flat floor");
        }

        [Test]
        public void ReducedHalvesTheMuntinCount()
        {
            // A six-over-four is where muntins dominate: 5 + 3 bars become 2 + 1.
            var soma = Bag(("w", 2.2f), ("h", 1.8f), ("sashCount", 1),
                           ("lowerCols", 6), ("lowerRows", 4),
                           ("casingProfile", "None"), ("sillProfile", "None"),
                           ("headProfile", "Flat"), ("frameRole", "Metal"));

            int full = Triangles(Build(With(soma, ("detail", "Full"))));
            int reduced = Triangles(Build(With(soma, ("detail", "Reduced"))));
            Assert.Greater(full, reduced * 2f, "8 bars + 24 lights → 3 bars + 6 lights");
        }

        [Test]
        public void EachDetailLevelLandsInTheRightOrderOfMagnitude()
        {
            // generators.md §5.3 estimates ~250 / ~70 / ~2. Those are counted off a vertex table by
            // hand, so the assertion is an order of magnitude, not the number.
            var basis = Reference();
            int full = Triangles(Build(With(basis, ("detail", "Full"))));
            int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
            int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

            Debug.Log($"[#457] reference 2-over-2 triangles: Full={full} Reduced={reduced} Flat={flat} " +
                      "(generators.md §5.3 estimate: 250 / 70 / 2)");

            Assert.IsTrue(full >= 100 && full <= 600, $"Full was {full}, expected ~250 ±");
            Assert.IsTrue(reduced >= 25 && reduced <= 200, $"Reduced was {reduced}, expected ~70 ±");
            Assert.AreEqual(2, flat);
        }

        // ---- 4. The window is built in the frame the assembler places it in ---------------

        [Test]
        public void GeometryIsAuthoredInThePartLocalFrameThePlacerExpects()
        {
            var pm = Build(Reference());
            var b = pm.mesh.bounds;

            // Opening spans x ∈ [-w/2, w/2], y ∈ [0, h] (BottomCenter); trim widens and heightens it.
            Assert.AreEqual(0f, b.center.x, 1e-4f, "centred on the anchor");
            Assert.Less(b.min.y, 0.001f, "the sill hangs below the opening");
            Assert.Greater(b.max.y, 2.0f, "the head sits above it");
            // Recessed half behind the wall plane, projecting half in front of it.
            Assert.Less(b.min.z, -0.10f, "the reveal and glass set back");
            Assert.Greater(b.max.z, 0.05f, "the casing/sill/head stand proud (+Z is toward the street)");
        }

        // ---- 5. The four neighborhood presets ---------------------------------------------

        static readonly string[] PresetIds =
        {
            "window_noe_2over2", "window_sunset_slider", "window_soma_steel", "window_marina_arched",
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
                Assert.AreEqual("window.double_hung", def.generatorId, id);
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
                keys.Add(p.KeyFor("window.double_hung"));
                sizes.Add(new Vector2(p.GetFloat("w"), p.GetFloat("h")));
            }

            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                {
                    Assert.AreNotEqual(keys[i], keys[j], $"{PresetIds[i]} and {PresetIds[j]} share a mesh key");
                    Assert.AreNotEqual(sizes[i], sizes[j], $"{PresetIds[i]} and {PresetIds[j]} are the same size");
                }
        }

        [Test]
        public void ThePresetsReadAsFourDifferentWindows()
        {
            PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

            var noe = Preset("window_noe_2over2");
            var sunset = Preset("window_sunset_slider");
            var soma = Preset("window_soma_steel");
            var marina = Preset("window_marina_arched");

            // Victorian: tall and narrow, deep-set, painted, trimmed, hooded, two sashes.
            Assert.Greater(noe.GetFloat("h") / noe.GetFloat("w"), 2f, "Noe is vertical");
            Assert.AreEqual(2, noe.GetInt("sashCount"));
            Assert.AreEqual(MaterialRole.Accent1, noe.GetEnum("frameRole", MaterialRole.Base));
            Assert.AreNotEqual(ProfileId.None, noe.GetEnum("casingProfile", ProfileId.None));
            Assert.AreEqual(HeadType.Hooded, noe.GetEnum("head", HeadType.Flat));

            // Post-war Sunset: wide, shallow, aluminium, untrimmed, one sliding sash.
            Assert.Less(sunset.GetFloat("h") / sunset.GetFloat("w"), 1f, "Sunset is horizontal");
            Assert.AreEqual(1, sunset.GetInt("sashCount"));
            Assert.AreEqual(MaterialRole.Metal, sunset.GetEnum("frameRole", MaterialRole.Base));
            Assert.AreEqual(ProfileId.None, sunset.GetEnum("casingProfile", ProfileId.None));
            Assert.Less(sunset.GetFloat("revealDepth"), noe.GetFloat("revealDepth"), "shallower than Noe");

            // SoMa steel sash: the widest opening, no trim at all, a dense industrial grid.
            Assert.AreEqual(6, soma.GetInt("lowerCols"));
            Assert.AreEqual(4, soma.GetInt("lowerRows"));
            Assert.AreEqual(ProfileId.None, soma.GetEnum("sillProfile", ProfileId.None));
            Assert.Less(soma.GetFloat("revealDepth"), sunset.GetFloat("revealDepth"), "flush industrial wall");

            // Marina: the deepest reveal of the four, and the only arch.
            Assert.AreEqual(HeadType.Round, marina.GetEnum("head", HeadType.Flat));
            Assert.Greater(marina.GetFloat("revealDepth"), noe.GetFloat("revealDepth"), "deep stucco reveal");
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

                Debug.Log($"[#457] {id}: Full={full} Reduced={reduced} Flat={flat}");

                Assert.Greater(full, reduced, id);
                Assert.Greater(reduced, flat, id);
                Assert.AreEqual(2, flat, id);
            }
        }

        // ---- 6. The presets are reachable: a template names each one, in its own neighborhood --

        [Test]
        public void ATemplateInEachNeighborhoodPlacesItsOwnPreset()
        {
            // The nhood strings come out of python/data/sf_analysis_neighborhoods.geojson and the
            // bake passes them through verbatim, so the slashes are load-bearing: "Sunset/Parkside"
            // and "South of Market" are the whole match key. This is the regression guard on them.
            var wiring = new (string template, string neighborhood, string part)[]
            {
                ("noe_valley_victorian",    "Noe Valley",      "window_noe_2over2"),
                ("sunset_parkside_postwar", "Sunset/Parkside", "window_sunset_slider"),
                ("soma_industrial",         "South of Market", "window_soma_steel"),
                ("marina_mediterranean",    "Marina",          "window_marina_arched"),
            };

            foreach (var (templateId, neighborhood, partId) in wiring)
            {
                string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Templates",
                                           templateId + ".template.json");
                Assert.IsTrue(File.Exists(path), $"missing template {path}");
                var def = JsonUtility.FromJson<TemplateDefJson>(File.ReadAllText(path));

                Assert.AreEqual(templateId, def.id);
                // Contains, not equals: a template may admit more than its namesake neighborhood
                // (#466 widened noe_valley_victorian to the whole play-area Victorian/Edwardian
                // belt). What this guards is that the exact slashed string still matches.
                CollectionAssert.Contains(def.compatibility.neighborhoods, neighborhood, templateId);
                // Likewise the preset must be reachable, but not necessarily as the only rule —
                // #475 adds door/garage/storefront/bay rules to these same templates.
                var windowRule = def.rules.FirstOrDefault(r => r.part == partId);
                Assert.IsNotNull(windowRule, $"{templateId} names no rule placing {partId}");
                Assert.AreEqual("Street", windowRule.facade, templateId);
            }
        }

        [Test]
        public void OnePresetPlacedManyTimesCollapsesToOneMesh()
        {
            // The cache property from the assembler's point of view: 4 presets across 200 jittered
            // placements are 4 meshes, not 200.
            var cache = new PartMeshCache();
            var rng = new System.Random(457);
            var blocks = PresetIds.Select(id => PartParams.From(LoadPreset(id).parameters)).ToArray();

            for (int i = 0; i < 200; i++)
            {
                // Seeded per-slot jitter perturbs by millimetres; the 5 mm quantum collapses it.
                var basis = blocks[i % blocks.Length];
                float j = (float)(rng.NextDouble() - 0.5) * 0.002f;
                var jittered = With(basis, ("w", basis.GetFloat("w") + j), ("h", basis.GetFloat("h") + j));
                cache.GetOrCreate(jittered.KeyFor(_gen.Id), mb => _gen.Generate(jittered, mb));
            }

            Assert.AreEqual(PresetIds.Length, cache.Generated);
            Assert.AreEqual(200 - PresetIds.Length, cache.Hits);
            cache.Clear();
        }
    }
}
