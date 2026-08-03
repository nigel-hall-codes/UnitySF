using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// Role-partitioned mesh accumulator — one instance per generation run, reused across every
    /// part in a chunk import rather than allocated per quad (#453, design #452 generators.md §1).
    /// Positions/normals/UVs are shared; indices are bucketed per <see cref="MaterialRole"/>, so
    /// <see cref="Finish"/> returns a mesh whose submeshes are index-aligned to a
    /// <c>MaterialRole[]</c> — the exact pair <c>BuildingAssembler.CloneRoleColored</c> already
    /// consumes, which is why <c>BakeAndCombine</c> needs zero changes (#453 acceptance 3).
    /// <para>
    /// <b>Winding.</b> A triangle <c>(a, b, c)</c> must be wound so that
    /// <c>Vector3.Cross(pb - pa, pc - pa)</c> points along the visible face normal — the same
    /// convention <c>Mesh.RecalculateNormals</c> uses, so the explicit normals written by
    /// <see cref="Vert(Vector3, Vector3)"/> and the winding can never disagree. Callers that would
    /// rather not reason about it use <see cref="TriFacing"/>/<see cref="QuadFacing"/>.
    /// </para>
    /// <para>
    /// <b>UVs, once, here</b> (design #452 D4). <c>uv0</c> is a planar projection in metres
    /// (<c>p.x, p.y</c>) for material-scale detail. <c>uv1</c> is the same point normalised to the
    /// part's own 0..1 rect (see <see cref="SetLocalRect"/>); the assembler remaps that rect onto
    /// the facade so the decal path (#280/#281) keeps landing correctly. The generator stays
    /// ignorant of which building it is on, which is what keeps <see cref="PartMeshCache"/> valid.
    /// </para>
    /// <para>
    /// <b>Composition</b> (design #452 D3, #489). A bay window is a volume with <i>the window
    /// family's</i> windows on it, a stoop is steps with the railing family's railing on it — so a
    /// generator has to be able to carry another's geometry into its own output. The transform stack
    /// (<see cref="PushTransform"/>/<see cref="PopTransform"/>) is what makes that a property of the
    /// builder rather than a loop every composite family writes for itself; <see cref="Append"/> is
    /// the one that does it for a finished <see cref="PartMesh"/>, roles intact.
    /// </para>
    /// </summary>
    public sealed class MeshBuilder
    {
        const int RoleCount = 6;   // MaterialRole: Base, Accent1, Accent2, Glass, Metal, Sign

        readonly List<Vector3> _verts = new List<Vector3>(512);
        readonly List<Vector3> _normals = new List<Vector3>(512);
        readonly List<Vector2> _uv0 = new List<Vector2>(512);
        readonly List<Vector2> _uv1 = new List<Vector2>(512);
        readonly List<int>[] _indices = NewBuckets();

        /// <summary>Composed transforms, innermost last. Empty is the fast path every non-composite
        /// generator takes: no matrix touches a vertex at all.</summary>
        readonly List<Matrix4x4> _transforms = new List<Matrix4x4>(2);

        MaterialRole _role = MaterialRole.Base;
        Vector2 _localOrigin = Vector2.zero;
        Vector2 _localSize = Vector2.one;

        static List<int>[] NewBuckets()
        {
            var b = new List<int>[RoleCount];
            for (int i = 0; i < RoleCount; i++) b[i] = new List<int>(512);
            return b;
        }

        /// <summary>Vertices added so far. Kernels use it to remember where their ring started.</summary>
        public int VertexCount => _verts.Count;

        /// <summary>The rect, in part-local metres, that <c>uv1</c> normalises against — the part's
        /// own extent, set once by the family generator before it starts emitting. Left at
        /// (origin 0, size 1) the builder simply writes metres into <c>uv1</c> as well.</summary>
        public void SetLocalRect(float width, float height, Vector2 origin = default)
        {
            _localSize = new Vector2(Mathf.Abs(width) > 1e-6f ? width : 1f,
                                     Mathf.Abs(height) > 1e-6f ? height : 1f);
            _localOrigin = origin;
        }

        /// <summary>Triangles added from here on land in <paramref name="role"/>'s bucket.</summary>
        public void BeginRole(MaterialRole role) => _role = role;

        // ---- transform stack (#489) ---------------------------------------------------------

        /// <summary>
        /// Every vertex added until the matching <see cref="PopTransform"/> is placed by
        /// <paramref name="m"/>, composed with whatever is already on the stack. Both UV sets are
        /// derived <i>after</i> the transform, so a child's <c>uv1</c> lands in the <b>parent's</b>
        /// rect — the thing the facade-decal remap wants.
        /// <para><b>Rigid and uniform-scale transforms only.</b> Normals go through
        /// <c>MultiplyVector</c> and are renormalised, which is the inverse transpose exactly when
        /// the rotation part is orthogonal. A non-uniform scale would need the real inverse
        /// transpose and is not supported; composition places parts, it does not squash them.</para>
        /// </summary>
        public void PushTransform(Matrix4x4 m)
            => _transforms.Add(_transforms.Count == 0 ? m : _transforms[_transforms.Count - 1] * m);

        /// <summary>Undo the innermost <see cref="PushTransform"/>.</summary>
        public void PopTransform()
        {
            if (_transforms.Count > 0) _transforms.RemoveAt(_transforms.Count - 1);
        }

        /// <summary>How many transforms are on the stack. Zero while a generator is emitting its own
        /// geometry; a caller can assert on it to prove it balanced its pushes.</summary>
        public int TransformDepth => _transforms.Count;

        /// <summary>The matrix that places geometry authored in the frame
        /// (<paramref name="axisX"/>, <paramref name="axisY"/>, <paramref name="axisZ"/>) with its
        /// origin at <paramref name="origin"/> — a child part's own +X/+Y/+Z laid onto a facet of
        /// the parent. Pass an orthonormal right-handed frame (<c>X × Y = Z</c>) and winding is
        /// preserved, so nothing needs re-facing.</summary>
        public static Matrix4x4 Frame(Vector3 origin, Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            var m = new Matrix4x4();
            m.SetColumn(0, new Vector4(axisX.x, axisX.y, axisX.z, 0f));
            m.SetColumn(1, new Vector4(axisY.x, axisY.y, axisY.z, 0f));
            m.SetColumn(2, new Vector4(axisZ.x, axisZ.y, axisZ.z, 0f));
            m.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1f));
            return m;
        }

        /// <summary>
        /// Fold a finished child part into this builder at <paramref name="transform"/>, keeping
        /// each submesh's <see cref="MaterialRole"/> (#489). This is the composition entry point:
        /// before it, every composite family wrote its own read-back-and-retransform loop, and #473
        /// shipped ~40 lines of one.
        /// <para>The active role is restored afterwards, so appending a child never silently
        /// redirects the geometry the caller emits next.</para>
        /// </summary>
        public void Append(in PartMesh src, Matrix4x4 transform)
        {
            if (!src.IsValid) return;
            Vector3[] verts = src.mesh.vertices;
            Vector3[] norms = src.mesh.normals;
            if (verts == null || verts.Length == 0) return;

            var map = new int[verts.Length];
            PushTransform(transform);
            for (int i = 0; i < verts.Length; i++)
                map[i] = Vert(verts[i], norms != null && i < norms.Length ? norms[i] : Vector3.forward);
            PopTransform();

            MaterialRole resume = _role;
            int subs = Mathf.Min(src.mesh.subMeshCount, src.submeshRoles.Length);
            for (int s = 0; s < subs; s++)
            {
                BeginRole(src.submeshRoles[s]);
                int[] tris = src.mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    Tri(map[tris[t]], map[tris[t + 1]], map[tris[t + 2]]);
            }
            BeginRole(resume);
        }

        // ---- vertices -------------------------------------------------------------------------

        /// <summary>Add a vertex, deriving both UV sets from its position (see the class remarks).</summary>
        public int Vert(Vector3 p, Vector3 n)
        {
            Place(ref p, ref n);
            var uv0 = new Vector2(p.x, p.y);
            var uv1 = new Vector2((p.x - _localOrigin.x) / _localSize.x,
                                  (p.y - _localOrigin.y) / _localSize.y);
            return Add(p, n, uv0, uv1);
        }

        /// <summary>Add a vertex with explicit UVs — for the rare generator that wants its own
        /// projection (a sign face, say) rather than the planar default.</summary>
        public int Vert(Vector3 p, Vector3 n, Vector2 uv0, Vector2 uv1)
        {
            Place(ref p, ref n);
            return Add(p, n, uv0, uv1);
        }

        void Place(ref Vector3 p, ref Vector3 n)
        {
            if (_transforms.Count == 0) return;
            Matrix4x4 m = _transforms[_transforms.Count - 1];
            p = m.MultiplyPoint3x4(p);
            n = m.MultiplyVector(n).normalized;
        }

        int Add(Vector3 p, Vector3 n, Vector2 uv0, Vector2 uv1)
        {
            _verts.Add(p);
            _normals.Add(n);
            _uv0.Add(uv0);
            _uv1.Add(uv1);
            return _verts.Count - 1;
        }

        public void Tri(int a, int b, int c)
        {
            var bucket = _indices[(int)_role];
            bucket.Add(a); bucket.Add(b); bucket.Add(c);
        }

        /// <summary>Two triangles from four corners given in loop order.</summary>
        public void Quad(int a, int b, int c, int d)
        {
            Tri(a, b, c);
            Tri(a, c, d);
        }

        /// <summary>As <see cref="Tri"/>, but flips the winding if it disagrees with
        /// <paramref name="outward"/>. Kernels that build geometry from a sign table (bevel bands,
        /// corner triangles) use this instead of hand-deriving handedness per case.</summary>
        public void TriFacing(int a, int b, int c, Vector3 outward)
        {
            Vector3 pa = _verts[a], pb = _verts[b], pc = _verts[c];
            if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), outward) < 0f) Tri(a, c, b);
            else Tri(a, b, c);
        }

        /// <summary>As <see cref="Quad"/>, but oriented to <paramref name="outward"/>.</summary>
        public void QuadFacing(int a, int b, int c, int d, Vector3 outward)
        {
            Vector3 pa = _verts[a], pb = _verts[b], pc = _verts[c];
            if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), outward) < 0f)
            {
                Tri(a, d, c);
                Tri(a, c, b);
            }
            else
            {
                Tri(a, b, c);
                Tri(a, c, d);
            }
        }

        /// <summary>Bake everything accumulated into one mesh. Submeshes appear in ascending
        /// <see cref="MaterialRole"/> order, skipping roles nothing was emitted for — a stable
        /// order, so a cached mesh and its <c>submeshRoles</c> never drift apart.</summary>
        public PartMesh Finish(string name = null)
        {
            var used = new List<MaterialRole>(RoleCount);
            for (int r = 0; r < RoleCount; r++)
                if (_indices[r].Count > 0) used.Add((MaterialRole)r);

            var mesh = new Mesh { name = string.IsNullOrEmpty(name) ? "GeneratedPart" : name };
            // Set the index format before the vertices, or Unity re-allocates the buffer.
            mesh.indexFormat = _verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uv0);
            mesh.SetUVs(1, _uv1);

            mesh.subMeshCount = used.Count;
            for (int s = 0; s < used.Count; s++)
                mesh.SetTriangles(_indices[(int)used[s]], s, false);

            mesh.RecalculateBounds();
            return new PartMesh(mesh, used.ToArray());
        }

        /// <summary>Reset for the next part. Keeps the backing arrays — that is the whole point of
        /// holding one builder for a chunk import.</summary>
        public void Clear()
        {
            _verts.Clear();
            _normals.Clear();
            _uv0.Clear();
            _uv1.Clear();
            for (int i = 0; i < RoleCount; i++) _indices[i].Clear();
            _transforms.Clear();
            _role = MaterialRole.Base;
            _localOrigin = Vector2.zero;
            _localSize = Vector2.one;
        }
    }
}
