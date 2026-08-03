using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;

namespace SFMap.Tests
{
    /// <summary>
    /// The wiring itself (#475): which family preset each neighborhood template reaches, and the
    /// arithmetic that keeps ground-floor artifacts off each other.
    ///
    /// <para>These are assertions about <b>authored data</b>, not about geometry — the generator
    /// families each have their own suite. What they guard is the one thing no single family issue
    /// could: that door, garage, storefront, bay and cornice coexist on the same building without
    /// stacking, given the placement schema as it actually is.</para>
    ///
    /// <para><b>The rule that shapes all of this.</b> <c>_exactMarks</c> is written by
    /// <c>PlaceExact</c> and read by <c>PlaceProcedural</c> when a rule sets <c>avoidExact</c>.
    /// The exclusion is therefore <i>exact → procedural only</i>: two procedural rules are blind to
    /// each other, and <c>RuleJson</c> has no "mutually exclusive with rule N" field. So a template
    /// may hold any number of <c>exact</c> placements on one floor (the author separates their
    /// <c>x</c> by hand) plus <b>at most one</b> rule that dodges them. Every mutually-exclusive
    /// ground-floor artifact — door, garage, storefront — is consequently authored as an
    /// <c>exact</c>, and where two of them cannot fit at all (a shopfront vs a garage) the split
    /// happens one level up, at template selection, via <c>districtTemplateWeights</c>. Lifting the
    /// limitation is #491.</para>
    /// </summary>
    public class TemplateWiringTests
    {
        // ---- fixtures ---------------------------------------------------------------------

        const string LibraryDirName = "SFBuildingTemplates";

        static string LibraryRoot => Path.Combine(Application.dataPath, LibraryDirName);
        static string TemplatesDir => Path.Combine(LibraryRoot, "Templates");
        static string PartsDir => Path.Combine(LibraryRoot, "Parts");

        static string RepoRoot =>
            Directory.GetParent(Path.GetFullPath(Application.dataPath)).Parent.FullName;

        /// <summary>The bundled MVP sample. Its <c>compatibility.neighborhoods</c> is empty, which
        /// means it admits every building in the city; it is excluded from the neighborhood
        /// bookkeeping below rather than being treated as a neighborhood template.</summary>
        const string SampleTemplateId = "trivial_window";

        /// <summary>Presets that no template reaches yet, and why. Asserted as an exact set (not a
        /// skip list) so that standing up a Mission or North Beach template — or deleting either
        /// preset — fails here and forces this note to be updated.
        /// <para><c>storefront_mission_tiled</c> and <c>bay_northbeach_squared</c> are the Mission
        /// and North Beach signature pieces from design #452 §3. Neither neighborhood has a
        /// template at all, and neither is inside the play-area <c>map.osm</c> bbox, so wiring them
        /// would be unverifiable and would also need a residential companion template plus weights
        /// to stop 100% of the neighborhood becoming commercial. Tracked as #492.</para>
        /// <para>The three <c>stoop_*</c> presets (#495) are unwired <b>by instruction</b>: a stoop
        /// is a floor-0, front-facade, projecting artifact that must line up with the door, so it
        /// lands in exactly the ground-floor contention #475 resolved, and #495 says to ship the
        /// generator and presets only and let a wiring pass apply the rule — the same split the five
        /// families before it took. The rule shape is an <c>exact</c> placement at the door's own
        /// <c>nx</c>, floor 0, <c>facade: "Front"</c>; wiring it also has to raise that door by
        /// <c>stepCount·rise</c>, which nothing expresses yet (#487).</para></summary>
        static readonly string[] PresetsAwaitingANeighborhoodTemplate =
        {
            "bay_northbeach_squared", "stoop_glenpark_short", "stoop_noe_victorian",
            "stoop_soma_flush", "storefront_mission_tiled",
        };

        /// <summary>One generator per family (#457, #470–#474). Every one must be reachable from
        /// some template, or that family renders on nothing.</summary>
        static readonly string[] FamilyGenerators =
        {
            "window.double_hung", "door.panel", "garage.sectional",
            "storefront.bay_row", "bay.projecting", "cornice.banded",
        };

        static Dictionary<string, TemplateDefJson> _templates;
        static Dictionary<string, PartDefJson> _parts;
        static LibraryManifestJson _manifest;
        static string _geojson;

        [OneTimeSetUp]
        public void LoadLibrary()
        {
            Assert.IsTrue(Directory.Exists(TemplatesDir), $"missing {TemplatesDir}");
            Assert.IsTrue(Directory.Exists(PartsDir), $"missing {PartsDir}");

            _templates = new Dictionary<string, TemplateDefJson>(StringComparer.Ordinal);
            foreach (string file in Json(TemplatesDir, "*.template.json"))
            {
                var def = JsonUtility.FromJson<TemplateDefJson>(File.ReadAllText(file));
                Assert.IsNotNull(def, file);
                Assert.IsFalse(string.IsNullOrEmpty(def.id), $"{file} has no id");
                Assert.AreEqual(def.id + ".template.json", Path.GetFileName(file),
                                "template id must match its file name");
                _templates[def.id] = def;
            }

            _parts = new Dictionary<string, PartDefJson>(StringComparer.Ordinal);
            foreach (string file in Json(PartsDir, "*.part.json"))
            {
                var def = JsonUtility.FromJson<PartDefJson>(File.ReadAllText(file));
                Assert.IsNotNull(def, file);
                _parts[def.id] = def;
            }

            string manifest = Path.Combine(LibraryRoot, "library.json");
            Assert.IsTrue(File.Exists(manifest), $"missing {manifest}");
            _manifest = JsonUtility.FromJson<LibraryManifestJson>(File.ReadAllText(manifest));
            Assert.IsNotNull(_manifest);
        }

        // Directory.GetFiles' legacy short-name matching lets "*.part.json" also match
        // "*.part.json.meta" — same filter the importer applies.
        static IEnumerable<string> Json(string dir, string pattern)
        {
            string suffix = pattern.Substring(1);
            return Directory.GetFiles(dir, pattern)
                            .Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => f, StringComparer.Ordinal);
        }

        static IEnumerable<TemplateDefJson> Neighborhood =>
            _templates.Values.Where(t => t.id != SampleTemplateId);

        static IEnumerable<string> PartIdsIn(TemplateDefJson t)
        {
            foreach (var e in t.exact ?? Array.Empty<ExactJson>())
                if (!string.IsNullOrEmpty(e.part)) yield return e.part;
            foreach (var r in t.rules ?? Array.Empty<RuleJson>())
            {
                if (!string.IsNullOrEmpty(r.part)) yield return r.part;
                foreach (string v in r.variants ?? Array.Empty<string>())
                    if (!string.IsNullOrEmpty(v)) yield return v;
            }
            foreach (string p in t.roofParts ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(p)) yield return p;
        }

        static PartDefJson Part(string id)
        {
            Assert.IsTrue(_parts.ContainsKey(id), $"no Parts/{id}.part.json");
            return _parts[id];
        }

        // Half the part's real width, which is what decides whether two placements on one facade
        // touch. size_m.w is the generator's own measured extent (the family suites assert it).
        static float HalfWidth(string partId) => Part(partId).size_m.w * 0.5f;

        // ---- 1. everything the templates name exists, and every family is reachable ---------

        [Test]
        public void EveryPartATemplateNamesExists()
        {
            foreach (var t in _templates.Values)
                foreach (string id in PartIdsIn(t))
                    Assert.IsTrue(_parts.ContainsKey(id), $"template '{t.id}' names unknown part '{id}'");
        }

        [Test]
        public void EveryFamilyIsReachableFromSomeTemplate()
        {
            var reached = _templates.Values
                .SelectMany(PartIdsIn)
                .Select(id => Part(id).generatorId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string generator in FamilyGenerators)
                Assert.IsTrue(reached.Contains(generator),
                              $"no template places any part built by '{generator}' — that family " +
                              "renders on nothing");
        }

        [Test]
        public void TheOnlyUnwiredPresetsAreTheOnesWithNoNeighborhoodTemplate()
        {
            var reached = _templates.Values.SelectMany(PartIdsIn).ToHashSet(StringComparer.Ordinal);
            var unwired = _parts.Keys.Where(id => !reached.Contains(id))
                                     .OrderBy(id => id, StringComparer.Ordinal)
                                     .ToArray();

            CollectionAssert.AreEqual(PresetsAwaitingANeighborhoodTemplate, unwired,
                "the set of presets no template reaches has changed; update " +
                nameof(PresetsAwaitingANeighborhoodTemplate) + " and say why");
        }

        // ---- 2. the cornice is a roofPart, not a rule (#474) --------------------------------

        [Test]
        public void TheCorniceIsOnlyEverAroofPart()
        {
            // PlaceRoofParts is the ONLY caller of TryPlaceWrapped, and TryPlaceWrapped is the only
            // path that sweeps an IPathPartGenerator along the chained corner run. A cornice reached
            // through a ProceduralRule or an Exact silently falls back to per-facade placement and
            // loses the corner wrap — the entire point of the family. So its reachability has to be
            // roofParts-only, not merely roofParts-also.
            foreach (var t in _templates.Values)
            {
                foreach (var e in t.exact ?? Array.Empty<ExactJson>())
                    Assert.AreNotEqual("cornice.banded", Part(e.part).generatorId,
                        $"template '{t.id}' places cornice '{e.part}' as an exact; it must be a roofPart");

                foreach (var r in t.rules ?? Array.Empty<RuleJson>())
                    foreach (string id in new[] { r.part }.Concat(r.variants ?? Array.Empty<string>()))
                        if (!string.IsNullOrEmpty(id))
                            Assert.AreNotEqual("cornice.banded", Part(id).generatorId,
                                $"template '{t.id}' places cornice '{id}' as a rule; it must be a roofPart");
            }

            var roofed = _templates.Values.SelectMany(t => t.roofParts ?? Array.Empty<string>()).ToArray();
            Assert.IsNotEmpty(roofed, "no template carries a roofPart, so the wrapped-run path never runs");
            foreach (string id in roofed)
                Assert.AreEqual("cornice.banded", Part(id).generatorId,
                                $"roofPart '{id}' is not a cornice");
        }

        // ---- 3. the ground floor: at most one rule, and exacts that clear each other --------

        [Test]
        public void AtMostOneRulePerTemplateClaimsAGivenFloor()
        {
            // Two rules on the same facade and floor cannot avoid each other — avoidExact reads
            // _exactMarks, which only Exact placements write. Anything beyond one rule per floor is
            // an authored guarantee of interpenetration (#491).
            foreach (var t in Neighborhood)
            {
                for (int floor = 0; floor <= 8; floor++)
                {
                    var claimants = (t.rules ?? Array.Empty<RuleJson>())
                        .Where(r => r.floorRange.min <= floor && floor <= r.floorRange.max)
                        .Where(r => r.facade == "Street" || r.facade == "Front")
                        .ToArray();
                    Assert.LessOrEqual(claimants.Length, 1,
                        $"template '{t.id}' has {claimants.Length} rules on floor {floor} of the " +
                        "front/street facade; procedural rules cannot exclude one another");
                }
            }
        }

        [Test]
        public void GroundFloorExactsClearEachOtherOnARealFacade()
        {
            // Exact placements scale with the facade: nx is normalized, the part's width is not.
            // Two of them touch when |dnx| · facadeLength < halfA + halfB, so each pair implies a
            // minimum front-facade width. wintest (Glen Park, 990 measured front facades) runs
            // median 6.8 m, p25 4.2 m, p10 2.8 m; 8 m is the bar every template must clear, and the
            // per-template number is reported so a preset resize shows up as a changed threshold.
            const float Bar = 8.0f;

            foreach (var t in Neighborhood)
            {
                var ground = (t.exact ?? Array.Empty<ExactJson>())
                    .Where(e => e.floor == 0 && (e.facade == "Front" || e.facade == "Street"))
                    .ToArray();

                float worst = 0f;
                string worstPair = "none";
                for (int i = 0; i < ground.Length; i++)
                    for (int j = i + 1; j < ground.Length; j++)
                    {
                        float dnx = Mathf.Abs(ground[i].x - ground[j].x);
                        Assert.Greater(dnx, 0.001f,
                            $"template '{t.id}': '{ground[i].part}' and '{ground[j].part}' share x");
                        float needed = (HalfWidth(ground[i].part) + HalfWidth(ground[j].part)) / dnx;
                        if (needed > worst) { worst = needed; worstPair = $"{ground[i].part}+{ground[j].part}"; }
                    }

                Debug.Log($"[#475] {t.id}: ground-floor exacts clear on a facade wider than " +
                          $"{worst:F2} m ({worstPair})");
                Assert.LessOrEqual(worst, Bar,
                    $"template '{t.id}' needs a {worst:F2} m front facade before its ground-floor " +
                    $"exacts stop overlapping ({worstPair})");
            }
        }

        [Test]
        public void AGroundFloorRuleIsWidthGatedSoItCannotLandOnAnExact()
        {
            // The mechanism that resolves door-vs-garage, and the reason the garage is a rule while
            // the door is an exact: a rule can be gated on facade width and an exact cannot.
            //
            // PlaceProcedural computes count = floor(spanMeters / spacing) BEFORE applying countMin,
            // so countMin 0 with a deliberately large spacingMeters withholds the artifact entirely
            // on a facade narrower than spacing / spanFraction; countMax 1 caps it above that. Then
            // avoidExact withholds the surviving slot whenever it lands within minSpacingMeters of
            // an exact. What this test asserts is that the wider of those two gates opens only after
            // the width at which the two parts would physically touch — so the pair cannot overlap
            // at ANY facade width, rather than merely on typical ones.
            foreach (var t in Neighborhood)
            {
                var groundExacts = (t.exact ?? Array.Empty<ExactJson>())
                    .Where(e => e.floor == 0 && (e.facade == "Front" || e.facade == "Street")).ToArray();

                foreach (var r in (t.rules ?? Array.Empty<RuleJson>())
                             .Where(r => r.floorRange.min <= 0 && groundExacts.Length > 0))
                {
                    Assert.AreEqual(0, r.repeat.countMin,
                        $"template '{t.id}': rule '{r.part}' shares floor 0 with an exact but has " +
                        "countMin > 0, which forces a placement below the width gate");
                    Assert.AreEqual(1, r.repeat.countMax,
                        $"template '{t.id}': rule '{r.part}' on floor 0 must be capped at one slot");
                    Assert.AreEqual(0f, r.jitter.x,
                        $"template '{t.id}': rule '{r.part}' jitters along floor 0, so its slot is " +
                        "no longer where the clearance arithmetic says it is");
                    Assert.IsTrue(r.constraints.alignToFloorLine,
                        $"template '{t.id}': rule '{r.part}' sits mid-floor rather than on the " +
                        "floor line; a ground-floor opening meets the pavement");

                    float x0 = Mathf.Clamp01(r.span[0] + r.constraints.edgeMargin);
                    float x1 = Mathf.Clamp01(r.span[1] - r.constraints.edgeMargin);
                    Assert.Greater(x1, x0, $"template '{t.id}': rule '{r.part}' has an empty span");

                    float nxRule = x0 + 0.5f * (x1 - x0);   // count == 1 puts the slot at t = 0.5
                    float spacing = Mathf.Max(r.repeat.spacingMeters,
                                              Mathf.Max(r.constraints.minSpacingMeters, 0.1f));
                    float countGate = spacing / (x1 - x0);
                    float halfRule = HalfWidth(r.part);

                    foreach (var e in groundExacts)
                    {
                        float d = Mathf.Abs(nxRule - e.x);
                        Assert.Greater(d, 0.001f, $"template '{t.id}': '{r.part}' lands on '{e.part}'");

                        float exclusionGate = Mathf.Max(r.constraints.minSpacingMeters, spacing * 0.5f) / d;
                        float touchesBelow = (halfRule + HalfWidth(e.part)) / d;
                        float opensAt = Mathf.Max(countGate, exclusionGate);

                        Debug.Log($"[#475] {t.id}: '{r.part}' first placed on a {opensAt:F2} m facade " +
                                  $"(count gate {countGate:F2} m, avoidExact gate {exclusionGate:F2} m); " +
                                  $"it would touch '{e.part}' below {touchesBelow:F2} m");
                        Assert.Greater(opensAt, touchesBelow,
                            $"template '{t.id}': rule '{r.part}' can be placed on a facade as narrow " +
                            $"as {opensAt:F2} m, but overlaps exact '{e.part}' up to {touchesBelow:F2} m");
                    }
                }
            }
        }

        [Test]
        public void EveryGroundFloorRuleAvoidsTheExactsBesideIt()
        {
            foreach (var t in Neighborhood)
            {
                bool hasGroundExact = (t.exact ?? Array.Empty<ExactJson>()).Any(e => e.floor == 0);
                if (!hasGroundExact) continue;

                foreach (var r in (t.rules ?? Array.Empty<RuleJson>()).Where(r => r.floorRange.min <= 0))
                    Assert.IsTrue(r.constraints.avoidExact,
                        $"template '{t.id}': rule '{r.part}' reaches floor 0 alongside an exact " +
                        "but does not set avoidExact, so it will place straight through it");
            }
        }

        [Test]
        public void TheExclusionRadiusClearsEveryGroundFloorExact()
        {
            // PlaceProcedural: exclusion = max(minSpacingMeters, spacing/2) / facadeLength, compared
            // against |nx_rule - nx_exact|. In metres that is simply minSpacingMeters, so the rule's
            // part clears the exact's when minSpacingMeters >= halfRule + halfExact.
            foreach (var t in Neighborhood)
            {
                foreach (var r in (t.rules ?? Array.Empty<RuleJson>()).Where(r => r.floorRange.min <= 0))
                {
                    float halfRule = new[] { r.part }.Concat(r.variants ?? Array.Empty<string>())
                        .Where(id => !string.IsNullOrEmpty(id)).Max(HalfWidth);

                    foreach (var e in (t.exact ?? Array.Empty<ExactJson>()).Where(e => e.floor == 0))
                    {
                        float needed = halfRule + HalfWidth(e.part);
                        Assert.GreaterOrEqual(r.constraints.minSpacingMeters, needed - 1e-4f,
                            $"template '{t.id}': rule '{r.part}' excludes only " +
                            $"{r.constraints.minSpacingMeters:F2} m around exacts but needs " +
                            $"{needed:F2} m to clear '{e.part}'");
                    }
                }
            }
        }

        [Test]
        public void RaisingTheExclusionRadiusNeverChangesTheRepeatRhythm()
        {
            // PlaceProcedural derives spacing as max(repeat.spacingMeters, minSpacingMeters, 0.1),
            // so minSpacingMeters is free to grow up to the repeat pitch and no further: past it,
            // widening the exclusion silently thins the family it belongs to. This invariant is the
            // reason the #475 tuning could raise every minSpacingMeters without re-tuning any
            // window rhythm.
            foreach (var t in _templates.Values)
                foreach (var r in t.rules ?? Array.Empty<RuleJson>())
                    Assert.LessOrEqual(r.constraints.minSpacingMeters, r.repeat.spacingMeters + 1e-4f,
                        $"template '{t.id}': rule '{r.part}' has minSpacingMeters " +
                        $"{r.constraints.minSpacingMeters} above its repeat pitch " +
                        $"{r.repeat.spacingMeters}, which changes the count, not just the exclusion");
        }

        [Test]
        public void TheBayIsTheOneArtifactTheExclusionRadiusCannotFullyClear()
        {
            // Recorded rather than hidden. The Noe bay is 3.80 m wide, so a window clears it only
            // at 2.45 m — above window_noe_2over2's 2.20 m repeat pitch, which the invariant above
            // makes the ceiling. And _exactMarks is keyed (edge_index, floor) with no notion of
            // floorsSpanned, so the bay's SECOND floor carries no mark at all.
            //
            // Neither is a visible defect today: a window inside a projecting bay shell is occluded
            // by it, not interpenetrating it — it costs triangles. Both would need schema work
            // (a marked span rather than a marked point) to fix properly — #491.
            var noe = _templates["noe_valley_victorian"];
            var bay = noe.exact.Single(e => Part(e.part).generatorId == "bay.projecting");
            var window = noe.rules.Single(r => Part(r.part).generatorId == "window.double_hung");

            Assert.AreEqual(1, bay.floor, "the bay starts one floor above the garage it sits over");
            float needed = HalfWidth(bay.part) + HalfWidth(window.part);
            Assert.Greater(needed, window.constraints.minSpacingMeters,
                "if a preset resize made the bay clearable, delete this test and tighten " +
                nameof(TheExclusionRadiusClearsEveryGroundFloorExact) + " to cover every floor");
            Assert.Greater(bay.floor + Part(bay.part).parameters
                               .Where(p => p.name == "floorsSpanned").Select(p => (int)p.value).Single(),
                           bay.floor + 1,
                           "the bay spans more than one floor, and only its base floor is marked");
        }

        // ---- 4. storefronts: what could not be gated, recorded where it is authored ---------

        [Test]
        public void StorefrontTemplatesRecordThatTheyCannotBeGatedToCommercialBuildings()
        {
            var storefrontTemplates = _templates.Values
                .Where(t => PartIdsIn(t).Any(id => Part(id).generatorId == "storefront.bay_row"))
                .ToArray();

            Assert.IsNotEmpty(storefrontTemplates, "no template places a storefront");

            foreach (var t in storefrontTemplates)
            {
                // No invented filter. building_type is the raw OSM `building` tag and is "yes" for
                // 93.3% of the 11,111 buildings measured in #472, so a building_types list here
                // would either match everything or nothing — it must stay empty until #486.
                CollectionAssert.IsEmpty(t.compatibility.building_types ?? Array.Empty<string>(),
                    $"template '{t.id}' gates storefronts on building_type, which is \"yes\" for " +
                    "93.3% of buildings (#486) — the filter would silently do nothing");

                string raw = File.ReadAllText(Path.Combine(TemplatesDir, t.id + ".template.json"));
                StringAssert.Contains("#486", raw,
                    $"template '{t.id}' must record that its shopfronts land on residential " +
                    "buildings until a commercial fact is baked");
                StringAssert.Contains("#487", raw,
                    $"template '{t.id}' must record that the shopfront does not stretch to the " +
                    "facade width it lands on");
            }
        }

        [Test]
        public void AStorefrontNeverSharesATemplateWithADoorOrGarage()
        {
            // The floor-0 collision, resolved structurally: a shopfront IS the ground floor (it
            // carries its own recessed entry), so it cannot coexist with a garage and a door at any
            // facade width. One exact owns floor 0 per template; the choice between them happens at
            // template selection instead.
            foreach (var t in _templates.Values)
            {
                var generators = PartIdsIn(t).Select(id => Part(id).generatorId).ToHashSet(StringComparer.Ordinal);
                if (!generators.Contains("storefront.bay_row")) continue;

                Assert.IsFalse(generators.Contains("door.panel"),
                               $"template '{t.id}' puts a door and a storefront on the same ground floor");
                Assert.IsFalse(generators.Contains("garage.sectional"),
                               $"template '{t.id}' puts a garage and a storefront on the same ground floor");
            }
        }

        [Test]
        public void NeighborhoodsServedByMoreThanOneTemplateHaveAuthoredWeights()
        {
            // Two templates admitting one neighborhood is deliberate (it is how the shopfront/house
            // split is expressed), but TryMatch's tie-break is uniform without weights, which would
            // make every second building a shop. If the split exists, the ratio must be authored.
            var byNeighborhood = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var t in Neighborhood)
                foreach (string n in t.compatibility.neighborhoods ?? Array.Empty<string>())
                {
                    if (!byNeighborhood.TryGetValue(n, out var list))
                        byNeighborhood[n] = list = new List<string>();
                    list.Add(t.id);
                }

            var weighted = (_manifest.districtTemplateWeights ?? Array.Empty<NeighborhoodTemplateWeightsJson>())
                .ToDictionary(w => w.neighborhood, w => w.weights ?? Array.Empty<TemplateWeightJson>(),
                              StringComparer.Ordinal);

            foreach (var kv in byNeighborhood.Where(kv => kv.Value.Count > 1))
            {
                Assert.IsTrue(weighted.ContainsKey(kv.Key),
                    $"'{kv.Key}' is admitted by {kv.Value.Count} templates " +
                    $"({string.Join(", ", kv.Value)}) but library.json authors no weights for it");
                foreach (string id in kv.Value)
                {
                    var w = weighted[kv.Key].FirstOrDefault(x => x.template == id);
                    Assert.IsNotNull(w.template, $"'{kv.Key}' weights omit template '{id}'");
                    Assert.Greater(w.weight, 0f,
                        $"'{kv.Key}' weights give '{id}' weight {w.weight}; " +
                        "WeightFor treats <= 0 as unset and falls back to 1");
                }
            }
        }

        // ---- 5. the neighborhood strings ----------------------------------------------------

        [Test]
        public void EveryNeighborhoodStringIsSpeltExactlyAsTheGeojsonSpellsIt()
        {
            // The bake copies `nhood` through verbatim into the sidecar and Compatibility.Admits
            // does an ordinal compare, so a slash or a word out of place silently templates nothing.
            string geojson = Geojson();

            foreach (var t in Neighborhood)
                foreach (string n in t.compatibility.neighborhoods ?? Array.Empty<string>())
                    StringAssert.Contains($"\"nhood\":\"{n}\"", geojson,
                        $"template '{t.id}' names a neighborhood '{n}' that is not in " +
                        "python/data/sf_analysis_neighborhoods.geojson");

            foreach (string n in _manifest.neighborhoods ?? Array.Empty<string>())
                StringAssert.Contains($"\"nhood\":\"{n}\"", geojson,
                    $"library.json lists a neighborhood '{n}' that is not in the geojson");
        }

        [Test]
        public void TheSlashedNeighborhoodsSurviveVerbatim()
        {
            // Named explicitly because they are the ones a tidy-up would "fix".
            CollectionAssert.Contains(_manifest.neighborhoods, "Sunset/Parkside");
            CollectionAssert.Contains(_manifest.neighborhoods, "South of Market");
            CollectionAssert.Contains(_templates["sunset_parkside_postwar"].compatibility.neighborhoods,
                                      "Sunset/Parkside");
            CollectionAssert.Contains(_templates["soma_industrial"].compatibility.neighborhoods,
                                      "South of Market");
        }

        [Test]
        public void LibraryJsonListsExactlyTheNeighborhoodsTheTemplatesAdmit()
        {
            // Informational (the importer reads it for one log line), so its only job is to be true.
            var admitted = Neighborhood
                .SelectMany(t => t.compatibility.neighborhoods ?? Array.Empty<string>())
                .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var listed = (_manifest.neighborhoods ?? Array.Empty<string>())
                .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(admitted, listed,
                "library.json's neighborhoods no longer match what the templates admit");
        }

        static string Geojson()
        {
            if (_geojson == null)
            {
                string path = Path.Combine(RepoRoot, "python", "data", "sf_analysis_neighborhoods.geojson");
                Assert.IsTrue(File.Exists(path), $"missing {path}");
                _geojson = File.ReadAllText(path);
            }
            return _geojson;
        }
    }
}
