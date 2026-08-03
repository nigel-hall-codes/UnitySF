using System;
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// Cache key for a generated part: generator id, detail level, and the parameter set quantised
    /// to <see cref="QuantumMeters"/> before hashing (#453, design #452 §6 mitigation 1).
    /// The quantum is the whole point — the assembler's seeded jitter perturbs parameters by
    /// millimetres, so without it every placement would be its own "distinct" mesh. With it,
    /// generation cost scales with distinct parameter sets (hundreds), not placements (millions).
    /// </summary>
    public readonly struct PartKey : IEquatable<PartKey>
    {
        /// <summary>5 mm — below the smallest difference anyone can see on a facade.</summary>
        public const float QuantumMeters = 0.005f;

        readonly string _generatorId;
        readonly DetailLevel _detail;
        readonly int[] _q;
        readonly int _hash;

        PartKey(string generatorId, DetailLevel detail, int[] quantised, int hash)
        {
            _generatorId = generatorId;
            _detail = detail;
            _q = quantised;
            _hash = hash;
        }

        public static PartKey From(string generatorId, DetailLevel detail, params float[] parameters)
        {
            parameters = parameters ?? Array.Empty<float>();
            var q = new int[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                q[i] = Mathf.RoundToInt(parameters[i] / QuantumMeters);

            // FNV-1a over the id, detail and quantised parameters — the same hash shape
            // BuildingAssembler.SeedFor uses, for no reason other than consistency.
            unchecked
            {
                uint h = 2166136261u;
                if (generatorId != null)
                    for (int i = 0; i < generatorId.Length; i++) { h ^= generatorId[i]; h *= 16777619u; }
                h ^= (uint)detail; h *= 16777619u;
                for (int i = 0; i < q.Length; i++)
                {
                    uint v = (uint)q[i];
                    for (int b = 0; b < 4; b++) { h ^= (byte)(v >> (b * 8)); h *= 16777619u; }
                }
                return new PartKey(generatorId, detail, q, (int)h);
            }
        }

        public bool Equals(PartKey other)
        {
            if (_hash != other._hash || _detail != other._detail) return false;
            if (!string.Equals(_generatorId, other._generatorId, StringComparison.Ordinal)) return false;
            int n = _q != null ? _q.Length : 0;
            int m = other._q != null ? other._q.Length : 0;
            if (n != m) return false;
            for (int i = 0; i < n; i++) if (_q[i] != other._q[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is PartKey k && Equals(k);
        public override int GetHashCode() => _hash;

        public override string ToString()
            => $"{_generatorId}/{_detail}/[{string.Join(",", _q ?? Array.Empty<int>())}]";
    }

    /// <summary>
    /// Distinct parameter sets → generated meshes (#453 acceptance 4). Holds one
    /// <see cref="MeshBuilder"/> for the whole run and clears it between generations, which is why
    /// the builder is an accumulator rather than something allocated per quad.
    /// <para>Not thread-safe, and not meant to be: generation runs on the import thread.</para>
    /// </summary>
    public sealed class PartMeshCache
    {
        readonly Dictionary<PartKey, PartMesh> _meshes = new Dictionary<PartKey, PartMesh>();
        readonly MeshBuilder _builder = new MeshBuilder();

        /// <summary>Distinct meshes actually generated. N placements of one preset must leave this
        /// at 1 — the property the cache exists for.</summary>
        public int Generated { get; private set; }

        /// <summary>Placements served from an already-generated mesh.</summary>
        public int Hits { get; private set; }

        public int Count => _meshes.Count;

        /// <summary>The mesh for <paramref name="key"/>, generating it on first sight. The callback
        /// receives a cleared builder and returns its <see cref="MeshBuilder.Finish"/> result — it
        /// must not hold on to the builder.</summary>
        public PartMesh GetOrCreate(in PartKey key, Func<MeshBuilder, PartMesh> generate)
        {
            if (_meshes.TryGetValue(key, out var cached)) { Hits++; return cached; }

            _builder.Clear();
            var made = generate(_builder);
            _meshes[key] = made;
            Generated++;
            return made;
        }

        /// <summary>Drop every cached mesh, destroying the ones this cache owns. Called at the end
        /// of an import run; meshes that were saved as assets must be removed by the caller first.</summary>
        public void Clear()
        {
            foreach (var kv in _meshes)
                if (kv.Value.mesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
            _meshes.Clear();
            Generated = 0;
            Hits = 0;
        }
    }
}
