using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using SFMap.Pipeline;

namespace SFMap.Pipeline.Editor
{
    /// <summary>
    /// Standalone facade-decal import path (design #278/#280). Reads the building-specific
    /// override files (their NEW <c>facadeDecals[]</c>, #281), matches each to a baked building
    /// by <c>(osm_id, footprint_hash)</c> from the classification sidecar (#268/#279), and builds
    /// one alpha-textured quad per decal on the building's real facade rect — offset
    /// <c>mountDepth_m</c> proud of the wall (D4 stuck-on, no boolean cut). Quads nest under
    /// <c>BuildingDecals {coord}</c> exactly like parked cars, so <c>ChunkStreamer</c> streams them
    /// unchanged. Independent of the #266 template Assembler — works on any bake with a sidecar.
    /// </summary>
    public static class BuildingDecalImporter
    {
        const string LibraryRoot = "Assets/SFBuildingTemplates";
        const float FloorHeightMeters = 3.0f;

        /// <summary>Build the chunk's facade decals under <paramref name="chunkRoot"/>. No-op when the
        /// sidecar is absent or <paramref name="decalOverrides"/> is empty. The overrides are loaded
        /// once per import by the caller (see <see cref="LoadDecalOverrides"/>) and passed in.</summary>
        public static void Import(string chunkDir, ChunkCoord coord, GameObject chunkRoot,
                                  IReadOnlyList<OverrideJson> decalOverrides)
        {
            if (decalOverrides == null || decalOverrides.Count == 0) return;

            string sidecarPath = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_buildings.json");
            if (!File.Exists(sidecarPath)) return;

            BuildingsSidecarJson sidecar;
            try { sidecar = JsonUtility.FromJson<BuildingsSidecarJson>(File.ReadAllText(sidecarPath)); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BuildingDecalImporter] Failed to parse {sidecarPath}: {ex.Message}");
                return;
            }
            if (sidecar?.buildings == null || sidecar.buildings.Length == 0) return;

            var facts = new Dictionary<long, BuildingFactsJson>(sidecar.buildings.Length);
            foreach (var b in sidecar.buildings) facts[b.osm_id] = b;

            // Batch every decal mesh + material CreateAsset into a single import pass. This pass runs
            // AFTER the importer's own StartAssetEditing/StopAssetEditing block has closed, so without
            // this wrapper each per-decal asset imports one-by-one (the #295 perf note).
            int placed;
            AssetDatabase.StartAssetEditing();
            try     { placed = BuildDecals(coord, chunkRoot, decalOverrides, facts); }
            finally { AssetDatabase.StopAssetEditing(); }

            if (placed > 0)
                Debug.Log($"[BuildingDecalImporter] {coord}: {placed} facade decal(s).");
        }

        // Build one alpha quad per matched decal under a single "BuildingDecals {coord}" parent and
        // return the number placed. Callers wrap this in Start/StopAssetEditing to batch the writes.
        static int BuildDecals(ChunkCoord coord, GameObject chunkRoot,
                               IReadOnlyList<OverrideJson> decalOverrides,
                               Dictionary<long, BuildingFactsJson> facts)
        {
            GameObject decalsParent = null;
            var materialCache = new Dictionary<string, Material>(StringComparer.Ordinal);
            int placed = 0;

            foreach (var ov in decalOverrides)
            {
                if (!facts.TryGetValue(ov.osm_id, out var f)) continue;   // not in this chunk

                // Hash guard — never dress the wrong building (design D3).
                if (string.IsNullOrEmpty(ov.footprint_hash) ||
                    !string.Equals(ov.footprint_hash, f.footprint_hash, StringComparison.Ordinal))
                {
                    Debug.LogWarning($"[BuildingDecalImporter] decals for {ov.osm_id} skipped: " +
                                     $"footprint_hash '{ov.footprint_hash}' != bake '{f.footprint_hash}'.");
                    continue;
                }

                var decals = ov.facadeDecals;
                Array.Sort(decals, (x, y) =>
                {
                    int c = x.layer.CompareTo(y.layer);
                    if (c != 0) return c;
                    c = x.mountDepth_m.CompareTo(y.mountDepth_m);
                    if (c != 0) return c;
                    return string.CompareOrdinal(x.texture ?? "", y.texture ?? "");  // stable tiebreak
                });

                int index = 0;
                foreach (var d in decals)
                {
                    foreach (var facade in FacadesFor(d.facade, f))
                    {
                        if (decalsParent == null)
                            decalsParent = CreateChild(chunkRoot, $"BuildingDecals {coord}");
                        if (BuildDecalQuad(decalsParent.transform, coord, f, d, facade, index, materialCache))
                        {
                            placed++;
                            index++;
                        }
                    }
                }
            }

            return placed;
        }

        // ---- quad construction -------------------------------------------------

        static bool BuildDecalQuad(Transform parent, ChunkCoord coord, BuildingFactsJson f,
                                   FacadeDecalJson d, StreetFacadeJson facade, int index,
                                   Dictionary<string, Material> materialCache)
        {
            if (facade.edge == null || facade.edge.Length < 4) return false;
            if (d.rect == null || d.rect.Length < 4) return false;

            // Resolve the material first; a missing texture skips the decal (no white patch).
            var material = DecalMaterial(d.texture, materialCache);
            if (material == null) return false;

            Vector3 a = new Vector3(facade.edge[0], f.base_y, facade.edge[1]);
            Vector3 b = new Vector3(facade.edge[2], f.base_y, facade.edge[3]);
            Vector3 along = b - a;
            float len = along.magnitude;
            if (len < 1e-4f) return false;
            along /= len;

            float br = facade.bearing_deg * Mathf.Deg2Rad;
            Vector3 outward = new Vector3(Mathf.Sin(br), 0f, Mathf.Cos(br));
            float fh = Mathf.Max(f.facade_height_m, FloorHeightMeters);

            float rx0 = d.rect[0], ry0 = d.rect[1], rx1 = d.rect[2], ry1 = d.rect[3];
            Vector3 mount = outward * d.mountDepth_m;
            Vector3 bl = Corner(a, along, len, f.base_y, fh, rx0, ry0) + mount;
            Vector3 brc = Corner(a, along, len, f.base_y, fh, rx1, ry0) + mount;
            Vector3 tr = Corner(a, along, len, f.base_y, fh, rx1, ry1) + mount;
            Vector3 tl = Corner(a, along, len, f.base_y, fh, rx0, ry1) + mount;

            var mesh = new Mesh { name = $"decal_{f.osm_id}_{index}" };
            mesh.vertices = new[] { bl, brc, tr, tl };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.normals = new[] { outward, outward, outward, outward };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };   // Cull Off → winding is immaterial
            mesh.RecalculateBounds();
            SaveAsset(mesh, $"{GeneratedAssets.ChunkDir(coord)}/Decals/decal_{f.osm_id}_{index}.mesh");

            var go = CreateChild(parent.gameObject, $"decal_{f.osm_id}_{index}");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return true;
        }

        static Vector3 Corner(Vector3 a, Vector3 along, float len, float baseY, float fh,
                              float u, float v)
        {
            Vector3 p = a + along * (Mathf.Clamp01(u) * len);
            p.y = baseY + Mathf.Clamp01(v) * fh;
            return p;
        }

        // ---- material / texture -----------------------------------------------

        // One material per decal texture, written to a map-wide path. Returns null (→ the decal is
        // skipped) when the texture can't be loaded, so a missing PNG never paints a white quad.
        static Material DecalMaterial(string texturePath, Dictionary<string, Material> cache)
        {
            string key = texturePath ?? "";
            if (cache.TryGetValue(key, out var cached)) return cached;   // may be null (cached miss)

            Texture2D tex = string.IsNullOrEmpty(texturePath)
                ? null : AssetDatabase.LoadAssetAtPath<Texture2D>($"{LibraryRoot}/{texturePath}");
            if (tex == null)
            {
                Debug.LogWarning($"[BuildingDecalImporter] decal texture not found: " +
                                 $"{LibraryRoot}/{texturePath} — decal skipped.");
                cache[key] = null;
                return null;
            }

            string path = $"{GeneratedAssets.Root}/Materials/decal_{Safe(key)}.mat";
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // REUSE an existing material asset (update it in place) rather than delete+recreate, so
            // its GUID is stable across chunks that share a decal texture — otherwise re-minting the
            // GUID would break an already-saved earlier chunk prefab's material reference.
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("SFMap/DecalUnlitTransparent")) { name = $"decal_{Safe(key)}" };
                mat.mainTexture = tex;
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                if (mat.shader == null) mat.shader = Shader.Find("SFMap/DecalUnlitTransparent");
                mat.mainTexture = tex;
                EditorUtility.SetDirty(mat);
            }
            cache[key] = mat;
            return mat;
        }

        // ---- override loading --------------------------------------------------

        /// <summary>Scan <c>Assets/SFBuildingTemplates/Overrides/*.override.json</c> ONCE and return the
        /// overrides that carry <c>facadeDecals</c>. Hoisted out of the per-chunk path so this O(overrides)
        /// disk scan runs a single time per import, not once for every chunk (#295).</summary>
        public static List<OverrideJson> LoadDecalOverrides()
        {
            var list = new List<OverrideJson>();
            string abs = Path.Combine(Application.dataPath, "SFBuildingTemplates/Overrides");
            if (!Directory.Exists(abs)) return list;
            foreach (string file in Directory.GetFiles(abs, "*.override.json", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".override.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var ov = JsonUtility.FromJson<OverrideJson>(File.ReadAllText(file));
                    if (ov != null && ov.facadeDecals != null && ov.facadeDecals.Length > 0)
                        list.Add(ov);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BuildingDecalImporter] Failed to parse override {file}: {ex.Message}");
                }
            }
            return list;
        }

        // ---- facade resolution (mirrors the assembler) ------------------------

        static IEnumerable<StreetFacadeJson> FacadesFor(string facade, BuildingFactsJson f)
        {
            bool isStreet = string.Equals(facade, "Street", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(facade) || string.Equals(facade, "Front", StringComparison.OrdinalIgnoreCase))
            {
                if (f.street_facades != null && f.street_facades.Length > 0) yield return f.street_facades[0];
            }
            else if (isStreet)
            {
                if (f.street_facades != null)
                    foreach (var s in f.street_facades) yield return s;
            }
            else
            {
                Debug.LogWarning($"[BuildingDecalImporter] building {f.osm_id}: facade '{facade}' has no " +
                                 "sidecar geometry (only Front/Street supported); decal skipped.");
            }
        }

        // ---- asset / hierarchy helpers ----------------------------------------

        static GameObject CreateChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static void SaveAsset(UnityEngine.Object asset, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
        }

        static string Safe(string s)
        {
            var chars = (s ?? "").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-') chars[i] = '_';
            string r = new string(chars);
            return string.IsNullOrEmpty(r) ? "_" : r;
        }
    }
}
