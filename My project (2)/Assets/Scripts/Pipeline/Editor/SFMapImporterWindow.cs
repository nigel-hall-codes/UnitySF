using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

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

        [SerializeField] string chunkDir   = "";
        [SerializeField] string presetName = "default";

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

                    PrefabUtility.SaveAsPrefabAsset(mapRoot.transform.Find(coord.ToString()).gameObject,
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

            var terrainData = new TerrainData();
            terrainData.heightmapResolution = hmapRes;
            terrainData.size = new Vector3(chunkSizeM, Mathf.Max(maxElevM - minElevM, 1f), chunkSizeM);
            terrainData.SetHeights(0, 0, heights2D);
            CreateOrReplaceAsset(terrainData, GeneratedAssets.TerrainAsset(coord));

            // ---- Mesh entries ----
            int meshCount = reader.ReadInt32();

            var byType = new Dictionary<MeshType, List<(Mesh mesh, long id)>>
            {
                [MeshType.Road]         = new(),
                [MeshType.Intersection] = new(),
                [MeshType.Sidewalk]     = new(),
                [MeshType.Building]     = new(),
            };

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

                var mesh = new Mesh { name = MeshName(meshType, osmId) };
                mesh.indexFormat = vertCnt > 65535
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

                string assetPath = MeshAssetPath(coord, meshType, osmId);
                CreateOrReplaceAsset(mesh, assetPath);

                if (byType.ContainsKey(meshType))
                    byType[meshType].Add((mesh, osmId));
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
            int roadLayer = LayerMask.NameToLayer("Road");

            var roadParent = CreateChild(chunkRoot, $"Roads {coord}");
            foreach (var (mesh, _) in byType[MeshType.Road])
            {
                var go = PlaceMesh(mesh, roadParent, roadMat);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.layer = roadLayer;
            }

            var intParent = CreateChild(chunkRoot, $"Intersections {coord}");
            foreach (var (mesh, _) in byType[MeshType.Intersection])
                PlaceMesh(mesh, intParent, roadMat);

            var swParent = CreateChild(chunkRoot, $"Sidewalks {coord}");
            foreach (var (mesh, _) in byType[MeshType.Sidewalk])
                PlaceMesh(mesh, swParent, swMat);

            var bldgParent = CreateChild(chunkRoot, $"Buildings {coord}");
            foreach (var (mesh, _) in byType[MeshType.Building])
                PlaceMesh(mesh, bldgParent, null);

            return minElevM;
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

        static void EnsureTopLevelFolders()
        {
            EnsureFolder("Assets", "Generated");
            EnsureFolder("Assets/Generated", GeneratedAssets.ActivePreset);
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "Generated");
            EnsureFolder("Assets/Resources/Generated", GeneratedAssets.ActivePreset);
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

        static string MeshAssetPath(ChunkCoord coord, MeshType t, long osmId) => t switch
        {
            MeshType.Road         => GeneratedAssets.RoadMesh(coord, osmId),
            MeshType.Intersection => GeneratedAssets.IntersectionMesh(coord, osmId),
            MeshType.Sidewalk     => GeneratedAssets.SidewalkMesh(coord, osmId),
            MeshType.Building     => GeneratedAssets.BuildingMesh(coord, osmId),
            _                     => $"{GeneratedAssets.ChunkDir(coord)}/mesh_{osmId}.mesh",
        };

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
