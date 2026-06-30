using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using SFMap.Pipeline.Buildings;

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
        public int version;
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
        readonly Dictionary<long, OverrideJson> _overrides;

        // Exact placement positions of the building currently being assembled, keyed by
        // (facade edge_index, floor) → normalized x's, so procedural rules with avoidExact can
        // skip slots that land on an Exact part. Reset per building in Assemble.
        readonly Dictionary<long, List<float>> _exactMarks = new Dictionary<long, List<float>>();

        // Parts placed for the current building, collected so BakeAndCombine can colour and fold
        // the non-sign ones into one mesh. Reset per building.
        struct Placed { public GameObject go; public BuildingPart part; public string partId; public Facade facade; public int floor; public bool isSign; }
        readonly List<Placed> _placed = new List<Placed>();

        int _templated;

        public int Templated => _templated;

        BuildingAssembler(Dictionary<long, BuildingFactsJson> facts,
                          List<BuildingTemplate> templates,
                          Dictionary<string, BuildingPart> partsById,
                          Dictionary<string, NeighborhoodPalette> palettes,
                          Dictionary<long, OverrideJson> overrides)
        {
            _facts = facts;
            _templates = templates;
            _partsById = partsById;
            _palettes = palettes;
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

            return new BuildingAssembler(facts, templates, partsById, palettes, LoadOverrides());
        }

        // Load Assets/SFBuildingTemplates/Overrides/*.override.json into a dict keyed by osm_id.
        // The footprint_hash guard is checked at apply time, not here.
        const string OverridesDir = "Assets/SFBuildingTemplates/Overrides";

        static Dictionary<long, OverrideJson> LoadOverrides()
        {
            var map = new Dictionary<long, OverrideJson>();
            string abs = Path.Combine(Application.dataPath, "SFBuildingTemplates/Overrides");
            if (!Directory.Exists(abs)) return map;
            foreach (string file in Directory.GetFiles(abs, "*.override.json", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".override.json", StringComparison.OrdinalIgnoreCase)) continue;  // exclude .meta
                try
                {
                    var ov = JsonUtility.FromJson<OverrideJson>(File.ReadAllText(file));
                    if (ov != null && ov.osm_id != 0) map[ov.osm_id] = ov;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BuildingAssembler] Failed to parse override {file}: {ex.Message}");
                }
            }
            return map;
        }

        /// <summary>Does this building have classification facts AND a compatible template?
        /// Ties among compatible templates are broken deterministically by osm_id, so a
        /// re-import is byte-stable (design §6 determinism).</summary>
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
            template = matches[(int)(SeedFor(osmId) % (uint)matches.Count)];
            return true;
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
            _exactMarks.Clear();

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
                        mesh = CloneRoleColored(mf.sharedMesh, pl.part, osmId, palette),
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
            // The importer's mass mesh is now folded into `combined` and referenced by nothing
            // else (it's no longer saved as a standalone asset) — free it.
            UnityEngine.Object.DestroyImmediate(massMesh);

            SaveMeshAsset(combined, GeneratedAssets.BuildingMesh(coord, osmId));
            root.AddComponent<MeshFilter>().sharedMesh = combined;
            root.AddComponent<MeshRenderer>().sharedMaterial = massMaterial;
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

        static Mesh CloneRoleColored(Mesh src, BuildingPart part, long osmId, NeighborhoodPalette palette)
        {
            var m = UnityEngine.Object.Instantiate(src);
            var colors = new Color32[m.vertexCount];
            Color32 baseCol = ResolveRoleColor(osmId, palette, MaterialRole.Base);
            for (int i = 0; i < colors.Length; i++) colors[i] = baseCol;
            // Override per submesh by its authored role (submeshRoles index-aligned to submeshes).
            if (part != null && part.submeshRoles != null)
            {
                int subs = Mathf.Min(m.subMeshCount, part.submeshRoles.Length);
                for (int s = 0; s < subs; s++)
                {
                    Color32 c = ResolveRoleColor(osmId, palette, part.submeshRoles[s]);
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
                      $"(merged) of {_templated + fallback} buildings.");
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
                        PlacePart(parent, f, facts, p.part, p.floor, p.x, p.y, p.scale, p.rotation, facade, isSign);
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

        void PlaceExact(Transform parent, ExactPlacement p, BuildingFactsJson facts)
        {
            foreach (var f in FacadesFor(p.facade, facts))
            {
                long key = ExactKey(f.edge_index, p.floor);
                if (!_exactMarks.TryGetValue(key, out var marks)) { marks = new List<float>(); _exactMarks[key] = marks; }
                marks.Add(Mathf.Clamp01(p.x));
                PlacePart(parent, f, facts, p.part, p.floor, p.x, p.y, p.scale, p.rotation, p.facade);
            }
        }

        static long ExactKey(int edgeIndex, int floor) => ((long)edgeIndex << 16) | (uint)(floor & 0xFFFF);

        bool NearExactMark(int edgeIndex, int floor, float nx, float radius)
        {
            if (_exactMarks.TryGetValue(ExactKey(edgeIndex, floor), out var marks))
                foreach (var ex in marks)
                    if (Mathf.Abs(ex - nx) < radius) return true;
            return false;
        }

        // The facade(s) a placement targets: Front → primary street facade; Street → every
        // ranked street facade; other faces aren't carried in the sidecar (MVP) → warn + none.
        IEnumerable<StreetFacadeJson> FacadesFor(Facade facade, BuildingFactsJson facts)
        {
            if (facade == Facade.Front)
            {
                var f = PrimaryFacade(facts);
                if (f != null) yield return f;
            }
            else if (facade == Facade.Street)
            {
                if (facts.street_facades != null)
                    foreach (var f in facts.street_facades) yield return f;
            }
            else
            {
                Debug.LogWarning($"[BuildingAssembler] building {facts.osm_id}: facade {facade} not " +
                                 "supported yet (only Front/Street carry sidecar geometry); skipped.");
            }
        }

        // Place one part on one facade at normalized (nx, ny) on floor `floor`. Shared by the
        // Exact and Procedural paths; all the facade-frame math lives here.
        void PlacePart(Transform parent, StreetFacadeJson f, BuildingFactsJson facts,
                       string partId, int floor, float nx, float ny, float scale, float rotationDeg,
                       Facade facadeEnum, bool isSign = false)
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
            float br = f.bearing_deg * Mathf.Deg2Rad;
            Vector3 outward = new Vector3(Mathf.Sin(br), 0f, Mathf.Cos(br));

            // Normalized facade coords → world. x along the real facade width; y up the facade
            // (floor band + within-floor offset), both scaled to this building's real frame.
            Vector3 pos = a + along * (Mathf.Clamp01(nx) * len);
            pos.y = facts.base_y + (floor + Mathf.Clamp01(ny)) * FloorHeightMeters;
            // Don't let a too-high floor index float the part above the roof: clamp to the
            // building's real facade height (the #279 facade_height_m fact).
            float facadeTop = facts.base_y + Mathf.Max(facts.facade_height_m, FloorHeightMeters);
            pos.y = Mathf.Min(pos.y, facadeTop);

            BuildingPart part = ResolvePart(partId);
            float mountDepth = part != null ? part.mountDepthMeters : 0f;
            pos += outward * mountDepth;   // outward is horizontal, so this leaves pos.y intact

            var child = InstantiatePart(part, partId, out bool isPlaceholder);
            child.transform.SetParent(parent, false);
            child.transform.position = pos;
            // A real GLB part authors its front as +Z, so point +Z outward. The placeholder
            // Quad's visible face is -Z, so face *that* to the street (else it's back-face-culled
            // from outside). Either way apply the placement's roll about the outward normal.
            Vector3 facing = isPlaceholder ? -outward : outward;
            child.transform.rotation = Quaternion.AngleAxis(rotationDeg, outward) *
                                       Quaternion.LookRotation(facing, Vector3.up);
            // Scale = the placement scale, applied on top of the placeholder's authored size
            // (a real prefab carries its own size, so its base is unit).
            float s = scale <= 0f ? 1f : scale;
            Vector3 baseScale = Vector3.one;
            if (isPlaceholder && part != null && part.sizeMeters != Vector3.zero)
                baseScale = new Vector3(Mathf.Max(part.sizeMeters.x, 0.1f),
                                        Mathf.Max(part.sizeMeters.y, 0.1f), 1f);
            child.transform.localScale = baseScale * s;

            _placed.Add(new Placed { go = child, part = part, partId = partId, facade = facadeEnum, floor = floor, isSign = isSign });
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

                // Exclusion radius (normalized) for avoidExact: keep a procedural part this far
                // from any Exact placement on the same facade edge + floor.
                float exclusion = Mathf.Max(rule.constraints.minSpacingMeters, spacing * 0.5f) / facadeLen;

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

                        if (rule.constraints.avoidExact && NearExactMark(f.edge_index, floor, nx, exclusion))
                            continue;

                        // align-to-floor-line → sit on the floor line; else mid-floor.
                        float ny = rule.constraints.alignToFloorLine ? 0f : 0.5f;

                        string partId = PickVariant(rule, ref rng);

                        float scale = 1f;
                        if (rule.jitter.scale != null && rule.jitter.scale.Length >= 2)
                            scale = rng.Range(rule.jitter.scale[0], rule.jitter.scale[1]);

                        float rot = rule.jitter.rotation != 0f
                            ? rng.Range(-rule.jitter.rotation, rule.jitter.rotation) : 0f;

                        PlacePart(parent, f, facts, partId, floor, nx, ny, scale, rot, rule.facade);
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

        static float FacadeLength(StreetFacadeJson f)
        {
            if (f.edge == null || f.edge.Length < 4) return 0f;
            float dx = f.edge[2] - f.edge[0], dz = f.edge[3] - f.edge[1];
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        GameObject InstantiatePart(BuildingPart part, string partId, out bool isPlaceholder)
        {
            if (part != null && part.prefab != null)
            {
                isPlaceholder = false;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(part.prefab);
                go.name = part.id;
                return go;
            }
            // No imported GLB yet: a flat placeholder quad (sized by the caller to the authored
            // part), so the placement is visible/correct before the geometry is authored — the
            // GLB is wired into BuildingPart.prefab by the #269 importer once glTFast + the .glb land.
            isPlaceholder = true;
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"{partId} (placeholder)";
            // CreatePrimitive adds a MeshCollider; buildings are non-interactive, so drop it
            // (it would otherwise bake a stray collider into the chunk prefab).
            var col = quad.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
            return quad;
        }

        BuildingPart ResolvePart(string id)
        {
            if (!string.IsNullOrEmpty(id) && _partsById.TryGetValue(id, out var part)) return part;
            return null;
        }

        static StreetFacadeJson PrimaryFacade(BuildingFactsJson facts)
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
