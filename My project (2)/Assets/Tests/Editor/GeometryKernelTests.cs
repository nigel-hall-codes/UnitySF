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

        // ---- shared assertions ------------------------------------------------------------

        static void Same(Vector3 expected, Vector3 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, what + " (x)");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, what + " (y)");
            Assert.AreEqual(expected.z, actual.z, 1e-4f, what + " (z)");
        }

        static int TriangleCount(PartMesh pm)
        {
            int n = 0;
            for (int s = 0; s < pm.mesh.subMeshCount; s++) n += pm.mesh.GetTriangles(s).Length / 3;
            return n;
        }

        /// <summary>Triangles in one role's submesh — the only thing that pairs a submesh index with
        /// a <see cref="MaterialRole"/> is the <see cref="PartMesh"/> the builder hands back.</summary>
        static int RoleTriangles(PartMesh pm, MaterialRole role)
        {
            for (int s = 0; s < pm.submeshRoles.Length && s < pm.mesh.subMeshCount; s++)
                if (pm.submeshRoles[s] == role) return pm.mesh.GetTriangles(s).Length / 3;
            return 0;
        }

        // ---- 5. ProfileSweep with a per-point frame (#483) --------------------------------
        //
        // One upHint describes a path that stays in one plane. A band that wraps a corner building
        // turns in plan and "which way is out of the wall" turns with it, so the frame has to be
        // per point — see Kernels.ProfileSweep's per-point overload.

        /// <summary>A corner building's two street facades, chained: 3 m south along −Z off an east
        /// wall (outward +X), then 4 m west along −X off a south wall (outward −Z). The footprint is
        /// the quadrant behind them, so the corner at (4, ·, 0) is convex — one continuous run, two
        /// outward directions, and the smallest thing a single up-hint cannot describe.</summary>
        static readonly Vector3[] LPath =
        {
            new Vector3(4f, 0f, 3f), new Vector3(4f, 0f, 0f), new Vector3(0f, 0f, 0f),
        };

        /// <summary>Per-point outward, shaped exactly as <c>FacadeRun.outward</c> is: the face normal
        /// at each free end, the bisector of the two at the corner.</summary>
        static readonly Vector3[] LOutward =
        {
            new Vector3(1f, 0f, 0f),
            new Vector3(0.70710678f, 0f, -0.70710678f),
            new Vector3(0f, 0f, -1f),
        };

        static Vector3[] AtEastEnd(Mesh m) => Where(m.vertices, p => p.z > 3f - 1e-3f);
        static Vector3[] AtSouthEnd(Mesh m) => Where(m.vertices, p => p.x < 1e-3f);

        static Vector3[] Where(Vector3[] src, System.Predicate<Vector3> keep)
        {
            var hit = new List<Vector3>();
            foreach (var p in src) if (keep(p)) hit.Add(p);
            return hit.ToArray();
        }

        [Test]
        public void APerPointFrameProjectsTheSectionOutOfWhicheverWallItIsOn()
        {
            var mb = new MeshBuilder();
            Kernels.ProfileSweep(mb, Profiles.Scaled(Profiles.Flat, Depth, Width), LPath, LOutward,
                                 MaterialRole.Accent1, closedPath: false, capEnds: false,
                                 smoothAlong: false);
            var m = Track(mb.Finish("l_per_point"));

            // The east leg's free end faces +X, so the section stands `Depth` proud in X and spans
            // `Width` in Y — the authored (across = outward, up = height) frame, untransposed.
            var east = AtEastEnd(m);
            Assert.AreEqual(2, east.Length, "one ring at the east facade's free end");
            foreach (var p in east) Assert.AreEqual(4f + Depth, p.x, 1e-4f, "the east leg projects +X");
            Assert.AreEqual(Width, Mathf.Abs(east[0].y - east[1].y), 1e-4f, "…and stands upright");

            // The south leg's free end faces −Z — the same section, turned with the wall. This is
            // the assertion one hint cannot satisfy at the same time as the one above.
            var south = AtSouthEnd(m);
            Assert.AreEqual(2, south.Length, "one ring at the south facade's free end");
            foreach (var p in south) Assert.AreEqual(-Depth, p.z, 1e-4f, "the south leg projects −Z");
            Assert.AreEqual(Width, Mathf.Abs(south[0].y - south[1].y), 1e-4f, "…and still stands upright");
        }

        [Test]
        public void OneUpHintTakenFromTheFirstOutwardIsDegenerateAtTheTurn()
        {
            // Why the overload exists, stated as a test: feed the run's first outward (+X) in as the
            // single hint and the south leg's tangent (−X) is parallel to it, so the guard fires and
            // that leg's section swings out of the wall it belongs to — vertically.
            var mb = new MeshBuilder();
            Kernels.ProfileSweep(mb, Profiles.Scaled(Profiles.Flat, Depth, Width), LPath,
                                 MaterialRole.Accent1, closedPath: false, capEnds: false,
                                 smoothAlong: false, upHint: LOutward[0]);
            var south = AtSouthEnd(Track(mb.Finish("l_one_hint")));

            Assert.AreEqual(2, south.Length);
            Assert.IsFalse(Mathf.Abs(south[0].z - (-Depth)) < 1e-3f,
                           "if this ever passes, one hint became sufficient and #483 can be reverted");
            Assert.AreEqual(Depth, south[0].y, 1e-4f, "instead the section projects toward the sky");
        }

        [Test]
        public void APerPointFrameReproducesTheTransposeItReplaces()
        {
            // The regression guard on #483: #474 swept a turning band with upHint = +Y and
            // transposed every section onto the mirrored axes that produced. The per-point frame is
            // the same geometry expressed properly, so the two must agree — modulo the traversal
            // direction, because the two conventions want opposite handedness, and modulo vertex
            // order, because a reversed path visits the same rings the other way round.
            var section = Profiles.Scaled(Profiles.Ogee, Depth, Width);

            var perPoint = new MeshBuilder();
            Kernels.ProfileSweep(perPoint, section, LPath, LOutward, MaterialRole.Accent1,
                                 closedPath: false, capEnds: true, smoothAlong: false);

            var transposed = new MeshBuilder();
            Kernels.ProfileSweep(transposed, Transposed(Profiles.Ogee, Depth, Width),
                                 Reversed(LPath), MaterialRole.Accent1,
                                 closedPath: false, capEnds: true, smoothAlong: false,
                                 upHint: Vector3.up);

            var a = Track(perPoint.Finish("per_point"));
            var b = Track(transposed.Finish("transposed"));

            Assert.AreEqual(b.GetTriangles(0).Length, a.GetTriangles(0).Length,
                            "the per-point frame changed the triangle count");
            var sortedA = Sorted(a.vertices);
            var sortedB = Sorted(b.vertices);
            for (int i = 0; i < sortedA.Length; i++)
                Same(sortedB[i], sortedA[i], $"vertex {i} of the sorted set moved");
        }

        /// <summary>#474's <c>Banded</c>, kept here and nowhere else: components swapped onto the
        /// mirrored axes an <c>upHint = +Y</c> sweep produces, point order reversed to undo the
        /// reflection that swap is.</summary>
        static Vector2[] Transposed(Vector2[] profile, float projection, float height)
        {
            var s = new Vector2[profile.Length];
            for (int i = 0; i < profile.Length; i++)
            {
                Vector2 q = profile[profile.Length - 1 - i];
                s[i] = new Vector2(q.y * height, q.x * projection);
            }
            return s;
        }

        static Vector3[] Reversed(Vector3[] pts)
        {
            var r = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++) r[i] = pts[pts.Length - 1 - i];
            return r;
        }

        static Vector3[] Sorted(Vector3[] pts)
        {
            var s = (Vector3[])pts.Clone();
            System.Array.Sort(s, (p, q) =>
            {
                int c = Compare(p.x, q.x); if (c != 0) return c;
                c = Compare(p.y, q.y); if (c != 0) return c;
                return Compare(p.z, q.z);
            });
            return s;
        }

        static int Compare(float a, float b)
            => Mathf.Abs(a - b) < 1e-4f ? 0 : (a < b ? -1 : 1);

        [Test]
        public void ThePlanarOverloadIsUnchangedByTheAdditionOfPerPointFrames()
        {
            // The compatibility guarantee #483 promises: a planar caller gets the same geometry it
            // always got, and a constant per-point array says the same thing twice.
            var path = Paths.Rect(2f, 1.5f);
            var section = Profiles.Scaled(Profiles.Ogee, Depth, Width);

            var hinted = new MeshBuilder();
            Kernels.ProfileSweep(hinted, section, path, MaterialRole.Accent1,
                                 closedPath: true, capEnds: false, smoothAlong: true);
            var perPoint = new MeshBuilder();
            Kernels.ProfileSweep(perPoint, section, path,
                                 new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward },
                                 MaterialRole.Accent1, closedPath: true, capEnds: false, smoothAlong: true);

            var a = Track(hinted.Finish("hinted"));
            var b = Track(perPoint.Finish("per_point"));

            Assert.AreEqual(a.vertices.Length, b.vertices.Length);
            for (int i = 0; i < a.vertices.Length; i++)
            {
                Same(a.vertices[i], b.vertices[i], $"vertex {i} moved");
                Same(a.normals[i], b.normals[i], $"normal {i} moved");
            }
            CollectionAssert.AreEqual(a.GetTriangles(0), b.GetTriangles(0));
        }

        [Test]
        public void AWronglySizedOutwardArrayFallsBackToThePlanarDefault()
        {
            var path = Paths.Rect(2f, 1.5f);
            var mismatched = new MeshBuilder();
            Kernels.ProfileSweep(mismatched, Profiles.Scaled(Profiles.Flat, Depth, Width), path,
                                 new[] { Vector3.forward }, MaterialRole.Accent1,
                                 closedPath: true, capEnds: false, smoothAlong: true);

            var a = Track(mismatched.Finish("mismatched"));
            var b = Track(SweepRect(2f, 1.5f));
            Assert.AreEqual(b.vertices.Length, a.vertices.Length);
            for (int i = 0; i < b.vertices.Length; i++) Same(b.vertices[i], a.vertices[i], $"vertex {i}");
        }

        // ---- 6. PanelGrid holds several grids side by side (#481) --------------------------

        static PanelGridParams Leaf(float w, float offsetX) => new PanelGridParams
        {
            w = w, h = 2.0f, offsetX = offsetX,
            cols = PanelGridParams.Even(1), rows = PanelGridParams.Even(2),
            barW = 0.04f, barD = 0.03f, frameW = 0.06f, frameD = 0.04f,
            infillInset = 0.03f, panelBevel = 0.01f,
            frameRole = MaterialRole.Accent1, infillRole = MaterialRole.Base,
        };

        [Test]
        public void TwoPanelGridsAtDifferentLateralOffsetsAreIndependentAndDoNotOverlap()
        {
            // The #481 acceptance: a double door is two leaves, each with its own frame, stiles and
            // panel rows, meeting on a shared line — not one grid with a box down the middle.
            const float LeafW = 0.9f;
            var mb = new MeshBuilder();
            Kernels.PanelGrid(mb, Leaf(LeafW, -LeafW * 0.5f));
            Kernels.PanelGrid(mb, Leaf(LeafW, LeafW * 0.5f));
            var both = mb.Finish("two_leaves");
            Track(both);

            var one = new MeshBuilder();
            Kernels.PanelGrid(one, Leaf(LeafW, 0f));
            var single = one.Finish("one_leaf");
            Track(single);

            Assert.AreEqual(2 * TriangleCount(single), TriangleCount(both),
                            "two leaves cost exactly two leaves");
            Assert.AreEqual(-LeafW, both.mesh.bounds.min.x, 1e-4f);
            Assert.AreEqual(LeafW, both.mesh.bounds.max.x, 1e-4f);

            // Neither leaf crosses the meeting line, and both reach it: they share an edge and no
            // geometry.
            int left = 0, right = 0;
            foreach (var p in both.mesh.vertices)
            {
                if (p.x < -1e-4f) left++;
                else if (p.x > 1e-4f) right++;
            }
            Assert.Greater(left, 0);
            Assert.AreEqual(left, right, "the two leaves are mirror images, vertex for vertex");
        }

        [Test]
        public void ALateralOffsetOnlyTranslatesTheGrid()
        {
            // The compatibility guarantee: offsetX defaults to 0 and is a pure translation, so its
            // addition cannot affect any caller that occupies a single opening.
            const float Shift = 0.75f;
            var centred = new MeshBuilder();
            Kernels.PanelGrid(centred, Leaf(1.1f, 0f), offsetY: 0.2f, offsetZ: -0.05f);
            var moved = new MeshBuilder();
            Kernels.PanelGrid(moved, Leaf(1.1f, Shift), offsetY: 0.2f, offsetZ: -0.05f);

            var a = Track(centred.Finish("centred"));
            var b = Track(moved.Finish("moved"));
            Assert.AreEqual(a.vertices.Length, b.vertices.Length);
            for (int i = 0; i < a.vertices.Length; i++)
                Same(a.vertices[i] + new Vector3(Shift, 0f, 0f), b.vertices[i],
                     $"vertex {i} is not a pure translation");
        }

        // ---- 7. MeshBuilder transform stack and Append (#489) -----------------------------

        [Test]
        public void PushTransformPlacesGeometryAndPopUndoesIt()
        {
            var mb = new MeshBuilder();
            Assert.AreEqual(0, mb.TransformDepth);

            // A quarter turn about +Y (+X onto +Z, +Z onto −X), moved 2 m along +X.
            mb.PushTransform(MeshBuilder.Frame(new Vector3(2f, 0f, 0f),
                                               Vector3.forward, Vector3.up, Vector3.left));
            Assert.AreEqual(1, mb.TransformDepth);
            Kernels.Box(mb, Vector3.one, Vector3.zero, MaterialRole.Base, Faces.Front);
            mb.PopTransform();
            Assert.AreEqual(0, mb.TransformDepth);
            Kernels.Box(mb, Vector3.one, Vector3.zero, MaterialRole.Base, Faces.Front);

            var m = Track(mb.Finish("transformed"));

            // The first face was authored at z = +0.5 facing +Z; the frame turns that into
            // x = 1.5 facing −X. The second, emitted after the pop, is where it was authored.
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(1.5f, m.vertices[i].x, 1e-4f, "the pushed face was not placed");
                Same(Vector3.left, m.normals[i], "the pushed normal did not turn with it");
            }
            for (int i = 4; i < 8; i++)
            {
                Assert.AreEqual(0.5f, m.vertices[i].z, 1e-4f, "the popped face moved anyway");
                Same(Vector3.forward, m.normals[i], "the popped normal moved anyway");
            }
        }

        [Test]
        public void NestedPushesCompose()
        {
            var mb = new MeshBuilder();
            mb.BeginRole(MaterialRole.Base);
            mb.PushTransform(MeshBuilder.Frame(new Vector3(1f, 0f, 0f),
                                               Vector3.right, Vector3.up, Vector3.forward));
            mb.PushTransform(MeshBuilder.Frame(new Vector3(0f, 2f, 0f),
                                               Vector3.right, Vector3.up, Vector3.forward));
            mb.Vert(Vector3.zero, Vector3.forward);
            mb.PopTransform();
            mb.Vert(Vector3.zero, Vector3.forward);
            mb.PopTransform();
            mb.Vert(Vector3.zero, Vector3.forward);
            mb.Tri(0, 1, 2);

            var v = Track(mb.Finish("nested")).vertices;
            Same(new Vector3(1f, 2f, 0f), v[0], "the inner push must compose with the outer");
            Same(new Vector3(1f, 0f, 0f), v[1], "popping the inner must leave the outer");
            Same(Vector3.zero, v[2], "popping both must leave nothing");
        }

        [Test]
        public void AppendCarriesAChildPartsTrianglesAndRolesIntoTheParent()
        {
            // The composition seam design.md D3 asks for and #473 had to hand-roll: a parent emits a
            // child generator's finished output with no read-back loop of its own, roles intact.
            var childBuilder = new MeshBuilder();
            Kernels.Box(childBuilder, new Vector3(0.4f, 0.4f, 0.1f), Vector3.zero,
                        MaterialRole.Glass, Faces.NoBack);
            Kernels.Box(childBuilder, new Vector3(0.5f, 0.05f, 0.1f), new Vector3(0f, 0.3f, 0f),
                        MaterialRole.Accent1, Faces.NoBack);
            var child = childBuilder.Finish("child");
            Track(child);

            var mb = new MeshBuilder();
            mb.Append(child, MeshBuilder.Frame(new Vector3(1f, 0f, 0f),
                                               Vector3.right, Vector3.up, Vector3.forward));
            mb.Append(child, MeshBuilder.Frame(new Vector3(-1f, 0f, 0f),
                                               Vector3.right, Vector3.up, Vector3.forward));
            // Emitted last, and with no BeginRole of its own: if Append left the active role on the
            // child's last submesh this quad lands in the wrong bucket.
            Kernels.Box(mb, Vector3.one, Vector3.zero, MaterialRole.Base, Faces.Front);
            var parent = mb.Finish("parent");
            Track(parent);

            Assert.AreEqual(2 * TriangleCount(child) + 2, TriangleCount(parent));
            Assert.AreEqual(2.5f, parent.mesh.bounds.size.x, 1e-4f, "both copies were placed");
            Assert.AreEqual(2 * RoleTriangles(child, MaterialRole.Glass),
                            RoleTriangles(parent, MaterialRole.Glass), "Glass did not survive");
            Assert.AreEqual(2 * RoleTriangles(child, MaterialRole.Accent1),
                            RoleTriangles(parent, MaterialRole.Accent1), "Accent1 did not survive");
            Assert.AreEqual(2, RoleTriangles(parent, MaterialRole.Base),
                            "Append left the active role pointing at the child's last submesh");
        }

        [Test]
        public void AppendRotatesAChildIntoTheParentsFrame()
        {
            // The bay's case: a child authored +Z-outward laid onto a facet whose outward is +X.
            var childBuilder = new MeshBuilder();
            Kernels.Box(childBuilder, new Vector3(0.6f, 0.4f, 0.2f), new Vector3(0f, 0f, 0.1f),
                        MaterialRole.Glass, Faces.Front);
            var child = childBuilder.Finish("child");
            Track(child);

            var mb = new MeshBuilder();
            mb.Append(child, MeshBuilder.Frame(new Vector3(5f, 0f, 0f),
                                               Vector3.forward, Vector3.up, Vector3.right));
            var parent = mb.Finish("rotated");
            Track(parent);

            Assert.AreEqual(TriangleCount(child), TriangleCount(parent));
            // The child's front face sat at z = 0.2; the facet frame puts it at x = 5.2, and its
            // 0.6 m width now runs along Z.
            Assert.AreEqual(5.2f, parent.mesh.bounds.min.x, 1e-4f);
            Assert.AreEqual(5.2f, parent.mesh.bounds.max.x, 1e-4f);
            Assert.AreEqual(0.6f, parent.mesh.bounds.size.z, 1e-4f);
            foreach (var n in parent.mesh.normals) Same(Vector3.right, n, "the normal turned with it");
        }
    }
}
