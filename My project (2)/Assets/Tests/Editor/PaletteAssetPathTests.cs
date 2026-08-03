using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;

namespace SFMap.Tests
{
    /// <summary>
    /// #464: a neighborhood name is a fact, not an id, and five real SF neighborhoods contain a
    /// forward slash — so the name cannot be a file name, but it MUST stay the lookup key.
    ///
    /// <para>What these guard, in order: that <see cref="AssetFileName"/> produces legal names, that
    /// it cannot map two neighborhoods onto one file (the property the issue actually turns on),
    /// that it changes nothing for the names that already worked, that the authored part/template
    /// ids never needed it in the first place, and that a slashed palette survives the whole round
    /// trip from JSON on disk to a <c>ResolvePalette</c> hit.</para>
    ///
    /// <para><b>What is NOT covered.</b> <c>AssetDatabase.CreateAsset</c> itself. These tests run
    /// outside the Editor, so the assertion about the generated path is that the string is a legal
    /// single-segment asset path — not that Unity accepted it. The importer's use of that string is
    /// reasonable-by-inspection only.</para>
    /// </summary>
    public class PaletteAssetPathTests
    {
        static string LibraryRoot => Path.Combine(Application.dataPath, "SFBuildingTemplates");
        static string PalettesDir => Path.Combine(LibraryRoot, "Palettes");

        static string RepoRoot =>
            Directory.GetParent(Path.GetFullPath(Application.dataPath)).Parent.FullName;

        /// <summary>The one the issue is named for, and the one the fixture palette uses.</summary>
        const string Slashed = "Sunset/Parkside";

        static string[] _nhoods;
        static Dictionary<string, PaletteDefJson> _palettes;

        [OneTimeSetUp]
        public void LoadGeojsonAndPalettes()
        {
            string geo = Path.Combine(RepoRoot, "python", "data", "sf_analysis_neighborhoods.geojson");
            Assert.IsTrue(File.Exists(geo), $"missing {geo}");
            _nhoods = Regex.Matches(File.ReadAllText(geo), "\"nhood\":\"(.*?)\"")
                           .Cast<Match>().Select(m => m.Groups[1].Value)
                           .Distinct(StringComparer.Ordinal)
                           .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.IsNotEmpty(_nhoods, "no nhood strings in the geojson");

            Assert.IsTrue(Directory.Exists(PalettesDir), $"missing {PalettesDir}");
            _palettes = new Dictionary<string, PaletteDefJson>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(PalettesDir, "*.palette.json")
                                             .Where(f => f.EndsWith(".palette.json", StringComparison.OrdinalIgnoreCase)))
            {
                var def = JsonUtility.FromJson<PaletteDefJson>(File.ReadAllText(file));
                Assert.IsNotNull(def, file);
                Assert.IsFalse(string.IsNullOrEmpty(def.neighborhood), $"{file} has no neighborhood");
                Assert.IsFalse(_palettes.ContainsKey(def.neighborhood),
                               $"two palette files claim neighborhood '{def.neighborhood}'");
                _palettes[def.neighborhood] = def;
            }
        }

        // ---- 1. the encoding ----------------------------------------------------------------

        [Test]
        public void ANameThatIsAlreadyLegalIsReturnedUnchanged()
        {
            // The stability half of the contract: existing palettes must not move, or a re-import
            // orphans every asset that was already generated.
            foreach (string n in new[] { "Noe Valley", "Sunset", "South of Market", "Marina", "Mission" })
            {
                Assert.IsTrue(AssetFileName.IsLegalFileName(n), n);
                Assert.AreEqual(n, AssetFileName.Encode(n));
            }
        }

        [Test]
        public void TheSlashedNamesEncodeToOneLegalPathSegment()
        {
            Assert.AreEqual("Sunset%2FParkside", AssetFileName.Encode(Slashed));
            Assert.AreEqual("Oceanview%2FMerced%2FIngleside",
                            AssetFileName.Encode("Oceanview/Merced/Ingleside"));

            foreach (string n in _nhoods)
            {
                string encoded = AssetFileName.Encode(n);
                Assert.IsTrue(AssetFileName.IsLegalFileName(encoded),
                              $"'{n}' encodes to '{encoded}', which is still not a legal file name");
                // A path separator is the failure mode the issue is about: it silently turns one
                // asset into a folder, so it is asserted directly rather than only via IsLegal.
                Assert.IsFalse(encoded.Contains("/") || encoded.Contains("\\"),
                               $"'{n}' encodes to '{encoded}', which still reads as a directory");
                Assert.AreEqual(encoded, Path.GetFileName(encoded), $"'{encoded}' is not one segment");
            }
        }

        [Test]
        public void NoTwoNeighborhoodsCanEncodeOntoTheSameFileName()
        {
            // The property #464 turns on. The encoding is percent-based and '%' is itself escaped,
            // so every '%' in the output starts a three-character escape and the output is uniquely
            // decodable — hence injective. Asserted over the whole real corpus plus the pairs a
            // lossy scheme (e.g. "replace anything unusual with '_'") would actually merge.
            var corpus = _nhoods
                .Concat(new[]
                {
                    "Sunset/Parkside", "Sunset Parkside", "Sunset_Parkside", "Sunset%2FParkside",
                    "Castro/Upper Market", "Castro Upper Market", "Castro%2FUpper Market",
                    "A/B", "A\\B", "A:B", "A|B", "A*B", "A?B", "A%2FB", "A%B",
                    "trailing.", "trailing ", "trailing", ".leading", "leading",
                })
                .Distinct(StringComparer.Ordinal).ToArray();

            var byEncoded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string n in corpus)
            {
                string e = AssetFileName.Encode(n);
                Assert.IsFalse(byEncoded.ContainsKey(e),
                    byEncoded.TryGetValue(e, out var other)
                        ? $"'{n}' and '{other}' both encode to '{e}'"
                        : $"collision on '{e}'");
                byEncoded[e] = n;
            }
        }

        [Test]
        public void NoTwoRealNeighborhoodsDifferOnlyInCase()
        {
            // The caveat the encoding cannot fix: Windows and macOS file systems are
            // case-insensitive, so injectivity under ordinal comparison is not enough on its own.
            // It happens to be enough here, and this fails loudly if the geojson ever changes.
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in _nhoods)
            {
                string e = AssetFileName.Encode(n);
                Assert.IsFalse(seen.ContainsKey(e),
                    $"'{n}' and '{(seen.TryGetValue(e, out var o) ? o : "?")}' differ only in case, " +
                    "so they share a file on a case-insensitive file system");
                seen[e] = n;
            }
        }

        // ---- 2. the authored ids never had this exposure ------------------------------------

        [Test]
        public void EveryAuthoredPartAndTemplateIdIsAlreadyALegalFileName()
        {
            // #464 asked this to be confirmed rather than assumed. Part and template ids are
            // authored, so unlike a neighborhood they are under our control — and every one of them
            // is already legal, which is why the importer only warns (SafeId) instead of quietly
            // renaming assets. If this ever fails, the id is the bug, not the path.
            foreach (string dir in new[] { "Parts", "Templates" })
            {
                string abs = Path.Combine(LibraryRoot, dir);
                Assert.IsTrue(Directory.Exists(abs), $"missing {abs}");
                string suffix = dir == "Parts" ? ".part.json" : ".template.json";

                foreach (string file in Directory.GetFiles(abs, "*" + suffix)
                                                 .Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    string json = File.ReadAllText(file);
                    string id = dir == "Parts"
                        ? JsonUtility.FromJson<PartDefJson>(json).id
                        : JsonUtility.FromJson<TemplateDefJson>(json).id;
                    Assert.IsFalse(string.IsNullOrEmpty(id), file);
                    Assert.IsTrue(AssetFileName.IsLegalFileName(id),
                                  $"{dir}/{Path.GetFileName(file)} has id '{id}', which cannot be a " +
                                  "file name — rename the id");
                    Assert.AreEqual(id, AssetFileName.Encode(id),
                                    $"{dir} id '{id}' would be renamed on import");
                }
            }
        }

        // ---- 3. the round trip ---------------------------------------------------------------

        [Test]
        public void APaletteShipsForASlashedNeighborhood()
        {
            Assert.IsTrue(_palettes.ContainsKey(Slashed),
                $"no palette claims '{Slashed}'; #464's regression fixture is gone");
            CollectionAssert.Contains(_nhoods, Slashed,
                "the fixture palette's neighborhood is not a real geojson nhood");
        }

        [Test]
        public void EveryShippedPaletteWouldGetALegalGeneratedAssetPath()
        {
            foreach (string n in _palettes.Keys)
            {
                string path = "Assets/SFBuildingTemplates/Generated/Palettes/" +
                              AssetFileName.Encode(n) + ".asset";
                // Exactly the shape BuildingTemplateLibraryImporter.BuildPalette builds. What is
                // asserted is that the file name is one segment under Generated/Palettes — NOT that
                // AssetDatabase.CreateAsset accepts it, which needs the Editor.
                Assert.AreEqual("Assets/SFBuildingTemplates/Generated/Palettes",
                                Path.GetDirectoryName(path).Replace('\\', '/'),
                                $"palette '{n}' escapes Generated/Palettes: {path}");
                Assert.AreEqual(".asset", Path.GetExtension(path));
            }
        }

        [Test]
        public void ASlashedPaletteSurvivesTheRoundTripToAResolvePaletteHit()
        {
            // End to end, minus the AssetDatabase: parse the JSON exactly as the importer does,
            // build the ScriptableObject the importer builds, then look it up the way
            // BuildingAssembler does — LoadAll into a Dictionary keyed by `neighborhood` with an
            // ordinal comparer, queried with the raw fact the bake wrote into the sidecar.
            var def = _palettes[Slashed];

            var so = ScriptableObject.CreateInstance<NeighborhoodPalette>();
            so.neighborhood = def.neighborhood;          // verbatim: the file name is NOT the key
            so.roles = def.roles.Select(rd => new RolePalette
            {
                role = (MaterialRole)Enum.Parse(typeof(MaterialRole), rd.role, true),
                mode = (PaletteMode)Enum.Parse(typeof(PaletteMode), rd.mode, true),
                colors = ((rd.ramp != null && rd.ramp.Length > 0) ? rd.ramp : rd.colors)
                         .Select(h => { ColorUtility.TryParseHtmlString(h, out var c); return c; })
                         .ToArray(),
            }).ToArray();

            Assert.AreEqual(Slashed, so.neighborhood,
                "the palette's lookup key must stay the slashed string the bake passes through");

            var byNeighborhood = new Dictionary<string, NeighborhoodPalette>(StringComparer.Ordinal);
            byNeighborhood[so.neighborhood] = so;

            Assert.IsTrue(byNeighborhood.TryGetValue(Slashed, out var found),
                          "ResolvePalette would miss the slashed neighborhood");
            Assert.AreSame(so, found);

            // And it resolves to a real colour rather than the missing-role magenta, so the
            // palette's own contents survived too, not just its key.
            Assert.AreNotEqual(Color.magenta, found.Resolve(MaterialRole.Base, 0u),
                               "Base resolves to the missing-role fallback");
            Assert.AreNotEqual(Color.magenta, found.Resolve(MaterialRole.Glass, 7u),
                               "Glass resolves to the missing-role fallback");
        }
    }
}
