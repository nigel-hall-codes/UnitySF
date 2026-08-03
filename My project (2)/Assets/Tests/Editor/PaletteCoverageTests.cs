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
    /// #508: the two halves of "why is every building the same colour". Colour resolution has two
    /// independent ways to fall through, and neither is loud at author time:
    ///
    /// <list type="number">
    /// <item><b>No palette for the neighborhood.</b> <c>BuildingAssembler.ResolvePalette</c> keys on
    /// the building's <c>neighborhood</c> fact, NOT on the template that matched it — so widening a
    /// template's <c>compatibility.neighborhoods</c> (as #466 did, to four districts at once) silently
    /// routes every building in those districts to <c>DefaultRoleColor</c>. That is a valid-looking
    /// neutral colour, and it is the SAME neutral colour for every building, because the per-building
    /// variation is the palette's seeded <c>Pick</c> draw. 1002 identical buildings, no warning.</item>
    /// <item><b>Palette present but a role missing.</b> <see cref="NeighborhoodPalette.Resolve"/>
    /// returns magenta for an absent role, deliberately. That mechanism is right and is not what is
    /// under test here — what is under test is that no shipped palette needs it.</item>
    /// </list>
    ///
    /// <para>Both are content faults, not code faults, so they are guarded against the content: the
    /// template files on disk are the source of "which neighborhoods must be covered", so admitting a
    /// new one without a palette fails here rather than at the next import.</para>
    ///
    /// <para><b>Sign.</b> Sign is a texture role, not a colour role, and is excluded from
    /// <see cref="RenderedRoles"/>. Two facts back that: <c>BuildingAssembler.BakeAndCombine</c> skips
    /// sign parts before the vertex-colour bake (they keep their own textured material and stay out of
    /// the combine), and no shipped generator ever calls <c>MeshBuilder.BeginRole(MaterialRole.Sign)</c>
    /// — the only Sign submesh anywhere is a marker triangle in a BayGeneratorTests stub. So a palette
    /// may author Sign (Mission, North Beach, SoMa and Sunset/Parkside do, as a colour to reach for
    /// when signage does get a resolved colour) but is not required to, and an unauthored Sign cannot
    /// render magenta today because nothing resolves it.</para>
    ///
    /// <para><b>What is NOT covered.</b> No Editor here: the palettes are parsed and resolved exactly
    /// as the importer and the assembler would, but nothing asserts what any of it LOOKS like. These
    /// tests can prove a colour exists and is not the fallback. They cannot prove it is a good colour,
    /// or that two neighborhoods read as different places.</para>
    /// </summary>
    public class PaletteCoverageTests
    {
        static string LibraryRoot => Path.Combine(Application.dataPath, "SFBuildingTemplates");

        /// <summary>Every <see cref="MaterialRole"/> that reaches a vertex colour — i.e. all of them
        /// except Sign. <see cref="TheRenderedRoleSetIsEveryMaterialRoleExceptSign"/> keeps this
        /// honest if the enum grows.</summary>
        static readonly MaterialRole[] RenderedRoles =
        {
            MaterialRole.Base, MaterialRole.Accent1, MaterialRole.Accent2,
            MaterialRole.Glass, MaterialRole.Metal,
        };

        /// <summary>The bundled MVP sample palette (README): "Sunset" is not a real
        /// <c>sf_analysis_neighborhoods.geojson</c> nhood — the real one is "Sunset/Parkside" — and no
        /// template admits it, so it can never resolve. It ships as the sample the authoring server's
        /// export test round-trips, so it is exempted by name rather than deleted, and the exemption
        /// is a list of one so a genuinely misspelled neighborhood still fails.</summary>
        static readonly string[] SamplePalettes = { "Sunset" };

        static Dictionary<string, PaletteDefJson> _palettes;
        static Dictionary<string, List<string>> _admittedBy;   // neighborhood → template ids

        [OneTimeSetUp]
        public void LoadLibrary()
        {
            string palettesDir = Path.Combine(LibraryRoot, "Palettes");
            Assert.IsTrue(Directory.Exists(palettesDir), $"missing {palettesDir}");
            _palettes = new Dictionary<string, PaletteDefJson>(StringComparer.Ordinal);
            foreach (string file in Files(palettesDir, ".palette.json"))
            {
                var def = JsonUtility.FromJson<PaletteDefJson>(File.ReadAllText(file));
                Assert.IsNotNull(def, file);
                Assert.IsFalse(string.IsNullOrEmpty(def.neighborhood), $"{file} has no neighborhood");
                _palettes[def.neighborhood] = def;
            }
            Assert.IsNotEmpty(_palettes, "no palettes on disk");

            string templatesDir = Path.Combine(LibraryRoot, "Templates");
            Assert.IsTrue(Directory.Exists(templatesDir), $"missing {templatesDir}");
            _admittedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string file in Files(templatesDir, ".template.json"))
            {
                var def = JsonUtility.FromJson<TemplateDefJson>(File.ReadAllText(file));
                Assert.IsNotNull(def, file);
                // An EMPTY neighborhood list means "admits every building in the city" (trivial_window,
                // the bundled sample — see library.json). No palette set can cover that, and #492
                // weighted it to 0 in every district that has a row, so it is not a coverage demand.
                if (def.compatibility?.neighborhoods == null) continue;
                foreach (string n in def.compatibility.neighborhoods)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!_admittedBy.TryGetValue(n, out var ids)) _admittedBy[n] = ids = new List<string>();
                    ids.Add(def.id);
                }
            }
            Assert.IsNotEmpty(_admittedBy, "no template admits any neighborhood");
        }

        // Directory.GetFiles' pattern matches 8.3 short names too, so the suffix is re-checked —
        // and it keeps the sibling .meta files out.
        static IEnumerable<string> Files(string dir, string suffix) =>
            Directory.GetFiles(dir, "*" + suffix)
                     .Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.Ordinal);

        // ---- 1. coverage: every admitted neighborhood has a palette --------------------------

        [Test]
        public void EveryNeighborhoodATemplateAdmitsHasAPalette()
        {
            var missing = _admittedBy.Keys.Where(n => !_palettes.ContainsKey(n))
                                          .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.IsEmpty(missing,
                "these neighborhoods are admitted by a template but have no palette, so every building " +
                "in them renders in the same DefaultRoleColor with no per-building variation: " +
                string.Join(", ", missing.Select(n => $"'{n}' (admitted by {string.Join("/", _admittedBy[n])})")));
        }

        [Test]
        public void EveryPaletteIsClaimedBySomeTemplate()
        {
            // The reverse direction, which catches the other way to get an unresolvable palette: a
            // neighborhood string that no template — and so no building — will ever ask for, e.g. a
            // typo, or a name that lost a slash on its way through a file name (#464).
            var orphans = _palettes.Keys
                .Where(n => !_admittedBy.ContainsKey(n) && !SamplePalettes.Contains(n, StringComparer.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.IsEmpty(orphans,
                "these palettes name a neighborhood no template admits, so ResolvePalette can never " +
                "hit them — check the spelling against the template's compatibility.neighborhoods: " +
                string.Join(", ", orphans.Select(n => $"'{n}'")));
        }

        // ---- 2. completeness: every palette defines every rendered role ----------------------

        [Test]
        public void EveryPaletteDefinesEveryRenderedRole()
        {
            foreach (var kv in _palettes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var authored = (kv.Value.roles ?? Array.Empty<RoleDefJson>())
                    .Select(r => r.role).Where(r => !string.IsNullOrEmpty(r)).ToArray();
                foreach (var role in RenderedRoles)
                    Assert.IsTrue(authored.Contains(role.ToString(), StringComparer.OrdinalIgnoreCase),
                        $"palette '{kv.Key}' has no {role} role — every submesh carrying it renders " +
                        $"magenta (authored: {string.Join(", ", authored)})");
            }
        }

        [Test]
        public void NoRenderedRoleResolvesToTheMissingRoleMagenta()
        {
            // Presence is not enough: an empty colour list, an unparseable hex, or a Lerp whose stops
            // only live in `ramp` while the importer reads `colors` would all leave a role authored
            // and still magenta. So build the SO the way the importer does and resolve it the way the
            // assembler does, across a spread of seeds (Pick indexes by seed, Lerp interpolates by it).
            foreach (var kv in _palettes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var so = Build(kv.Value);
                foreach (var role in RenderedRoles)
                    foreach (uint seed in new uint[] { 0u, 1u, 2u, 7u, 65535u, 123457u, uint.MaxValue })
                        Assert.AreNotEqual(Color.magenta, so.Resolve(role, seed),
                            $"palette '{kv.Key}' role {role} resolves to the missing-role fallback at seed {seed}");
            }
        }

        [Test]
        public void ABaseColourVariesAcrossBuildingsInEveryPalette()
        {
            // The acceptance criterion #508 was actually filed on: "buildings vary in colour rather
            // than being identical". Per-building variation is the palette's job — a Base authored as
            // one Constant colour is a district of clones even though every role resolves.
            foreach (var kv in _palettes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var so = Build(kv.Value);
                var seen = new HashSet<Color>();
                for (uint seed = 0; seed < 64; seed++) seen.Add(so.Resolve(MaterialRole.Base, seed));
                Assert.Greater(seen.Count, 1,
                    $"palette '{kv.Key}' resolves one Base colour for every seed, so every building in " +
                    "the district is identical — give Base several `pick` colours");
            }
        }

        // ---- 3. the role set itself ----------------------------------------------------------

        [Test]
        public void TheRenderedRoleSetIsEveryMaterialRoleExceptSign()
        {
            // So that adding a role to MaterialRole is a decision rather than an omission: a new role
            // either joins RenderedRoles (and every palette must then author it) or is justified as a
            // non-colour role next to Sign in the class docs.
            CollectionAssert.AreEquivalent(
                Enum.GetValues(typeof(MaterialRole)).Cast<MaterialRole>().Where(r => r != MaterialRole.Sign),
                RenderedRoles,
                "MaterialRole changed; decide whether the new role resolves to a colour");
        }

        // ---- helper: the importer's JSON → SO mapping, verbatim -------------------------------

        static NeighborhoodPalette Build(PaletteDefJson def)
        {
            var so = ScriptableObject.CreateInstance<NeighborhoodPalette>();
            so.neighborhood = def.neighborhood;
            so.roles = (def.roles ?? Array.Empty<RoleDefJson>()).Select(rd =>
            {
                var mode = (PaletteMode)Enum.Parse(typeof(PaletteMode), rd.mode, true);
                // BuildingTemplateLibraryImporter.BuildPalette: `ramp` only wins for Lerp.
                string[] hex = (mode == PaletteMode.Lerp && rd.ramp != null && rd.ramp.Length > 0)
                    ? rd.ramp : rd.colors;
                return new RolePalette
                {
                    role = (MaterialRole)Enum.Parse(typeof(MaterialRole), rd.role, true),
                    mode = mode,
                    colors = (hex ?? Array.Empty<string>())
                        .Where(h => ColorUtility.TryParseHtmlString(h, out _))
                        .Select(h => { ColorUtility.TryParseHtmlString(h, out var c); return c; })
                        .ToArray(),
                };
            }).ToArray();
            return so;
        }
    }
}
