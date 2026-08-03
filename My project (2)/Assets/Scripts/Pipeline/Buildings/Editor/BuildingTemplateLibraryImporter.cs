using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Pipeline.Buildings.Editor
{
    /// <summary>
    /// Converts the server-exported <c>Assets/SFBuildingTemplates/</c> library — PartDef /
    /// TemplateDef / PaletteDef JSON (+ GLB / PNG) — into Unity ScriptableObjects
    /// (<see cref="BuildingPart"/>, <see cref="BuildingTemplate"/>, <see cref="NeighborhoodPalette"/>),
    /// the assembler's (#270) inputs (design #266 data-model.md §2–3). Menu-driven (not an
    /// auto post-processor) so the conversion is explicit and re-runnable.
    ///
    /// Parts carry no geometry: a <c>PartDef</c> names an <c>IPartGenerator</c> and its parameter
    /// block, and the assembler constructs the mesh at import time (design #452 D2, #454). There is
    /// no GLB and no glTF package dependency.
    /// </summary>
    public static class BuildingTemplateLibraryImporter
    {
        private const string LibraryRoot = SFBuildingTemplatePaths.LibraryRoot;
        private const string GeneratedRoot = LibraryRoot + "/Generated";

        [MenuItem("SFMap/Rebuild Building Template Library")]
        public static void Rebuild()
        {
            string absRoot = ToAbsolute(LibraryRoot);
            if (!Directory.Exists(absRoot))
            {
                Debug.LogWarning($"[SFBuildingTemplates] No library at {LibraryRoot} — nothing to import.");
                return;
            }

            EnsureFolder(GeneratedRoot + "/Parts");
            EnsureFolder(GeneratedRoot + "/Palettes");
            EnsureFolder(GeneratedRoot + "/Templates");
            EnsureFolder(GeneratedRoot + "/DistrictWeights");

            int parts = 0, palettes = 0, templates = 0, districtWeights = 0, warnings = 0;

            // Manifest carries district template weights (#343) alongside the informational
            // version/neighborhood report, so it's parsed unconditionally (not just logged).
            string manifestPath = Path.Combine(absRoot, "library.json");
            if (File.Exists(manifestPath))
            {
                var manifest = TryParse<LibraryManifestJson>(manifestPath, ref warnings);
                if (manifest != null)
                {
                    Debug.Log($"[SFBuildingTemplates] library.json v{manifest.version}, " +
                              $"{(manifest.neighborhoods != null ? manifest.neighborhoods.Length : 0)} neighborhood(s).");

                    if (manifest.districtTemplateWeights != null)
                    {
                        foreach (var row in manifest.districtTemplateWeights)
                        {
                            if (row == null || string.IsNullOrEmpty(row.neighborhood)) { warnings++; continue; }
                            BuildDistrictWeights(row, ref warnings);
                            districtWeights++;
                        }
                    }
                }
            }

            // Pass 1 — parts (templates resolve roof-part references against these).
            var partsById = new Dictionary<string, BuildingPart>(StringComparer.Ordinal);
            foreach (string file in EnumerateJson(absRoot, "*.part.json"))
            {
                var def = TryParse<PartDefJson>(file, ref warnings);
                if (def == null || string.IsNullOrEmpty(def.id)) { warnings++; continue; }
                var part = BuildPart(def, ref warnings);
                partsById[def.id] = part;
                parts++;
            }

            // Pass 2 — palettes (independent).
            foreach (string file in EnumerateJson(absRoot, "*.palette.json"))
            {
                var def = TryParse<PaletteDefJson>(file, ref warnings);
                if (def == null || string.IsNullOrEmpty(def.neighborhood)) { warnings++; continue; }
                BuildPalette(def, ref warnings);
                palettes++;
            }

            // Pass 3 — templates (resolve roofParts by id against pass 1).
            foreach (string file in EnumerateJson(absRoot, "*.template.json"))
            {
                var def = TryParse<TemplateDefJson>(file, ref warnings);
                if (def == null || string.IsNullOrEmpty(def.id)) { warnings++; continue; }
                BuildTemplate(def, partsById, ref warnings);
                templates++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[SFBuildingTemplates] Imported {parts} part(s), {palettes} palette(s), " +
                      $"{templates} template(s), {districtWeights} district weight table(s) into " +
                      $"{GeneratedRoot} — {warnings} warning(s).");
        }

        // ---- builders ---------------------------------------------------------

        private static BuildingPart BuildPart(PartDefJson def, ref int warnings)
        {
            var so = LoadOrCreate<BuildingPart>($"{GeneratedRoot}/Parts/{SafeId(def.id, "part", ref warnings)}.asset");
            so.id = def.id;
            so.category = ParseEnum(def.category, PartCategory.Window, ref warnings, $"part '{def.id}' category");
            so.sizeMeters = new Vector3(def.size_m.w, def.size_m.h, def.size_m.d);
            so.anchor = def.anchor;
            so.mountDepthMeters = def.mountDepth_m;
            so.isSign = so.category == PartCategory.Sign;

            so.generatorId = def.generatorId;
            so.parameters = PartParams.From(def.parameters);
            WarnOnDuplicateParameters(def, ref warnings);

            if (string.IsNullOrEmpty(def.generatorId))
            {
                // An expected authoring state, not an error — informational so a library whose
                // generators aren't written yet still imports cleanly. The part is skipped (with a
                // warning) at assembly time, where it actually matters.
                Debug.Log($"[SFBuildingTemplates] Part '{def.id}' has no generatorId; it will be " +
                          "skipped when a building is assembled until one is authored.");
            }

            EditorUtility.SetDirty(so);
            return so;
        }

        // Parameters are name-keyed and first-wins, so a repeated name means the second value is
        // silently dead. Nothing else validates the bag (it has no compile-time schema), so this is
        // the one authoring mistake worth catching at import.
        private static void WarnOnDuplicateParameters(PartDefJson def, ref int warnings)
        {
            if (def.parameters == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in def.parameters)
            {
                if (string.IsNullOrEmpty(p.name))
                {
                    warnings++;
                    Debug.LogWarning($"[SFBuildingTemplates] Part '{def.id}': parameter with no name.");
                }
                else if (!seen.Add(p.name))
                {
                    warnings++;
                    Debug.LogWarning($"[SFBuildingTemplates] Part '{def.id}': parameter '{p.name}' " +
                                     "declared more than once; the first occurrence wins.");
                }
            }
        }

        private static void BuildPalette(PaletteDefJson def, ref int warnings)
        {
            // File name percent-encoded, lookup key verbatim (#464): five real SF neighborhoods
            // carry a slash, and BuildingAssembler.ResolvePalette matches `neighborhood` ordinally
            // against the string the bake copied out of the geojson.
            var so = LoadOrCreate<NeighborhoodPalette>(
                $"{GeneratedRoot}/Palettes/{AssetFileName.Encode(def.neighborhood)}.asset");
            so.neighborhood = def.neighborhood;

            var roles = new List<RolePalette>();
            if (def.roles != null)
            {
                foreach (var rd in def.roles)
                {
                    var mode = ParsePaletteMode(rd.mode, ref warnings, $"palette '{def.neighborhood}'");
                    // Lerp prefers an explicit ramp; otherwise the colour list serves as stops.
                    string[] hex = (mode == PaletteMode.Lerp && rd.ramp != null && rd.ramp.Length > 0)
                        ? rd.ramp : rd.colors;
                    roles.Add(new RolePalette
                    {
                        role = ParseEnum(rd.role, MaterialRole.Base, ref warnings,
                                         $"palette '{def.neighborhood}' role"),
                        colors = ParseColors(hex, ref warnings, def.neighborhood),
                        mode = mode,
                    });
                }
            }
            so.roles = roles.ToArray();
            EditorUtility.SetDirty(so);
        }

        private static void BuildDistrictWeights(NeighborhoodTemplateWeightsJson row, ref int warnings)
        {
            // Same slash exposure as the palettes (#464) — this table is keyed by neighborhood too,
            // and "Sunset/Parkside" is already in the shipped library.json.
            var so = LoadOrCreate<NeighborhoodTemplateWeights>(
                $"{GeneratedRoot}/DistrictWeights/{AssetFileName.Encode(row.neighborhood)}.asset");
            so.neighborhood = row.neighborhood;

            if (row.weights == null) { so.weights = Array.Empty<TemplateWeight>(); EditorUtility.SetDirty(so); return; }

            // Built as a list, not a fixed-length array: a malformed entry used to leave a
            // default-initialised TemplateWeight (null id, weight 0) in the table, which now that a
            // 0 excludes rather than defaults (#475) would be a live hole rather than an inert one.
            var weights = new List<TemplateWeight>(row.weights.Length);
            foreach (var w in row.weights)
            {
                if (string.IsNullOrEmpty(w.template)) { warnings++; continue; }
                if (w.weight < 0f)
                {
                    warnings++;
                    Debug.LogWarning($"[SFBuildingTemplates] District '{row.neighborhood}': template " +
                                     $"'{w.template}' has negative weight {w.weight}; treated as 0 (excluded).");
                }
                else if (w.weight == 0f)
                {
                    // Deliberate under the #475 semantics, but also what a JSON row that simply
                    // omits "weight" deserializes to — so it is logged, not silent.
                    Debug.Log($"[SFBuildingTemplates] District '{row.neighborhood}' excludes template " +
                              $"'{w.template}' (weight 0). Omit the entry instead to let it compete " +
                              "at the default weight.");
                }
                weights.Add(new TemplateWeight { templateId = w.template, weight = w.weight });
            }
            so.weights = weights.ToArray();
            EditorUtility.SetDirty(so);
        }

        private static void BuildTemplate(TemplateDefJson def, Dictionary<string, BuildingPart> partsById,
                                          ref int warnings)
        {
            var so = LoadOrCreate<BuildingTemplate>(
                $"{GeneratedRoot}/Templates/{SafeId(def.id, "template", ref warnings)}.asset");
            so.id = def.id;
            so.displayName = string.IsNullOrEmpty(def.displayName) ? def.id : def.displayName;
            so.compatibility = BuildCompatibility(def.compatibility);
            so.exact = BuildExact(def.exact, ref warnings, def.id);
            so.rules = BuildRules(def.rules, ref warnings, def.id);
            so.roofParts = ResolveParts(def.roofParts, partsById, ref warnings, def.id);
            EditorUtility.SetDirty(so);
        }

        private static Compatibility BuildCompatibility(CompatibilityJson c)
        {
            if (c == null) return new Compatibility();
            return new Compatibility
            {
                neighborhoods = c.neighborhoods ?? Array.Empty<string>(),
                buildingTypes = c.building_types ?? Array.Empty<string>(),
                footprintShapes = c.footprint_shapes ?? Array.Empty<string>(),
                uses = c.uses ?? Array.Empty<string>(),
                widthM = new FloatRange { min = c.width_m.min, max = c.width_m.max },
                depthM = new FloatRange { min = c.depth_m.min, max = c.depth_m.max },
                floorCount = new IntRange { min = c.floor_count.min, max = c.floor_count.max },
            };
        }

        private static ExactPlacement[] BuildExact(ExactJson[] src, ref int warnings, string templateId)
        {
            if (src == null) return Array.Empty<ExactPlacement>();
            var outp = new ExactPlacement[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var e = src[i];
                var roles = new RoleMap[e.roles != null ? e.roles.Length : 0];
                for (int j = 0; j < roles.Length; j++)
                    roles[j] = new RoleMap
                    {
                        from = ParseEnum(e.roles[j].from, MaterialRole.Base, ref warnings, $"template '{templateId}' role"),
                        to = ParseEnum(e.roles[j].to, MaterialRole.Base, ref warnings, $"template '{templateId}' role"),
                    };
                outp[i] = new ExactPlacement
                {
                    part = e.part,
                    facade = ParseEnum(e.facade, Facade.Front, ref warnings, $"template '{templateId}' facade"),
                    floor = e.floor, x = e.x, y = e.y, scale = e.scale, rotation = e.rotation, roles = roles,
                };
            }
            return outp;
        }

        private static ProceduralRule[] BuildRules(RuleJson[] src, ref int warnings, string templateId)
        {
            if (src == null) return Array.Empty<ProceduralRule>();
            var outr = new ProceduralRule[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var r = src[i];
                outr[i] = new ProceduralRule
                {
                    part = r.part,
                    facade = ParseEnum(r.facade, Facade.Front, ref warnings, $"template '{templateId}' rule facade"),
                    floorRange = new IntRange { min = r.floorRange.min, max = r.floorRange.max },
                    span = r.span ?? Array.Empty<float>(),
                    repeat = new Repeat { spacingMeters = r.repeat.spacingMeters, countMin = r.repeat.countMin, countMax = r.repeat.countMax },
                    probability = r.probability,
                    constraints = new PlacementConstraints
                    {
                        minSpacingMeters = r.constraints.minSpacingMeters,
                        edgeMargin = r.constraints.edgeMargin,
                        alignToFloorLine = r.constraints.alignToFloorLine,
                        avoidExact = r.constraints.avoidExact,
                    },
                    jitter = new Jitter { x = r.jitter.x, scale = r.jitter.scale ?? Array.Empty<float>(), rotation = r.jitter.rotation },
                    variants = r.variants ?? Array.Empty<string>(),
                };
            }
            return outr;
        }

        private static BuildingPart[] ResolveParts(string[] ids, Dictionary<string, BuildingPart> partsById,
                                                   ref int warnings, string ownerId)
        {
            if (ids == null) return Array.Empty<BuildingPart>();
            var resolved = new List<BuildingPart>(ids.Length);
            foreach (string id in ids)
            {
                if (!string.IsNullOrEmpty(id) && partsById.TryGetValue(id, out var p))
                {
                    resolved.Add(p);
                }
                else
                {
                    warnings++;
                    Debug.LogWarning($"[SFBuildingTemplates] Template '{ownerId}' references unknown part '{id}'.");
                }
            }
            return resolved.ToArray();
        }

        // ---- parsing helpers --------------------------------------------------

        private static Color[] ParseColors(string[] hex, ref int warnings, string owner)
        {
            if (hex == null || hex.Length == 0) return Array.Empty<Color>();
            var colors = new List<Color>(hex.Length);
            foreach (string h in hex)
            {
                if (ColorUtility.TryParseHtmlString(h, out var c)) colors.Add(c);
                else { warnings++; Debug.LogWarning($"[SFBuildingTemplates] Palette '{owner}': bad colour '{h}'."); }
            }
            return colors.ToArray();
        }

        private static TEnum ParseEnum<TEnum>(string value, TEnum fallback, ref int warnings, string context)
            where TEnum : struct
        {
            if (!string.IsNullOrEmpty(value) && Enum.TryParse<TEnum>(value, true, out var parsed))
                return parsed;
            warnings++;
            Debug.LogWarning($"[SFBuildingTemplates] {context}: unknown value '{value}', using {fallback}.");
            return fallback;
        }

        private static PaletteMode ParsePaletteMode(string mode, ref int warnings, string context)
            => ParseEnum(mode, PaletteMode.Pick, ref warnings, $"{context} mode");

        private static T TryParse<T>(string file, ref int warnings) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                warnings++;
                Debug.LogWarning($"[SFBuildingTemplates] Failed to parse {file}: {ex.Message}");
                return null;
            }
        }

        // ---- asset / path helpers --------------------------------------------

        /// <summary>Part and template ids are *authored*, not facts, so unlike a neighborhood name
        /// (#464) an id that cannot be a file name is a mistake rather than data. Encoding it keeps
        /// the import from throwing, but it is warned about: the id is the key the whole library
        /// cross-references by, and TemplateWiringTests requires a template's file to be named
        /// after it. Every id shipped today is already legal — PaletteAssetPathTests asserts that,
        /// so this path is a guard, not a transformation anything relies on.</summary>
        private static string SafeId(string id, string kind, ref int warnings)
        {
            string encoded = AssetFileName.Encode(id);
            if (encoded == id) return id;
            warnings++;
            Debug.LogWarning($"[SFBuildingTemplates] {kind} id '{id}' cannot be an asset file name; " +
                             $"writing it as '{encoded}.asset'. Rename the id.");
            return encoded;
        }

        private static T LoadOrCreate<T>(string assetPath) where T : ScriptableObject
        {
            var so = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (so == null)
            {
                so = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(so, assetPath);
            }
            return so;
        }

        private static IEnumerable<string> EnumerateJson(string absRoot, string pattern)
        {
            // Directory.GetFiles' legacy short-name matching can let "*.part.json" also match
            // "*.part.json.meta"; filter to the exact suffix so Unity .meta files are excluded.
            string suffix = pattern.Substring(1); // "*.part.json" -> ".part.json"
            foreach (string f in Directory.GetFiles(absRoot, pattern, SearchOption.AllDirectories))
                if (f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    yield return f;
        }

        private static string ToAbsolute(string assetPath)
            => Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));

        private static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            string leaf = Path.GetFileName(assetFolder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
