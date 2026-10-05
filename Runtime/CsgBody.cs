// An object whose shape can be changed again and again - cut by a weapon, built up, intersected:
//
//     RaycastHit hit;
//     if (Physics.Raycast(ray, out hit))
//     {
//         CsgBody body = hit.collider.GetComponent<CsgBody>();
//         Quaternion rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
//         if (body != null) body.Subtract(bullet, Matrix4x4.TRS(hit.point, rotation, Vector3.one * 0.3f));
//     }
//
// The body imports its mesh on the first operation and from then on only works on its own Manifold
// body, never on the Unity mesh. Every face remembers where it came from, so the materials of the
// mesh, the interior material and the materials of tools survive any number of operations.
// Nothing about the object changes before the first operation, and ResetShape puts everything back:
// mesh, materials, colliders and mass. Native memory belongs to the component and is freed in OnDestroy.
using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Serialization;

namespace ManifoldCSG
{
    [RequireComponent(typeof(MeshFilter))]
    [DisallowMultipleComponent]
    public class CsgBody : MonoBehaviour
    {
        [Tooltip("Material of the body's inside, for the faces tools cut into it. Empty = same as the outside.")]
        [FormerlySerializedAs("cutMaterial")]
        [SerializeField] Material interiorMaterial;
        [Tooltip("Recalculate: from the geometry, edges sharper than Sharp Angle stay hard. Keep Imported: the mesh's " +
                 "own normals. Read on the first operation.")]
        public NormalMode normals = NormalMode.Recalculate;
        [Tooltip("Edges with a larger angle (degrees) stay sharp.")]
        [Range(0, 180)] public float sharpAngle = 50f;
        [Tooltip("Auto: an exact MeshCollider, a convex one on a non-kinematic Rigidbody, none on an object without " +
                 "a collider. Keep: the colliders stay as they are.")]
        public ColliderMode colliders = ColliderMode.Auto;
        [Tooltip("Scale the mass of the Rigidbody on this object with the remaining volume.")]
        public bool scaleMassWithVolume = true;
        [Tooltip("What happens when nothing is left of the body.")]
        public EmptyAction whenEmpty = EmptyAction.Destroy;

        /// <summary>Raised after every change of shape: operations and ResetShape.</summary>
        public event Action<CsgBody> Changed;

        // Visible in the Unity Profiler
        static readonly ProfilerMarker ApplyMarker = new ProfilerMarker("CsgBody.Apply");
        static readonly ProfilerMarker MeshMarker = new ProfilerMarker("CsgBody.UpdateMesh");
        static readonly ProfilerMarker ColliderMarker = new ProfilerMarker("CsgBody.UpdateCollider");

        // The object as it was authored, for ResetShape
        bool _initialized;
        MeshFilter _filter;
        MeshRenderer _renderer;
        Mesh _sourceMesh;
        Material[] _sourceMaterials;

        Manifold _original;                              // the imported source mesh, local space
        Manifold _body;                                  // the current shape, local space
        double _originalVolume;
        int _operations;
        bool _unusable;                                  // the mesh cannot be imported - stays that way
        string _lastError;
        bool _deactivated;

        Mesh _mesh;                                      // our own mesh, overwritten after every operation
        MeshData _meshData = new MeshData();             // export buffers, reused after every operation
        Mesh _hullMesh;                                  // convex colliders only
        MeshData _hullData = new MeshData();

        // Materials: every original id in the body belongs to a slot (= a submesh of our mesh), and every
        // slot stands for one material. A slot is an int (that submesh of the source mesh),
        // ToolEntry.Interior (the interior material) or a Material (a tool's own material).
        Dictionary<uint, int> _slotOfId = new Dictionary<uint, int>();
        List<object> _slots = new List<object>();
        Material[] _appliedMaterials;
        bool _materialsApplied;

        // Plane caps need normals even when the authored normals are kept.
        readonly Dictionary<uint, Vector3> _planeNormals = new Dictionary<uint, Vector3>();
        readonly List<CsgBody> _separatedParts = new List<CsgBody>();
        Mesh _ownedSourceMesh;                           // source mesh created for a separated part

        // Colliders: one MeshCollider is kept in step, the other solid colliders are disabled until ResetShape
        bool _collidersTaken;
        MeshCollider _meshCollider;
        bool _addedCollider;                             // ours: only disabled by ResetShape, used again later
        Mesh _colliderSourceMesh;
        bool _colliderSourceConvex;
        bool _colliderSourceEnabled;
        List<Collider> _disabledColliders = new List<Collider>();
        bool _warnedDynamic;

        // Mass
        Rigidbody _rigidbody;
        float _sourceMass;

        // --- Properties ---------------------------------------------------------------

        /// <summary>Material of the faces tools cut into the body. Null = same as the outside. Takes effect at once.</summary>
        public Material InteriorMaterial
        {
            get { return interiorMaterial; }
            set
            {
                interiorMaterial = value;
                if (_operations > 0) ApplyMaterials();
            }
        }

        /// <summary>Operations applied since the start or the last ResetShape.</summary>
        public int Operations
        {
            get { return _operations; }
        }

        /// <summary>Why the last operation failed, or null if it worked.</summary>
        public string LastError
        {
            get { return _lastError; }
        }

        /// <summary>False if the mesh cannot be used (see LastError). Imports the mesh to find out.</summary>
        public bool IsUsable
        {
            get { return EnsureBody(); }
        }

        /// <summary>True when nothing is left of the body.</summary>
        public bool IsEmpty
        {
            get { return _body != null && _body.IsEmpty; }
        }

        /// <summary>Volume in local units (imports the mesh if that has not happened yet).</summary>
        public float Volume
        {
            get
            {
                if (!EnsureBody()) return 0f;
                return (float)_body.Volume;
            }
        }

        /// <summary>Triangles of the mesh the object shows right now.</summary>
        public int TriangleCount
        {
            get
            {
                Mesh mesh = GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null) return 0;
                long indices = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) indices += mesh.GetIndexCount(s);
                return (int)(indices / 3);
            }
        }

        /// <summary>The collider mode in use right now: Auto turns into Exact, Convex or Keep.</summary>
        public ColliderMode EffectiveColliderMode
        {
            get { return ResolveColliderMode(false); }
        }

        // --- Operations -------------------------------------------------------------
        // Every operation returns true if it worked. If it fails, the body stays as it was and LastError says why.
        // toolToWorld places the tool in the world; a CsgShape primitive is unit size with +Y out of the surface.

        /// <summary>Cuts the shape out of this body.</summary>
        public bool Subtract(CsgShape shape, Matrix4x4 toolToWorld)
        {
            return Apply(CsgOperation.Subtract, shape, toolToWorld);
        }

        /// <summary>Cuts a mesh out of this body.</summary>
        public bool Subtract(Mesh mesh, Matrix4x4 toolToWorld)
        {
            return Apply(CsgOperation.Subtract, mesh, toolToWorld);
        }

        /// <summary>Cuts a scene object out of this body, where that object stands.</summary>
        public bool Subtract(MeshFilter toolObject)
        {
            return Apply(CsgOperation.Subtract, toolObject);
        }

        /// <summary>Adds the shape to this body.</summary>
        public bool Union(CsgShape shape, Matrix4x4 toolToWorld)
        {
            return Apply(CsgOperation.Union, shape, toolToWorld);
        }

        /// <summary>Adds a mesh to this body.</summary>
        public bool Union(Mesh mesh, Matrix4x4 toolToWorld)
        {
            return Apply(CsgOperation.Union, mesh, toolToWorld);
        }

        /// <summary>Adds a scene object to this body, where that object stands.</summary>
        public bool Union(MeshFilter toolObject)
        {
            return Apply(CsgOperation.Union, toolObject);
        }

        /// <summary>Keeps only what this body and the shape have in common.</summary>
        public bool Intersect(CsgShape shape, Matrix4x4 toolToWorld)
        {
            return Apply(CsgOperation.Intersect, shape, toolToWorld);
        }

        /// <summary>Keeps only what this body and a mesh have in common.</summary>
        public bool Intersect(Mesh mesh, Matrix4x4 toolToWorld)
        {
            return Apply(CsgOperation.Intersect, mesh, toolToWorld);
        }

        /// <summary>Keeps only what this body and a scene object have in common.</summary>
        public bool Intersect(MeshFilter toolObject)
        {
            return Apply(CsgOperation.Intersect, toolObject);
        }

        /// <summary>Applies any of the three operations with a CSG Shape.</summary>
        public bool Apply(CsgOperation operation, CsgShape shape, Matrix4x4 toolToWorld)
        {
            if (shape == null) return ApplyTool(operation, null, "The CSG Shape is missing.", null, toolToWorld);
            string error;
            ToolEntry tool = shape.GetEntry(out error);
            return ApplyTool(operation, tool, error, shape.material, toolToWorld);
        }

        /// <summary>Applies any of the three operations with a mesh (converted once, then cached).</summary>
        public bool Apply(CsgOperation operation, Mesh mesh, Matrix4x4 toolToWorld)
        {
            if (mesh == null) return ApplyTool(operation, null, "The tool mesh is missing.", null, toolToWorld);
            string error;
            ToolEntry tool = ToolCache.Of(mesh, out error);
            return ApplyTool(operation, tool, error, null, toolToWorld);
        }

        /// <summary>Applies any of the three operations with a scene object, where that object stands.</summary>
        public bool Apply(CsgOperation operation, MeshFilter toolObject)
        {
            if (toolObject == null) return ApplyTool(operation, null, "The tool object is missing.", null, Matrix4x4.identity);
            return Apply(operation, toolObject.sharedMesh, toolObject.transform.localToWorldMatrix);
        }

        bool ApplyTool(CsgOperation operation, ToolEntry tool, string toolError, Material toolMaterial, Matrix4x4 toolToWorld)
        {
            if (!EnsureBody()) return false;
            if (tool == null) return Fail(toolError);
            Matrix4x4 toolToLocal = transform.worldToLocalMatrix * toolToWorld;
            if (!ManifoldUnity.IsUsable(toolToLocal))
                return Fail("The placement is degenerate: a scale of 0, or numbers that are not finite.");

            // Which material the tool's faces get decides which id they carry
            object kind;
            if (toolMaterial != null) kind = toolMaterial;
            else if (operation == CsgOperation.Union) kind = ToolEntry.Outside;
            else kind = ToolEntry.Interior;
            Manifold shape = tool.ForKind(kind);

            Manifold next = null;
            using (ApplyMarker.Auto())
            {
                Manifold placed = null;
                try
                {
                    placed = ManifoldUnity.Transform(shape, toolToLocal);
                    next = _body.Boolean(placed, operation);
                    ManifoldError status = next.Evaluate();   // the actual computation - booleans are lazy
                    if (status != ManifoldError.NoError) throw new InvalidOperationException(Manifold.Describe(status));
                }
                catch (Exception e)
                {
                    if (next != null) next.Dispose();
                    return Fail(operation + " failed, the body is unchanged (" + e.Message + ")");
                }
                finally
                {
                    if (placed != null) placed.Dispose();
                }
            }

            MapId((uint)shape.OriginalId, kind);
            _body.Dispose();
            _body = next;
            _operations++;
            _lastError = null;
            Commit();
            return true;
        }

        /// <summary>
        /// Keeps the positive side of a world-space plane. New cut faces use InteriorMaterial.
        /// </summary>
        public bool TrimByPlane(Plane plane)
        {
            Vector3 normal = plane.normal;
            if (!IsFinite(normal.x) || !IsFinite(normal.y) || !IsFinite(normal.z) ||
                !IsFinite(plane.distance) || normal.sqrMagnitude < 1e-12f)
                return Fail("The cutting plane must have a finite, non-zero normal and a finite distance.");
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            if (!ManifoldUnity.IsUsable(localToWorld))
                return Fail("The body's transform is degenerate: a scale of 0, or numbers that are not finite.");

            // Plane coefficients transform with the transpose, including the translation.
            Vector4 local = localToWorld.transpose * new Vector4(normal.x, normal.y, normal.z, plane.distance);
            Vector3 localNormal = new Vector3(local.x, local.y, local.z);
            float length = localNormal.magnitude;
            if (!IsFinite(length) || length < 1e-12f || !IsFinite(local.w))
                return Fail("The cutting plane cannot be represented in the body's local space.");
            localNormal /= length;
            double offset = -(double)local.w / length;
            return ApplyGeometry("Plane trim", current =>
                current.TrimByPlane(localNormal.x, localNormal.y, localNormal.z, offset), -localNormal);
        }

        /// <summary>Simplifies the current geometry within a tolerance in local mesh units.</summary>
        public bool Simplify(double tolerance = 0)
        {
            if (!IsFinite(tolerance) || tolerance < 0)
                return Fail("Simplification tolerance must be finite and non-negative.");
            return ApplyGeometry("Simplification", current => current.Simplify(tolerance), null);
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        bool ApplyGeometry(string operation, Func<Manifold, Manifold> create, Vector3? capNormal)
        {
            if (!EnsureBody()) return false;
            Manifold next = null;
            MeshData data = null;
            using (ApplyMarker.Auto())
            {
                try
                {
                    next = create(_body);
                    ManifoldError status = next.Evaluate();
                    if (status != ManifoldError.NoError) throw new InvalidOperationException(Manifold.Describe(status));
                    if (normals == NormalMode.Recalculate)
                    {
                        Manifold withNormals = next.CalculateNormals(sharpAngle);
                        next.Dispose();
                        next = withNormals;
                        status = next.Evaluate();
                        if (status != ManifoldError.NoError) throw new InvalidOperationException(Manifold.Describe(status));
                    }
                    if (capNormal.HasValue) data = next.GetMeshData();
                }
                catch (Exception e)
                {
                    if (next != null) next.Dispose();
                    return Fail($"{operation} failed, the body is unchanged ({e.Message})");
                }
            }
            if (data != null)
            {
                for (int run = 0; run < data.NumRun; run++)
                {
                    uint id = data.RunOriginalId[run];
                    if (_slotOfId.ContainsKey(id)) continue;
                    MapId(id, ToolEntry.Interior);
                    _planeNormals[id] = capNormal.Value;
                }
            }
            _body.Dispose();
            _body = next;
            _operations++;
            _lastError = null;
            Commit();
            return true;
        }

        /// <summary>
        /// Creates one independent CsgBody per disconnected part and deactivates this body.
        /// A connected body returns itself. ResetShape removes the spawned parts and restores this body.
        /// New objects contain mesh, renderer, collider and optional Rigidbody components; other scripts are not copied.
        /// Returns null on failure.
        /// </summary>
        public CsgBody[] Decompose()
        {
            if (!EnsureBody()) return null;
            if (_separatedParts.Count > 0)
            {
                Fail("This body has already been separated. ResetShape before separating it again.");
                return null;
            }
            Manifold[] parts = null;
            Mesh[] meshes = null;
            double[] volumes = null;
            List<CsgBody> created = new List<CsgBody>();
            try
            {
                parts = _body.Decompose();
                if (parts.Length <= 1)
                {
                    _lastError = null;
                    return parts.Length == 0 ? new CsgBody[0] : new[] { this };
                }
                meshes = new Mesh[parts.Length];
                volumes = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    MeshData data = parts[i].GetMeshData(null, 0);
                    ApplyPlaneNormals(data);
                    meshes[i] = ManifoldUnity.ToUnityMesh(data, _slotOfId, _slots.Count);
                    meshes[i].name = $"{name} (part {i + 1})";
                    volumes[i] = parts[i].Volume;
                }
                Material[] materials = new Material[_slots.Count];
                for (int i = 0; i < materials.Length; i++) materials[i] = MaterialOf(_slots[i]);
                Rigidbody sourceBody = GetComponent<Rigidbody>();
                double totalVolume = _body.Volume;
                for (int i = 0; i < parts.Length; i++)
                {
                    GameObject piece = new GameObject($"{name} (part {i + 1})");
                    piece.SetActive(false);
                    CsgBody fragment = piece.AddComponent<CsgBody>();
                    created.Add(fragment);
                    fragment._ownedSourceMesh = meshes[i];
                    piece.GetComponent<MeshFilter>().sharedMesh = meshes[i];
                    meshes[i] = null; // ownership transferred to the fragment
                    piece.layer = gameObject.layer;
                    piece.tag = gameObject.tag;
                    piece.transform.SetParent(transform.parent, false);
                    piece.transform.localPosition = transform.localPosition;
                    piece.transform.localRotation = transform.localRotation;
                    piece.transform.localScale = transform.localScale;
                    MeshRenderer renderer = piece.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = materials;
                    if (_renderer != null)
                    {
                        renderer.shadowCastingMode = _renderer.shadowCastingMode;
                        renderer.receiveShadows = _renderer.receiveShadows;
                    }
                    fragment.normals = normals;
                    fragment.sharpAngle = sharpAngle;
                    fragment.interiorMaterial = interiorMaterial;
                    fragment.colliders = colliders;
                    fragment.scaleMassWithVolume = scaleMassWithVolume;
                    fragment.whenEmpty = whenEmpty;
                    if (sourceBody != null)
                    {
                        Rigidbody rigidbody = piece.AddComponent<Rigidbody>();
                        rigidbody.isKinematic = sourceBody.isKinematic;
                        rigidbody.useGravity = sourceBody.useGravity;
                        rigidbody.constraints = sourceBody.constraints;
                        rigidbody.interpolation = sourceBody.interpolation;
                        rigidbody.collisionDetectionMode = sourceBody.collisionDetectionMode;
                        rigidbody.linearDamping = sourceBody.linearDamping;
                        rigidbody.angularDamping = sourceBody.angularDamping;
                        rigidbody.mass = Mathf.Max(1e-7f, sourceBody.mass * (float)(volumes[i] / totalVolume));
                        if (!sourceBody.isKinematic)
                        {
                            Vector3 center = piece.transform.TransformPoint(piece.GetComponent<MeshFilter>().sharedMesh.bounds.center);
                            rigidbody.linearVelocity = sourceBody.GetPointVelocity(center);
                            rigidbody.angularVelocity = sourceBody.angularVelocity;
                        }
                    }
                    ColliderMode mode = ResolveColliderMode(false);
                    if (mode != ColliderMode.Keep || HasSolidCollider())
                    {
                        MeshCollider collider = piece.AddComponent<MeshCollider>();
                        collider.sharedMesh = piece.GetComponent<MeshFilter>().sharedMesh;
                        collider.convex = sourceBody != null && !sourceBody.isKinematic || mode == ColliderMode.Convex;
                    }
                    if (!fragment.EnsureBody()) throw new InvalidOperationException(fragment.LastError);
                    fragment.UpdateMesh();
                    fragment.UpdateCollider();
                }
            }
            catch (Exception e)
            {
                foreach (CsgBody part in created) DestroyPart(part);
                if (meshes != null) foreach (Mesh mesh in meshes) DestroyMesh(mesh);
                Fail($"Separation failed, the body is unchanged ({e.Message})");
                return null;
            }
            finally
            {
                if (parts != null) foreach (Manifold part in parts) part.Dispose();
            }
            _separatedParts.AddRange(created);
            _operations++;
            _lastError = null;
            _deactivated = true;
            gameObject.SetActive(false);
            foreach (CsgBody part in created) part.gameObject.SetActive(true);
            if (Changed != null) Changed(this);
            return created.ToArray();
        }

        static void DestroyPart(CsgBody part)
        {
            if (part == null) return;
            part.gameObject.SetActive(false);
            part.ReleaseGeometry();
            if (Application.isPlaying) Destroy(part.gameObject);
            else DestroyImmediate(part.gameObject);
        }

        /// <summary>Back to the authored object: mesh, materials, colliders and mass.</summary>
        public void ResetShape()
        {
            foreach (CsgBody part in _separatedParts) DestroyPart(part);
            _separatedParts.Clear();
            if (!_initialized) return;   // nothing has changed yet
            if (_original != null)
            {
                if (_body != null) _body.Dispose();
                _body = _original.Copy();
            }
            _operations = 0;
            _filter.sharedMesh = _sourceMesh;
            if (_renderer != null) _renderer.sharedMaterials = _sourceMaterials;
            _materialsApplied = false;
            RestoreColliders();
            RestoreMass();
            if (_deactivated)
            {
                _deactivated = false;
                gameObject.SetActive(true);
            }
            if (Changed != null) Changed(this);
        }

        // --- Import ---------------------------------------------------------------------

        void Init()
        {
            if (_initialized) return;
            _initialized = true;
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _sourceMesh = _filter.sharedMesh;
            if (_renderer != null) _sourceMaterials = _renderer.sharedMaterials;
            else _sourceMaterials = new Material[0];
        }

        // Imports the mesh on first use. False if it cannot be used.
        bool EnsureBody()
        {
            if (_body != null) return true;
            if (_unusable) return false;
            Init();
            try
            {
                uint firstId;
                bool keepNormals = normals == NormalMode.KeepImported;
                _original = ManifoldUnity.FromUnityMesh(_sourceMesh, out firstId, sharpAngle, keepNormals);
                _originalVolume = _original.Volume;
                // Every submesh of the source mesh gets the slot with its own number
                for (int s = 0; s < _sourceMesh.subMeshCount; s++)
                {
                    _slotOfId[firstId + (uint)s] = _slots.Count;
                    _slots.Add(s);
                }
                _body = _original.Copy();
                return true;
            }
            catch (Exception e)
            {
                // An unusable mesh stays unusable - warn once, then stay quiet
                if (_original != null) _original.Dispose();
                _original = null;
                _unusable = true;
                _lastError = "CsgBody '" + name + "' cannot be changed: " + e.Message;
                Debug.LogWarning(_lastError, this);
                return false;
            }
        }

        bool Fail(string message)
        {
            _lastError = "CsgBody '" + name + "': " + message;
            Debug.LogWarning(_lastError, this);
            return false;
        }

        // Gives the faces of a tool their slot: the first submesh for the outside, otherwise the slot of that kind
        void MapId(uint id, object kind)
        {
            if (_slotOfId.ContainsKey(id)) return;
            int slot = 0;
            if (kind != ToolEntry.Outside)
            {
                slot = _slots.IndexOf(kind);
                if (slot < 0)
                {
                    slot = _slots.Count;
                    _slots.Add(kind);
                }
            }
            _slotOfId[id] = slot;
        }

        // --- Applying a new shape ---------------------------------------------------------

        void Commit()
        {
            bool empty = _body.IsEmpty;
            if (empty && whenEmpty == EmptyAction.Destroy)
            {
                if (Changed != null) Changed(this);
                if (Application.isPlaying) Destroy(gameObject);
                else DestroyImmediate(gameObject);
                return;
            }
            UpdateMesh();
            UpdateCollider();
            UpdateMass();
            if (empty && whenEmpty == EmptyAction.Deactivate)
            {
                _deactivated = true;
                gameObject.SetActive(false);
            }
            if (Changed != null) Changed(this);
        }

        void UpdateMesh()
        {
            using (MeshMarker.Auto())
            {
                // normalIdx 0: imported normals are turned along with their part, just like calculated ones
                _body.GetMeshData(_meshData, 0);
                ApplyPlaneNormals(_meshData);
                if (_mesh == null)
                {
                    _mesh = new Mesh();
                    _mesh.name = name + " (CSG)";
                }
                ManifoldUnity.ToUnityMesh(_meshData, _slotOfId, _slots.Count, _mesh);
                _filter.sharedMesh = _mesh;
                ApplyMaterials();
            }
        }

        void ApplyPlaneNormals(MeshData data)
        {
            if (normals != NormalMode.KeepImported || data.NumProp < 6) return;
            for (int run = 0; run < data.NumRun; run++)
            {
                Vector3 normal;
                if (!_planeNormals.TryGetValue(data.RunOriginalId[run], out normal)) continue;
                for (uint index = data.RunIndex[run]; index < data.RunIndex[run + 1]; index++)
                {
                    int vertex = checked((int)data.Tris[index] * data.NumProp + 3);
                    data.VertProps[vertex] = normal.x;
                    data.VertProps[vertex + 1] = normal.y;
                    data.VertProps[vertex + 2] = normal.z;
                }
            }
        }

        // The renderer only gets a new array when a material changed (Unity copies it on assignment)
        void ApplyMaterials()
        {
            if (_renderer == null) return;
            if (_appliedMaterials == null || _appliedMaterials.Length != _slots.Count)
            {
                _appliedMaterials = new Material[_slots.Count];
                _materialsApplied = false;
            }
            bool changed = !_materialsApplied;
            for (int i = 0; i < _slots.Count; i++)
            {
                Material material = MaterialOf(_slots[i]);
                if (_appliedMaterials[i] != material)
                {
                    _appliedMaterials[i] = material;
                    changed = true;
                }
            }
            if (!changed) return;
            _renderer.sharedMaterials = _appliedMaterials;
            _materialsApplied = true;
        }

        Material MaterialOf(object slot)
        {
            Material outside = null;
            if (_sourceMaterials.Length > 0) outside = _sourceMaterials[0];

            if (slot is int)
            {
                int subMesh = (int)slot;
                if (subMesh < _sourceMaterials.Length) return _sourceMaterials[subMesh];
                return outside;
            }
            if (slot == ToolEntry.Interior)
            {
                if (interiorMaterial != null) return interiorMaterial;
                return outside;
            }
            return slot as Material;
        }

        // --- Colliders -------------------------------------------------------------------

        ColliderMode ResolveColliderMode(bool warn)
        {
            Rigidbody body = GetComponentInParent<Rigidbody>();
            bool dynamic = body != null && !body.isKinematic;

            if (colliders == ColliderMode.Auto)
            {
                if (!_collidersTaken && !HasSolidCollider()) return ColliderMode.Keep;
                if (dynamic) return ColliderMode.Convex;
                return ColliderMode.Exact;
            }
            if (colliders == ColliderMode.Exact && dynamic)
            {
                // Unity does not simulate non-convex MeshColliders on dynamic Rigidbodies
                if (warn && !_warnedDynamic)
                {
                    _warnedDynamic = true;
                    Debug.LogWarning("CsgBody '" + name + "': Exact colliders do not work on a non-kinematic Rigidbody, using Convex.", this);
                }
                return ColliderMode.Convex;
            }
            return colliders;
        }

        bool HasSolidCollider()
        {
            foreach (Collider c in GetComponents<Collider>())
            {
                if (c.enabled && !c.isTrigger) return true;
            }
            return false;
        }

        void UpdateCollider()
        {
            using (ColliderMarker.Auto())
            {
                ColliderMode mode = ResolveColliderMode(true);
                if (mode == ColliderMode.Keep) return;
                if (!_collidersTaken) TakeColliders();

                bool convex = mode == ColliderMode.Convex;
                Mesh colliderMesh = _mesh;
                int triangles = _meshData.NumTri;
                if (convex)
                {
                    colliderMesh = HullMesh();
                    triangles = _hullData.NumTri;
                }

                // PhysX cannot cook what is not a solid; the collider waits for the next usable shape
                if (triangles < 4)
                {
                    _meshCollider.enabled = false;
                    return;
                }
                // Every property change cooks the collider again, so: off with the mesh (forces a re-read of the
                // changed mesh), convex only if it changes and only while there is no mesh, then one cook
                _meshCollider.sharedMesh = null;
                if (_meshCollider.convex != convex) _meshCollider.convex = convex;
                _meshCollider.sharedMesh = colliderMesh;
                if (!_meshCollider.enabled) _meshCollider.enabled = true;
            }
        }

        // PhysX would build the hull from every vertex of the mesh (10 ms for 13k triangles); Manifold's hull
        // takes a fraction of that and leaves PhysX only the vertices it needs
        Mesh HullMesh()
        {
            Manifold hull = _body.Hull();
            hull.GetMeshData(_hullData);
            hull.Dispose();
            if (_hullMesh == null)
            {
                _hullMesh = new Mesh();
                _hullMesh.name = name + " (CSG hull)";
            }
            return ManifoldUnity.ToUnityMesh(_hullData, null, 1, _hullMesh);
        }

        // First change: reuse a MeshCollider or add one, disable the other solid colliders - nothing is destroyed
        void TakeColliders()
        {
            _collidersTaken = true;
            foreach (Collider c in GetComponents<Collider>())
            {
                if (c.isTrigger || c == _meshCollider) continue;
                MeshCollider meshCollider = c as MeshCollider;
                if (_meshCollider == null && meshCollider != null)
                {
                    _meshCollider = meshCollider;
                    _colliderSourceMesh = meshCollider.sharedMesh;
                    _colliderSourceConvex = meshCollider.convex;
                    _colliderSourceEnabled = meshCollider.enabled;
                    continue;
                }
                if (!c.enabled) continue;
                c.enabled = false;
                _disabledColliders.Add(c);
            }
            if (_meshCollider == null)
            {
                // A new MeshCollider takes the MeshFilter's mesh and cooks it at once - with no mesh there,
                // it neither wastes that work nor complains about a non-convex mesh on a dynamic Rigidbody
                Mesh shown = _filter.sharedMesh;
                _filter.sharedMesh = null;
                _meshCollider = gameObject.AddComponent<MeshCollider>();
                _filter.sharedMesh = shown;
                _addedCollider = true;
            }
        }

        void RestoreColliders()
        {
            if (!_collidersTaken) return;
            _collidersTaken = false;
            if (_addedCollider)
            {
                // Destroy would only happen at the end of the frame, too late for an operation right after
                // the reset - so the collider we added stays, disabled, for the next change
                if (_meshCollider != null)
                {
                    _meshCollider.enabled = false;
                    _meshCollider.sharedMesh = null;
                }
                else
                {
                    _addedCollider = false;
                }
            }
            else if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                _meshCollider.convex = _colliderSourceConvex;
                _meshCollider.sharedMesh = _colliderSourceMesh;
                _meshCollider.enabled = _colliderSourceEnabled;
                _meshCollider = null;
            }
            foreach (Collider c in _disabledColliders)
            {
                if (c != null) c.enabled = true;
            }
            _disabledColliders.Clear();
        }

        // --- Mass ------------------------------------------------------------------------

        void UpdateMass()
        {
            if (!scaleMassWithVolume || _originalVolume <= 0) return;
            if (_rigidbody == null)
            {
                _rigidbody = GetComponent<Rigidbody>();
                if (_rigidbody == null) return;
                _sourceMass = _rigidbody.mass;
            }
            float share = (float)(_body.Volume / _originalVolume);
            _rigidbody.mass = Mathf.Max(1e-7f, _sourceMass * share);
            if (_rigidbody.automaticCenterOfMass) _rigidbody.ResetCenterOfMass();
            if (_rigidbody.automaticInertiaTensor) _rigidbody.ResetInertiaTensor();
        }

        void RestoreMass()
        {
            if (_rigidbody == null) return;
            _rigidbody.mass = _sourceMass;
            if (_rigidbody.automaticCenterOfMass) _rigidbody.ResetCenterOfMass();
            if (_rigidbody.automaticInertiaTensor) _rigidbody.ResetInertiaTensor();
            _rigidbody = null;
        }

        // --- Lifetime ----------------------------------------------------------------------

        void OnValidate()
        {
            // Interior material changed in the Inspector while playing
            if (_operations > 0) ApplyMaterials();
        }

        void OnDestroy()
        {
            ReleaseGeometry();
        }

        void ReleaseGeometry()
        {
            if (_body != null) _body.Dispose();
            if (_original != null) _original.Dispose();
            _body = null;
            _original = null;
            DestroyMesh(_mesh);
            DestroyMesh(_hullMesh);
            DestroyMesh(_ownedSourceMesh);
            _mesh = null;
            _hullMesh = null;
            _ownedSourceMesh = null;
        }

        static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }
    }
}
