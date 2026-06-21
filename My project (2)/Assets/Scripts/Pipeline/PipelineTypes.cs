using UnityEngine;

namespace SFMap.Pipeline
{
    public struct HeightmapData
    {
        // [row, col] indexing: row=0 is south (min Z), col=0 is west (min X).
        // Matches Unity TerrainData.SetHeights(xBase, yBase, heights[y,x]) convention.
        public float[,] Values;
        public int Resolution;
        public float MinElevationMeters;
        public float MaxElevationMeters;
        public Rect WorldRect;

        // Bilinearly resample this heightmap into a new one covering chunk.WorldRect at targetResolution.
        public HeightmapData CropToChunk(ChunkBounds chunk, int targetResolution)
        {
            float srcCellW = WorldRect.width  / (Resolution - 1);
            float srcCellH = WorldRect.height / (Resolution - 1);
            float dstCellW = chunk.WorldRect.width  / (targetResolution - 1);
            float dstCellH = chunk.WorldRect.height / (targetResolution - 1);

            var values = new float[targetResolution, targetResolution];
            for (int row = 0; row < targetResolution; row++)
            {
                float wz = chunk.WorldRect.yMin + row * dstCellH;
                for (int col = 0; col < targetResolution; col++)
                {
                    float wx = chunk.WorldRect.xMin + col * dstCellW;
                    values[row, col] = SampleBilinear(wx, wz, srcCellW, srcCellH);
                }
            }

            return new HeightmapData
            {
                Values             = values,
                Resolution         = targetResolution,
                WorldRect          = chunk.WorldRect,
                MinElevationMeters = MinElevationMeters,
                MaxElevationMeters = MaxElevationMeters,
            };
        }

        float SampleBilinear(float wx, float wz, float srcCellW, float srcCellH)
        {
            float nx = (wx - WorldRect.xMin) / srcCellW;
            float nz = (wz - WorldRect.yMin) / srcCellH;

            int col0 = Mathf.Clamp(Mathf.FloorToInt(nx), 0, Resolution - 2);
            int row0 = Mathf.Clamp(Mathf.FloorToInt(nz), 0, Resolution - 2);
            float tx = Mathf.Clamp01(nx - col0);
            float tz = Mathf.Clamp01(nz - row0);

            float v00 = Values[row0,     col0    ];
            float v10 = Values[row0,     col0 + 1];
            float v01 = Values[row0 + 1, col0    ];
            float v11 = Values[row0 + 1, col0 + 1];

            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), tz);
        }
    }

    public readonly struct ChunkCoord
    {
        public readonly int Col;
        public readonly int Row;

        public ChunkCoord(int col, int row) { Col = col; Row = row; }
        public override string ToString() => $"chunk_{Col:00}_{Row:00}";
    }

    public struct ChunkBounds
    {
        public ChunkCoord Coord;
        public Rect WorldRect;

        public ChunkBounds(ChunkCoord coord, float chunkSizeMeters)
        {
            Coord     = coord;
            WorldRect = new Rect(
                coord.Col * chunkSizeMeters,
                coord.Row * chunkSizeMeters,
                chunkSizeMeters,
                chunkSizeMeters);
        }
    }

    public static class GeneratedAssets
    {
        public static string ActivePreset = "default";

        public static string Root => $"Assets/Generated/{ActivePreset}";

        public static string ChunkDir(ChunkCoord c)           => $"{Root}/{c}";
        public static string TerrainAsset(ChunkCoord c)       => $"{ChunkDir(c)}/Terrain.asset";
        public static string RoadMesh(ChunkCoord c, long id)  => $"{ChunkDir(c)}/Roads/road_{id}.mesh";
        public static string IntersectionMesh(ChunkCoord c, long id) => $"{ChunkDir(c)}/Intersections/intersection_{id}.mesh";
        public static string SidewalkMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Sidewalks/sidewalk_{id}.mesh";
        public static string BuildingMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Buildings/building_{id}.mesh";
        public static string RoadMaterial()                          => $"{Root}/Materials/RoadSurface.mat";
        public static string SidewalkMaterial()                      => $"{Root}/Materials/SidewalkSurface.mat";
        public static string ManifestPath()                          => $"{Root}/manifest.json";
    }
}
