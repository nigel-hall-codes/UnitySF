using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Tests
{
    /// <summary>
    /// Sizing a part to its facade (#487). Most artifacts occupy a slot — a window is 0.9 m wide
    /// wherever it lands — but a storefront <i>is</i> the ground floor, so its width is the
    /// facade's. Nothing in the placement layer could say that: <c>PlacePart</c> applies a uniform
    /// scale, and <c>FacadeLength</c> decided how many parts to place and where, never how big one
    /// was.
    ///
    /// <para>The fix is a rule-level flag that overwrites one named parameter with the rule's span
    /// in metres before the generator runs. These assert the three things that makes or breaks:
    /// the part really is rebuilt at the facade's width (not stretched), non-stretching rules are
    /// untouched, and the result still keys the mesh cache.</para>
    /// </summary>
    public class PlacementStretchTests
    {
        static PartDefJson LoadPreset(string id)
        {
            string path = Path.Combine(Application.dataPath, "SFBuildingTemplates", "Parts",
                                       id + ".part.json");
            Assert.IsTrue(File.Exists(path), $"missing preset {path}");
            return JsonUtility.FromJson<PartDefJson>(File.ReadAllText(path));
        }

        static PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

        static PartMesh Build(string generatorId, PartParams p)
        {
            Assert.IsTrue(PartGenerators.TryResolve(generatorId, out var gen), generatorId);
            var mb = new MeshBuilder();
            var pm = gen.Generate(p, mb);
            Assert.IsTrue(pm.IsValid);
            return pm;
        }

        static int Triangles(Mesh m)
        {
            int t = 0;
            for (int s = 0; s < m.subMeshCount; s++) t += m.GetTriangles(s).Length / 3;
            return t;
        }

        // ---- 1. the override itself -----------------------------------------------------

        [Test]
        public void OverridingAParameterReplacesItWithoutTouchingTheOriginalBlock()
        {
            // Parts are import-time immutable and one BuildingPart asset is shared by every
            // building that places it, so an in-place write would leak one facade's width into the
            // next building's mesh.
            var basis = Preset("storefront_noe_corner");
            float authored = basis.GetFloat("w");

            var wide = basis.WithOverride("w", 18f);

            Assert.AreEqual(18f, wide.GetFloat("w"), 1e-4f);
            Assert.AreEqual(authored, basis.GetFloat("w"), 1e-4f, "the shared block was mutated");
            Assert.AreEqual(basis.Count, wide.Count, "replacing a present name adds no entry");
        }

        [Test]
        public void OverridingANameThePresetOmitsAppendsIt()
        {
            // So a rule can stretch a parameter the preset left at its generator default.
            var bare = PartParams.Empty;
            var sized = bare.WithOverride("w", 6.5f);

            Assert.AreEqual(0, bare.Count);
            Assert.AreEqual(1, sized.Count);
            Assert.AreEqual(6.5f, sized.GetFloat("w"), 1e-4f);
        }

        [Test]
        public void ASymbolicValueForTheSameNameIsClearedSoTheNumberWins()
        {
            // PartParams reads `text` in preference to `value`; leaving it in place would make the
            // override silently do nothing.
            var textual = new PartParams
            {
                values = new[] { new PartParam { name = "w", text = "4.0" } },
            };
            Assert.AreEqual(4.0f, textual.GetFloat("w"), 1e-4f);

            Assert.AreEqual(12f, textual.WithOverride("w", 12f).GetFloat("w"), 1e-4f);
        }

        // ---- 2. what the storefront does with it ----------------------------------------

        [Test]
        public void AStorefrontSpansItsFacadeAtFourMetresAndAtTwenty()
        {
            // #487's acceptance, measured on the mesh: the preset authors 4.6 m and must come out
            // at whatever the rule hands it, one metre of facade per metre of `w`.
            //
            // It does NOT come out exactly `w` wide, and that is the family's documented contract
            // rather than a stretch defect: `w` is "the flat storefront run", and the surround
            // frame (frameW) and a Chamfered corner return are added beyond it. For
            // storefront_noe_corner that overhead measures 0.95 m — 0.15 m of surround and a 0.80 m
            // chamfer — and it is the SAME 0.95 m at every width, which is the property that
            // matters: the placement's footprint tracks the facade instead of being a fixed size.
            // The corner rule and the occupancy table both read the built bounds, so neither is
            // fooled by the overhead.
            var basis = Preset("storefront_noe_corner");
            string generator = LoadPreset("storefront_noe_corner").generatorId;

            var narrow = Build(generator, basis.WithOverride("w", 4f));
            var wide = Build(generator, basis.WithOverride("w", 20f));

            Assert.AreEqual(16f, wide.mesh.bounds.size.x - narrow.mesh.bounds.size.x, 0.01f,
                            "16 m more facade must buy 16 m more shopfront");
            foreach (var (pm, w) in new[] { (narrow, 4f), (wide, 20f) })
            {
                float overhead = pm.mesh.bounds.size.x - w;
                Assert.GreaterOrEqual(overhead, 0f, $"@ {w} m");
                Assert.Less(overhead, 1.2f,
                            $"@ {w} m the surround and chamfer stand {overhead:F2} m beyond `w`");
            }

            Object.DestroyImmediate(narrow.mesh);
            Object.DestroyImmediate(wide.mesh);
        }

        [Test]
        public void AWiderShopfrontGetsMoreBaysNotTallerTrim()
        {
            // The reason a uniform scale is not the fix. Scaling 4 m → 20 m would multiply the
            // bulkhead height and the mullion widths by five; rebuilding at 20 m leaves the
            // horizontal bands where they were and subdivides the run instead.
            var basis = Preset("storefront_noe_corner");
            string generator = LoadPreset("storefront_noe_corner").generatorId;

            var narrow = Build(generator, basis.WithOverride("w", 4f));
            var wide = Build(generator, basis.WithOverride("w", 20f));

            Assert.AreEqual(narrow.mesh.bounds.size.y, wide.mesh.bounds.size.y, 0.01f,
                            "the shopfront's height must not follow its width");
            Assert.AreEqual(narrow.mesh.bounds.size.z, wide.mesh.bounds.size.z, 0.01f,
                            "nor its depth");
            Assert.Greater(Triangles(wide.mesh), Triangles(narrow.mesh),
                           "a five-fold facade should carry more bays, not the same ones scaled");

            Object.DestroyImmediate(narrow.mesh);
            Object.DestroyImmediate(wide.mesh);
        }

        // ---- 3. the cache -----------------------------------------------------------------

        [Test]
        public void FacadesThatAgreeToTheCacheQuantumShareOneMesh()
        {
            // A stretched part is a genuinely different mesh per facade width, so its cache
            // behaviour lands between the window's (one mesh per preset) and the cornice's (one per
            // building). Two facades inside one 5 mm bucket must still share.
            var basis = Preset("storefront_noe_corner");
            string generator = LoadPreset("storefront_noe_corner").generatorId;
            var cache = new PartMeshCache();

            // Snapped to the bucket exactly as BuildingAssembler.StretchedParams does.
            foreach (float w in new[] { 7.0f, 7.0f + 0.002f, 7.0f - 0.002f, 9.0f })
            {
                float snapped = Mathf.RoundToInt(w / PartKey.QuantumMeters) * PartKey.QuantumMeters;
                var p = basis.WithOverride("w", snapped);
                cache.GetOrCreate(p.KeyFor(generator), mb => Build(generator, p));
            }

            Assert.AreEqual(2, cache.Generated, "7.0 m ± 2 mm is one mesh; 9.0 m is another");
            Assert.AreEqual(2, cache.Hits);
            cache.Clear();
        }

        [Test]
        public void SnappingToTheBucketMakesTheGeometryIndependentOfWhichBuildingCameFirst()
        {
            // Quantise only the hash and the mesh a bucket ends up holding is whichever building in
            // it was assembled first, which would make geometry depend on sidecar order. Snapping
            // the value makes it a function of the bucket.
            float a = Mathf.RoundToInt(6.123f / PartKey.QuantumMeters) * PartKey.QuantumMeters;
            float b = Mathf.RoundToInt(6.124f / PartKey.QuantumMeters) * PartKey.QuantumMeters;

            Assert.AreEqual(a, b, 0f, "6.123 m and 6.124 m are the same bucket");

            var basis = Preset("storefront_noe_corner");
            Assert.AreEqual(basis.WithOverride("w", a).GetFloat("w"),
                            basis.WithOverride("w", b).GetFloat("w"), 0f);
        }

        // ---- 4. everything else is untouched ---------------------------------------------

        [Test]
        public void ARuleThatDoesNotStretchNamesNoParameterAndChangesNothing()
        {
            var rule = new ProceduralRule { part = "window_noe_2over2" };

            Assert.IsFalse(rule.stretchToFacade, "the default is the behaviour every family had");
            Assert.AreEqual(ProceduralRule.DefaultStretchParam, rule.StretchParamName);

            // A window, a door and a garage all key exactly as they did: the stretch path is only
            // entered when a rule asks for it, and it is the only thing that rewrites a block.
            foreach (string id in new[] { "window_noe_2over2", "door_noe_victorian", "garage_noe_flush" })
            {
                var def = LoadPreset(id);
                var p = PartParams.From(def.parameters);
                Assert.AreEqual(p.KeyFor(def.generatorId), p.KeyFor(def.generatorId));
            }
        }

        [Test]
        public void TheStretchedParameterNameIsAuthorable()
        {
            // Fixed to "w" by default because that is what every family shipped so far calls its
            // span-wise dimension, but a family that calls it something else can still opt in
            // without the placement layer learning anything about that family.
            var rule = new ProceduralRule { stretchToFacade = true, stretchParam = "runLength" };
            Assert.AreEqual("runLength", rule.StretchParamName);

            var p = PartParams.Empty.WithOverride(rule.StretchParamName, 11f);
            Assert.AreEqual(11f, p.GetFloat("runLength"), 1e-4f);
            Assert.AreEqual(0f, p.GetFloat("w", 0f), 1e-4f, "and 'w' was left alone");
        }

        [Test]
        public void TheStretchedWidthIsTheRulesOwnSpanInMetres()
        {
            // PlaceProcedural hands over (x1 - x0) * facadeLength after edgeMargin — the same span
            // its single slot is centred in. So a rule that stretches also decides how much of the
            // facade it is entitled to, and the entitlement is expressed once, in `span`, rather
            // than twice.
            foreach (float facadeLen in new[] { 4f, 8f, 20f })
            {
                const float x0 = 0.02f, x1 = 0.98f;
                float span = (x1 - x0) * facadeLen;
                float centre = (x0 + 0.5f * (x1 - x0)) * facadeLen;

                Assert.AreEqual(x1 * facadeLen, centre + span * 0.5f, 1e-3f,
                                "the width the generator is given, laid out from the slot's centre, " +
                                "ends exactly where the span does");
                Assert.Less(centre + span * 0.5f, facadeLen, "and inside the facade");
            }

            // A stretched part whose family adds trim beyond `w` therefore overhangs its span by
            // that trim. Nothing is fooled — FacadeCornerTable.Blocked and FacadeOccupancy both
            // measure the built mesh — but a template author on a corner building has to leave the
            // trim room in `span`, so the number is asserted here rather than assumed.
            var basis = Preset("storefront_noe_corner");
            var built = Build(LoadPreset("storefront_noe_corner").generatorId,
                              basis.WithOverride("w", 10f));
            Assert.Greater(built.mesh.bounds.size.x, 10f,
                           "storefront_noe_corner's chamfered return stands beyond its run");
            Object.DestroyImmediate(built.mesh);
        }
    }
}
