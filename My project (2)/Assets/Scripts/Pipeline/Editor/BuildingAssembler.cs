using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Pipeline.Editor
{
    // ---- chunk_CC_RR_buildings.json sidecar DTOs (Python bake #268/#279) ----

    [Serializable]
    public sealed class BuildingsSidecarJson
    {
        public int version;
        public BuildingFactsJson[] buildings;
    }

    [Serializable]
    public sealed class BuildingFactsJson
    {
        public long osm_id;
        public string neighborhood;
        public string building_type;
        public string footprint_shape;
        public float width_m;
        public float depth_m;
        public float height_m;
        public int floor_count;
        public float base_y;            // mass foundation Y (#279)
        public float facade_height_m;   // floor_count × floor height (#279)
        public StreetFacadeJson[] street_facades;
        // Best-effort edges relative to street_facades[0] ("front"); null when there's
        // no front to reason from (#407, sidecar version 3).
        public StreetFacadeJson back_facade;
        public StreetFacadeJson left_facade;
        public StreetFacadeJson right_facade;
        public string footprint_hash;
    }

    [Serializable]
    public sealed class StreetFacadeJson
    {
        public int edge_index;
        public float bearing_deg;       // outward normal, degrees CW from +Z
        public long street_osm_id;
        public float score;
        public float[] edge;            // [x0, z0, x1, z1] world metres (#279)
    }

    // ---- Overrides/<osm_id>.override.json (BuildingSpecificDef, data-model.md §2) ----

    [Serializable]
    public sealed class OverrideJson
    {
        public long osm_id;
        public string footprint_hash;          // MUST equal the bake's or the override is skipped
        public string baseTemplate;
        public OverridePlacementJson[] placements;
        public OverridePlacementJson[] suppress;
        public FacadeDecalJson[] facadeDecals;  // NEW (#280/#281): canvas paint + placed images
        public int version;
    }

    [Serializable]
    public sealed class FacadeDecalJson
    {
        public string facade;                  // Front | Street | … (which facade to dress)
        public float[] rect;                   // normalized [x0, y0, x1, y1] on that facade
        public int layer;                      // z-order within the facade
        public string texture;                 // library-relative PNG, e.g. Signs/paint_<id>_front.png
        public float mountDepth_m;             // protrude proud of the wall (D4 stuck-on)
        public string signAsset;               // optional link to a #275 sign asset
    }

    [Serializable]
    public sealed class OverridePlacementJson
    {
        public string part;
        public string facade;
        public int floor;
        public float x;
        public float y;
        public float scale;
        public float rotation;
        public string signAsset;               // non-empty → a sign (own texture, stays separate)
    }

    /// <summary>
    /// The MVP heart of the generation loop (design #266): per building, MATCH a
    /// <see cref="BuildingTemplate"/> by the bake's classification facts, fork
    /// <b>templated → per-building nested prefab</b> vs <b>no-match → existing merged
    /// path</b>, run the template's <b>Exact</b> placements against the building's real
    /// facade frame (from the #279 sidecar edge geometry), and report templated-vs-fallback
    /// coverage (design D6). Procedural rules (#271), overrides (#273), and role→palette
    /// colour resolution (#272) are out of scope here.
    ///
    /// Created once per import via <see cref="TryCreate"/> (null when no sidecar or no
    /// templates exist — the import then behaves exactly as today). The importer calls
    /// <see cref="TryMatch"/> in its building loop to fork, then <see cref="Assemble"/> for
    /// each matched building, then <see cref="LogCoverage"/>.
    /// </summary>
    public sealed class BuildingAssembler
    {
        const float FloorHeightMeters = 3.0f;   // matches the bake (classify._FLOOR_HEIGHT_M)

        readonly Dictionary<long, BuildingFactsJson> _facts;
        readonly List<BuildingTemplate> _templates;
        readonly Dictionary<string, BuildingPart> _partsById;
        readonly Dictionary<string, NeighborhoodPalette> _palettes;
        readonly Dictionary<string, NeighborhoodTemplateWeights> _districtWeights;
        readonly Dictionary<long, OverrideJson> _overrides;

        // What every facade of the building currently being assembled is already wearing (#491),
        // as real along-facade and vertical extents taken off the generated meshes. Written by
        // Exact and Procedural placements alike and consulted by every procedural one, so two
        // rules can see each other and an artifact that spans two floors is felt on both. Replaces
        // the exact-only (edge, floor) → normalized-x point marks. Reset per building in Assemble.
        readonly FacadeOccupancy _occupancy = new FacadeOccupancy();

        // Parameter blocks with one entry overwritten to a facade-sized value (#487), keyed by
        // (part, parameter, 5 mm bucket). Two facades in the same bucket share a block, and
        // therefore a PartKey, and therefore a mesh. Snapping to the bucket rather than keeping
        // the first facade's exact width is what makes the generated geometry a pure function of
        // the bucket instead of of import order. Lives for the whole chunk, like the mesh cache.
        readonly Dictionary<string, PartParams> _stretchedParams =
            new Dictionary<string, PartParams>(StringComparer.Ordinal);
        // Counts parts SIZED, not parts kept: a stretched part is resolved before the corner and
        // occupancy rules get a say, so a refused shopfront still shows up here.
        int _stretchedParts;

        // Parts placed for the current building, collected so BakeAndCombine can colour and fold
        // the non-sign ones into one mesh. Reset per building.
        // `roles` is the generator's own submesh→role tagging (PartMesh.submeshRoles), carried on
        // the placement rather than read off the part asset — the part no longer hand-authors it
        // (#454), so it cannot drift from the geometry it describes.
        struct Placed { public GameObject go; public BuildingPart part; public MaterialRole[] roles; public string partId; public Facade facade; public int floor; public bool isSign; }
        readonly List<Placed> _placed = new List<Placed>();

        // Distinct (generator, quantised parameters, detail) → one shared Mesh, for this chunk's
        // import (design #452 §6 mitigation 1). Thousands of placements, hundreds of meshes.
        readonly PartMeshCache _partMeshes = new PartMeshCache();

        // Part ids already reported as ungeneratable, so a template that references one doesn't
        // log once per placement.
        readonly HashSet<string> _warnedParts = new HashSet<string>(StringComparer.Ordinal);

        // Where the current building's street facades meet (#459). Rebuilt per building in Assemble;
        // empty (and therefore inert) for the single-street-facade majority.
        FacadeCornerTable _corners;

        int _templated;

        public int Templated => _templated;

        BuildingAssembler(Dictionary<long, BuildingFactsJson> facts,
                          List<BuildingTemplate> templates,
                          Dictionary<string, BuildingPart> partsById,
                          Dictionary<string, NeighborhoodPalette> palettes,
                          Dictionary<string, NeighborhoodTemplateWeights> districtWeights,
                          Dictionary<long, OverrideJson> overrides)
        {
            _facts = facts;
            _templates = templates;
            _partsById = partsById;
            _palettes = palettes;
            _districtWeights = districtWeights;
            _overrides = overrides;
        }

        /// <summary>Load the chunk's sidecar + the template library. Returns null (so the
        /// importer keeps today's behaviour) when the sidecar is absent/empty or no
        /// BuildingTemplate assets exist.</summary>
        public static BuildingAssembler TryCreate(string chunkDir, ChunkCoord coord)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_buildings.json");
            if (!File.Exists(src)) return null;

            BuildingsSidecarJson sidecar;
            try { sidecar = JsonUtility.FromJson<BuildingsSidecarJson>(File.ReadAllText(src)); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BuildingAssembler] Failed to parse {src}: {ex.Message}");
                return null;
            }
            if (sidecar?.buildings == null || sidecar.buildings.Length == 0) return null;

            var templates = LoadAll<BuildingTemplate>();
            if (templates.Count == 0) return null;   // nothing to match → merged path as today

            var facts = new Dictionary<long, BuildingFactsJson>(sidecar.buildings.Length);
            foreach (var b in sidecar.buildings) facts[b.osm_id] = b;

            var partsById = new Dictionary<string, BuildingPart>(StringComparer.Ordinal);
            foreach (var p in LoadAll<BuildingPart>())
                if (!string.IsNullOrEmpty(p.id)) partsById[p.id] = p;

            var palettes = new Dictionary<string, NeighborhoodPalette>(StringComparer.Ordinal);
            foreach (var pal in LoadAll<NeighborhoodPalette>())
                if (!string.IsNullOrEmpty(pal.neighborhood)) palettes[pal.neighborhood] = pal;

            var districtWeights = new Dictionary<string, NeighborhoodTemplateWeights>(StringComparer.Ordinal);
            foreach (var w in LoadAll<NeighborhoodTemplateWeights>())
                if (!string.IsNullOrEmpty(w.neighborhood)) districtWeights[w.neighborhood] = w;

            return new BuildingAssembler(facts, templates, partsById, palettes, districtWeights, LoadOverrides());
        }

        /// <summary>Scan Assets/SFBuildingTemplates/Overrides/ once and yield each parsed
        /// override.json (unparseable files are logged and skipped). Shared by LoadOverrides here
        /// and BuildingDecalImporter.LoadDecalOverrides (#430) so the disk scan is defined once.</summary>
        public static IEnumerable<OverrideJson> ScanOverrides()
        {
            string abs = SFBuildingTemplatePaths.OverridesAbsolute;
            if (!Directory.Exists(abs)) yield break;
            foreach (string file in Directory.GetFiles(abs, "*.override.json", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".override.json", StringComparison.OrdinalIgnoreCase)) continue;  // exclude .meta
                OverrideJson ov = null;
                try { ov = JsonUtility.FromJson<OverrideJson>(File.ReadAllText(file)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SFBuildingTemplates] Failed to parse override {file}: {ex.Message}");
                }
                if (ov != null) yield return ov;
            }
        }

        // Load Overrides/*.override.json into a dict keyed by osm_id. The footprint_hash guard is
        // checked at apply time, not here.
        static Dictionary<long, OverrideJson> LoadOverrides()
        {
            var map = new Dictionary<long, OverrideJson>();
            foreach (var ov in ScanOverrides())
                if (ov.osm_id != 0) map[ov.osm_id] = ov;
            return map;
        }

        /// <summary>Does this building have classification facts AND a compatible template?
        /// Ties among compatible templates are broken deterministically by osm_id — weighted by
        /// the building's neighborhood's district template weights (#343) when one exists,
        /// otherwise uniform, same as before. Either way a re-import is byte-stable (design §6
        /// determinism): the same seed always yields the same weighted draw.</summary>
        public bool TryMatch(long osmId, out BuildingFactsJson facts, out BuildingTemplate template)
        {
            template = null;
            if (!_facts.TryGetValue(osmId, out facts)) return false;

            // Gather every admitting template, in a stable order, then pick one seeded by osm_id.
            List<BuildingTemplate> matches = null;
            foreach (var t in _templates)
            {
                if (t != null && t.compatibility != null &&
                    t.compatibility.Admits(facts.neighborhood, facts.building_type,
                                           facts.footprint_shape, facts.width_m, facts.depth_m,
                                           facts.floor_count))
                {
                    (matches ??= new List<BuildingTemplate>()).Add(t);
                }
            }
            if (matches == null) return false;
            matches.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            template = PickWeighted(matches, facts.neighborhood, SeedFor(osmId));
            return true;
        }

        /// <summary>Weighted tie-break among compatible <paramref name="matches"/> (already sorted
        /// for determinism). Falls back to the original uniform pick when the building's
        /// neighborhood has no authored district weights, or its total resolves to zero — an
        /// isolated selection change (design #326 D4): no other assembly behaviour is affected.
        /// An unlisted template still competes at weight 1, so authoring a district doesn't
        /// silently exclude compatible templates the district's author didn't mention.</summary>
        BuildingTemplate PickWeighted(List<BuildingTemplate> matches, string neighborhood, uint seed)
        {
            NeighborhoodTemplateWeights weights = null;
            if (!string.IsNullOrEmpty(neighborhood))
                _districtWeights.TryGetValue(neighborhood, out weights);
            if (weights == null)
                return matches[(int)(seed % (uint)matches.Count)];

            const float DefaultWeight = 1f;
            var w = new float[matches.Count];
            float total = 0f;
            for (int i = 0; i < matches.Count; i++)
            {
                w[i] = weights.WeightFor(matches[i].id, DefaultWeight);
                total += w[i];
            }
            if (total <= 0f)
                return matches[(int)(seed % (uint)matches.Count)];

            // Same [0,1) draw as the rest of the deterministic placement model (Rng.NextFloat),
            // scaled across the weighted total.
            float r = ((seed & 0xFFFFFFu) / 16777216f) * total;
            float acc = 0f;
            for (int i = 0; i < matches.Count; i++)
            {
                acc += w[i];
                if (r < acc) return matches[i];
            }
            return matches[matches.Count - 1];   // float rounding guard
        }

        /// <summary>Build the templated building as a nested child of <paramref name="buildingsParent"/>:
        /// the Python mass mesh + the template's Exact part placements resolved onto this building's
        /// real Front/Street facade. Saved into the chunk prefab by the importer.</summary>
        public void Assemble(Transform buildingsParent, ChunkCoord coord, long osmId, Mesh massMesh,
                             BuildingFactsJson facts, BuildingTemplate template, Material massMaterial)
        {
            var root = new GameObject($"building_{osmId}");
            root.transform.SetParent(buildingsParent, false);

            _placed.Clear();
            _occupancy.Clear();
            // Corner buildings dress every street edge (design D2), and until #459 nothing
            // reconciled two facades meeting at a corner. This table is what does.
            _corners = FacadeCornerTable.Build(facts);

            // Roof first (design #266 generation order: Create Mass → Apply Roof → Doors → …).
            PlaceRoofParts(root.transform, template, facts);

            // Exact first (it also records marks the procedural avoidExact constraint reads).
            if (template.exact != null)
                foreach (var p in template.exact)
                    PlaceExact(root.transform, p, facts);

            // Procedural rules — the engine of believable variety (#271). The rule index is
            // part of every per-slot seed, so a wider building deterministically gets more
            // parts and a re-import is byte-stable.
            if (template.rules != null)
                for (int r = 0; r < template.rules.Length; r++)
                    PlaceProcedural(root.transform, template.rules[r], facts, r);

            // Building-specific override applied LAST (precedence Building-Specific > Exact >
            // Procedural): suppress conflicting template parts, then add the override's exact
            // placements — but only when its footprint_hash matches the bake's (#273).
            ApplyOverride(root.transform, osmId, facts);

            // Resolve material roles → colours via the neighborhood palette and bake them into
            // vertex colours, then mesh-combine the mass + non-sign parts into one batchable mesh
            // (design D1: vertex colours, NOT a per-renderer MaterialPropertyBlock). Sign parts
            // keep their own texture material and stay separate — the batching break.
            BakeAndCombine(root, coord, osmId, massMesh, facts, massMaterial);

            _templated++;
        }

        // ---- role → colour + vertex-colour bake + mesh-combine (#272, design D1) ----

        void BakeAndCombine(GameObject root, ChunkCoord coord, long osmId, Mesh massMesh,
                            BuildingFactsJson facts, Material massMaterial)
        {
            var palette = ResolvePalette(facts.neighborhood);

            var combines = new List<CombineInstance>();
            // Mass → Base role colour (overrides the importer's fallback colour for coherence).
            combines.Add(new CombineInstance
            {
                mesh = CloneSolidColor(massMesh, ResolveRoleColor(osmId, palette, MaterialRole.Base)),
                transform = Matrix4x4.identity,   // mass verts are world-space; root sits at origin
            });

            // Non-sign parts → per-submesh role colours, folded into the combine. Signs stay.
            foreach (var pl in _placed)
            {
                if (pl.isSign || (pl.part != null && pl.part.isSign)) continue;   // sign → keep separate (texture)
                bool folded = false;
                foreach (var mf in pl.go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    combines.Add(new CombineInstance
                    {
                        mesh = CloneRoleColored(mf.sharedMesh, pl.roles, osmId, palette),
                        transform = mf.transform.localToWorldMatrix,
                    });
                    folded = true;
                }
                if (folded) UnityEngine.Object.DestroyImmediate(pl.go);
            }

            var combined = new Mesh { name = $"building_{osmId}" };
            combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combined.CombineMeshes(combines.ToArray(), true, true);   // computes bounds itself
            foreach (var ci in combines) UnityEngine.Object.DestroyImmediate(ci.mesh);  // temp sources
            // NOTE: `massMesh` itself is deliberately NOT freed here (it used to be, once it had
            // been folded into `combined`). The collider below needs the undecorated mass volume,
            // so it is saved as its own asset instead — see the collider comment (#455). The
            // combine folded in a *clone* (CloneSolidColor instantiates), so `massMesh` is
            // untouched by the vertex-colour bake and can be persisted as-is.

            SaveMeshAsset(combined, GeneratedAssets.BuildingMesh(coord, osmId));
            root.AddComponent<MeshFilter>().sharedMesh = combined;
            root.AddComponent<MeshRenderer>().sharedMaterial = massMaterial;

            // Templated buildings need collision too, exactly like the merged path (#415): a
            // non-convex MeshCollider so on-foot spray raycasts hit the facade and cars/player
            // stop at the wall. `root` stays on the Default layer the spray mask and
            // character/vehicle collision expect (new GameObject defaults to it).
            //
            // The collider cooks the MASS mesh, not `combined` (#455). Nothing in the gameplay
            // contract wants the decoration: colliding with a window's individual muntin bars is
            // strictly worse than colliding with the wall plane, and once #452's generated
            // geometry lands `combined` carries every bar, bracket and sill profile — per-building
            // collider cook is already a measured import bottleneck (#263). Cooking the mass keeps
            // the collider's triangle count independent of how decorated the building is.
            //
            // It has to be a saved asset, not the in-memory mesh: the chunk is written out as a
            // prefab, and a prefab reference to an unpersisted Mesh serialises as null. Cost is one
            // extra small .mesh per templated building on disk, and the mass hull resident in
            // memory alongside `combined` at runtime instead of only `combined`.
            massMesh.name = $"building_{osmId}_collision";
            SaveMeshAsset(massMesh, GeneratedAssets.BuildingCollisionMesh(coord, osmId));
            root.AddComponent<MeshCollider>().sharedMesh = massMesh;
        }

        NeighborhoodPalette ResolvePalette(string neighborhood)
            => (!string.IsNullOrEmpty(neighborhood) && _palettes.TryGetValue(neighborhood, out var p)) ? p : null;

        // The building's resolved colour for a role: the neighborhood palette seeded by
        // hash(osm_id, role) — deterministic — or a neutral per-role default when no palette.
        static Color32 ResolveRoleColor(long osmId, NeighborhoodPalette palette, MaterialRole role)
            => palette != null ? (Color32)palette.Resolve(role, SeedFor(osmId, (int)role, 0)) : DefaultRoleColor(role);

        static Color32 DefaultRoleColor(MaterialRole role)
        {
            switch (role)
            {
                case MaterialRole.Glass: return new Color32(58, 74, 85, 255);
                case MaterialRole.Metal: return new Color32(107, 107, 107, 255);
                case MaterialRole.Accent1: return new Color32(182, 160, 106, 255);
                case MaterialRole.Accent2: return new Color32(150, 130, 90, 255);
                default: return new Color32(216, 201, 168, 255);   // Base / Sign fallback
            }
        }

        /// <summary>The neighborhood-palette Base colour for a building, for the importer's
        /// fallback (un-templated) path — so both paths read the same palette SO and stay
        /// visually coherent. False when the building has no facts or no neighborhood palette.</summary>
        public bool TryFallbackColor(long osmId, out Color32 color)
        {
            color = default;
            if (!_facts.TryGetValue(osmId, out var facts)) return false;
            var palette = ResolvePalette(facts.neighborhood);
            if (palette == null) return false;
            color = ResolveRoleColor(osmId, palette, MaterialRole.Base);
            return true;
        }

        static Mesh CloneSolidColor(Mesh src, Color32 color)
        {
            var m = UnityEngine.Object.Instantiate(src);
            var colors = new Color32[m.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = color;
            m.SetColors(colors);
            return m;
        }

        static Mesh CloneRoleColored(Mesh src, MaterialRole[] submeshRoles, long osmId, NeighborhoodPalette palette)
        {
            var m = UnityEngine.Object.Instantiate(src);
            var colors = new Color32[m.vertexCount];
            Color32 baseCol = ResolveRoleColor(osmId, palette, MaterialRole.Base);
            for (int i = 0; i < colors.Length; i++) colors[i] = baseCol;
            // Override per submesh by its role (index-aligned to submeshes, exactly as before — the
            // array now comes from the generator instead of the part asset).
            if (submeshRoles != null)
            {
                int subs = Mathf.Min(m.subMeshCount, submeshRoles.Length);
                for (int s = 0; s < subs; s++)
                {
                    Color32 c = ResolveRoleColor(osmId, palette, submeshRoles[s]);
                    foreach (int vi in m.GetIndices(s))
                        if (vi >= 0 && vi < colors.Length) colors[vi] = c;
                }
            }
            m.SetColors(colors);
            return m;
        }

        static void SaveMeshAsset(Mesh mesh, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
        }

        public void LogCoverage(ChunkCoord coord, int fallback)
        {
            Debug.Log($"[BuildingAssembler] {coord}: {_templated} templated, {fallback} fallback " +
                      $"(merged) of {_templated + fallback} buildings; {_partMeshes.Generated} part " +
                      $"mesh(es) generated, {_partMeshes.Hits} served from cache; " +
                      $"{_stretchedParts} facade-sized part(s) resolved over " +
                      $"{_stretchedParams.Count} distinct width bucket(s) (#487).");
        }

        // ---- building-specific overrides (#273, design §Placement Model) -------

        void ApplyOverride(Transform parent, long osmId, BuildingFactsJson facts)
        {
            if (_overrides == null || !_overrides.TryGetValue(osmId, out var ov)) return;

            // Hash guard — never dress the wrong building. An override MUST carry a footprint_hash
            // that equals the bake's (design D3): a footprint edit changes the bake's hash, and a
            // missing hash means it was never bound to this footprint. Either way → skip + warn.
            if (string.IsNullOrEmpty(ov.footprint_hash) ||
                !string.Equals(ov.footprint_hash, facts.footprint_hash, StringComparison.Ordinal))
            {
                Debug.LogWarning($"[BuildingAssembler] override for building {osmId} skipped: " +
                                 $"footprint_hash '{ov.footprint_hash}' does not match the bake's " +
                                 $"'{facts.footprint_hash}' (missing or stale — not applied).");
                return;
            }

            // Suppress template parts the override replaces, matched by (part, facade, floor).
            if (ov.suppress != null)
                foreach (var s in ov.suppress)
                    SuppressPlaced(s);

            // Add the override's exact placements — they win over the template (applied last). A
            // placement with a signAsset is a sign (own texture, kept out of the combine).
            if (ov.placements != null)
                foreach (var p in ov.placements)
                {
                    if (!ParseFacade(p.facade, out var facade)) continue;
                    // signAsset flags the placement as a sign so it stays out of the mesh-combine
                    // (own texture). Loading the named sign PNG onto the part is deferred to the
                    // sign-texture path (#275/#278); here it renders as a placeholder.
                    bool isSign = !string.IsNullOrEmpty(p.signAsset);
                    foreach (var f in FacadesFor(facade, facts))
                        PlacePart(parent, f, facts, p.part, p.floor, p.x, p.y, p.scale, p.rotation, facade,
                                  isSign, respectCorners: false);
                }
        }

        void SuppressPlaced(OverridePlacementJson s)
        {
            if (!ParseFacade(s.facade, out var facade)) return;
            for (int i = _placed.Count - 1; i >= 0; i--)
            {
                var pl = _placed[i];
                if (pl.partId == s.part && pl.facade == facade && pl.floor == s.floor)
                {
                    if (pl.go != null) UnityEngine.Object.DestroyImmediate(pl.go);
                    _placed.RemoveAt(i);
                }
            }
        }

        static bool ParseFacade(string s, out Facade facade)
        {
            if (string.IsNullOrEmpty(s)) { facade = Facade.Front; return true; }
            // IsDefined rejects out-of-range numeric strings ("99") that TryParse would accept.
            return Enum.TryParse(s, true, out facade) && Enum.IsDefined(typeof(Facade), facade);
        }

        // ---- placement ---------------------------------------------------------

        // Roof parts (data-model.md §2: TemplateDef.roofParts, e.g. "cornice_sunset") are direct
        // BuildingPart references, not placement directives — they carry no per-instance facade/
        // floor/x/y like Exact/Procedural placements do. Each is placed once per street-facing
        // facade (corner buildings dress every street edge, design D2), centred, at the roofline:
        // floor = facts.floor_count / ny = 1 overshoots the top floor band, which PlacePart clamps
        // to the building's real facade_height_m (the #279 fact), landing exactly on the roofline.
        //
        // Unless the part's generator is path-aware, in which case the whole point is that it does
        // NOT stop at each facade's end: a cornice on a corner building is one mitred run around the
        // corner, not two runs butted against it (#459).
        void PlaceRoofParts(Transform parent, BuildingTemplate template, BuildingFactsJson facts)
        {
            if (template.roofParts == null) return;
            foreach (var part in template.roofParts)
            {
                if (part == null || string.IsNullOrEmpty(part.id)) continue;
                if (TryPlaceWrapped(parent, part, facts)) continue;
                foreach (var f in FacadesFor(Facade.Street, facts))
                    PlacePart(parent, f, facts, part.id, facts.floor_count, 0.5f, 1f, 1f, 0f, Facade.Roof);
            }
        }

        /// <summary>The continuity half of #459: place a banding part <b>once</b>, swept along the
        /// polyline that chains every street facade through its corners, instead of once per facade.
        /// <para>False — and the caller falls back to per-facade placement — when the part's
        /// generator is not an <see cref="IPathPartGenerator"/>, or the sidecar carried no usable
        /// facade geometry to chain. Since no path-aware family exists yet, this returns false for
        /// every part in the library today and the roofline is placed exactly as it was.</para></summary>
        bool TryPlaceWrapped(Transform parent, BuildingPart part, BuildingFactsJson facts)
        {
            if (part == null || _corners == null) return false;
            if (string.IsNullOrEmpty(part.generatorId)) return false;
            if (!PartGenerators.TryResolve(part.generatorId, out var generator)) return false;
            if (!(generator is IPathPartGenerator pathGenerator)) return false;

            float y = PlacementY(facts, facts.floor_count, 1f);
            if (!_corners.TryBuildRun(y, part.mountDepthMeters, out FacadeRun world)) return false;

            // World → part-local: the run has no single bearing, so its frame is world-aligned with
            // the origin at its first point (FacadeRun's frame contract) and the GameObject carries
            // no rotation or scale.
            Vector3 anchor = world.points[0];
            FacadeRun run = world.Rebased(anchor);

            var parameters = part.parameters ?? PartParams.Empty;
            // A wrapped run is this building's own geometry, so the run itself has to be in the
            // cache key or two differently-shaped corners would share one mesh. Buildings whose
            // runs agree to the cache's 5 mm quantum still share, which is the most sharing that is
            // correct here.
            var mesh = _partMeshes.GetOrCreate(
                PartKey.From($"{part.generatorId}|wrap|{part.id}", parameters.Detail, RunKeyNumbers(run)),
                mb => pathGenerator.GenerateAlong(parameters, run, mb));
            if (!mesh.IsValid)
            {
                WarnOnce(part.id, $"generator '{part.generatorId}' produced no mesh for the wrapped run");
                return false;   // fall back to per-facade placement rather than losing the part
            }

            var child = new GameObject(part.id);
            child.AddComponent<MeshFilter>().sharedMesh = mesh.mesh;
            child.AddComponent<MeshRenderer>();
            child.transform.SetParent(parent, false);
            child.transform.position = anchor;
            child.transform.localScale = Vector3.one;

            _placed.Add(new Placed
            {
                go = child, part = part, roles = mesh.submeshRoles, partId = part.id,
                facade = Facade.Roof, floor = facts.floor_count, isSign = part.isSign,
            });
            return true;
        }

        // Flatten a run into cache-key material: every point plus a closed flag well outside the
        // coordinate range so an open and a closed run of the same points never collide.
        static float[] RunKeyNumbers(FacadeRun run)
        {
            var nums = new float[run.points.Length * 3 + 1];
            for (int i = 0; i < run.points.Length; i++)
            {
                nums[i * 3] = run.points[i].x;
                nums[i * 3 + 1] = run.points[i].y;
                nums[i * 3 + 2] = run.points[i].z;
            }
            nums[nums.Length - 1] = run.closed ? 1e6f : -1e6f;
            return nums;
        }

        // Exact placements are the fixed bones of a style: they claim their space (so rules keep
        // clear of them) but never yield it, which is the stated precedence Exact > Procedural.
        // Two exacts on one floor are still separated by the author's own x values —
        // TemplateWiringTests.GroundFloorExactsClearEachOtherOnARealFacade is the guard.
        void PlaceExact(Transform parent, ExactPlacement p, BuildingFactsJson facts)
        {
            foreach (var f in FacadesFor(p.facade, facts))
                PlacePart(parent, f, facts, p.part, p.floor, p.x, p.y, p.scale, p.rotation, p.facade,
                          recordOccupancy: true);
        }

        // The facade(s) a placement targets: Front → primary street facade; Back/Left/Right →
        // the matching best-effort edge (#407, null if the building had no front to reason
        // one from); Street → every ranked street facade; Roof isn't a placement target here
        // (PlaceRoofParts always targets Street) → warn + none.
        IEnumerable<StreetFacadeJson> FacadesFor(Facade facade, BuildingFactsJson facts)
        {
            if (facade == Facade.Front)
            {
                var f = PrimaryFacade(facts);
                if (f != null) yield return f;
            }
            else if (facade == Facade.Back)
            {
                if (facts.back_facade != null) yield return facts.back_facade;
            }
            else if (facade == Facade.Left)
            {
                if (facts.left_facade != null) yield return facts.left_facade;
            }
            else if (facade == Facade.Right)
            {
                if (facts.right_facade != null) yield return facts.right_facade;
            }
            else if (facade == Facade.Street)
            {
                if (facts.street_facades != null)
                    foreach (var f in facts.street_facades) yield return f;
            }
            else
            {
                Debug.LogWarning($"[BuildingAssembler] building {facts.osm_id}: facade {facade} not " +
                                 "supported as a placement target; skipped.");
            }
        }

        // Place one part on one facade at normalized (nx, ny) on floor `floor`. Shared by the
        // Exact and Procedural paths; all the facade-frame math lives here.
        //
        // `respectCorners` is the #459 exclusion. It is on for everything the template drives and
        // off for building-specific overrides, matching the stated precedence (Building-Specific >
        // Exact > Procedural): a human who placed a part at a corner on purpose meant it.
        //
        // `consultOccupancy` / `recordOccupancy` are the two halves of the #491 occupancy table,
        // and the defaults encode the same precedence:
        //   • procedural  — consults and records (it must dodge everything and be dodged);
        //   • exact       — records only (fixed bones: always placed, never displaced);
        //   • roof parts  — neither. They sit at the roofline above the facade band, and a
        //                   per-facade cornice fallback claiming the top floor would start
        //                   suppressing top-floor windows, which no issue asks for;
        //   • override    — neither, for the same reason it ignores corners.
        // `sourceRule` is the rule index behind a procedural placement, so a rule never excludes
        // against its own slots — spacing within one family is the repeat pitch's job.
        //
        // `stretchMeters` > 0 rebuilds the part at that width by overwriting one named parameter
        // (#487) instead of scaling it.
        void PlacePart(Transform parent, StreetFacadeJson f, BuildingFactsJson facts,
                       string partId, int floor, float nx, float ny, float scale, float rotationDeg,
                       Facade facadeEnum, bool isSign = false, bool respectCorners = true,
                       int sourceRule = FacadeOccupancy.NotARule,
                       bool consultOccupancy = false, bool recordOccupancy = false,
                       float stretchMeters = 0f, string stretchParam = null)
        {
            if (f.edge == null || f.edge.Length < 4) return;

            Vector3 a = new Vector3(f.edge[0], facts.base_y, f.edge[1]);
            Vector3 b = new Vector3(f.edge[2], facts.base_y, f.edge[3]);
            Vector3 along = b - a;
            float len = along.magnitude;
            if (len < 1e-4f) return;
            along /= len;

            // Outward normal straight from the sidecar bearing (winding-independent), so the
            // part faces the street regardless of OSM footprint orientation.
            Vector3 outward = FacadeFrame.OutwardNormal(f.bearing_deg);

            // Normalized facade coords → world. x along the real facade width; y up the facade
            // (floor band + within-floor offset), both scaled to this building's real frame.
            Vector3 pos = a + along * (Mathf.Clamp01(nx) * len);
            pos.y = PlacementY(facts, floor, ny);

            BuildingPart part = ResolvePart(partId);
            float mountDepth = part != null ? part.mountDepthMeters : 0f;
            pos += outward * mountDepth;   // outward is horizontal, so this leaves pos.y intact

            if (!TryPartMesh(part, partId, stretchMeters, stretchParam, out PartMesh mesh))
                return;   // no generator → nothing to place (#454)

            // Corner reconciliation (#459/#488). The part's real extents come off the generated
            // mesh, so the rule needs no authored parameter and follows whatever a family actually
            // builds: half-width along the facade for the overhang, mount depth + outward extent
            // for how far it stands proud. Cheap — the mesh is already cached by this point — and,
            // crucially, it draws no random numbers, so rejecting a slot cannot shift any other
            // slot's seeded choices (each slot's Rng is seeded independently by
            // hash(osm_id, ruleIndex, slot)).
            float s = scale <= 0f ? 1f : scale;
            Bounds bounds = mesh.mesh.bounds;
            if (respectCorners && _corners != null &&
                _corners.Blocked(f, nx, bounds.min.x * s, bounds.max.x * s,
                                 bounds.max.z * s + Mathf.Max(mountDepth, 0f)))
                return;

            // Occupancy (#491), read off the same mesh for the same reason: what matters is what
            // the family actually built, not what a preset says it is. The along-facade interval is
            // in metres from the facade's start, so nothing here is capped by a rule's repeat
            // pitch; the vertical interval is world Y, so an artifact that rises through two floors
            // is felt on both without anyone authoring a floorsSpanned anywhere.
            float alongCenter = Mathf.Clamp01(nx) * len;
            float spanMin = alongCenter + bounds.min.x * s;
            float spanMax = alongCenter + bounds.max.x * s;
            float spanBottom = pos.y + bounds.min.y * s;
            float spanTop = pos.y + bounds.max.y * s;

            if (consultOccupancy &&
                _occupancy.Occupied(f.edge_index, sourceRule, spanMin, spanMax, spanBottom, spanTop))
                return;

            var child = InstantiatePart(part, mesh);
            MaterialRole[] roles = mesh.submeshRoles;

            child.transform.SetParent(parent, false);
            child.transform.position = pos;
            // A generated part authors +Z outward (design #452 generators.md, part-local frame), so
            // point +Z along the facade's outward normal. Then apply the placement's roll about it.
            child.transform.rotation = Quaternion.AngleAxis(rotationDeg, outward) *
                                       Quaternion.LookRotation(outward, Vector3.up);
            // Scale = the placement scale only: the generated mesh already carries its real size,
            // built from the part's own parameters.
            child.transform.localScale = Vector3.one * s;

            if (recordOccupancy)
                _occupancy.Add(f.edge_index, sourceRule, spanMin, spanMax, spanBottom, spanTop);

            _placed.Add(new Placed { go = child, part = part, roles = roles, partId = partId, facade = facadeEnum, floor = floor, isSign = isSign });
        }

        /// <summary>World Y for a placement on <paramref name="floor"/> at within-floor offset
        /// <paramref name="ny"/>, clamped to the building's real facade height (the #279
        /// <c>facade_height_m</c> fact) so a too-high floor index cannot float a part above the
        /// roof. Shared by <see cref="PlacePart"/> and the wrapped-run roofline so the two agree on
        /// where the roofline is.</summary>
        static float PlacementY(BuildingFactsJson facts, int floor, float ny)
        {
            float y = facts.base_y + (floor + Mathf.Clamp01(ny)) * FloorHeightMeters;
            float facadeTop = facts.base_y + Mathf.Max(facts.facade_height_m, FloorHeightMeters);
            return Mathf.Min(y, facadeTop);
        }

        // ---- procedural rule engine (#271) -------------------------------------

        // Deterministic per-slot draws: a tiny xorshift32 seeded by hash(osm_id, ruleIndex,
        // slotIndex), so every random choice (count rounding, probability, jitter, variant) is
        // reproducible — the same building always assembles identically (design §Placement Model).
        struct Rng
        {
            uint _s;
            public Rng(uint seed) { _s = seed == 0u ? 1u : seed; }
            public uint NextUInt() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
            public float NextFloat() => (NextUInt() & 0xFFFFFFu) / 16777216f;   // [0,1)
            public float Range(float lo, float hi) => lo + (hi - lo) * NextFloat();
        }

        void PlaceProcedural(Transform parent, ProceduralRule rule, BuildingFactsJson facts, int ruleIndex)
        {
            if (rule == null) return;
            bool hasPart = !string.IsNullOrEmpty(rule.part) ||
                           (rule.variants != null && rule.variants.Length > 0);
            if (!hasPart) return;

            float x0 = rule.span != null && rule.span.Length > 0 ? Mathf.Clamp01(rule.span[0]) : 0f;
            float x1 = rule.span != null && rule.span.Length > 1 ? Mathf.Clamp01(rule.span[1]) : 1f;
            float margin = Mathf.Clamp01(rule.constraints.edgeMargin);
            x0 = Mathf.Clamp01(x0 + margin);
            x1 = Mathf.Clamp01(x1 - margin);
            if (x1 <= x0) return;

            // Floor band, clamped to the building's real floor count (no parts above the roof).
            int floorMin = Mathf.Max(rule.floorRange.min, 0);
            int floorMax = Mathf.Max(rule.floorRange.max, floorMin);
            floorMax = Mathf.Min(floorMax, Mathf.Max(facts.floor_count, 1) - 1);
            if (floorMax < floorMin) return;

            foreach (var f in FacadesFor(rule.facade, facts))
            {
                float facadeLen = FacadeLength(f);
                if (facadeLen < 1e-3f) continue;

                // Count from spacing across the real span, honouring min spacing as a floor and
                // the rule's count bounds — so a wider building deterministically gets more parts.
                float spanMeters = (x1 - x0) * facadeLen;
                float spacing = Mathf.Max(rule.repeat.spacingMeters, rule.constraints.minSpacingMeters, 0.1f);
                int count = Mathf.FloorToInt(spanMeters / spacing);
                // countMin guarantees a minimum even on a sub-spacing facade (an intended floor on
                // the low end); countMax caps it. Above the floor, count grows with facade width.
                count = Mathf.Max(count, Mathf.Max(rule.repeat.countMin, 0));
                if (rule.repeat.countMax > 0) count = Mathf.Min(count, rule.repeat.countMax);
                if (count <= 0) continue;

                // Size the part to the facade rather than to its authored width (#487). The span is
                // the rule's own, after edgeMargin, so a rule that stretches also decides how much
                // of the facade it is entitled to. With countMax = 1 the single slot sits at the
                // span's centre, which is exactly where a part of this width belongs.
                //
                // `rule.constraints.minSpacingMeters` no longer sets an exclusion radius: the
                // occupancy table (#491) compares the parts' real extents instead, which is what
                // lifts the old ceiling (the radius could not exceed the repeat pitch without also
                // changing the slot count). It keeps its other job, as a floor under `spacing`.
                float stretchMeters = rule.stretchToFacade ? (x1 - x0) * facadeLen : 0f;

                for (int floor = floorMin; floor <= floorMax; floor++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        // Wide stride so (floor, slot) never collide in the seed for any real count.
                        var rng = new Rng(SeedFor(facts.osm_id, ruleIndex, floor * 65536 + i));

                        // probability ≤ 0 is the serialized default ("unset") → place; express "off"
                        // by omitting the rule. Otherwise a seeded Bernoulli trial.
                        float prob = rule.probability <= 0f ? 1f : Mathf.Clamp01(rule.probability);
                        if (prob < 1f && rng.NextFloat() >= prob) continue;

                        float t = count == 1 ? 0.5f : (i + 0.5f) / count;
                        float nx = x0 + t * (x1 - x0);
                        if (rule.jitter.x != 0f)
                            nx += rng.Range(-rule.jitter.x, rule.jitter.x) / facadeLen;  // metres → normalized
                        nx = Mathf.Clamp(nx, x0, x1);

                        // align-to-floor-line → sit on the floor line; else mid-floor.
                        float ny = rule.constraints.alignToFloorLine ? 0f : 0.5f;

                        string partId = PickVariant(rule, ref rng);

                        float scale = 1f;
                        if (rule.jitter.scale != null && rule.jitter.scale.Length >= 2)
                            scale = rng.Range(rule.jitter.scale[0], rule.jitter.scale[1]);

                        float rot = rule.jitter.rotation != 0f
                            ? rng.Range(-rule.jitter.rotation, rule.jitter.rotation) : 0f;

                        // Every procedural placement both dodges and joins the occupancy table, so
                        // the exclusion is no longer exact → procedural only: rule N sees the
                        // exacts AND rules 0..N-1, and is itself seen by rules N+1... The check
                        // happens inside PlacePart, where the generated mesh's real extents are
                        // known — and draws no random numbers, so a refused slot cannot shift any
                        // other slot's seeded choices.
                        PlacePart(parent, f, facts, partId, floor, nx, ny, scale, rot, rule.facade,
                                  sourceRule: ruleIndex, consultOccupancy: true, recordOccupancy: true,
                                  stretchMeters: stretchMeters, stretchParam: rule.StretchParamName);
                    }
                }
            }
        }

        static string PickVariant(ProceduralRule rule, ref Rng rng)
        {
            if (rule.variants != null && rule.variants.Length > 0)
                return rule.variants[(int)(rng.NextUInt() % (uint)rule.variants.Length)];
            return rule.part;
        }

        static float FacadeLength(StreetFacadeJson f) => FacadeCornerTable.EdgeLength(f);

        /// <summary>The seam (design #452 D2, #454): resolve the part's generator and build (or
        /// reuse) its mesh. False when the part cannot be generated — there is deliberately no
        /// fallback geometry, so a missing generator is visibly missing rather than quietly a grey
        /// quad.
        /// <para>Split out from <see cref="InstantiatePart"/> for #459: the corner rule needs the
        /// mesh's real extents to decide whether the placement fits, which has to happen before a
        /// GameObject is made for it.</para></summary>
        bool TryPartMesh(BuildingPart part, string partId, float stretchMeters, string stretchParam,
                         out PartMesh mesh)
        {
            mesh = default;

            if (part == null) return WarnOnce(partId, $"no BuildingPart with id '{partId}'");
            if (string.IsNullOrEmpty(part.generatorId))
                return WarnOnce(partId, $"part '{partId}' has no generatorId");
            if (!PartGenerators.TryResolve(part.generatorId, out var generator))
                return WarnOnce(partId, $"part '{partId}' names unknown generator " +
                                        $"'{part.generatorId}' (known: {string.Join(", ", PartGenerators.Ids)})");

            var parameters = part.parameters ?? PartParams.Empty;
            if (stretchMeters > 0f)
                parameters = StretchedParams(part, parameters, stretchParam, stretchMeters);

            mesh = _partMeshes.GetOrCreate(parameters.KeyFor(part.generatorId),
                                           mb => generator.Generate(parameters, mb));
            if (!mesh.IsValid)
                return WarnOnce(partId, $"generator '{part.generatorId}' produced no mesh for part '{partId}'");

            return true;
        }

        /// <summary>The part's parameters with its span-wise dimension overwritten by the facade's
        /// own metres (#487), snapped to <see cref="PartKey.QuantumMeters"/>.
        /// <para>Snapping before the override — rather than letting <see cref="PartKey"/> quantise
        /// only the hash — is what makes a stretched part's geometry a pure function of its 5 mm
        /// bucket. Quantise only the key and the mesh a bucket ends up holding is whichever
        /// building in that bucket was assembled first; that is deterministic today, but it makes
        /// the geometry depend on the order buildings appear in the sidecar, which no other part of
        /// the placement model does.</para>
        /// <para>Blocks are memoised per (part, parameter, bucket) so a chunk-wide sweep of
        /// storefronts allocates one block per distinct width, not one per placement.</para></summary>
        PartParams StretchedParams(BuildingPart part, PartParams source, string stretchParam,
                                   float stretchMeters)
        {
            string name = string.IsNullOrEmpty(stretchParam)
                ? ProceduralRule.DefaultStretchParam : stretchParam;
            int bucket = Mathf.RoundToInt(stretchMeters / PartKey.QuantumMeters);

            _stretchedParts++;
            string key = $"{part.id}|{name}|{bucket}";
            if (_stretchedParams.TryGetValue(key, out var cached)) return cached;

            var made = source.WithOverride(name, bucket * PartKey.QuantumMeters);
            _stretchedParams[key] = made;
            return made;
        }

        /// <summary>A GameObject carrying an already-generated part mesh.</summary>
        static GameObject InstantiatePart(BuildingPart part, PartMesh mesh)
        {
            var go = new GameObject(part.id);
            go.AddComponent<MeshFilter>().sharedMesh = mesh.mesh;
            // Renderer for the same reason the placeholder quad had one: sign parts survive
            // BakeAndCombine as live GameObjects and need to draw. Its material is owned by the
            // sign/texture path (#274/#275), not by this seam.
            go.AddComponent<MeshRenderer>();
            return go;
        }

        bool WarnOnce(string partId, string reason)
        {
            if (_warnedParts.Add(partId ?? string.Empty))
                Debug.LogWarning($"[BuildingAssembler] skipping placements of '{partId}': {reason}.");
            return false;
        }

        /// <summary>Destroy the meshes generated for this chunk, once its buildings are assembled
        /// and their decorated combines have been saved as assets.
        /// <para>Safe because every generated mesh is either cloned into a per-building combine (and
        /// the clone, not this mesh, is what the prefab references) or belongs to a part that was
        /// never placed. The one part kind that survives the combine as a live GameObject is a
        /// sign, and signs are texture-based, not generated (design #452 §5) — if a sign generator
        /// is ever written, its mesh must be saved as an asset before this runs, exactly as the
        /// per-building meshes are.</para></summary>
        public void ReleaseGeneratedMeshes() => _partMeshes.Clear();

        BuildingPart ResolvePart(string id)
        {
            if (!string.IsNullOrEmpty(id) && _partsById.TryGetValue(id, out var part)) return part;
            return null;
        }

        // internal: also used by SFMapImporterWindow to pick the front edge for facade
        // backdrop renders (#407).
        internal static StreetFacadeJson PrimaryFacade(BuildingFactsJson facts)
            => (facts.street_facades != null && facts.street_facades.Length > 0)
                ? facts.street_facades[0] : null;

        // FNV-1a of the osm_id → a stable per-building seed (matches the bake's intent: a
        // deterministic choice keyed on osm_id).
        static uint SeedFor(long osmId)
        {
            unchecked
            {
                uint h = 2166136261u;
                ulong v = (ulong)osmId;
                for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 16777619u; }
                return h;
            }
        }

        // Per-slot seed: FNV-1a of (osm_id, ruleIndex, slotIndex) — the design's
        // hash(osm_id, ruleIndex, slotIndex), so each procedural draw is reproducible.
        static uint SeedFor(long osmId, int ruleIndex, int slotIndex)
        {
            unchecked
            {
                uint h = 2166136261u;
                ulong v = (ulong)osmId;
                for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 16777619u; }
                uint a = (uint)ruleIndex, b = (uint)slotIndex;
                for (int i = 0; i < 4; i++) { h ^= (byte)(a >> (i * 8)); h *= 16777619u; }
                for (int i = 0; i < 4; i++) { h ^= (byte)(b >> (i * 8)); h *= 16777619u; }
                return h;
            }
        }

        static List<T> LoadAll<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
            foreach (var guid in AssetDatabase.FindAssets($"t:{typeof(T).Name}"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) list.Add(asset);
            }
            return list;
        }
    }
}
