using UnityEngine;

namespace GoalNetXPBD
{
    /// <summary>
    /// Phase-1 XPBD solver. Drives a single cloth (the goal net) under gravity, with
    /// pin constraints and distance constraints. No bending, no aerodynamic, no ball
    /// collision yet — those land in later phases.
    ///
    /// Particle vs mesh-vertex separation:
    ///   The mesh may contain coincident vertices coming from independent sub-meshes
    ///   (e.g. a rope mesh and the net mesh sharing endpoints). <see cref="GoalNetMesh"/>
    ///   welds those into a single particle. The simulator works on the smaller particle
    ///   array; mesh write-back uses <c>vertexToParticle</c> to fan particles back out
    ///   to every mesh vertex they cover.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [DisallowMultipleComponent]
    public class GoalNetSimulator : MonoBehaviour
    {
        [Header("Pin Detection")]
        [Tooltip("RGB <= this counts as 'black' = pinned. Match the value used in the inspector window.")]
        [Range(0f, 0.5f)] public float blackThreshold = 0.1f;

        [Tooltip(
            "Distance (in mesh-local units) within which two mesh vertices are merged into one particle. " +
            "Critical when the FBX contains separate sub-meshes (rope + net) that touch at the seams. " +
            "Set 0 to disable welding.")]
        [Min(0f)] public float weldDistance = 0.001f;

        [Header("Solver")]
        [Tooltip("How many sub-iterations per FixedUpdate. More substeps = more stable under fast motion.")]
        [Range(1, 16)] public int substeps = 4;

        [Tooltip("XPBD constraint iterations per substep.")]
        [Range(1, 40)] public int iterations = 12;

        [Tooltip("Gravity acceleration applied to free particles, in world space.")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip(
            "XPBD compliance for the distance constraint. " +
            "0 = perfectly rigid (no stretch). Increase to make the net softer (1e-6 to 1e-4 typical for the pocket effect).")]
        [Min(0f)] public float distanceCompliance = 0f;

        [Header("Stability")]
        [Tooltip("Velocity damping applied each substep: v *= (1 - damping). 0 = no damping, 1 = stop instantly.")]
        [Range(0f, 1f)] public float damping = 0.02f;

        [Tooltip("Hard clamp on per-particle speed (m/s). Keeps the simulation from exploding on bad data.")]
        [Min(0.1f)] public float maxVelocity = 50f;

        [Header("Debug")]
        [Tooltip("Draw pinned (red) / free (green) particles as Gizmos when the simulator is selected.")]
        public bool drawDebugGizmos = false;

        [Tooltip("Pause the solver — particles freeze in place. Toggle to inspect a specific frame.")]
        public bool pauseSimulation = false;

        // ---- Runtime state ----
        private MeshFilter _meshFilter;
        private Mesh _runtimeMesh;          // owned copy, safe to mutate
        private GoalNetMesh _data;          // particle/edge data

        private Vector3[] _positions;       // world-space, end-of-step (length == particleCount)
        private Vector3[] _predicted;       // world-space, mutated by constraint solve
        private Vector3[] _velocities;      // world-space
        private Vector3[] _meshLocalScratch; // length == meshVertexCount, for write-back
        private float[] _lambda;

        private bool _initialized;

        private void Start()
        {
            _meshFilter = GetComponent<MeshFilter>();
            if (_meshFilter == null || _meshFilter.sharedMesh == null)
            {
                Debug.LogError("[GoalNetSimulator] No MeshFilter / mesh found on this GameObject.", this);
                enabled = false;
                return;
            }

            // Make a runtime copy so we don't mutate the imported FBX asset.
            _runtimeMesh = Instantiate(_meshFilter.sharedMesh);
            _runtimeMesh.name = _meshFilter.sharedMesh.name + " (Runtime)";
            _runtimeMesh.MarkDynamic();
            _meshFilter.mesh = _runtimeMesh;

            _data = new GoalNetMesh();
            if (!_data.Initialize(_runtimeMesh, blackThreshold, weldDistance))
            {
                Debug.LogError("[GoalNetSimulator] Mesh initialization failed. Solver disabled.", this);
                enabled = false;
                return;
            }

            int n = _data.particleCount;
            _positions = new Vector3[n];
            _predicted = new Vector3[n];
            _velocities = new Vector3[n];
            _meshLocalScratch = new Vector3[_data.meshVertexCount];
            _lambda = new float[_data.edges.Length];

            // Particles start at rest pose, in world space.
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = transform.TransformPoint(_data.restPositionsLocal[i]);
                _positions[i] = wp;
                _predicted[i] = wp;
                _velocities[i] = Vector3.zero;
            }

            _initialized = true;
        }

        private void FixedUpdate()
        {
            if (!_initialized || pauseSimulation) return;

            float fullDt = Time.fixedDeltaTime;
            if (fullDt <= 0f) return;

            int s = Mathf.Max(1, substeps);
            float dt = fullDt / s;

            for (int step = 0; step < s; step++)
            {
                Substep(dt);
            }

            WriteBackToMesh();
        }

        private void Substep(float dt)
        {
            int n = _data.particleCount;
            float dt2 = dt * dt;
            float alphaTilde = distanceCompliance / dt2;

            // 1. Predict.
            for (int i = 0; i < n; i++)
            {
                if (_data.isPinned[i])
                {
                    Vector3 worldRest = transform.TransformPoint(_data.restPositionsLocal[i]);
                    _predicted[i] = worldRest;
                    _positions[i] = worldRest;
                    _velocities[i] = Vector3.zero;
                }
                else
                {
                    _velocities[i] += gravity * dt;
                    _predicted[i] = _positions[i] + _velocities[i] * dt;
                }
            }

            // 2. Reset XPBD multipliers and solve constraints.
            var edges = _data.edges;
            int eCount = edges.Length;
            for (int i = 0; i < eCount; i++) _lambda[i] = 0f;

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int e = 0; e < eCount; e++)
                {
                    SolveDistance(edges[e], e, alphaTilde);
                }
            }

            // 3. Recover velocities, commit predicted -> positions.
            float invDt = 1f / dt;
            for (int i = 0; i < n; i++)
            {
                if (_data.isPinned[i]) continue;
                _velocities[i] = (_predicted[i] - _positions[i]) * invDt;
                _positions[i] = _predicted[i];
            }

            // 4. Damping + speed clamp.
            float keep = 1f - damping;
            for (int i = 0; i < n; i++)
            {
                if (_data.isPinned[i]) continue;
                _velocities[i] *= keep;
                float spd = _velocities[i].magnitude;
                if (spd > maxVelocity) _velocities[i] *= (maxVelocity / spd);
            }
        }

        private void SolveDistance(GoalNetMesh.Edge edge, int edgeIdx, float alphaTilde)
        {
            int a = edge.a;
            int b = edge.b;
            float wa = _data.invMass[a];
            float wb = _data.invMass[b];
            float wSum = wa + wb;
            if (wSum <= 0f) return;

            Vector3 pa = _predicted[a];
            Vector3 pb = _predicted[b];
            Vector3 dab = pa - pb;
            float dist = dab.magnitude;
            if (dist < 1e-8f) return;

            Vector3 n = dab / dist;
            float C = dist - edge.restLength;

            float lambda = _lambda[edgeIdx];
            float deltaLambda = (-C - alphaTilde * lambda) / (wSum + alphaTilde);
            _lambda[edgeIdx] = lambda + deltaLambda;

            Vector3 corr = deltaLambda * n;
            if (wa > 0f) _predicted[a] = pa + wa * corr;
            if (wb > 0f) _predicted[b] = pb - wb * corr;
        }

        private void WriteBackToMesh()
        {
            if (_runtimeMesh == null) return;
            int meshN = _data.meshVertexCount;
            for (int i = 0; i < meshN; i++)
            {
                int particle = _data.vertexToParticle[i];
                _meshLocalScratch[i] = transform.InverseTransformPoint(_positions[particle]);
            }
            _runtimeMesh.vertices = _meshLocalScratch;
            _runtimeMesh.RecalculateNormals();
            _runtimeMesh.RecalculateBounds();
        }

        /// <summary>
        /// Reset the cloth back to its rest pose. Useful after experimenting with parameters.
        /// </summary>
        [ContextMenu("Reset To Rest Pose")]
        public void ResetToRest()
        {
            if (!_initialized) return;
            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = transform.TransformPoint(_data.restPositionsLocal[i]);
                _positions[i] = wp;
                _predicted[i] = wp;
                _velocities[i] = Vector3.zero;
            }
            WriteBackToMesh();
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawDebugGizmos || !_initialized || _positions == null) return;
            for (int i = 0; i < _positions.Length; i++)
            {
                Gizmos.color = _data.isPinned[i] ? Color.red : Color.green;
                Gizmos.DrawSphere(_positions[i], 0.02f);
            }
        }
#endif
    }
}
