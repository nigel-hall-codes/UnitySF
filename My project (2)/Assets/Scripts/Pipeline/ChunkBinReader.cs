using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>Mesh category tag stored per mesh in a chunk .bin (matches the Python MeshType).</summary>
    public enum ChunkMeshType : byte { Road = 0, Intersection = 1, Sidewalk = 2, Building = 3 }

    /// <summary>One parsed mesh record from a chunk .bin.</summary>
    public struct ChunkBinMesh
    {
        public ChunkMeshType Type;
        public long          OsmId;
        public Vector3[]     Vertices;
        public Vector3[]     Normals;
        public Vector2[]     Uvs;
        public int[]         Indices;
    }

    /// <summary>Fully parsed contents of one chunk_CC_RR.bin (header + heightmap + meshes).</summary>
    public sealed class ChunkBinData
    {
        public int   Col;
        public int   Row;
        public float WorldX;
        public float WorldZ;
        public float ChunkSizeM;
        public float MinElevM;
        public float MaxElevM;
        public int   HmapRes;
        public float[,] Heights;               // [row, col], normalised [0,1]
        public List<ChunkBinMesh> Meshes;
    }

    /// <summary>
    /// Reads the binary chunk format written by python/sfmap/serialize.py write_chunk().
    /// The normative byte layout is docs/chunk-bin-format.md.
    ///
    /// Pure parsing (System.IO + UnityEngine only, no UnityEditor dependency), so it lives
    /// in the runtime assembly and is shared by the editor importer and the edit-mode tests.
    /// The tests read the same golden fixture the Python round-trip test uses
    /// (python/tests/fixtures/chunk_01_02.bin), so both language sides pin one format.
    /// </summary>
    public static class ChunkBinReader
    {
        public const uint Magic   = 0x4B4E4843u; // "CHNK"
        public const uint Version = 1;

        /// <summary>Read and fully parse a chunk .bin file.</summary>
        public static ChunkBinData Read(string binPath)
        {
            using var fs     = File.OpenRead(binPath);
            using var reader = new BinaryReader(fs);
            return Read(reader, binPath);
        }

        /// <summary>Parse a chunk from an open binary reader (used by tests reading a fixture).</summary>
        public static ChunkBinData Read(BinaryReader reader, string sourceName = "<stream>")
        {
            // ---- Header (40 bytes) ----
            uint  magic      = reader.ReadUInt32();
            uint  version    = reader.ReadUInt32();
            int   col        = reader.ReadInt32();
            int   row        = reader.ReadInt32();
            float worldX     = reader.ReadSingle();
            float worldZ     = reader.ReadSingle();
            float chunkSizeM = reader.ReadSingle();
            float minElevM   = reader.ReadSingle();
            float maxElevM   = reader.ReadSingle();
            int   hmapRes    = reader.ReadInt32();

            if (magic != Magic)
                throw new InvalidDataException(
                    $"Bad magic in {sourceName}: expected 0x{Magic:X8}, got 0x{magic:X8}");
            if (version != Version)
                throw new InvalidDataException(
                    $"Unsupported .bin version {version} in {sourceName} (expected {Version})");

            // ---- Heightmap ----
            int    hmapCount = hmapRes * hmapRes;
            byte[] hmapBytes = reader.ReadBytes(hmapCount * 4);
            var    heights1D = new float[hmapCount];
            Buffer.BlockCopy(hmapBytes, 0, heights1D, 0, hmapBytes.Length);

            var heights2D = new float[hmapRes, hmapRes]; // [row, col]
            for (int idx = 0; idx < hmapCount; idx++)
                heights2D[idx / hmapRes, idx % hmapRes] = heights1D[idx];

            // ---- Mesh entries ----
            int meshCount = reader.ReadInt32();
            var meshes = new List<ChunkBinMesh>(meshCount);
            for (int m = 0; m < meshCount; m++)
            {
                var  type    = (ChunkMeshType)reader.ReadByte();
                long osmId   = reader.ReadInt64();
                int  vertCnt = reader.ReadInt32();
                int  idxCnt  = reader.ReadInt32();

                // Read in stream order: vertices, normals, uvs, indices.
                var verts   = ReadVec3Array(reader, vertCnt);
                var normals = ReadVec3Array(reader, vertCnt);
                var uvs     = ReadVec2Array(reader, vertCnt);
                var indices = ReadIndices(reader, idxCnt);

                meshes.Add(new ChunkBinMesh
                {
                    Type     = type,
                    OsmId    = osmId,
                    Vertices = verts,
                    Normals  = normals,
                    Uvs      = uvs,
                    Indices  = indices,
                });
            }

            return new ChunkBinData
            {
                Col        = col,
                Row        = row,
                WorldX     = worldX,
                WorldZ     = worldZ,
                ChunkSizeM = chunkSizeM,
                MinElevM   = minElevM,
                MaxElevM   = maxElevM,
                HmapRes    = hmapRes,
                Heights    = heights2D,
                Meshes     = meshes,
            };
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
    }
}
