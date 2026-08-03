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
    /// The properties the storefront family has to hold (#472, design #452 design.md §2/§3).
    ///
    /// <para>Most of this file is about <b>width</b>. Every family shipped before this one occupies
    /// a slot of an authored size that the placement engine repeats every N metres; a storefront
    /// spans a whole ground-floor facade, so <c>w</c> is whatever the facade is and the generator
    /// has to stay sane from a 4 m corner shop to a 20 m warehouse frontage. Those two widths, and
    /// the absurd ones on either side of them, are what the bay-layout tests below pin down.</para>
    ///
    /// <para>Triangle counts are logged rather than asserted exactly — they are the measurement
    /// #456 wants, and asserting them exactly would make every geometry tweak a test edit.</para>
    /// </summary>
    public class StorefrontGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly StorefrontGenerator _gen = new StorefrontGenerator();

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

        static int TrianglesInRole(PartMesh pm, MaterialRole role)
        {
            for (int s = 0; s < pm.submeshRoles.Length; s++)
                if (pm.submeshRoles[s] == role) return pm.mesh.GetTriangles(s).Length / 3;
            return 0;
        }

        /// <summary>A middling storefront: three display bays, a centred recessed entry, a tiled
        /// bulkhead and a transom band. The configuration the width sweep varies <c>w</c> on.</summary>
        static PartParams Reference() => Bag(
            ("w", 7.0f), ("h", 3.8f), ("revealDepth", 0.10f), ("glassInset", 0.02f),
            ("bayCount", 0), ("bayPitch", 2.4f), ("bayRhythm", "Even"),
            ("bulkheadH", 0.70f), ("bulkheadPanelRows", 2), ("bulkheadBevel", 0.02f),
            ("bulkheadRole", "Accent2"),
            ("transomH", 0.50f), ("transomLights", 4),
            ("mullionW", 0.09f), ("mullionD", 0.06f),
            ("entrySide", "Center"), ("entryWidth", 1.8f), ("entryRecessDepth", 0.70f),
            ("entryLeaves", 2), ("entryKickH", 0.30f),
            ("frameProfile", "Flat"), ("frameW", 0.14f), ("frameD", 0.06f),
            ("frameRole", "Metal"));

        // ---- 1. The generator is reachable exactly as a part file names it ----------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUse()
        {
            Assert.IsTrue(PartGenerators.TryResolve("storefront.bay_row", out var g));
            Assert.IsInstanceOf<StorefrontGenerator>(g);
        }

        // ---- 2. The part-local frame the placer expects -----------------------------------

        [Test]
        public void GeometryIsAuthoredInThePartLocalFrameThePlacerExpects()
        {
            var pm = Build(Reference());
            var b = pm.mesh.bounds;

            // x ∈ [-w/2, w/2], y ∈ [0, h] from the BottomCenter anchor; the surround widens both by
            // a full band — its centreline sits half a band outside the opening and it is half a
            // band wide either side of that, so the outer edge lands at w/2 + frameW exactly.
            Assert.AreEqual(0f, b.center.x, 1e-3f, "centred on the anchor");
            Assert.AreEqual(-(3.5f + 0.14f), b.min.x, 0.02f, "half the run plus one surround band");
            Assert.Less(b.min.y, 0.001f, "the storefront starts at grade");
            Assert.AreEqual(3.8f + 0.14f, b.max.y, 0.02f, "the surround stands above the head");

            // The whole assembly lives at z ≤ frameD: it is a box projecting from the wall toward
            // the street, and the wall plane is at -mountDepth (see the class doc's depth model).
            Assert.Greater(b.max.z, 0.05f, "the surround stands proud (+Z is toward the street)");
            Assert.AreEqual(-0.82f, b.min.z, 0.02f, "reveal + glassInset + entryRecessDepth back");
        }

        [Test]
        public void ThePresetMountDepthPutsTheRecessBackPlaneOnTheWall()
        {
            // The depth contract that makes the recessed entry visible at all: the mass has no hole
            // cut in it, so mountDepth_m must lift the assembly out by exactly the amount the
            // geometry reaches back. If a preset drifts from that the recess is either buried in the
            // wall or floating off it, and nothing else in the pipeline would notice.
            foreach (string id in PresetIds)
            {
                var def = LoadPreset(id);
                var p = PartParams.From(def.parameters);
                float reach = p.GetFloat("revealDepth") + p.GetFloat("glassInset") +
                              p.GetFloat("entryRecessDepth");
                Assert.AreEqual(reach, def.mountDepth_m, 1e-3f, id);

                var pm = Build(p);
                Assert.AreEqual(-reach, pm.mesh.bounds.min.z, 0.02f, $"{id} reaches its mount depth");
            }
        }

        // ---- 3. Width: the property this family exists to get right ------------------------

        /// <summary>Bay edges are not exposed, so count bays the way the geometry does: every bay
        /// edge carries one vertical mullion box, and there is one more edge than there are bays.</summary>
        static int BayCountOf(PartMesh pm, PartParams p)
        {
            // Cheap and robust: the mullion posts are the only Metal/Accent1 boxes that span the
            // full glazed height, so infer from the frame-role triangle count instead of guessing.
            // Simpler still — rebuild with the transom and bulkhead suppressed so the frame role
            // holds nothing but the posts and the two rails.
            var bare = With(p, ("transomH", 0f), ("bulkheadH", 0f), ("frameProfile", "None"),
                               ("entrySide", "None"));
            var mb = new MeshBuilder();
            var only = new StorefrontGenerator().Generate(bare, mb);
            int posts = TrianglesInRole(only, p.GetEnum("frameRole", MaterialRole.Metal)) / 10;
            Object.DestroyImmediate(only.mesh);
            return posts - 1;      // n bays ⇒ n+1 posts
        }

        [Test]
        public void ANarrowFacadeDegradesToATwoBayShopNotToSlivers()
        {
            // 4 m: a corner shop. bayPitch 2.4 would round to 2 bays anyway, but the entry is what
            // forces the floor — an entry needs a display bay beside it or it is not a storefront.
            var narrow = With(Reference(), ("w", 4.0f));
            var pm = Build(narrow);

            Assert.AreEqual(2, BayCountOf(pm, narrow), "4 m is an entry plus one display bay");
            Assert.Greater(Triangles(pm), 40, "still a real storefront, not a degenerate stub");
            Assert.AreEqual(-(2.0f + 0.14f), pm.mesh.bounds.min.x, 0.02f, "spans exactly the 4 m it was given");

            // Every element still present: bulkhead + door kick (Accent2), glass, posts, box returns.
            var roles = pm.submeshRoles.OrderBy(r => (int)r).ToArray();
            CollectionAssert.AreEqual(
                new[] { MaterialRole.Base, MaterialRole.Accent2, MaterialRole.Glass, MaterialRole.Metal },
                roles);
        }

        [Test]
        public void AWideFacadeGrowsBaysRatherThanStretchingThem()
        {
            // 20 m: a warehouse frontage. The bay count follows the pitch, so the bays stay the size
            // a shopfront bay actually is instead of one 20 m sheet of glass.
            var wide = With(Reference(), ("w", 20.0f));
            var pm = Build(wide);

            int bays = BayCountOf(pm, wide);
            Assert.AreEqual(8, bays, "round(20 / 2.4) = 8");

            // Bay width: run minus the entry, over the display bays. ~2.6 m — a shopfront bay.
            float displayBay = (20.0f - 1.8f) / (bays - 1);
            Assert.Greater(displayBay, 1.5f);
            Assert.Less(displayBay, 4.0f);
            Assert.AreEqual(-(10.0f + 0.14f), pm.mesh.bounds.min.x, 0.02f, "spans exactly the 20 m it was given");
        }

        [Test]
        public void TriangleCountGrowsAboutLinearlyWithWidthAndIsBoundedByTheBayCap()
        {
            // The failure this guards is quadratic growth (bays × tiles-per-bay both following w).
            // Tiles are sized, and bays are capped, so cost is linear and then flat.
            int at4 = Triangles(Build(With(Reference(), ("w", 4.0f))));
            int at20 = Triangles(Build(With(Reference(), ("w", 20.0f))));
            int at60 = Triangles(Build(With(Reference(), ("w", 60.0f))));

            Debug.Log($"[#472] width sweep: 4m={at4} 20m={at20} 60m={at60}");

            Assert.Greater(at20, at4, "a wider facade is a bigger storefront");
            Assert.Less(at20, at4 * 8f, "…but 5× the width must not be 8× the cost");
            Assert.Less(at60, at20 * 2.5f, "past the bay cap the bays widen instead of multiplying");
        }

        [Test]
        public void AFacadeTooNarrowForAnEntryBecomesAnUnbrokenGlassFront()
        {
            // 1.0 m is not a storefront, but a template can still hand one over on a stub facade.
            // Dropping the entry is the graceful answer; a 20 cm door would not be.
            var stub = Build(With(Reference(), ("w", 1.0f)));

            Assert.Greater(Triangles(stub), 4, "still emits geometry");
            // Role Base holds the surround reveal, the projecting box's returns and the entry jambs.
            // With no entry only the first exists, so it is exactly the reveal's four jamb quads.
            Assert.AreEqual(8, TrianglesInRole(stub, MaterialRole.Base),
                            "no entry ⇒ no recess, so no projecting box and no jambs");
            Assert.Greater(stub.mesh.bounds.min.z, -0.25f, "sits on the wall, not out over the pavement");
        }

        [Test]
        public void EveryWidthFromAStubToABlockFaceProducesFiniteForwardWoundGeometry()
        {
            // The blunt sweep. A storefront's width is not authored, it is whatever facade it lands
            // on, so "no width produces NaN, an inside-out mesh or a bay of negative width" is the
            // actual acceptance criterion.
            for (float w = 0.5f; w <= 40.0f; w += 0.5f)
            {
                var pm = Build(With(Reference(), ("w", w)));
                var b = pm.mesh.bounds;
                Assert.Greater(Triangles(pm), 0, $"w={w}");
                Assert.IsFalse(float.IsNaN(b.size.x) || float.IsNaN(b.size.y) || float.IsNaN(b.size.z),
                               $"w={w} produced NaN bounds");
                Assert.AreEqual(0f, b.center.x, 0.01f, $"w={w} stayed centred on the anchor");
                Assert.Greater(b.size.x, 0f, $"w={w}");
                Assert.Less(b.size.x, w + 1.0f, $"w={w} did not overrun its facade");
            }
        }

        [Test]
        public void AbsurdBandHeightsAreAbsorbedRatherThanInverted()
        {
            // A bulkhead and transom that together exceed the opening: shrink both proportionally
            // and keep a glazing band, instead of emitting a negative-height panel.
            var squashed = Build(With(Reference(), ("h", 2.0f), ("bulkheadH", 1.6f), ("transomH", 1.2f)));
            Assert.Greater(Triangles(squashed), 0);
            Assert.AreEqual(2.0f, squashed.mesh.bounds.max.y, 0.15f, "stays inside the authored height");
            Assert.Greater(TrianglesInRole(squashed, MaterialRole.Glass), 0, "a glazing band survives");
        }

        // ---- 4. The recessed entry is a real inset, not a texture --------------------------

        [Test]
        public void TheRecessedEntryIsABoxWithSideJambsThatReachesTheWall()
        {
            var withEntry = Build(Reference());
            var without = Build(With(Reference(), ("entrySide", "None")));

            // With no entry there is nothing to project, so the storefront lies on the wall — the
            // deepest thing left is a mullion section (revealDepth + mullionD).
            Assert.Greater(without.mesh.bounds.min.z, -0.25f, "flush without an entry");
            // With one it reaches back by the recess depth — that IS the recess.
            Assert.Less(withEntry.mesh.bounds.min.z, -0.6f, "the entry sets back");

            // Jambs, soffit, floor and the box returns are all role Base — the wall's own colour, so
            // the recess reads as depth rather than as decoration.
            Assert.Greater(TrianglesInRole(withEntry, MaterialRole.Base),
                           TrianglesInRole(without, MaterialRole.Base) + 8,
                           "the recess adds real jamb geometry");
        }

        [Test]
        public void TheEntrySideMovesTheDoorwayAlongTheFacade()
        {
            // Where the doorway is shows up as the gap in the glazing plane. Measure it by the
            // x-centroid of the Base (jamb) triangles: the jambs are the recess.
            float CentroidX(PartMesh pm)
            {
                var verts = pm.mesh.vertices;
                int s = System.Array.IndexOf(pm.submeshRoles, MaterialRole.Base);
                var tris = pm.mesh.GetTriangles(s);
                float sum = 0f;
                foreach (int i in tris) sum += verts[i].x;
                return sum / tris.Length;
            }

            float left = CentroidX(Build(With(Reference(), ("entrySide", "Left"))));
            float center = CentroidX(Build(With(Reference(), ("entrySide", "Center"))));
            float right = CentroidX(Build(With(Reference(), ("entrySide", "Right"))));

            Assert.Less(left, center, "Left sits nearer -X than Center");
            Assert.Less(center, right, "Right sits nearer +X than Center");
        }

        // ---- 5. Bay rhythm ------------------------------------------------------------------

        [Test]
        public void WideNarrowWideIsSymmetricAboutACentredEntry()
        {
            // Weighting by bay index rather than by display-bay ordinal is what makes the rhythm
            // read as wide · narrow · entry · narrow · wide instead of drifting to one side.
            var rhythmic = Build(With(Reference(), ("bayCount", 5), ("bayRhythm", "WideNarrowWide")));
            var even = Build(With(Reference(), ("bayCount", 5), ("bayRhythm", "Even")));

            Assert.AreEqual(0f, rhythmic.mesh.bounds.center.x, 1e-3f);
            // Same element set, same cost class — only the bay widths differ.
            CollectionAssert.AreEqual(even.submeshRoles, rhythmic.submeshRoles);
            Assert.AreNotEqual(even.mesh.vertices.Length == rhythmic.mesh.vertices.Length &&
                               even.mesh.vertices.SequenceEqual(rhythmic.mesh.vertices), true,
                               "the rhythm has to actually move the mullions");
        }

        // ---- 6. The chamfered corner -------------------------------------------------------

        [Test]
        public void AChamferedCornerCutsTheBoxBackOnTheDiagonal()
        {
            var square = Build(With(Reference(), ("corner", "Square")));
            var chamfered = Build(With(Reference(), ("corner", "Chamfered"), ("cornerChamfer", 0.9f)));

            // The chamfer is added beyond the run, so it widens the part on exactly one side. It
            // starts at the run's edge (w/2), not at the surround's outer edge.
            Assert.AreEqual(square.mesh.bounds.min.x, chamfered.mesh.bounds.min.x, 1e-3f);
            Assert.AreEqual(3.5f + 0.9f, chamfered.mesh.bounds.max.x, 0.02f);
            Assert.Greater(chamfered.mesh.bounds.max.x, square.mesh.bounds.max.x + 0.5f);
            Assert.Greater(Triangles(chamfered), Triangles(square) - 4, "three bands in, one return out");
        }

        [Test]
        public void AChamferNeedsSomethingToChamfer()
        {
            // The chamfer cuts the corner of the *projecting* box; with no entry there is no
            // projection, so it has nothing to cut and is silently a square corner rather than a
            // diagonal plane hanging in the wall.
            var flat = Build(With(Reference(), ("entrySide", "None"), ("corner", "Chamfered")));
            var flatSquare = Build(With(Reference(), ("entrySide", "None"), ("corner", "Square")));
            CollectionAssert.AreEqual(flatSquare.mesh.vertices, flat.mesh.vertices);
        }

        // ---- 7. The awning --------------------------------------------------------------------

        [Test]
        public void AnAwningProjectsOverTheSidewalkAndOverhangsTheRun()
        {
            var bare = Build(With(Reference(), ("awningProjection", 0f)));
            var shaded = Build(With(Reference(), ("awningProjection", 0.9f), ("awningDrop", 0.45f),
                                                 ("awningValance", 0.25f), ("awningOverhang", 0.40f)));

            Assert.Greater(shaded.mesh.bounds.max.z, bare.mesh.bounds.max.z + 0.8f, "it projects");
            Assert.Greater(shaded.mesh.bounds.max.x, bare.mesh.bounds.max.x + 0.1f, "it overhangs");
            Assert.Greater(Triangles(shaded), Triangles(bare));
        }

        // ---- 8. DetailLevel degradation is local to the generator (design #452 D6) ----------

        [Test]
        public void FlatDetailIsOneRoleColouredQuad()
        {
            var pm = Build(With(Reference(), ("detail", "Flat")));
            Assert.AreEqual(2, Triangles(pm), "the safe floor is exactly today's placeholder");
            CollectionAssert.AreEqual(new[] { MaterialRole.Glass }, pm.submeshRoles);
            Assert.AreEqual(4, pm.mesh.vertexCount);
        }

        [Test]
        public void ReducedKeepsTheRecessAndTheBaysButNotTheirDetail()
        {
            var full = Build(With(Reference(), ("detail", "Full")));
            var reduced = Build(With(Reference(), ("detail", "Reduced")));

            // The recessed entry is what makes a storefront read as enterable, so it survives
            // Reduced intact; what goes is tile subdivision, transom lights and the bulkhead rail.
            CollectionAssert.AreEqual(full.submeshRoles.OrderBy(r => (int)r).ToArray(),
                                      reduced.submeshRoles.OrderBy(r => (int)r).ToArray());
            Assert.AreEqual(full.mesh.bounds.min.z, reduced.mesh.bounds.min.z, 0.01f,
                            "Reduced still reaches the wall — the recess is not an optional detail");

            Assert.Greater(Triangles(full), Triangles(reduced) * 1.5f,
                           "Reduced has to be a real saving, not a rounding difference");
            Assert.Greater(Triangles(reduced), 2, "…and still more than the Flat floor");
        }

        // ---- 9. The three neighborhood presets ---------------------------------------------

        static readonly string[] PresetIds =
        {
            "storefront_mission_tiled", "storefront_noe_corner", "storefront_soma_loft",
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
                Assert.AreEqual("Storefront", def.category, id);
                Assert.AreEqual("storefront.bay_row", def.generatorId, id);
                Assert.Greater(PartParams.From(def.parameters).Count, 15, id);
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
                keys.Add(p.KeyFor("storefront.bay_row"));
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
        public void ThePresetsReadAsThreeDifferentShopfronts()
        {
            PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

            var mission = Preset("storefront_mission_tiled");
            var noe = Preset("storefront_noe_corner");
            var soma = Preset("storefront_soma_loft");

            // Mission: the tiled bulkhead is the piece (design.md §3) — a grid-tiled Accent2 base,
            // a centre recessed entry, a transom band, and an awning over the sidewalk.
            Assert.AreEqual(MaterialRole.Accent2, mission.GetEnum("bulkheadRole", MaterialRole.Base));
            Assert.AreEqual(2, mission.GetInt("bulkheadPanelRows"), "tiled, not one flat panel");
            Assert.Greater(mission.GetFloat("bulkheadBevel"), 0f, "the tiles stand proud");
            Assert.AreEqual(EntrySide.Center, mission.GetEnum("entrySide", EntrySide.None));
            Assert.Greater(mission.GetFloat("transomH"), 0f);
            Assert.Greater(mission.GetFloat("awningProjection"), 0f, "the only preset with an awning");
            Assert.AreEqual(BayRhythm.WideNarrowWide, mission.GetEnum("bayRhythm", BayRhythm.Even));

            // Noe: the narrowest, the chamfered corner condition, painted Accent1 frame.
            Assert.Less(noe.GetFloat("w"), mission.GetFloat("w"), "narrower than Mission");
            Assert.AreEqual(CornerCondition.Chamfered, noe.GetEnum("corner", CornerCondition.Square));
            Assert.AreEqual(MaterialRole.Accent1, noe.GetEnum("frameRole", MaterialRole.Base));
            Assert.AreEqual(EntrySide.Right, noe.GetEnum("entrySide", EntrySide.None),
                            "the entry sits against the chamfered corner");

            // SoMa: the widest, steel mullions, minimal bulkhead, Metal throughout.
            Assert.Greater(soma.GetFloat("w"), mission.GetFloat("w"), "wider than Mission");
            Assert.AreEqual(MaterialRole.Metal, soma.GetEnum("frameRole", MaterialRole.Base));
            Assert.Less(soma.GetFloat("bulkheadH"), noe.GetFloat("bulkheadH") * 0.6f, "minimal bulkhead");
            Assert.Greater(soma.GetFloat("mullionD"), mission.GetFloat("mullionD"), "deep steel sections");
            Assert.AreEqual(1, soma.GetInt("bulkheadPanelCols"), "one flat panel, no tiling");
        }

        [Test]
        public void EveryPresetGeneratesAtEveryDetailLevel()
        {
            // The per-preset triangle table #456 wants. Asserted only as an ordering — the absolute
            // numbers are the measurement, and they are in the PR body.
            foreach (string id in PresetIds)
            {
                var basis = PartParams.From(LoadPreset(id).parameters);
                int full = Triangles(Build(With(basis, ("detail", "Full"))));
                int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
                int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

                Debug.Log($"[#472] {id}: Full={full} Reduced={reduced} Flat={flat}");

                Assert.Greater(full, reduced, id);
                Assert.Greater(reduced, flat, id);
                Assert.AreEqual(2, flat, id);
            }
        }

        [Test]
        public void EveryPresetSurvivesBeingStretchedToAnyRealFacade()
        {
            // The presets author a nominal width, but the integration issue will stretch them onto
            // whatever facade the building has. Each has to hold up across the whole range.
            foreach (string id in PresetIds)
            {
                var basis = PartParams.From(LoadPreset(id).parameters);
                foreach (float w in new[] { 3.0f, 4.0f, 6.0f, 12.0f, 20.0f, 30.0f })
                {
                    var pm = Build(With(basis, ("w", w)));
                    Assert.Greater(Triangles(pm), 0, $"{id} @ {w}m");
                    Assert.AreEqual(0f, pm.mesh.bounds.center.x, 1.0f, $"{id} @ {w}m drifted off-anchor");
                    Assert.Less(pm.mesh.bounds.size.x, w + 2.0f, $"{id} @ {w}m overran its facade");
                }
            }
        }

        [Test]
        public void OnePresetPlacedManyTimesCollapsesToOneMesh()
        {
            // The cache property from the assembler's point of view (#453 acceptance 4). It matters
            // more here than for a window: a storefront mesh is an order of magnitude bigger, and
            // the seeded jitter would otherwise make every frontage its own.
            var cache = new PartMeshCache();
            var rng = new System.Random(472);
            var blocks = PresetIds.Select(id => PartParams.From(LoadPreset(id).parameters)).ToArray();

            for (int i = 0; i < 120; i++)
            {
                var basis = blocks[i % blocks.Length];
                float j = (float)(rng.NextDouble() - 0.5) * 0.002f;   // millimetres
                var jittered = With(basis, ("w", basis.GetFloat("w") + j), ("h", basis.GetFloat("h") + j));
                cache.GetOrCreate(jittered.KeyFor(_gen.Id), mb => _gen.Generate(jittered, mb));
            }

            Assert.AreEqual(PresetIds.Length, cache.Generated);
            Assert.AreEqual(120 - PresetIds.Length, cache.Hits);
            cache.Clear();
        }
    }
}
