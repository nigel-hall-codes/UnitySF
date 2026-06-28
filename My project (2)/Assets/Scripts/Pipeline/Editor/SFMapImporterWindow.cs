using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using SFMap.Pipeline;

namespace SFMap.Pipeline.Editor
{
    // JSON layout produced by python/sfmap/serialize.py write_manifest()
    [Serializable]
    class ManifestJson
    {
        public string             preset;
        public float              chunkSize;
        public ManifestChunkJson[] chunks;
    }

    [Serializable]
    class ManifestChunkJson
    {
        public int   col;
        public int   row;
        public float worldX;
        public float worldZ;
    }

    public class SFMapImporterWindow : EditorWindow
    {
        const uint ChunkMagic   = 0x4B4E4843u; // "CHNK"
        const uint ChunkVersion = 1;

        enum MeshType : byte { Road = 0, Intersection = 1, Sidewalk = 2, Building = 3 }

        [SerializeField] string chunkDir    = "";
        [SerializeField] string presetName  = "default";
        [SerializeField] bool   bakeParkedCars = true;

        // 7-color pastel palette for buildings. Each building picks one by hashing
        // its osm_id and the colour is baked into the building's vertex colors, so
        // a whole chunk of buildings shares one material (and one draw call) while
        // staying varied. Adding colours here needs no other pipeline changes.
        static readonly Color[] BuildingPalette =
        {
            new Color(0.847f, 0.655f, 0.694f), // dusty rose
            new Color(0.710f, 0.776f, 0.631f), // sage green
            new Color(0.953f, 0.914f, 0.824f), // warm cream
            new Color(0.682f, 0.776f, 0.910f), // sky blue
            new Color(0.886f, 0.647f, 0.494f), // terracotta
            new Color(0.788f, 0.722f, 0.878f), // lavender
            new Color(0.961f, 0.953f, 0.925f), // off-white
        };

        // Vehicle prefabs used for parked cars, indexed by the python model selector
        // (m in [0,1) → floor(m * length)). Curated to street vehicles; the plane and
        // monster truck from the asset pack are intentionally excluded. The python
        // bake stays decoupled from this list — it only emits a float, so reordering
        // or adding prefabs here needs no re-bake.
        static readonly string[] CarPrefabPaths =
        {
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Classic Car_9.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Hatchback Car_15.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/N_Muscle Car_10.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Sport Car_39.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/N Van_10.prefab",
            "Assets/Awb-Free Low Poly Vehicles/Prefabs/Pick Up_11.prefab",
        };

        // The Awb vehicles are authored a bit oversized for this map; halve them.
        // The python placement footprint (sfmap/geometry/parking.py) is scaled to
        // match, so spacing stays dense without overlap.
        const float ParkedCarScale = 0.5f;

        [MenuItem("Window/SF Map Importer")]
        public static void Open() => GetWindow<SFMapImporterWindow>("SF Map Importer");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            chunkDir = EditorGUILayout.TextField("Chunk Directory", chunkDir);
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
            {
                string picked = EditorUtility.OpenFolderPanel("Select Chunk Directory", chunkDir, "");
                if (!string.IsNullOrEmpty(picked))
                    chunkDir = picked;
            }
            EditorGUILayout.EndHorizontal();

            presetName = EditorGUILayout.TextField("Preset Name", presetName);
            bakeParkedCars = EditorGUILayout.Toggle("Bake Parked Cars into Prefab", bakeParkedCars);

            EditorGUILayout.Space();
            if (GUILayout.Button("Import Chunks"))
                RunImport();
            if (GUILayout.Button("Clear Generated Assets"))
                ClearGenerated();
        }

        void RunImport()
        {
            GeneratedAssets.ActivePreset = presetName;

            string manifestPath = Path.Combine(chunkDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"[SFMapImporter] manifest.json not found at: {manifestPath}");
                return;
            }

            var manifest = JsonUtility.FromJson<ManifestJson>(File.ReadAllText(manifestPath));
            if (manifest?.chunks == null || manifest.chunks.Length == 0)
            {
                Debug.LogError("[SFMapImporter] manifest.json parsed but has no chunks.");
                return;
            }

            EnsureTopLevelFolders();
            EnsureMaterials();

            var mapRoot    = new GameObject("SF Map");
            var coordList  = new List<ChunkManifestEntry>();
            float globalMinElev = float.MaxValue;

            try
            {
                for (int i = 0; i < manifest.chunks.Length; i++)
                {
                    var mc    = manifest.chunks[i];
                    var coord = new ChunkCoord(mc.col, mc.row);
                    EditorUtility.DisplayProgressBar("SF Map Importer",
                        $"Importing {coord}…", (float)i / manifest.chunks.Length);

                    string binPath = Path.Combine(chunkDir, $"chunk_{mc.col:00}_{mc.row:00}.bin");
                    if (!File.Exists(binPath))
                    {
                        Debug.LogWarning($"[SFMapImporter] Missing .bin: {binPath}");
                        continue;
                    }

                    float chunkMinElev = ImportChunk(binPath, coord, mc.worldX, mc.worldZ, mapRoot);
                    if (chunkMinElev < globalMinElev)
                        globalMinElev = chunkMinElev;

                    ImportRoadNames(chunkDir, coord);
                    ImportParkedCarsJson(chunkDir, coord);

                    var chunkRootGo = mapRoot.transform.Find(coord.ToString()).gameObject;
                    if (bakeParkedCars)
                        ImportParkedCars(chunkDir, coord, chunkRootGo);

                    PrefabUtility.SaveAsPrefabAsset(chunkRootGo,
                        GeneratedAssets.ChunkPrefabPath(coord));

                    coordList.Add(new ChunkManifestEntry
                    {
                        col    = mc.col,
                        row    = mc.row,
                        worldX = mc.worldX,
                        worldZ = mc.worldZ,
                    });
                }

                if (globalMinElev == float.MaxValue) globalMinElev = 0f;
                SaveChunkManifest(manifest, coordList, globalMinElev);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[SFMapImporter] Imported {coordList.Count} chunk(s) for preset '{presetName}'.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SFMapImporter] Import failed: {e}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DestroyImmediate(mapRoot);
            }
        }

        // Returns the chunk's min elevation (for the global ChunkManifest).
        float ImportChunk(string binPath, ChunkCoord coord, float worldX, float worldZ, GameObject mapRoot)
        {
            using var fs     = File.OpenRead(binPath);
            using var reader = new BinaryReader(fs);

            // ---- Header (40 bytes) ----
            uint  magic      = reader.ReadUInt32();
            uint  version    = reader.ReadUInt32();
            int   col        = reader.ReadInt32();
            int   row        = reader.ReadInt32();
            float _worldX    = reader.ReadSingle();
            float _worldZ    = reader.ReadSingle();
            float chunkSizeM = reader.ReadSingle();
            float minElevM   = reader.ReadSingle();
            float maxElevM   = reader.ReadSingle();
            int   hmapRes    = reader.ReadInt32();

            if (magic != ChunkMagic)
                throw new InvalidDataException(
                    $"Bad magic in {binPath}: expected 0x{ChunkMagic:X8}, got 0x{magic:X8}");
            if (version != ChunkVersion)
                throw new InvalidDataException(
                    $"Unsupported .bin version {version} in {binPath} (expected {ChunkVersion})");

            // ---- Heightmap ----
            int    hmapCount = hmapRes * hmapRes;
            byte[] hmapBytes = reader.ReadBytes(hmapCount * 4);
            var    heights1D = new float[hmapCount];
            Buffer.BlockCopy(hmapBytes, 0, heights1D, 0, hmapBytes.Length);

            var heights2D = new float[hmapRes, hmapRes]; // [row, col]
            for (int idx = 0; idx < hmapCount; idx++)
                heights2D[idx / hmapRes, idx % hmapRes] = heights1D[idx];

            EnsureChunkFolder(coord);
            var baseLayer = EnsureBaseTerrainLayer();

            // Bulk-create this chunk's terrain + mesh assets inside one StartAssetEditing
            // block. Without it the AssetDatabase imports each road/sidewalk/intersection
            // mesh asset individually, which makes a multi-chunk import slow; StopAssetEditing
            // imports them all in a single pass. (Buildings are combined, so they contribute
            // just one asset per chunk.)
            TerrainData terrainData = null;

            // Roads, sidewalks, and intersections stay one-asset-and-GameObject-per-mesh so
            // each is individually selectable; all carry per-mesh colliders, with roads and
            // intersections also on the Road layer.
            var roadMeshes         = new List<Mesh>();
            var sidewalkMeshes     = new List<Mesh>();
            var intersectionMeshes = new List<Mesh>();
            // Buildings are non-interactive static geometry: built in memory (no per-mesh
            // asset), then merged into one combined mesh per chunk.
            var buildingParts     = new List<Mesh>();
            Mesh combinedBuildings = null;

            AssetDatabase.StartAssetEditing();
            try
            {
                terrainData = new TerrainData();
                terrainData.heightmapResolution = hmapRes;
                terrainData.size = new Vector3(chunkSizeM, Mathf.Max(maxElevM - minElevM, 1f), chunkSizeM);
                terrainData.SetHeights(0, 0, heights2D);
                terrainData.terrainLayers = new[] { baseLayer };
                CreateOrReplaceAsset(terrainData, GeneratedAssets.TerrainAsset(coord));

                // ---- Mesh entries ----
                int meshCount = reader.ReadInt32();
                for (int m = 0; m < meshCount; m++)
                {
                    var   meshType = (MeshType)reader.ReadByte();
                    long  osmId    = reader.ReadInt64();
                    int   vertCnt  = reader.ReadInt32();
                    int   idxCnt   = reader.ReadInt32();

                    var verts   = ReadVec3Array(reader, vertCnt);
                    var normals = ReadVec3Array(reader, vertCnt);
                    var uvs     = ReadVec2Array(reader, vertCnt);
                    var indices = ReadIndices(reader, idxCnt);

                    if (vertCnt == 0 || idxCnt == 0) continue;

                    var mesh = BuildMesh(MeshName(meshType, osmId), verts, normals, uvs, indices);

                    switch (meshType)
                    {
                        case MeshType.Road:
                            CreateOrReplaceAsset(mesh, GeneratedAssets.RoadMesh(coord, osmId));
                            roadMeshes.Add(mesh);
                            break;
                        case MeshType.Sidewalk:
                            CreateOrReplaceAsset(mesh, GeneratedAssets.SidewalkMesh(coord, osmId));
                            sidewalkMeshes.Add(mesh);
                            break;
                        case MeshType.Intersection:
                            CreateOrReplaceAsset(mesh, GeneratedAssets.IntersectionMesh(coord, osmId));
                            intersectionMeshes.Add(mesh);
                            break;
                        case MeshType.Building:
                            // Bake the building's palette colour into vertex colors so the
                            // combined mesh keeps per-building colour with one material.
                            Color32 c = BuildingPalette[(int)(Math.Abs(osmId) % BuildingPalette.Length)];
                            var colors = new Color32[vertCnt];
                            for (int v = 0; v < vertCnt; v++) colors[v] = c;
                            mesh.SetColors(colors);
                            buildingParts.Add(mesh);
                            break;
                    }
                }

                // Merge the static, non-interactive building geometry into one mesh per
                // chunk. Vertices are already world-space (GameObjects sit at the origin),
                // so combine without transforms — placement is byte-identical.
                combinedBuildings = CombineParts(buildingParts, $"buildings_{coord}",
                                                 GeneratedAssets.BuildingsCombinedMesh(coord));
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // ---- Build GameObject hierarchy ----
            var chunkRoot = new GameObject(coord.ToString());
            chunkRoot.transform.SetParent(mapRoot.transform, false);

            var terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = $"Terrain {coord}";
            terrainGo.transform.SetParent(chunkRoot.transform, false);
            terrainGo.transform.position = new Vector3(worldX, minElevM, worldZ);

            var roadMat = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.RoadMaterial());
            var swMat   = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.SidewalkMaterial());
            var bldgMat = AssetDatabase.LoadAssetAtPath<Material>(GeneratedAssets.BuildingMaterial());
            int roadLayer = LayerMask.NameToLayer("Road");

            // Roads: one GameObject per mesh, each with a collider on the Road layer.
            var roadParent = CreateChild(chunkRoot, $"Roads {coord}");
            foreach (var mesh in roadMeshes)
            {
                var go = PlaceMesh(mesh, roadParent, roadMat);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.layer = roadLayer;
            }

            // Sidewalks get a collider so cars can't drive through them, but stay off the
            // Road layer so traffic raycasts don't treat the kerb as a drivable surface.
            var swParent = CreateChild(chunkRoot, $"Sidewalks {coord}");
            foreach (var mesh in sidewalkMeshes)
            {
                var go = PlaceMesh(mesh, swParent, swMat);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            // Intersections: one GameObject per mesh, like roads (collider + Road layer),
            // so each junction is individually selectable in the Hierarchy.
            var intParent = CreateChild(chunkRoot, $"Intersections {coord}");
            foreach (var mesh in intersectionMeshes)
            {
                var go = PlaceMesh(mesh, intParent, roadMat);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.layer = roadLayer;
            }

            // Buildings: one combined mesh on a single GameObject per chunk.
            var bldgGo = CreateChild(chunkRoot, $"Buildings {coord}");
            if (combinedBuildings != null)
            {
                bldgGo.AddComponent<MeshFilter>().sharedMesh = combinedBuildings;
                bldgGo.AddComponent<MeshRenderer>().sharedMaterial = bldgMat;
            }

            return minElevM;
        }

        static void ImportRoadNames(string chunkDir, ChunkCoord coord)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_names.json");
            if (!File.Exists(src)) return;

            string dst = GeneratedAssets.ChunkRoadNamesAsset(coord);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: true);
            AssetDatabase.ImportAsset(dst);
        }

        static void ImportParkedCarsJson(string chunkDir, ChunkCoord coord)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_parked.json");
            if (!File.Exists(src)) return;

            string dst = GeneratedAssets.ChunkParkedCarsAsset(coord);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: true);
            AssetDatabase.ImportAsset(dst);
        }

        // Instantiate parked-car prefabs from chunk_CC_RR_parked.json under a
        // "ParkedCars {coord}" parent on the chunk root, so they bake into the chunk
        // prefab (as nested prefab instances) and stream in with the rest of the chunk.
        // Cars are grouped under a child per street name so a future tool can add or
        // remove a street's cars by toggling one GameObject. No-op when the sidecar is
        // absent (no --parking source) or empty.
        void ImportParkedCars(string chunkDir, ChunkCoord coord, GameObject chunkRoot)
        {
            string src = Path.Combine(chunkDir, $"chunk_{coord.Col:00}_{coord.Row:00}_parked.json");
            if (!File.Exists(src)) return;

            var data = JsonUtility.FromJson<ParkedCarsJson>(File.ReadAllText(src));
            if (data?.cars == null || data.cars.Length == 0) return;

            var prefabs = LoadCarPrefabs();
            if (prefabs.Length == 0)
            {
                Debug.LogWarning("[SFMapImporter] No parked-car prefabs found — skipping parked cars. " +
                                 "Expected the 'Awb-Free Low Poly Vehicles' asset under Assets/.");
                return;
            }

            var carsParent = CreateChild(chunkRoot, $"ParkedCars {coord}");
            var streetGroups = new Dictionary<string, Transform>();
            int placed = 0;

            foreach (var car in data.cars)
            {
                if (car.p == null || car.p.Length < 3) continue;

                int idx = Mathf.Clamp(Mathf.FloorToInt(car.m * prefabs.Length), 0, prefabs.Length - 1);
                var prefab = prefabs[idx];
                if (prefab == null) continue;

                string street = string.IsNullOrEmpty(car.s) ? "(unknown)" : car.s;
                if (!streetGroups.TryGetValue(street, out var group))
                {
                    group = CreateChild(carsParent, street).transform;
                    streetGroups[street] = group;
                }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(group, false);
                go.transform.localPosition = new Vector3(car.p[0], car.p[1], car.p[2]);
                go.transform.localRotation = car.Rotation();
                go.transform.localScale = prefab.transform.localScale * ParkedCarScale;
                placed++;
            }

            if (placed > 0)
                Debug.Log($"[SFMapImporter] {coord}: placed {placed} parked car(s) across {streetGroups.Count} street(s).");
        }

        // Load (and cache for this import run) the curated vehicle prefabs. Missing
        // entries are dropped so a partial asset install still places what it can.
        GameObject[] _carPrefabCache;
        GameObject[] LoadCarPrefabs()
        {
            if (_carPrefabCache != null) return _carPrefabCache;
            var list = new List<GameObject>(CarPrefabPaths.Length);
            foreach (var path in CarPrefabPaths)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) list.Add(go);
                else Debug.LogWarning($"[SFMapImporter] Parked-car prefab not found: {path}");
            }
            _carPrefabCache = list.ToArray();
            return _carPrefabCache;
        }

        void SaveChunkManifest(ManifestJson src, List<ChunkManifestEntry> entries, float minElev)
        {
            var asset = ScriptableObject.CreateInstance<ChunkManifest>();
            asset.preset          = src.preset;
            asset.chunkSizeMeters = src.chunkSize;
            asset.minElevation    = minElev;
            asset.chunks          = entries.ToArray();

            string path = GeneratedAssets.ChunkManifestPath();
            CreateOrReplaceAsset(asset, path);
        }

        // ------------------------------------------------------------------ helpers

        static void EnsureMaterials()
        {
            EnsureFolder(GeneratedAssets.Root, "Materials");

            string roadPath = GeneratedAssets.RoadMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(roadPath) == null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0.25f, 0.25f, 0.25f);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0f);
                AssetDatabase.CreateAsset(mat, roadPath);
            }

            string swPath = GeneratedAssets.SidewalkMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(swPath) == null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0.75f, 0.75f, 0.75f);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0f);
                AssetDatabase.CreateAsset(mat, swPath);
            }

            // One shared building material. Per-building colour comes from vertex
            // colors (baked in ImportChunk), so the BuildingVertexColor shader tints
            // albedo by vertex colour and a whole chunk renders in one draw call.
            string bldgPath = GeneratedAssets.BuildingMaterial();
            if (AssetDatabase.LoadAssetAtPath<Material>(bldgPath) == null)
            {
                var shader = Shader.Find("SFMap/BuildingVertexColor");
                if (shader == null)
                {
                    Debug.LogError("[SFMapImporter] Shader 'SFMap/BuildingVertexColor' not found " +
                                   "(expected at Assets/Shaders/BuildingVertexColor.shader).");
                    return;
                }
                var mat = new Material(shader) { color = Color.white };
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0.05f);
                AssetDatabase.CreateAsset(mat, bldgPath);
            }
        }

        static void EnsureTopLevelFolders()
        {
            EnsureFolder("Assets", "Generated");
            EnsureFolder("Assets/Generated", GeneratedAssets.ActivePreset);
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "Generated");
            EnsureFolder("Assets/Resources/Generated", GeneratedAssets.ActivePreset);
        }

        static TerrainLayer EnsureBaseTerrainLayer()
        {
            string path = GeneratedAssets.TerrainBaseLayer();
            var existing = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (existing != null)
                return existing;

            EnsureFolder(GeneratedAssets.Root, "Materials");

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
            tex.SetPixel(0, 0, new Color(0.42f, 0.47f, 0.38f));
            tex.Apply();
            tex.name = "BaseColor";

            var layer = new TerrainLayer { tileSize = new Vector2(10, 10) };
            AssetDatabase.CreateAsset(layer, path);
            AssetDatabase.AddObjectToAsset(tex, path);
            layer.diffuseTexture = tex;
            EditorUtility.SetDirty(layer);
            AssetDatabase.SaveAssets();
            return layer;
        }

        static void EnsureChunkFolder(ChunkCoord coord)
        {
            string root   = GeneratedAssets.Root;
            string parent = $"{root}/{coord}";
            EnsureFolder(root, coord.ToString());
            EnsureFolder(parent, "Roads");
            EnsureFolder(parent, "Intersections");
            EnsureFolder(parent, "Sidewalks");
            EnsureFolder(parent, "Buildings");
        }

        static void EnsureFolder(string parent, string name)
        {
            string path = $"{parent}/{name}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        static void CreateOrReplaceAsset(UnityEngine.Object obj, string path)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null)
                AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
        }

        static string MeshName(MeshType t, long osmId) => t switch
        {
            MeshType.Road         => $"road_{osmId}",
            MeshType.Intersection => $"intersection_{osmId}",
            MeshType.Sidewalk     => $"sidewalk_{osmId}",
            MeshType.Building     => $"building_{osmId}",
            _                     => $"mesh_{osmId}",
        };

        static Mesh BuildMesh(string name, Vector3[] verts, Vector3[] normals, Vector2[] uvs, int[] indices)
        {
            var mesh = new Mesh { name = name };
            mesh.indexFormat = verts.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(indices, 0);
            mesh.SetUVs(0, uvs);

            if (AllZero(normals))
                mesh.RecalculateNormals();
            else
                mesh.SetNormals(normals);

            mesh.RecalculateBounds();
            return mesh;
        }

        // Merge in-memory part meshes into a single chunk mesh asset and return it
        // (null when there are no parts). Parts are combined without transforms — their
        // vertices are already world-space — so the result sits at the origin exactly
        // like the originals. Source normals/UVs/colors are preserved (no recalc, which
        // would average across touching buildings). The temporary parts are released.
        static Mesh CombineParts(List<Mesh> parts, string name, string assetPath)
        {
            if (parts.Count == 0)
                return null;

            var instances = new CombineInstance[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                instances[i] = new CombineInstance { mesh = parts[i], subMeshIndex = 0 };

            var combined = new Mesh
            {
                name        = name,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, // combined verts exceed 65535
            };
            combined.CombineMeshes(instances, mergeSubMeshes: true, useMatrices: false);
            combined.RecalculateBounds();

            foreach (var p in parts)
                DestroyImmediate(p);

            CreateOrReplaceAsset(combined, assetPath);
            return combined;
        }

        static Vector3[] ReadVec3Array(BinaryReader r, int count)
        {
            var bytes  = r.ReadBytes(count * 12);
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
                result[i] = new Vector3(
                    BitConverter.ToSingle(bytes, i * 12),
                    BitConverter.ToSingle(bytes, i * 12 + 4),
                    BitConverter.ToSingle(bytes, i * 12 + 8));
            return result;
        }

        static Vector2[] ReadVec2Array(BinaryReader r, int count)
        {
            var bytes  = r.ReadBytes(count * 8);
            var result = new Vector2[count];
            for (int i = 0; i < count; i++)
                result[i] = new Vector2(
                    BitConverter.ToSingle(bytes, i * 8),
                    BitConverter.ToSingle(bytes, i * 8 + 4));
            return result;
        }

        static int[] ReadIndices(BinaryReader r, int count)
        {
            var bytes  = r.ReadBytes(count * 4);
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = (int)BitConverter.ToUInt32(bytes, i * 4);
            return result;
        }

        static bool AllZero(Vector3[] vecs)
        {
            foreach (var v in vecs)
                if (v.sqrMagnitude > 1e-8f) return false;
            return true;
        }

        static GameObject CreateChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static GameObject PlaceMesh(Mesh mesh, GameObject parent, Material mat)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return go;
        }

        void ClearGenerated()
        {
            GeneratedAssets.ActivePreset = presetName;
            string presetDir = GeneratedAssets.Root;
            if (AssetDatabase.IsValidFolder(presetDir))
            {
                AssetDatabase.DeleteAsset(presetDir);
                AssetDatabase.Refresh();
                Debug.Log($"[SFMapImporter] Cleared {presetDir}");
            }
        }
    }
}
