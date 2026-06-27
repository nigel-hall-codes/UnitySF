using System;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Parses the binary heightcache written by <c>python/sfmap/elevation.py</c> and
    /// provides world-space elevation sampling and analytic line-of-sight testing.
    ///
    /// Binary layout (little-endian):
    /// <code>
    ///   uint32  magic        0x454D4843 ("EMHC")
    ///   int32   resolution
    ///   float32 minElevation metres
    ///   float32 maxElevation metres
    ///   float32 worldXMin
    ///   float32 worldZMin
    ///   float32 worldWidth
    ///   float32 worldHeight
    ///   float32[resolution*resolution]  normalised [0,1] heights, row-major
    ///                                   row = south→north, col = west→east
    /// </code>
    /// </summary>
    public sealed class HeightField
    {
        const uint Magic = 0x454D4843u;

        public readonly int   Resolution;
        public readonly float MinElevation;
        public readonly float MaxElevation;
        public readonly float WorldXMin;
        public readonly float WorldZMin;
        public readonly float WorldWidth;
        public readonly float WorldHeight;

        readonly float[] _values;

        /// <summary>Parse a heightcache TextAsset. Returns null and logs a warning on failure.</summary>
        public static HeightField Load(TextAsset asset)
        {
            if (asset == null) return null;
            try   { return new HeightField(asset.bytes); }
            catch (Exception e)
            {
                Debug.LogWarning($"[HeightField] Failed to parse '{asset.name}': {e.Message}");
                return null;
            }
        }

        HeightField(byte[] data)
        {
            int off = 0;

            uint magic = BitConverter.ToUInt32(data, off); off += 4;
            if (magic != Magic)
                throw new Exception($"Bad magic 0x{magic:X8} (expected 0x{Magic:X8})");

            Resolution   = BitConverter.ToInt32 (data, off); off += 4;
            MinElevation = BitConverter.ToSingle(data, off); off += 4;
            MaxElevation = BitConverter.ToSingle(data, off); off += 4;
            WorldXMin    = BitConverter.ToSingle(data, off); off += 4;
            WorldZMin    = BitConverter.ToSingle(data, off); off += 4;
            WorldWidth   = BitConverter.ToSingle(data, off); off += 4;
            WorldHeight  = BitConverter.ToSingle(data, off); off += 4;

            if (Resolution < 2 || Resolution > 32768)
                throw new Exception($"Resolution {Resolution} out of valid range [2, 32768].");

            if (WorldWidth <= 0f || WorldHeight <= 0f)
                throw new Exception(
                    $"Invalid world extents: width={WorldWidth}, height={WorldHeight}.");

            int count = Resolution * Resolution;
            int expected = off + count * sizeof(float);
            if (data.Length < expected)
                throw new Exception(
                    $"File too short: {data.Length} bytes, expected ≥ {expected}.");

            _values = new float[count];
            Buffer.BlockCopy(data, off, _values, 0, count * sizeof(float));
        }

        /// <summary>World-space Y elevation at (x, z) via bilinear interpolation.</summary>
        public float SampleWorld(float x, float z)
        {
            int   res   = Resolution;
            float cellW = WorldWidth  / (res - 1);
            float cellH = WorldHeight / (res - 1);

            float nx = (x - WorldXMin) / cellW;
            float nz = (z - WorldZMin) / cellH;

            int col0 = Mathf.Clamp(Mathf.FloorToInt(nx), 0, res - 2);
            int row0 = Mathf.Clamp(Mathf.FloorToInt(nz), 0, res - 2);
            float tx = Mathf.Clamp01(nx - col0);
            float tz = Mathf.Clamp01(nz - row0);

            float v00 = _values[ row0      * res + col0    ];
            float v10 = _values[ row0      * res + col0 + 1];
            float v01 = _values[(row0 + 1) * res + col0    ];
            float v11 = _values[(row0 + 1) * res + col0 + 1];

            float norm = (v00 * (1 - tx) + v10 * tx) * (1 - tz)
                       + (v01 * (1 - tx) + v11 * tx) * tz;

            return MinElevation + norm * (MaxElevation - MinElevation);
        }

        /// <summary>
        /// Returns true if the line segment from <paramref name="from"/> to
        /// <paramref name="to"/> is blocked by terrain at any of <paramref name="steps"/>
        /// equally-spaced sample points (endpoints excluded).
        /// </summary>
        public bool IsOccluded(Vector3 from, Vector3 to, int steps = 12)
        {
            // i=1..steps inclusive — endpoint (the car's own position) is checked at i==steps.
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = from.x + t * (to.x - from.x);
                float y = from.y + t * (to.y - from.y);
                float z = from.z + t * (to.z - from.z);
                if (SampleWorld(x, z) > y)
                    return true;
            }
            return false;
        }
    }
}
