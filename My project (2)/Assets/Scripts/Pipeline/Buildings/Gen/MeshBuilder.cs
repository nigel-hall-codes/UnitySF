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
    /// </summary>
    public sealed class MeshBuilder
    {
        const int RoleCount = 6;   // MaterialRole: Base, Accent1, Accent2, Glass, Metal, Sign

        readonly List<Vector3> _verts = new List<Vector3>(512);
        readonly List<Vector3> _normals = new List<Vector3>(512);
        readonly List<Vector2> _uv0 = new List<Vector2>(512);
        readonly List<Vector2> _uv1 = new List<Vector2>(512);
        readonly List<int>[] _indices = NewBuckets();

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

        /// <summary>Add a vertex, deriving both UV sets from its position (see the class remarks).</summary>
        public int Vert(Vector3 p, Vector3 n)
        {
            var uv0 = new Vector2(p.x, p.y);
            var uv1 = new Vector2((p.x - _localOrigin.x) / _localSize.x,
                                  (p.y - _localOrigin.y) / _localSize.y);
            return Vert(p, n, uv0, uv1);
        }

        /// <summary>Add a vertex with explicit UVs — for the rare generator that wants its own
        /// projection (a sign face, say) rather than the planar default.</summary>
        public int Vert(Vector3 p, Vector3 n, Vector2 uv0, Vector2 uv1)
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
            _role = MaterialRole.Base;
            _localOrigin = Vector2.zero;
            _localSize = Vector2.one;
        }
    }
}
