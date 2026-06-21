using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace SFMap.Pipeline
{
    // IJobParallelFor: index = road segment (one per p0→p1 span across all active edges).
    [BurstCompile]
    struct StampRoadSegmentJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> P0X, P0Y, P0Z;
        [ReadOnly] public NativeArray<float> P1X, P1Y, P1Z;
        [ReadOnly] public NativeArray<float> StampW;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> Heightmap;

        public int   Res;
        public float CellW, CellH, WrX, WrY, Pad, MinElev, ElevRange;

        public void Execute(int seg)
        {
            float p0x = P0X[seg], p0y = P0Y[seg], p0z = P0Z[seg];
            float p1x = P1X[seg], p1y = P1Y[seg], p1z = P1Z[seg];
            float sw = StampW[seg];

            float dx = p1x - p0x;
            float dz = p1z - p0z;
            float segLen = (float)System.Math.Sqrt(dx * dx + dz * dz);
            if (segLen < 0.001f) return;

            float dirNx = dx / segLen;
            float dirNz = dz / segLen;
            float perpX = -dirNz;
            float perpZ =  dirNx;

            float minX = p0x < p1x ? p0x : p1x;
            float maxX = p0x > p1x ? p0x : p1x;
            float minZ = p0z < p1z ? p0z : p1z;
            float maxZ = p0z > p1z ? p0z : p1z;

            int colMin = FloorClamped((minX - sw - WrX) / CellW, Res);
            int colMax = CeilClamped ((maxX + sw - WrX) / CellW, Res);
            int rowMin = FloorClamped((minZ - sw - WrY) / CellH, Res);
            int rowMax = CeilClamped ((maxZ + sw - WrY) / CellH, Res);

            for (int row = rowMin; row <= rowMax; row++)
            {
                float wz   = WrY + row * CellH;
                float toPz = wz - p0z;
                for (int col = colMin; col <= colMax; col++)
                {
                    float wx   = WrX + col * CellW;
                    float toPx = wx - p0x;
                    float along   = toPx * dirNx + toPz * dirNz;
                    float lateral = toPx * perpX  + toPz * perpZ;
                    if (lateral < 0f) lateral = -lateral;
                    if (along < -Pad || along > segLen + Pad || lateral > sw) continue;

                    float t    = along / segLen;
                    float elev = p0y + t * (p1y - p0y);
                    Heightmap[row * Res + col] = (elev - MinElev) / ElevRange;
                }
            }
        }

        static int FloorClamped(float v, int res)
        {
            int i = (int)v;
            if (v < i) i--;          // correct truncation toward zero for negatives
            return i < 0 ? 0 : i;
        }

        static int CeilClamped(float v, int res)
        {
            int i = (int)v;
            if (v > i) i++;
            return i >= res ? res - 1 : i;
        }
    }

    // IJobParallelFor: index = intersection (one per intersection node with a valid polygon).
    [BurstCompile]
    struct StampCircleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> CX, CZ, R, Normalized;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> Heightmap;

        public int   Res;
        public float CellW, CellH, WrX, WrY;

        public void Execute(int i)
        {
            float cx   = CX[i];
            float cz   = CZ[i];
            float r    = R[i];
            float r2   = r * r;
            float norm = Normalized[i];

            int colMin = FloorClamped((cx - r - WrX) / CellW, Res);
            int colMax = CeilClamped ((cx + r - WrX) / CellW, Res);
            int rowMin = FloorClamped((cz - r - WrY) / CellH, Res);
            int rowMax = CeilClamped ((cz + r - WrY) / CellH, Res);

            for (int row = rowMin; row <= rowMax; row++)
            {
                float wz = WrY + row * CellH;
                float dz = wz - cz;
                for (int col = colMin; col <= colMax; col++)
                {
                    float wx = WrX + col * CellW;
                    float dx = wx - cx;
                    if (dx * dx + dz * dz > r2) continue;
                    Heightmap[row * Res + col] = norm;
                }
            }
        }

        static int FloorClamped(float v, int res)
        {
            int i = (int)v;
            if (v < i) i--;
            return i < 0 ? 0 : i;
        }

        static int CeilClamped(float v, int res)
        {
            int i = (int)v;
            if (v > i) i++;
            return i >= res ? res - 1 : i;
        }
    }

    // Dispatch helpers — own the NativeArray lifecycle for each stamp pass.
    static class HeightmapStampJobs
    {
        const float SidewalkWidth = 1.5f; // mirrors RoadMeshGenerator

        // Flattens all road segments across all edges into a single IJobParallelFor dispatch.
        public static void StampAllRoadSegments(
            IList<StreetEdge> edges,
            IList<Vector3[]>  stampedCenterlines,
            HeightmapData     heightmap,
            Rect              worldRect,
            float             widthMultiplier)
        {
            int   res      = heightmap.Resolution;
            float cellW    = worldRect.width  / (res - 1);
            float cellH    = worldRect.height / (res - 1);
            float pad      = (float)System.Math.Sqrt(cellW * cellW + cellH * cellH) * 0.5f;
            float elevRange = heightmap.MaxElevationMeters - heightmap.MinElevationMeters;
            if (elevRange < 0.001f) elevRange = 1f;

            // Build flat per-segment arrays.
            var p0xL = new List<float>();
            var p0yL = new List<float>();
            var p0zL = new List<float>();
            var p1xL = new List<float>();
            var p1yL = new List<float>();
            var p1zL = new List<float>();
            var swL  = new List<float>();

            for (int e = 0; e < edges.Count; e++)
            {
                float stampW = edges[e].Width * widthMultiplier * 0.5f + SidewalkWidth + pad;
                Vector3[] cl = stampedCenterlines[e];
                for (int s = 0; s < cl.Length - 1; s++)
                {
                    p0xL.Add(cl[s].x);     p0yL.Add(cl[s].y);     p0zL.Add(cl[s].z);
                    p1xL.Add(cl[s + 1].x); p1yL.Add(cl[s + 1].y); p1zL.Add(cl[s + 1].z);
                    swL.Add(stampW);
                }
            }

            int n = p0xL.Count;
            if (n == 0) return;

            var nP0X = new NativeArray<float>(n, Allocator.TempJob);
            var nP0Y = new NativeArray<float>(n, Allocator.TempJob);
            var nP0Z = new NativeArray<float>(n, Allocator.TempJob);
            var nP1X = new NativeArray<float>(n, Allocator.TempJob);
            var nP1Y = new NativeArray<float>(n, Allocator.TempJob);
            var nP1Z = new NativeArray<float>(n, Allocator.TempJob);
            var nSW  = new NativeArray<float>(n, Allocator.TempJob);

            for (int i = 0; i < n; i++)
            {
                nP0X[i] = p0xL[i]; nP0Y[i] = p0yL[i]; nP0Z[i] = p0zL[i];
                nP1X[i] = p1xL[i]; nP1Y[i] = p1yL[i]; nP1Z[i] = p1zL[i];
                nSW[i]  = swL[i];
            }

            var nHm = ToNative(heightmap);

            new StampRoadSegmentJob
            {
                P0X = nP0X, P0Y = nP0Y, P0Z = nP0Z,
                P1X = nP1X, P1Y = nP1Y, P1Z = nP1Z,
                StampW    = nSW,
                Heightmap = nHm,
                Res       = res,
                CellW     = cellW,
                CellH     = cellH,
                WrX       = worldRect.x,
                WrY       = worldRect.y,
                Pad       = pad,
                MinElev   = heightmap.MinElevationMeters,
                ElevRange = elevRange,
            }.Schedule(n, 4).Complete();

            CopyBack(nHm, heightmap);

            nP0X.Dispose(); nP0Y.Dispose(); nP0Z.Dispose();
            nP1X.Dispose(); nP1Y.Dispose(); nP1Z.Dispose();
            nSW.Dispose();
            nHm.Dispose();
        }

        // Stamps all intersection circles in a single IJobParallelFor dispatch.
        // circles: (cx, cz, r, normalized) — r already includes half-cell-diagonal pad.
        public static void StampAllCircles(
            IList<(float cx, float cz, float r, float normalized)> circles,
            HeightmapData heightmap,
            Rect          worldRect)
        {
            int   res   = heightmap.Resolution;
            float cellW = worldRect.width  / (res - 1);
            float cellH = worldRect.height / (res - 1);

            int n = circles.Count;
            var nCX   = new NativeArray<float>(n, Allocator.TempJob);
            var nCZ   = new NativeArray<float>(n, Allocator.TempJob);
            var nR    = new NativeArray<float>(n, Allocator.TempJob);
            var nNorm = new NativeArray<float>(n, Allocator.TempJob);

            for (int i = 0; i < n; i++)
            {
                nCX[i]   = circles[i].cx;
                nCZ[i]   = circles[i].cz;
                nR[i]    = circles[i].r;
                nNorm[i] = circles[i].normalized;
            }

            var nHm = ToNative(heightmap);

            new StampCircleJob
            {
                CX = nCX, CZ = nCZ, R = nR, Normalized = nNorm,
                Heightmap = nHm,
                Res   = res,
                CellW = cellW,
                CellH = cellH,
                WrX   = worldRect.x,
                WrY   = worldRect.y,
            }.Schedule(n, 4).Complete();

            CopyBack(nHm, heightmap);

            nCX.Dispose(); nCZ.Dispose(); nR.Dispose(); nNorm.Dispose();
            nHm.Dispose();
        }

        static NativeArray<float> ToNative(HeightmapData hm)
        {
            int res = hm.Resolution;
            var arr = new NativeArray<float>(res * res, Allocator.TempJob);
            for (int r = 0; r < res; r++)
                for (int c = 0; c < res; c++)
                    arr[r * res + c] = hm.Values[r, c];
            return arr;
        }

        static void CopyBack(NativeArray<float> arr, HeightmapData hm)
        {
            int res = hm.Resolution;
            for (int r = 0; r < res; r++)
                for (int c = 0; c < res; c++)
                    hm.Values[r, c] = arr[r * res + c];
        }
    }
}
