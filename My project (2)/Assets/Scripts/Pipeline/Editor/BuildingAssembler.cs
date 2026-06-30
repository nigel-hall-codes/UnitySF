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

        int _templated;

        public int Templated => _templated;

        BuildingAssembler(Dictionary<long, BuildingFactsJson> facts,
                          List<BuildingTemplate> templates,
                          Dictionary<string, BuildingPart> partsById)
        {
            _facts = facts;
            _templates = templates;
            _partsById = partsById;
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

            return new BuildingAssembler(facts, templates, partsById);
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
        public void Assemble(Transform buildingsParent, long osmId, Mesh massMesh,
                             BuildingFactsJson facts, BuildingTemplate template, Material massMaterial)
        {
            var root = new GameObject($"building_{osmId}");
            root.transform.SetParent(buildingsParent, false);

            // The Python mass, as-is (already palette-vertex-coloured by the importer).
            var massGo = new GameObject("Mass");
            massGo.transform.SetParent(root.transform, false);
            massGo.AddComponent<MeshFilter>().sharedMesh = massMesh;
            massGo.AddComponent<MeshRenderer>().sharedMaterial = massMaterial;

            if (template.exact == null) { _templated++; return; }
            foreach (var p in template.exact)
                PlaceExact(root.transform, p, facts);

            _templated++;
        }

        public void LogCoverage(ChunkCoord coord, int fallback)
        {
            Debug.Log($"[BuildingAssembler] {coord}: {_templated} templated, {fallback} fallback " +
                      $"(merged) of {_templated + fallback} buildings.");
        }

        // ---- placement ---------------------------------------------------------

        void PlaceExact(Transform parent, ExactPlacement p, BuildingFactsJson facts)
        {
            // Resolve which facade(s) this placement targets. Front = primary street facade;
            // Street = every ranked street facade; other faces aren't carried in the sidecar
            // (MVP), so they're skipped with a warning.
            if (p.facade == Facade.Front)
            {
                var f = PrimaryFacade(facts);
                if (f != null) PlaceOnFacade(parent, p, facts, f);
            }
            else if (p.facade == Facade.Street)
            {
                if (facts.street_facades != null)
                    foreach (var f in facts.street_facades) PlaceOnFacade(parent, p, facts, f);
            }
            else
            {
                Debug.LogWarning($"[BuildingAssembler] building {facts.osm_id}: facade {p.facade} not " +
                                 "supported yet (only Front/Street carry sidecar geometry); skipped.");
            }
        }

        void PlaceOnFacade(Transform parent, ExactPlacement p, BuildingFactsJson facts, StreetFacadeJson f)
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
            Vector3 pos = a + along * (Mathf.Clamp01(p.x) * len);
            pos.y = facts.base_y + (p.floor + Mathf.Clamp01(p.y)) * FloorHeightMeters;
            // Don't let a too-high floor index float the part above the roof: clamp to the
            // building's real facade height (the #279 facade_height_m fact).
            float facadeTop = facts.base_y + Mathf.Max(facts.facade_height_m, FloorHeightMeters);
            pos.y = Mathf.Min(pos.y, facadeTop);

            BuildingPart part = ResolvePart(p.part);
            float mountDepth = part != null ? part.mountDepthMeters : 0f;
            pos += outward * mountDepth;   // outward is horizontal, so this leaves pos.y intact

            var child = InstantiatePart(part, p.part, out bool isPlaceholder);
            child.transform.SetParent(parent, false);
            child.transform.position = pos;
            // A real GLB part authors its front as +Z, so point +Z outward. The placeholder
            // Quad's visible face is -Z, so face *that* to the street (else it's back-face-culled
            // from outside). Either way apply the placement's roll about the outward normal.
            Vector3 facing = isPlaceholder ? -outward : outward;
            child.transform.rotation = Quaternion.AngleAxis(p.rotation, outward) *
                                       Quaternion.LookRotation(facing, Vector3.up);
            // Scale = the placement scale, applied on top of the placeholder's authored size
            // (a real prefab carries its own size, so its base is unit).
            float s = p.scale <= 0f ? 1f : p.scale;
            Vector3 baseScale = Vector3.one;
            if (isPlaceholder && part != null && part.sizeMeters != Vector3.zero)
                baseScale = new Vector3(Mathf.Max(part.sizeMeters.x, 0.1f),
                                        Mathf.Max(part.sizeMeters.y, 0.1f), 1f);
            child.transform.localScale = baseScale * s;
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
