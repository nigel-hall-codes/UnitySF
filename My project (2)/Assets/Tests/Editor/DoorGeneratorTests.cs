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
    /// The properties the door family has to hold (#470, design #452 §2 "Door" row). The family's
    /// claim is that a door is a window with solid raised-panel infill and a wide bottom rail, so
    /// these tests are mostly about the two things that make it a door — the rail is real geometry
    /// that lifts the panel field, and <c>glazedFraction</c> moves the leaf continuously between
    /// solid and glazed — plus the same <see cref="DetailLevel"/> and preset discipline #457
    /// established for windows.
    /// <para>Triangle figures are <i>measured</i> and logged rather than asserted exactly; the
    /// assertions are orderings and orders of magnitude, because the absolute numbers are the
    /// measurement #456 wants.</para>
    /// </summary>
    public class DoorGeneratorTests
    {
        readonly List<Object> _spawned = new List<Object>();
        readonly PanelDoorGenerator _gen = new PanelDoorGenerator();

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

        static bool HasRole(PartMesh pm, MaterialRole r) => pm.submeshRoles.Contains(r);

        /// <summary>Vertices sitting on the leaf's infill plane below <paramref name="belowY"/> —
        /// i.e. panel or glass geometry where the bottom rail is supposed to be solid.</summary>
        static int InfillVertsBelow(PartMesh pm, float belowY, float infillZ)
            => pm.mesh.vertices.Count(v => v.y < belowY - 1e-3f && Mathf.Abs(v.z - infillZ) < 1e-3f);

        /// <summary>A single-leaf panelled door with a transom, a surround, a threshold and a knob —
        /// the configuration the Noe preset is a dressed-up version of.</summary>
        static PartParams Reference() => Bag(
            ("w", 1.05f), ("h", 2.35f), ("revealDepth", 0.16f), ("infillInset", 0.035f),
            ("leafCount", 1), ("panelCols", 1), ("panelRows", 3),
            ("glazedFraction", 0.34f), ("panelBevel", 0.022f),
            ("railW", 0.26f), ("stileW", 0.075f), ("barW", 0.06f), ("barD", 0.035f), ("frameD", 0.05f),
            ("transomH", 0.34f), ("transomCols", 1),
            ("surroundProfile", "Ogee"), ("surroundW", 0.12f), ("surroundD", 0.045f),
            ("thresholdProfile", "Bullnose"), ("thresholdH", 0.08f),
            ("thresholdProjection", 0.12f), ("thresholdOverhang", 0.06f),
            ("hardware", 0.14f), ("frameRole", "Accent1"), ("panelRole", "Accent1"));

        // ---- 1. The generator is reachable exactly as a part file names it ----------------

        [Test]
        public void TheGeneratorIsDiscoveredUnderTheIdThePartFilesUse()
        {
            Assert.IsTrue(PartGenerators.TryResolve("door.panel", out var g));
            Assert.IsInstanceOf<PanelDoorGenerator>(g);
        }

        // ---- 2. The bottom rail is what makes a door a door -------------------------------

        [Test]
        public void TheWideBottomRailIsSolidGeometryAndLiftsThePanelField()
        {
            var basis = With(Reference(), ("transomH", 0f), ("surroundProfile", "None"),
                                          ("thresholdH", 0f), ("hardware", 0f));
            float infillZ = -(0.16f + 0.035f);          // revealDepth + infillInset

            var railed = Build(With(basis, ("railW", 0.26f)));
            var railless = Build(With(basis, ("railW", 0f)));

            // With a rail, nothing glazed or panelled reaches below it; without one, the field runs
            // to the floor. That is the whole visual difference between a door and a tall window.
            Assert.AreEqual(0, InfillVertsBelow(railed, 0.26f, infillZ),
                            "panel/glass geometry intrudes into the bottom rail");
            Assert.Greater(InfillVertsBelow(railless, 0.26f, infillZ), 0,
                           "without a rail the field should reach the threshold");

            // The rail itself is one flush-mounted Box: six faces less the back = 10 triangles.
            Assert.AreEqual(10, Triangles(railed) - Triangles(railless));
        }

        [Test]
        public void TheRailNeverEatsMoreThanHalfTheLeaf()
        {
            // A nonsense authoring (railW > h) must clamp, not produce an inverted field.
            var pm = Build(With(Reference(), ("railW", 9f)));
            Assert.Greater(Triangles(pm), 2);
            Assert.Less(pm.mesh.bounds.min.y, 0.001f);
        }

        // ---- 3. glazedFraction moves the leaf from solid to glazed ------------------------

        [Test]
        public void GlazedFractionZeroIsASolidDoorAndOneIsAFullyGlazedOne()
        {
            var basis = With(Reference(), ("transomH", 0f), ("panelRole", "Accent1"));

            var solid = Build(With(basis, ("glazedFraction", 0f)));
            var half = Build(With(basis, ("glazedFraction", 0.5f)));
            var glazed = Build(With(basis, ("glazedFraction", 1f)));

            Assert.IsFalse(HasRole(solid, MaterialRole.Glass), "a solid door has no glass at all");
            Assert.IsTrue(HasRole(half, MaterialRole.Glass));
            Assert.IsTrue(HasRole(glazed, MaterialRole.Glass));

            // Fully glazed drops the panelled grid entirely, so the painted leaf no longer carries
            // an infill of its own — one grid instead of two.
            Assert.Less(Triangles(glazed), Triangles(half));
        }

        [Test]
        public void AFractionalGlazingPutsTheGlassAboveTheSolidPanels()
        {
            var pm = Build(With(Reference(), ("transomH", 0f), ("glazedFraction", 0.34f),
                                             ("panelRole", "Base")));
            var mesh = pm.mesh;

            // Role → the y range its triangles occupy. Glass must sit strictly above the panels.
            float glassMinY = float.MaxValue, panelMaxY = float.MinValue;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                foreach (int i in tris)
                {
                    float y = mesh.vertices[i].y;
                    if (pm.submeshRoles[s] == MaterialRole.Glass) glassMinY = Mathf.Min(glassMinY, y);
                }
                if (pm.submeshRoles[s] != MaterialRole.Base) continue;
            }
            // The panels are Base here, but so is the door's own reveal (which spans the whole
            // opening), so compare against the raised-panel field instead: its front face is the
            // only Base geometry standing proud of the infill plane.
            float fieldZ = -(0.16f + 0.035f) + 0.022f;
            foreach (var v in mesh.vertices)
                if (Mathf.Abs(v.z - fieldZ) < 1e-3f) panelMaxY = Mathf.Max(panelMaxY, v.y);

            Assert.AreNotEqual(float.MaxValue, glassMinY, "expected glazing");
            Assert.AreNotEqual(float.MinValue, panelMaxY, "expected raised panels");
            Assert.Greater(glassMinY, panelMaxY - 1e-3f, "the light must be the upper part of the leaf");
        }

        // ---- 4. Two leaves are one grid plus a meeting stile ------------------------------

        [Test]
        public void ADoubleLeafDividesTheOpeningAndCarriesAMeetingStile()
        {
            var basis = With(Reference(), ("transomH", 0f), ("panelCols", 1), ("hardware", 0.4f));
            var single = Build(With(basis, ("leafCount", 1)));
            var pair = Build(With(basis, ("leafCount", 2)));

            // The pair adds: a vertical division per grid (2 grids × 10 tris), the meeting stile
            // (10), and a second piece of hardware (10).
            Assert.Greater(Triangles(pair), Triangles(single));
            Assert.IsTrue(HasRole(pair, MaterialRole.Metal), "both leaves get a pull");

            // The meeting line is the opening's centre, so the stile straddles x = 0.
            int onCentre = pair.mesh.vertices.Count(v => Mathf.Abs(Mathf.Abs(v.x) - 0.075f) < 1e-3f);
            Assert.Greater(onCentre, 0, "no geometry a stile's width either side of centre");
        }

        // ---- 5. DetailLevel degrades locally (design #452 D6) -----------------------------

        [Test]
        public void FlatDetailIsOneRoleColouredQuad()
        {
            var pm = Build(With(Reference(), ("detail", "Flat")));
            Assert.AreEqual(2, Triangles(pm), "the safe floor is exactly today's placeholder");
            Assert.AreEqual(4, pm.mesh.vertexCount);
            Assert.AreEqual(1, pm.submeshRoles.Length);
        }

        [Test]
        public void TheFlatQuadTakesTheRoleThatDominatesTheDoor()
        {
            // A door is mostly solid, so its floor is the paint, not the glass — unlike a window.
            var panelled = Build(With(Reference(), ("detail", "Flat"), ("glazedFraction", 0.2f)));
            var shopDoor = Build(With(Reference(), ("detail", "Flat"), ("glazedFraction", 0.9f)));

            CollectionAssert.AreEqual(new[] { MaterialRole.Accent1 }, panelled.submeshRoles);
            CollectionAssert.AreEqual(new[] { MaterialRole.Glass }, shopDoor.submeshRoles);
        }

        [Test]
        public void FullAndReducedCarryTheSameElementSetAtDifferentCost()
        {
            var full = Build(With(Reference(), ("detail", "Full")));
            var reduced = Build(With(Reference(), ("detail", "Reduced")));

            CollectionAssert.AreEqual(full.submeshRoles.OrderBy(r => (int)r).ToArray(),
                                      reduced.submeshRoles.OrderBy(r => (int)r).ToArray(),
                                      "Reduced cheapens the elements, it does not drop them");
            Assert.Greater(Triangles(full), Triangles(reduced) * 1.25f,
                           "Reduced has to be a real saving, not a rounding difference");
            Assert.Greater(Triangles(reduced), 2, "…and still more than the Flat floor");
        }

        [Test]
        public void ReducedFlattensTheRaisedPanelsAndHalvesTheSlatCount()
        {
            // The dock door is where the panel field is the whole budget: 9 slats × 2 leaves at 10
            // triangles a raised cell. Reduced halves the rows *and* drops the relief.
            var dock = Bag(("w", 3.2f), ("h", 3.4f), ("leafCount", 2),
                           ("panelCols", 1), ("panelRows", 9), ("panelBevel", 0.012f),
                           ("glazedFraction", 0f), ("railW", 0.16f), ("stileW", 0.06f),
                           ("surroundProfile", "None"), ("frameRole", "Metal"), ("panelRole", "Metal"));

            int full = Triangles(Build(With(dock, ("detail", "Full"))));
            int reduced = Triangles(Build(With(dock, ("detail", "Reduced"))));
            Assert.Greater(full, reduced * 2f, "18 raised slats → 10 flat ones");
        }

        [Test]
        public void ReducingDetailNeverMakesADoorMoreExpensive()
        {
            // The regression guard on Sectioned(). A door whose only mouldings are 2-point Flat
            // bands — the SoMa steel threshold and header — has nothing a 3-point Chamfer could
            // make cheaper, and substituting one anyway costs triangles instead of saving them.
            var flatBanded = Bag(("w", 1.0f), ("h", 2.0f), ("railW", 0.20f), ("stileW", 0.05f),
                                 ("panelCols", 1), ("panelRows", 1), ("glazedFraction", 0f),
                                 ("panelBevel", 0f), ("surroundProfile", "None"),
                                 ("thresholdProfile", "Flat"), ("thresholdH", 0.05f),
                                 ("headerProfile", "Flat"), ("headerH", 0.10f));

            int full = Triangles(Build(With(flatBanded, ("detail", "Full"))));
            int reduced = Triangles(Build(With(flatBanded, ("detail", "Reduced"))));
            Assert.GreaterOrEqual(full, reduced, "Reduced must never cost more than Full");
        }

        [Test]
        public void EachDetailLevelLandsInTheRightOrderOfMagnitude()
        {
            var basis = Reference();
            int full = Triangles(Build(With(basis, ("detail", "Full"))));
            int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
            int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

            Debug.Log($"[#470] reference panelled door triangles: Full={full} Reduced={reduced} Flat={flat}");

            Assert.IsTrue(full >= 80 && full <= 600, $"Full was {full}");
            Assert.IsTrue(reduced >= 20 && reduced <= 300, $"Reduced was {reduced}");
            Assert.AreEqual(2, flat);
        }

        // ---- 6. The door is built in the frame the assembler places it in -----------------

        [Test]
        public void GeometryIsAuthoredInThePartLocalFrameThePlacerExpects()
        {
            var pm = Build(Reference());
            var b = pm.mesh.bounds;

            Assert.AreEqual(0f, b.center.x, 1e-4f, "centred on the anchor");
            Assert.Less(b.min.y, 0.001f, "the threshold hangs below the opening");
            Assert.Greater(b.max.y, 2.35f, "the surround stands above it");
            Assert.Less(b.min.z, -0.16f, "the reveal and leaf set back");
            Assert.Greater(b.max.z, 0.02f, "the surround/threshold stand proud (+Z is toward the street)");
        }

        // ---- 7. The three neighborhood presets --------------------------------------------

        static readonly string[] PresetIds =
        {
            "door_noe_victorian", "door_sunset_flush", "door_soma_loading",
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
                Assert.AreEqual("Door", def.category, id);
                Assert.AreEqual("door.panel", def.generatorId, id);
                Assert.Greater(PartParams.From(def.parameters).Count, 10, id);

                // The mass has no hole cut in it: the assembly must stand proud by at least the
                // depth it recesses, or the leaf is swallowed by the wall.
                var p = PartParams.From(def.parameters);
                Assert.GreaterOrEqual(def.mountDepth_m + 1e-4f,
                                      p.GetFloat("revealDepth") + p.GetFloat("infillInset"), id);
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
                keys.Add(p.KeyFor("door.panel"));
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
        public void ThePresetsReadAsThreeDifferentDoors()
        {
            PartParams Preset(string id) => PartParams.From(LoadPreset(id).parameters);

            var noe = Preset("door_noe_victorian");
            var sunset = Preset("door_sunset_flush");
            var soma = Preset("door_soma_loading");

            // Victorian entry: single leaf, two raised panels under a glazed upper third, a transom
            // light over it, an Ogee surround, painted, deep-set.
            Assert.AreEqual(1, noe.GetInt("leafCount"));
            Assert.AreEqual(3, noe.GetInt("panelRows"));
            Assert.AreEqual(1, Mathf.RoundToInt(noe.GetInt("panelRows") * noe.GetFloat("glazedFraction")),
                            "glazed upper third");
            Assert.Greater(noe.GetFloat("panelBevel"), 0f, "raised panels");
            Assert.Greater(noe.GetFloat("transomH"), 0f, "fixed light over the door");
            Assert.AreEqual(ProfileId.Ogee, noe.GetEnum("surroundProfile", ProfileId.None));
            Assert.AreEqual(MaterialRole.Accent1, noe.GetEnum("frameRole", MaterialRole.Base));

            // Post-war Sunset: flush leaf (no relief at all), a small light, a Metal pull, shallow.
            Assert.AreEqual(0f, sunset.GetFloat("panelBevel"), "flush — no raised panels");
            Assert.AreEqual(ProfileId.None, sunset.GetEnum("surroundProfile", ProfileId.Ogee));
            Assert.Greater(sunset.GetFloat("hardware"), 0f, "Metal hardware is the Sunset signature");
            Assert.Less(sunset.GetFloat("glazedFraction"), 0.4f, "a small light, not a half-glazed door");
            Assert.Less(sunset.GetFloat("revealDepth"), noe.GetFloat("revealDepth"), "shallower than Noe");

            // SoMa loading dock: wide double leaf, horizontal slats, steel, header, no surround.
            Assert.AreEqual(2, soma.GetInt("leafCount"));
            Assert.AreEqual(1, soma.GetInt("panelCols"));
            Assert.GreaterOrEqual(soma.GetInt("panelRows"), 8, "a horizontal slat grid");
            Assert.AreEqual(0f, soma.GetFloat("glazedFraction"), "a dock door has no glass");
            Assert.AreEqual(ProfileId.None, soma.GetEnum("surroundProfile", ProfileId.Ogee));
            Assert.AreNotEqual(ProfileId.None, soma.GetEnum("headerProfile", ProfileId.None), "steel header");
            Assert.AreEqual(MaterialRole.Metal, soma.GetEnum("frameRole", MaterialRole.Base));
            Assert.Greater(soma.GetFloat("w"), 2f * noe.GetFloat("w"), "wide enough for a truck");
        }

        [Test]
        public void EveryPresetGeneratesAtEveryDetailLevel()
        {
            foreach (string id in PresetIds)
            {
                var basis = PartParams.From(LoadPreset(id).parameters);
                int full = Triangles(Build(With(basis, ("detail", "Full"))));
                int reduced = Triangles(Build(With(basis, ("detail", "Reduced"))));
                int flat = Triangles(Build(With(basis, ("detail", "Flat"))));

                Debug.Log($"[#470] {id}: Full={full} Reduced={reduced} Flat={flat}");

                // GreaterOrEqual, not Greater, and the reason is worth stating: door_sunset_flush is
                // a deliberately minimal door — one flush panel, one light, a 2-point steel
                // threshold, no surround, no transom, no raised relief. There is nothing left for
                // Reduced to halve or flatten, so it measures identical to Full. That is the correct
                // answer, and the guard that matters is that it is never *larger* (see Sectioned).
                Assert.GreaterOrEqual(full, reduced, id);
                Assert.Greater(reduced, flat, id);
                Assert.AreEqual(2, flat, id);
            }
        }

        [Test]
        public void OnePresetPlacedManyTimesCollapsesToOneMesh()
        {
            var cache = new PartMeshCache();
            var rng = new System.Random(470);
            var blocks = PresetIds.Select(id => PartParams.From(LoadPreset(id).parameters)).ToArray();

            for (int i = 0; i < 150; i++)
            {
                var basis = blocks[i % blocks.Length];
                float j = (float)(rng.NextDouble() - 0.5) * 0.002f;   // millimetres of seeded jitter
                var jittered = With(basis, ("w", basis.GetFloat("w") + j), ("h", basis.GetFloat("h") + j));
                cache.GetOrCreate(jittered.KeyFor(_gen.Id), mb => _gen.Generate(jittered, mb));
            }

            Assert.AreEqual(PresetIds.Length, cache.Generated);
            Assert.AreEqual(150 - PresetIds.Length, cache.Hits);
            cache.Clear();
        }
    }
}
