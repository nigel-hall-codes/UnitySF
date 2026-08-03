using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Tests
{
    /// <summary>
    /// The correctness properties the procedural geometry kernel exists to hold (#453, design #452
    /// generators.md §6): the corner mitre, the flat/arched head sharing one code path, the
    /// submesh/role shape <c>BuildingAssembler.CloneRoleColored</c> consumes, and the parameter
    /// quantisation that collapses seeded jitter onto shared meshes.
    /// </summary>
    public class GeometryKernelTests
    {
        readonly List<Object> _spawned = new List<Object>();

        Mesh Track(PartMesh pm) { _spawned.Add(pm.mesh); return pm.mesh; }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        // ---- 1. ProfileSweep mitres a closed Rect without pinching -----------------------
        // The single most visible defect this kernel can have: a rectangular casing that narrows
        // to nothing at each corner. A correct mitre does the opposite — it *widens* the section
        // by 1/cos(θ/2), which for a 90° corner is exactly sqrt(2).

        const float Depth = 0.05f;    // how far the band stands proud of the wall
        const float Width = 0.20f;    // the band's lateral width

        static PartMesh SweepRect(float w, float h)
        {
            var mb = new MeshBuilder();
            Kernels.ProfileSweep(mb, Profiles.Scaled(Profiles.Flat, Depth, Width),
                                 Paths.Rect(w, h), MaterialRole.Accent1,
                                 closedPath: true, capEnds: false, smoothAlong: true);
            return mb.Finish("rect_sweep");
        }

        [Test]
        public void ClosedRectSweepWidensAtCornersInsteadOfPinching()
        {
            var pm = SweepRect(2f, 1.5f);
            var v = Track(pm).vertices;

            // smoothAlong shares one ring per path vertex, so verts are ring-major:
            // 4 path corners x 2 profile points.
            Assert.AreEqual(8, v.Length, "expected one shared ring per rect corner");

            for (int i = 0; i < 4; i++)
            {
                float lateral = (v[i * 2 + 1] - v[i * 2]).magnitude;
                Assert.Greater(lateral, Width * 1.001f,
                    $"corner {i} pinched: section is {lateral:F4} m, narrower than the nominal {Width} m");
                Assert.AreEqual(Width * Mathf.Sqrt(2f), lateral, 1e-4f,
                    $"corner {i} is not mitred by 1/cos(45 deg)");
            }
        }

        [Test]
        public void ClosedRectSweepKeepsSectionCentredOnThePathAndOffTheWall()
        {
            var path = Paths.Rect(2f, 1.5f);
            var pm = SweepRect(2f, 1.5f);
            var v = Track(pm).vertices;

            // The mitre may only move the section within the plane of the turn. The turn is in XY,
            // so the outward (+Z) offset must survive it untouched, and the section must stay
            // centred on its path point.
            for (int i = 0; i < 4; i++)
            {
                Vector3 mid = (v[i * 2] + v[i * 2 + 1]) * 0.5f;
                Vector3 offset = mid - path[i];
                Assert.AreEqual(0f, offset.x, 1e-4f, $"corner {i} section drifted along X");
                Assert.AreEqual(0f, offset.y, 1e-4f, $"corner {i} section drifted along Y");
                Assert.AreEqual(Depth, offset.z, 1e-4f, $"corner {i} lost its projection off the wall");
            }
        }

        [Test]
        public void ClosedRectSweepStitchesTheSeamRatherThanCapping()
        {
            var pm = SweepRect(2f, 1.5f);
            var mesh = Track(pm);
            // 4 segments (the seam included) x 1 profile span x 2 triangles.
            Assert.AreEqual(1, mesh.subMeshCount);
            Assert.AreEqual(8 * 3, mesh.GetTriangles(0).Length);
        }

        [Test]
        public void StraightRunIsNotWidened()
        {
            // Baseline for the assertions above: with no corner to mitre, the section keeps its
            // nominal width exactly.
            var mb = new MeshBuilder();
            Kernels.ProfileSweep(mb, Profiles.Scaled(Profiles.Flat, Depth, Width),
                                 Paths.Line(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), 4),
                                 MaterialRole.Accent1, capEnds: false, smoothAlong: true);
            var v = Track(mb.Finish("line_sweep")).vertices;
            for (int i = 0; i < v.Length / 2; i++)
                Assert.AreEqual(Width, (v[i * 2 + 1] - v[i * 2]).magnitude, 1e-5f);
        }

        // ---- 2. Arc(rise: 0) is Line ----------------------------------------------------
        // So a flat window head and an arched one are the same code path with one parameter
        // changed, and no caller ever branches on head type to pick a sweep.

        [Test]
        public void ArcWithZeroRiseProducesExactlyTheLinePath()
        {
            var line = Paths.Line(new Vector3(-1.1f, 0f, 0f), new Vector3(1.1f, 0f, 0f), 8);
            var arc = Paths.Arc(2.2f, 0f, 8);

            Assert.AreEqual(line.Length, arc.Length);
            for (int i = 0; i < line.Length; i++)
                Assert.AreEqual(line[i], arc[i], $"path point {i} differs");
        }

        [Test]
        public void ArcWithZeroRiseSweepsIdenticalGeometryToLine()
        {
            var profile = Profiles.Scaled(Profiles.Ogee, 0.08f, 0.12f);

            var lineMb = new MeshBuilder();
            Kernels.ProfileSweep(lineMb, profile,
                                 Paths.Line(new Vector3(-1.1f, 0f, 0f), new Vector3(1.1f, 0f, 0f), 8),
                                 MaterialRole.Accent2, smoothAlong: true);
            var flat = Track(lineMb.Finish("flat_head"));

            var arcMb = new MeshBuilder();
            Kernels.ProfileSweep(arcMb, profile, Paths.Arc(2.2f, 0f, 8),
                                 MaterialRole.Accent2, smoothAlong: true);
            var arched = Track(arcMb.Finish("arched_head"));

            Assert.AreEqual(flat.vertexCount, arched.vertexCount);
            var a = flat.vertices; var b = arched.vertices;
            for (int i = 0; i < a.Length; i++) Assert.AreEqual(a[i], b[i], $"vertex {i}");
            var na = flat.normals; var nb = arched.normals;
            for (int i = 0; i < na.Length; i++) Assert.AreEqual(na[i], nb[i], $"normal {i}");
            CollectionAssert.AreEqual(flat.GetTriangles(0), arched.GetTriangles(0));
        }

        [Test]
        public void ArcWithRiseActuallyArches()
        {
            // Guards the degenerate case above from being vacuously true.
            var arc = Paths.Arc(2f, 1f, 12);          // rise = w/2 → semicircle
            Assert.AreEqual(-1f, arc[0].x, 1e-4f);
            Assert.AreEqual(1f, arc[arc.Length - 1].x, 1e-4f);
            Assert.AreEqual(0f, arc[0].y, 1e-4f);
            Assert.AreEqual(1f, arc[6].y, 1e-3f, "apex should sit at the sagitta");
        }

        // ---- 3. Finish() emits the (mesh, submeshRoles) shape CloneRoleColored consumes ----

        [Test]
        public void FinishEmitsOneSubmeshPerUsedRoleInStableOrder()
        {
            var mb = new MeshBuilder();
            Kernels.Box(mb, Vector3.one * 0.2f, Vector3.zero, MaterialRole.Metal);
            Kernels.Box(mb, Vector3.one * 0.2f, Vector3.right, MaterialRole.Base);
            var pm = mb.Finish("two_roles");
            var mesh = Track(pm);

            // CloneRoleColored walks submeshes by index into submeshRoles, so the two must agree
            // in length; the order is ascending MaterialRole, which is why it is stable.
            Assert.AreEqual(mesh.subMeshCount, pm.submeshRoles.Length);
            CollectionAssert.AreEqual(new[] { MaterialRole.Base, MaterialRole.Metal }, pm.submeshRoles);
            Assert.AreEqual(12 * 3, mesh.GetTriangles(0).Length, "a full box is 12 triangles");
            Assert.AreEqual(12 * 3, mesh.GetTriangles(1).Length);
        }

        [Test]
        public void FinishSkipsRolesNothingWasEmittedFor()
        {
            var mb = new MeshBuilder();
            Kernels.Box(mb, Vector3.one * 0.2f, Vector3.zero, MaterialRole.Glass);
            var pm = mb.Finish("one_role");
            Track(pm);
            CollectionAssert.AreEqual(new[] { MaterialRole.Glass }, pm.submeshRoles);
        }

        [Test]
        public void DroppingTheBackFaceRemovesExactlyTwoTriangles()
        {
            var mb = new MeshBuilder();
            Kernels.Box(mb, Vector3.one * 0.2f, Vector3.zero, MaterialRole.Base, Faces.NoBack);
            var pm = mb.Finish("no_back");
            Assert.AreEqual(20, Track(pm).vertexCount);
            Assert.AreEqual(10 * 3, pm.mesh.GetTriangles(0).Length);
        }

        // ---- 4. PartMeshCache collapses seeded jitter -------------------------------------

        static PartMesh Generate(MeshBuilder mb)
        {
            Kernels.Box(mb, new Vector3(0.9f, 2f, 0.1f), Vector3.zero, MaterialRole.Accent1);
            return mb.Finish("cached_part");
        }

        [Test]
        public void CacheCollapsesSubQuantumJitterOntoOneMesh()
        {
            var cache = new PartMeshCache();
            var rng = new System.Random(1234);
            Mesh first = null;

            // 32 placements of one preset, each jittered by well under the 5 mm quantum — exactly
            // what BuildingAssembler's seeded per-slot Rng does to a parameter set.
            for (int i = 0; i < 32; i++)
            {
                float jitter = (float)(rng.NextDouble() - 0.5) * 0.002f;   // +/- 1 mm
                var key = PartKey.From("window.double_hung", DetailLevel.Full,
                                       1.2f + jitter, 2.0f + jitter, 0.07f);
                var pm = cache.GetOrCreate(key, Generate);
                if (first == null) first = pm.mesh; else Assert.AreSame(first, pm.mesh);
            }

            Assert.AreEqual(1, cache.Generated, "N placements of one preset must generate one mesh");
            Assert.AreEqual(31, cache.Hits);
            Assert.AreEqual(1, cache.Count);
            cache.Clear();
        }

        [Test]
        public void CacheStillSeparatesGenuinelyDifferentParameterSets()
        {
            var cache = new PartMeshCache();
            cache.GetOrCreate(PartKey.From("window.double_hung", DetailLevel.Full, 1.2f), Generate);
            cache.GetOrCreate(PartKey.From("window.double_hung", DetailLevel.Full, 1.3f), Generate);
            cache.GetOrCreate(PartKey.From("window.double_hung", DetailLevel.Reduced, 1.2f), Generate);
            cache.GetOrCreate(PartKey.From("door.panel", DetailLevel.Full, 1.2f), Generate);

            Assert.AreEqual(4, cache.Generated);
            Assert.AreEqual(0, cache.Hits);
            cache.Clear();
        }

        [Test]
        public void QuantisedKeysAreEqualAndHashAlike()
        {
            var a = PartKey.From("g", DetailLevel.Full, 1.2f, 0.35f);
            var b = PartKey.From("g", DetailLevel.Full, 1.2004f, 0.3496f);
            Assert.AreEqual(a, b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }
    }
}
