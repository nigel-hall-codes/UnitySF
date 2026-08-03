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
    /// The properties the <see cref="IPartGenerator"/> seam has to hold (#454, design #452 D2): a
    /// parameter block that round-trips <c>*.part.json</c> → importer → generator, hashes into a
    /// <see cref="PartKey"/> without per-family code, and a registry that resolves
    /// <c>generatorId</c> or cleanly says it cannot.
    /// </summary>
    public class PartGeneratorSeamTests
    {
        // ---- 1. The parameter block round-trips from the wire format --------------------

        const string SunsetWindowJson = @"{
            ""id"": ""window_sunset_2x3"",
            ""category"": ""Window"",
            ""generatorId"": ""window.double_hung"",
            ""parameters"": [
                { ""name"": ""w"", ""value"": 1.2 },
                { ""name"": ""h"", ""value"": 1.6 },
                { ""name"": ""sashCount"", ""value"": 1 },
                { ""name"": ""frameRole"", ""text"": ""Metal"" },
                { ""name"": ""sillProfile"", ""text"": ""Bullnose"" },
                { ""name"": ""detail"", ""text"": ""Reduced"" }
            ],
            ""size_m"": { ""w"": 1.2, ""h"": 1.6, ""d"": 0.15 },
            ""anchor"": ""BottomCenter"",
            ""mountDepth_m"": -0.08,
            ""version"": 2
        }";

        [Test]
        public void PartJsonRoundTripsIntoATypedParameterBlock()
        {
            var def = JsonUtility.FromJson<PartDefJson>(SunsetWindowJson);
            Assert.AreEqual("window.double_hung", def.generatorId);

            var p = PartParams.From(def.parameters);

            // Numbers as numbers, symbols as enums — the two kinds a family's params struct needs
            // (design #452 generators.md §5.1 WindowParams).
            Assert.AreEqual(1.2f, p.GetFloat("w"), 1e-5f);
            Assert.AreEqual(1.6f, p.GetFloat("h"), 1e-5f);
            Assert.AreEqual(1, p.GetInt("sashCount"));
            Assert.AreEqual(MaterialRole.Metal, p.GetEnum("frameRole", MaterialRole.Base));
            Assert.AreEqual(ProfileId.Bullnose, p.GetEnum("sillProfile", ProfileId.None));
            Assert.AreEqual(DetailLevel.Reduced, p.Detail);
        }

        [Test]
        public void UnauthoredParametersTakeTheGeneratorsDefault()
        {
            var p = PartParams.From(JsonUtility.FromJson<PartDefJson>(SunsetWindowJson).parameters);

            Assert.AreEqual(0.07f, p.GetFloat("frameW", 0.07f), 1e-5f, "absent → fallback");
            Assert.AreEqual(ProfileId.Ogee, p.GetEnum("casingProfile", ProfileId.Ogee));
            Assert.IsFalse(p.Has("shutters"));
            // Case matters: the bag has no schema, so a mis-cased name is simply a different name.
            Assert.AreEqual(9f, p.GetFloat("W", 9f), 1e-5f);
        }

        [Test]
        public void AnUnparseableSymbolFallsBackInsteadOfThrowing()
        {
            var p = new PartParams
            {
                values = new[]
                {
                    new PartParam { name = "frameRole", text = "Chartreuse" },
                    new PartParam { name = "head", value = 2f },              // ordinal form
                }
            };
            Assert.AreEqual(MaterialRole.Accent1, p.GetEnum("frameRole", MaterialRole.Accent1));
            Assert.AreEqual(ProfileId.Chamfer, p.GetEnum("head", ProfileId.None), "ordinal 2 = Chamfer");
        }

        [Test]
        public void DetailDefaultsToTheOneBudgetConstantWhenUnauthored()
            => Assert.AreEqual(DetailBudget.Default, PartParams.Empty.Detail);

        [Test]
        public void AnEmptyOrNullParameterArrayIsTheEmptyBlock()
        {
            Assert.AreSame(PartParams.Empty, PartParams.From(null));
            Assert.AreSame(PartParams.Empty, PartParams.From(new PartParamJson[0]));
            Assert.AreEqual(0, PartParams.Empty.Count);
        }

        // ---- 2. The block keys the mesh cache -------------------------------------------

        static PartParams Bag(params (string name, float value)[] entries)
            => new PartParams
            {
                values = entries.Select(e => new PartParam { name = e.name, value = e.value }).ToArray()
            };

        [Test]
        public void SubQuantumJitterKeysToTheSameMesh()
        {
            // What BuildingAssembler's seeded per-slot Rng does: perturb by millimetres. The 5 mm
            // quantum has to collapse that, or every placement becomes its own "distinct" mesh.
            var a = Bag(("w", 1.2f), ("h", 2.0f)).KeyFor("window.double_hung");
            var b = Bag(("w", 1.2004f), ("h", 1.9996f)).KeyFor("window.double_hung");

            Assert.AreEqual(a, b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Test]
        public void ParameterOrderDoesNotAffectTheKey()
        {
            Assert.AreEqual(Bag(("w", 1.2f), ("h", 2.0f)).KeyFor("g"),
                            Bag(("h", 2.0f), ("w", 1.2f)).KeyFor("g"));
        }

        [Test]
        public void DifferentParameterNamesWithTheSameValuesKeyApart()
        {
            // The failure this test exists for: hashing only the value vector would make {a:1} and
            // {b:1} share a mesh.
            Assert.AreNotEqual(Bag(("a", 1f)).KeyFor("g"), Bag(("b", 1f)).KeyFor("g"));
        }

        [Test]
        public void SymbolicValuesKeyApart()
        {
            var painted = new PartParams { values = new[] { new PartParam { name = "frameRole", text = "Accent1" } } };
            var metal = new PartParams { values = new[] { new PartParam { name = "frameRole", text = "Metal" } } };
            Assert.AreNotEqual(painted.KeyFor("g"), metal.KeyFor("g"));
        }

        [Test]
        public void DetailLevelIsPartOfTheKey()
        {
            var full = new PartParams { values = new[] { new PartParam { name = "detail", text = "Full" } } };
            var reduced = new PartParams { values = new[] { new PartParam { name = "detail", text = "Reduced" } } };
            Assert.AreNotEqual(full.KeyFor("g"), reduced.KeyFor("g"));
        }

        [Test]
        public void DifferentGeneratorsKeyApartOnIdenticalParameters()
        {
            Assert.AreNotEqual(Bag(("w", 1f)).KeyFor("window.double_hung"),
                               Bag(("w", 1f)).KeyFor("door.panel"));
        }

        [Test]
        public void OnePresetPlacedManyTimesGeneratesOneMesh()
        {
            // The end-to-end property the cache exists for, driven through PartParams rather than a
            // hand-built float vector: 40 jittered placements of one part → one mesh.
            var cache = new PartMeshCache();
            var rng = new System.Random(7);
            Mesh first = null;

            for (int i = 0; i < 40; i++)
            {
                float jitter = (float)(rng.NextDouble() - 0.5) * 0.002f;
                var bag = Bag(("w", 1.2f + jitter), ("h", 1.6f + jitter));
                var pm = cache.GetOrCreate(bag.KeyFor("window.double_hung"), Panel);
                if (first == null) first = pm.mesh; else Assert.AreSame(first, pm.mesh);
            }

            Assert.AreEqual(1, cache.Generated);
            Assert.AreEqual(39, cache.Hits);
            cache.Clear();
        }

        static PartMesh Panel(MeshBuilder mb)
        {
            Kernels.Box(mb, new Vector3(1.2f, 1.6f, 0.1f), Vector3.zero, MaterialRole.Accent1, Faces.NoBack);
            return mb.Finish("test_panel");
        }

        // ---- 3. The registry resolves generatorId ---------------------------------------

        sealed class StubGenerator : IPartGenerator
        {
            public string Id => "test.stub";
            public PartMesh Generate(PartParams p, MeshBuilder mb)
            {
                Kernels.Box(mb, new Vector3(p.GetFloat("w", 1f), p.GetFloat("h", 1f), 0.05f),
                            Vector3.zero, MaterialRole.Glass, Faces.NoBack);
                return mb.Finish("stub");
            }
        }

        /// <summary>No parameterless constructor, so reflection discovery skips it — which is what
        /// makes it usable to prove explicit registration and duplicate handling.</summary>
        sealed class ExplicitGenerator : IPartGenerator
        {
            readonly string _id;
            public ExplicitGenerator(string id) { _id = id; }
            public string Id => _id;
            public PartMesh Generate(PartParams p, MeshBuilder mb) => mb.Finish("explicit");
        }

        [Test]
        public void AGeneratorIsDiscoveredWithoutAnyRegistrationCall()
        {
            // The property that makes #457 a one-class change: write an IPartGenerator, and
            // `generatorId` resolves to it with no registration edit anywhere.
            Assert.IsTrue(PartGenerators.TryResolve("test.stub", out var g));
            Assert.IsInstanceOf<StubGenerator>(g);
            Assert.Contains("test.stub", PartGenerators.Ids.ToList());
        }

        [Test]
        public void AnUnknownOrEmptyGeneratorIdDoesNotResolve()
        {
            Assert.IsFalse(PartGenerators.TryResolve("nope.missing", out var missing));
            Assert.IsNull(missing);
            Assert.IsFalse(PartGenerators.TryResolve("", out _));
            Assert.IsFalse(PartGenerators.TryResolve(null, out _));
        }

        [Test]
        public void AGeneratorThatCannotBeDiscoveredCanBeRegistered()
        {
            Assert.IsFalse(PartGenerators.TryResolve("test.explicit", out _));
            PartGenerators.Register(new ExplicitGenerator("test.explicit"));
            Assert.IsTrue(PartGenerators.TryResolve("test.explicit", out var g));
            Assert.IsInstanceOf<ExplicitGenerator>(g);
        }

        [Test]
        public void ADuplicateIdKeepsTheFirstRegistration()
        {
            PartGenerators.Register(new ExplicitGenerator("test.stub"));
            Assert.IsTrue(PartGenerators.TryResolve("test.stub", out var g));
            Assert.IsInstanceOf<StubGenerator>(g, "the discovered generator wins");
        }

        [Test]
        public void AResolvedGeneratorFeedsTheCacheAndEmitsItsOwnRoles()
        {
            // The whole seam in one line of assembler code: resolve → key → generate → (mesh, roles).
            Assert.IsTrue(PartGenerators.TryResolve("test.stub", out var g));
            var bag = Bag(("w", 1.4f), ("h", 2.2f));

            var cache = new PartMeshCache();
            var pm = cache.GetOrCreate(bag.KeyFor(g.Id), mb => g.Generate(bag, mb));

            Assert.IsTrue(pm.IsValid);
            Assert.AreEqual(pm.mesh.subMeshCount, pm.submeshRoles.Length,
                            "CloneRoleColored indexes submeshRoles by submesh");
            CollectionAssert.AreEqual(new[] { MaterialRole.Glass }, pm.submeshRoles);
            Assert.AreEqual(1.4f, pm.mesh.bounds.size.x, 1e-4f, "the generator read its parameters");
            cache.Clear();
        }

        // ---- 4. The detail budget is monotone, for every family at once -----------------
        //
        // #480. DetailLevel.Reduced degrades a swept moulding by substituting a 3-point Chamfer.
        // Done unconditionally that *upgrades* a band whose authored section is the 2-point Flat
        // profile, so the cheaper level emits more triangles than the dearer one. It shipped in
        // five of the six families before it was caught, because Sectioned() was copy-pasted rather
        // than shared. The helpers now live in one place (Gen/Detail.cs); these tests are the guard
        // that stops the next family from reintroducing the inversion, and they are deliberately
        // family-agnostic — a new artifact family is covered the moment its preset lands in
        // SFBuildingTemplates/Parts, with no edit here.

        static string PartsDir => Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts");

        static int Triangles(IPartGenerator g, PartDefJson def, string detail)
        {
            var vals = new List<PartParam>(PartParams.From(def.parameters).values ?? new PartParam[0]);
            int at = vals.FindIndex(v => v.name == "detail");
            var np = new PartParam { name = "detail", text = detail };
            if (at >= 0) vals[at] = np; else vals.Add(np);

            var pm = g.Generate(new PartParams { values = vals.ToArray() }, new MeshBuilder());
            int n = 0;
            for (int s = 0; s < pm.mesh.subMeshCount; s++) n += pm.mesh.GetTriangles(s).Length / 3;
            Object.DestroyImmediate(pm.mesh);
            return n;
        }

        [Test]
        public void EveryShippedPresetGetsCheaperAsDetailDrops()
        {
            var files = Directory.GetFiles(PartsDir, "*.part.json").OrderBy(f => f).ToArray();
            Assert.Greater(files.Length, 0, $"no presets found under {PartsDir}");

            foreach (string file in files)
            {
                var def = JsonUtility.FromJson<PartDefJson>(File.ReadAllText(file));
                Assert.IsTrue(PartGenerators.TryResolve(def.generatorId, out var g),
                              $"{def.id} names generator '{def.generatorId}', which does not resolve");

                int full = Triangles(g, def, "Full");
                int reduced = Triangles(g, def, "Reduced");
                int flat = Triangles(g, def, "Flat");
                Debug.Log($"[#480] {def.id} ({def.generatorId}): Full={full} Reduced={reduced} Flat={flat}");

                // Not "strictly cheaper": a preset whose trim is entirely Flat/None has nothing left
                // for Reduced to cheapen, and door_sunset_flush is exactly that (66 / 66 / 2). The
                // property is that a smaller budget is never a bigger mesh.
                Assert.LessOrEqual(reduced, full,
                                   $"{def.id}: Reduced ({reduced}) costs more than Full ({full})");
                Assert.LessOrEqual(flat, reduced,
                                   $"{def.id}: Flat ({flat}) costs more than Reduced ({reduced})");
            }
        }

        [Test]
        public void EveryFamilyWithAGeneratorHasAPresetInTheSweep()
        {
            // Guards the test above against passing vacuously for a family: every generator id that
            // ships (i.e. is not one of this fixture's stubs) must be named by at least one preset.
            var covered = Directory.GetFiles(PartsDir, "*.part.json")
                .Select(f => JsonUtility.FromJson<PartDefJson>(File.ReadAllText(f)).generatorId)
                .Distinct().ToList();

            foreach (string id in PartGenerators.Ids.Where(i => !i.StartsWith("test.")).ToList())
                CollectionAssert.Contains(covered, id, $"generator '{id}' has no preset under Parts/");
        }

        [Test]
        public void DegradingASectionNeverAddsPointsToIt()
        {
            // The unit-level statement of the same rule, over every profile in the table rather than
            // only the ones a preset happens to use.
            foreach (ProfileId id in System.Enum.GetValues(typeof(ProfileId)))
                foreach (DetailLevel d in System.Enum.GetValues(typeof(DetailLevel)))
                {
                    var authored = Profiles.Get(id);
                    var degraded = Profiles.Get(Detail.Sectioned(id, d));
                    if (authored == null) { Assert.IsNull(degraded, $"{id} is not a section"); continue; }
                    Assert.LessOrEqual(degraded.Length, authored.Length,
                                       $"{d} turns {id} ({authored.Length} points) into " +
                                       $"{Detail.Sectioned(id, d)} ({degraded.Length} points)");
                }

            // …and it does still cheapen the sections it was written for.
            Assert.AreEqual(ProfileId.Chamfer, Detail.Sectioned(ProfileId.Ogee, DetailLevel.Reduced));
            Assert.AreEqual(ProfileId.Flat, Detail.Sectioned(ProfileId.Flat, DetailLevel.Reduced),
                            "a 2-point section is already cheaper than a Chamfer");
            Assert.AreEqual(ProfileId.Ogee, Detail.Sectioned(ProfileId.Ogee, DetailLevel.Full));
            Assert.AreEqual(ProfileId.None, Detail.Sectioned(ProfileId.None, DetailLevel.Reduced));
        }

        [Test]
        public void HalvingAFieldNeverGrowsItAndNeverEmptiesIt()
        {
            for (int n = -2; n <= 32; n++)
            {
                int full = Detail.Divisions(n, DetailLevel.Full);
                int reduced = Detail.Divisions(n, DetailLevel.Reduced);
                int flat = Detail.Divisions(n, DetailLevel.Flat);
                Assert.GreaterOrEqual(full, 1);
                Assert.LessOrEqual(reduced, full, $"Reduced grew a {n}-cell field");
                Assert.LessOrEqual(flat, reduced);
                Assert.GreaterOrEqual(reduced, 1, "a field never disappears at a level meant to cheapen it");
            }
            Assert.AreEqual(3, Detail.Divisions(6, DetailLevel.Reduced));
            Assert.AreEqual(6, Detail.Divisions(6, DetailLevel.Full));
        }

        // Leave no explicit registration behind; the next resolve re-discovers.
        [TearDown]
        public void ResetRegistry() => PartGenerators.Reset();
    }
}
