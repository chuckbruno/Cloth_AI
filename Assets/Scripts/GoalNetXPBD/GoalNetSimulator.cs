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
        [Range(1, 16)] public int substeps = 2;

        [Tooltip("XPBD constraint iterations per substep.")]
        [Range(1, 40)] public int iterations = 6;

        [Tooltip("Gravity acceleration applied to free particles, in world space.")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip(
            "XPBD compliance for the distance constraint. " +
            "0 = perfectly rigid (no stretch). Increase to make the net softer (1e-6 to 1e-4 typical for the pocket effect).")]
        [Min(0f)] public float distanceCompliance = 1e-6f;

        [Header("Bending")]
        [Tooltip("Enable adjacent-triangle bending constraints. Helps the net keep volume and resist folding flat.")]
        public bool enableBending = true;

        [Tooltip(
            "XPBD compliance for bending constraints. Lower = more resistant to folding. " +
            "Try 1e-4 to 1e-3 for a soft soccer net.")]
        [Min(0f)] public float bendingCompliance = 0.01f;

        [Header("Ball Collision (Phase 3)")]
        [Tooltip("Enable one-way sphere projection: active ball pushes net particles out of its SphereCollider. No reaction force yet.")]
        public bool enableBallCollision = true;

        [Tooltip("Optional explicit ball. If empty, the simulator uses ballPool.CurrentActiveBall.")]
        public Rigidbody collisionBall;

        [Tooltip("Optional pool used to find the latest active ball automatically.")]
        public BallPool ballPool;

        [Tooltip("Extra distance outside the ball sphere, in meters, to reduce visual intersection.")]
        [Min(0f)] public float collisionSkin = 0.01f;

        [Tooltip("Extra solver-only radius added to the ball collision sphere. Use this to keep a visually realistic ball while compensating for vertex-only net collision.")]
        [Min(0f)] public float collisionRadiusPadding = 0.11f;

        [Tooltip("Only particles within ball radius + this margin are checked. Larger values can catch faster motion but cost a little more.")]
        [Min(0f)] public float collisionSearchMargin = 0.05f;

        [Header("Ball Reaction (Phase 4)")]
        [Tooltip("Apply the opposite of net particle collision corrections back to the active Rigidbody.")]
        public bool enableBallReaction = true;

        [Tooltip("Effective mass coefficient used to convert net particle position corrections into ball impulse. This is a tuning value, not the real mass of one rendered net vertex.")]
        [Min(0.0001f)] public float collisionParticleMass = 0.43f;

        [Tooltip("Scales the final reaction impulse. Lower values give softer catching; higher values push the ball back harder.")]
        [Min(0f)] public float reactionImpulseScale = 0.5f;

        [Tooltip("Maximum total impulse applied to the ball per FixedUpdate. A 1 kg ball at 25 m/s needs about 25 N*s to stop.")]
        [Min(0f)] public float maxReactionImpulse = 6.5f;

        [Tooltip("How much collision correction is converted into velocity-opposing catch impulse. Helps avoid symmetric particle corrections cancelling out.")]
        [Min(0f)] public float velocityOpposingImpulseScale = 1f;

        [Header("Stability")]
        [Tooltip("Velocity damping applied each substep: v *= (1 - damping). 0 = no damping, 1 = stop instantly.")]
        [Range(0f, 1f)] public float damping = 0.02f;

        [Tooltip("Hard clamp on per-particle speed (m/s). Keeps the simulation from exploding on bad data.")]
        [Min(0.1f)] public float maxVelocity = 50f;

        [Header("Mesh Update Performance")]
        [Tooltip("Recalculate mesh normals after vertex write-back. Disable for much better performance on dense rope/net meshes.")]
        public bool recalculateNormals = false;

        [Tooltip("Recalculate mesh bounds after vertex write-back. Usually safe to keep off if the net stays inside its imported bounds.")]
        public bool recalculateBounds = false;

        [Tooltip("Run normal/bounds recalculation every N mesh updates when enabled. 1 = every FixedUpdate.")]
        [Min(1)] public int geometryRecalculateInterval = 5;

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
        private Vector3[] _pinnedRestWorld; // world-space rest pose for pinned particles
        private Vector3[] _meshLocalScratch; // length == meshVertexCount, for write-back
        private float[] _lambda;
        private float[] _bendingLambda;
        private int _meshWriteCount;
        private SphereCollider _collisionSphere;
        private Rigidbody _collisionBody;
        private Vector3 _pendingBallImpulse;
        private float _pendingVelocityOpposingImpulse;
        private int _lastCollisionCount;

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

            if (ballPool == null)
            {
                ballPool = FindObjectOfType<BallPool>();
            }

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
            _pinnedRestWorld = new Vector3[n];
            _meshLocalScratch = new Vector3[_data.meshVertexCount];
            _lambda = new float[_data.edges.Length];
            _bendingLambda = new float[_data.bendingConstraints.Length];

            // Particles start at rest pose, in world space.
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = transform.TransformPoint(_data.restPositionsLocal[i]);
                _positions[i] = wp;
                _predicted[i] = wp;
                _velocities[i] = Vector3.zero;
                _pinnedRestWorld[i] = wp;
            }

            transform.hasChanged = false;
            _initialized = true;
        }

        private void FixedUpdate()
        {
            if (!_initialized || pauseSimulation) return;

            float fullDt = Time.fixedDeltaTime;
            if (fullDt <= 0f) return;

            if (transform.hasChanged)
            {
                RefreshPinnedRestWorld();
                transform.hasChanged = false;
            }

            RefreshCollisionTarget();
            _pendingBallImpulse = Vector3.zero;
            _pendingVelocityOpposingImpulse = 0f;

            int s = Mathf.Max(1, substeps);
            float dt = fullDt / s;

            for (int step = 0; step < s; step++)
            {
                Substep(dt);
            }

            ApplyBallReactionImpulse();
            WriteBackToMesh();
        }

        private void RefreshPinnedRestWorld()
        {
            if (_data == null || _pinnedRestWorld == null) return;

            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_data.isPinned[i])
                {
                    _pinnedRestWorld[i] = transform.TransformPoint(_data.restPositionsLocal[i]);
                }
            }
        }

        private void Substep(float dt)
        {
            int n = _data.particleCount;
            float dt2 = dt * dt;
            float distanceAlphaTilde = distanceCompliance / dt2;
            float bendingAlphaTilde = bendingCompliance / dt2;

            // 1. Predict.
            for (int i = 0; i < n; i++)
            {
                if (_data.isPinned[i])
                {
                    _predicted[i] = _pinnedRestWorld[i];
                    _positions[i] = _pinnedRestWorld[i];
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

            var bending = _data.bendingConstraints;
            int bCount = enableBending ? bending.Length : 0;
            for (int i = 0; i < bCount; i++) _bendingLambda[i] = 0f;

            _lastCollisionCount = 0;

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int e = 0; e < eCount; e++)
                {
                    SolveDistance(edges[e], e, distanceAlphaTilde);
                }

                for (int b = 0; b < bCount; b++)
                {
                    SolveBending(bending[b], b, bendingAlphaTilde);
                }

                SolveBallCollision(dt);
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
            SolveDistanceLike(a, b, edge.restLength, _lambda, edgeIdx, alphaTilde);
        }

        private void SolveBending(GoalNetMesh.BendingConstraint constraint, int constraintIdx, float alphaTilde)
        {
            int a = constraint.a;
            int b = constraint.b;
            SolveDistanceLike(a, b, constraint.restLength, _bendingLambda, constraintIdx, alphaTilde);
        }

        private void SolveDistanceLike(int a, int b, float restLength, float[] lambdas, int lambdaIdx, float alphaTilde)
        {
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
            float C = dist - restLength;

            float lambda = lambdas[lambdaIdx];
            float deltaLambda = (-C - alphaTilde * lambda) / (wSum + alphaTilde);
            lambdas[lambdaIdx] = lambda + deltaLambda;

            Vector3 corr = deltaLambda * n;
            if (wa > 0f) _predicted[a] = pa + wa * corr;
            if (wb > 0f) _predicted[b] = pb - wb * corr;
        }

        private void RefreshCollisionTarget()
        {
            if (!enableBallCollision)
            {
                _collisionSphere = null;
                _collisionBody = null;
                return;
            }

            Rigidbody activeBall = collisionBall;
            if (activeBall == null && ballPool != null)
            {
                GameObject active = ballPool.CurrentActiveBall;
                if (active != null) activeBall = active.GetComponent<Rigidbody>();
            }

            if (activeBall == null || !activeBall.gameObject.activeInHierarchy)
            {
                _collisionSphere = null;
                _collisionBody = null;
                return;
            }

            _collisionBody = activeBall;
            if (_collisionSphere == null || _collisionSphere.gameObject != activeBall.gameObject)
            {
                _collisionSphere = activeBall.GetComponent<SphereCollider>();
                if (_collisionSphere == null)
                {
                    Debug.LogWarning("[GoalNetSimulator] Active collision ball has no SphereCollider. Ball-net collision disabled for this ball.", activeBall);
                }
            }
        }

        private void SolveBallCollision(float dt)
        {
            if (!enableBallCollision || _collisionSphere == null) return;

            Transform sphereTransform = _collisionSphere.transform;
            Vector3 center = sphereTransform.TransformPoint(_collisionSphere.center);
            Vector3 lossyScale = sphereTransform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z));
            float radius = _collisionSphere.radius * maxScale + collisionSkin + collisionRadiusPadding;
            float searchRadius = radius + collisionSearchMargin;
            float searchRadiusSqr = searchRadius * searchRadius;
            float radiusSqr = radius * radius;

            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_data.invMass[i] <= 0f) continue;

                Vector3 offset = _predicted[i] - center;
                float d2 = offset.sqrMagnitude;
                if (d2 > searchRadiusSqr || d2 >= radiusSqr) continue;

                if (d2 > 1e-10f)
                {
                    Vector3 oldPosition = _predicted[i];
                    _predicted[i] = center + offset * (radius / Mathf.Sqrt(d2));
                    AccumulateBallReaction(_predicted[i] - oldPosition, dt);
                }
                else
                {
                    Vector3 oldPosition = _predicted[i];
                    _predicted[i] = center + Vector3.up * radius;
                    AccumulateBallReaction(_predicted[i] - oldPosition, dt);
                }

                _lastCollisionCount++;
            }
        }

        private void AccumulateBallReaction(Vector3 particleCorrection, float dt)
        {
            if (!enableBallReaction || _collisionBody == null || dt <= 0f) return;

            Vector3 particleImpulse = particleCorrection * (collisionParticleMass / dt);
            _pendingBallImpulse -= particleImpulse;
            _pendingVelocityOpposingImpulse += particleImpulse.magnitude;
        }

        private void ApplyBallReactionImpulse()
        {
            if (!enableBallReaction || _collisionBody == null || _collisionBody.isKinematic) return;

            Vector3 impulse = _pendingBallImpulse * reactionImpulseScale;
            Vector3 velocity = _collisionBody.velocity;
            if (_pendingVelocityOpposingImpulse > 0f && velocity.sqrMagnitude > 1e-8f)
            {
                impulse += -velocity.normalized * (_pendingVelocityOpposingImpulse * reactionImpulseScale * velocityOpposingImpulseScale);
            }

            float maxImpulse = maxReactionImpulse;
            if (maxImpulse > 0f)
            {
                float magnitude = impulse.magnitude;
                if (magnitude > maxImpulse)
                {
                    impulse *= maxImpulse / magnitude;
                }
            }

            if (impulse.sqrMagnitude > 1e-10f)
            {
                _collisionBody.AddForce(impulse, ForceMode.Impulse);
            }
        }

        private void WriteBackToMesh()
        {
            if (_runtimeMesh == null) return;
            int meshN = _data.meshVertexCount;
            Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

            for (int i = 0; i < meshN; i++)
            {
                int particle = _data.vertexToParticle[i];
                _meshLocalScratch[i] = worldToLocal.MultiplyPoint3x4(_positions[particle]);
            }

            _runtimeMesh.vertices = _meshLocalScratch;

            _meshWriteCount++;
            int interval = Mathf.Max(1, geometryRecalculateInterval);
            bool recalculateThisUpdate = (_meshWriteCount % interval) == 0;

            if (recalculateNormals && recalculateThisUpdate)
            {
                _runtimeMesh.RecalculateNormals();
            }

            if (recalculateBounds && recalculateThisUpdate)
            {
                _runtimeMesh.RecalculateBounds();
            }
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
                _pinnedRestWorld[i] = wp;
            }
            transform.hasChanged = false;
            WriteBackToMesh();
        }

        [ContextMenu("Apply Realtime Preview Settings")]
        public void ApplyRealtimePreviewSettings()
        {
            substeps = 2;
            iterations = 6;
            enableBending = true;
            distanceCompliance = 1e-6f;
            bendingCompliance = 0.01f;
            enableBallCollision = true;
            collisionSkin = 0.01f;
            collisionRadiusPadding = 0.11f;
            collisionSearchMargin = 0.05f;
            enableBallReaction = true;
            collisionParticleMass = 0.43f;
            reactionImpulseScale = 0.5f;
            maxReactionImpulse = 6.5f;
            velocityOpposingImpulseScale = 1f;
            recalculateNormals = false;
            recalculateBounds = false;
            geometryRecalculateInterval = 8;
        }

        [ContextMenu("Apply High Quality Settings")]
        public void ApplyHighQualitySettings()
        {
            substeps = 4;
            iterations = 12;
            enableBending = true;
            bendingCompliance = 0.0001f;
            enableBallCollision = true;
            collisionSkin = 0.01f;
            collisionRadiusPadding = 0.06f;
            collisionSearchMargin = 0.08f;
            enableBallReaction = true;
            collisionParticleMass = 0.5f;
            reactionImpulseScale = 0.5f;
            maxReactionImpulse = 25f;
            velocityOpposingImpulseScale = 1.25f;
            recalculateNormals = true;
            recalculateBounds = false;
            geometryRecalculateInterval = 5;
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

            if (_collisionSphere != null)
            {
                Gizmos.color = _lastCollisionCount > 0 ? Color.yellow : Color.cyan;
                Transform sphereTransform = _collisionSphere.transform;
                Vector3 center = sphereTransform.TransformPoint(_collisionSphere.center);
                Vector3 lossyScale = sphereTransform.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z));
                float radius = _collisionSphere.radius * maxScale + collisionSkin + collisionRadiusPadding;
                Gizmos.DrawWireSphere(center, radius);
            }
        }
#endif
    }
}
