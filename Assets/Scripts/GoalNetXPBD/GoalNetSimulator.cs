using UnityEngine;

namespace GoalNetXPBD
{
    /// <summary>
    /// XPBD solver for the goal net. Drives welded mesh particles with pin, distance,
    /// bending, ball collision/reaction, impact pocket shaping, and simple ground contact
    /// constraints.
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
        [Tooltip("Enable sphere projection: active ball pushes net particles out of its SphereCollider.")]
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

        [Header("Swept Ball Collision")]
        [Tooltip("Use the ball's previous-to-current path as a swept sphere. This reduces high-speed tunneling through sparse net particles.")]
        public bool enableSweptBallCollision = true;

        [Tooltip("Check structural net edges against the swept sphere, not only particles. This catches balls passing between particles on a rope segment.")]
        public bool enableSweptEdgeCollision = true;

        [Tooltip("Extra radius used only for swept collision. Increase slightly if 45-50 m/s shots still leak through.")]
        [Min(0f)] public float sweptCollisionSkin = 0.02f;

        [Tooltip("Maximum correction applied by one swept contact per solver pass. Keeps high-speed hits from exploding the net.")]
        [Min(0f)] public float sweptCollisionMaxCorrection = 0.12f;

        [Tooltip("How much swept collision correction contributes to ball reaction impulse.")]
        [Range(0f, 1f)] public float sweptCollisionReactionScale = 0.35f;

        [Tooltip("How many early solver iterations run swept collision per substep. 1-2 is usually enough and avoids heavy contact-frame slowdowns.")]
        [Range(1, 8)] public int sweptCollisionPassesPerSubstep = 2;

        [Tooltip("Maximum swept edge contacts solved per solver pass. Prevents dense edge contact bursts from dropping FPS.")]
        [Range(1, 128)] public int maxSweptEdgeContactsPerPass = 24;

        [Tooltip("Allow swept edge contacts to trigger neighbor spreading. Expensive; usually keep disabled because particle contacts already spread the pocket.")]
        public bool sweptEdgeInfluenceSpread = false;

        [Header("Collision Influence Spread (Phase 4.5)")]
        [Tooltip("Spread a fraction of each direct ball-collision correction to neighboring particles so the net forms a wider pocket.")]
        public bool enableCollisionInfluenceSpread = true;

        [Tooltip("Fraction of the direct collision correction applied to first-ring neighbors.")]
        [Range(0f, 1f)] public float collisionSpreadStrength = 0.35f;

        [Tooltip("How many structural-edge rings receive spread corrections. 1-2 is usually enough; higher costs more and can over-soften the net.")]
        [Range(0, 3)] public int collisionSpreadRings = 2;

        [Tooltip("Per-ring falloff after the first neighbor ring.")]
        [Range(0f, 1f)] public float collisionSpreadFalloff = 0.5f;

        [Tooltip("How much spread correction contributes to ball reaction impulse. Keep lower than 1 so pocket widening does not make the ball response too stiff.")]
        [Range(0f, 1f)] public float collisionSpreadReactionScale = 0.2f;

        [Header("Ball Impact Drive (Phase 4.7)")]
        [Tooltip("Transfer part of the ball's motion to contacted net particles so impacts push the net deeper instead of only sliding particles around the sphere.")]
        public bool enableBallImpactDrive = true;

        [Tooltip("Fraction of ball displacement transferred to directly contacted particles each solver pass.")]
        [Range(0f, 1f)] public float ballImpactDriveStrength = 0.45f;

        [Tooltip("Maximum extra particle displacement from impact drive per solver pass, in meters.")]
        [Min(0f)] public float ballImpactDriveMaxStep = 0.035f;

        [Tooltip("How many structural-edge rings receive impact-drive displacement.")]
        [Range(0, 3)] public int ballImpactDriveRings = 2;

        [Tooltip("Per-ring falloff for impact-drive displacement.")]
        [Range(0f, 1f)] public float ballImpactDriveFalloff = 0.55f;

        [Tooltip("How much impact-drive displacement contributes to ball reaction impulse. Keep low because soft-catch damping already handles most deceleration.")]
        [Range(0f, 1f)] public float ballImpactDriveReactionScale = 0.05f;

        [Header("Pocket Pressure Field (Phase 4.8)")]
        [Tooltip("When the ball is in contact, push nearby net particles in the ball velocity direction to create a deeper pocket.")]
        public bool enablePocketPressureField = true;

        [Tooltip("Extra radius around the solver ball that receives pocket pressure, in meters.")]
        [Min(0f)] public float pocketPressureRadius = 0.55f;

        [Tooltip("Fraction of ball displacement transferred to nearby particles by the pressure field.")]
        [Range(0f, 2f)] public float pocketPressureStrength = 0.9f;

        [Tooltip("Maximum extra particle displacement from pocket pressure per solver pass, in meters.")]
        [Min(0f)] public float pocketPressureMaxStep = 0.08f;

        [Tooltip("Falloff exponent from the ball surface to the outer pressure radius. Higher values focus pressure nearer the ball.")]
        [Range(0.25f, 4f)] public float pocketPressureFalloffPower = 1f;

        [Tooltip("How much pocket-pressure displacement contributes to ball reaction impulse.")]
        [Range(0f, 1f)] public float pocketPressureReactionScale = 0.04f;

        [Header("Ball Reaction (Phase 4)")]
        [Tooltip("Apply the opposite of net particle collision corrections back to the active Rigidbody.")]
        public bool enableBallReaction = true;

        [Tooltip("Effective mass coefficient used to convert net particle position corrections into ball impulse. This is a tuning value, not the real mass of one rendered net vertex.")]
        [Min(0.0001f)] public float collisionParticleMass = 0.43f;

        [Tooltip("Scales the final reaction impulse. Lower values give softer catching; higher values push the ball back harder.")]
        [Min(0f)] public float reactionImpulseScale = 0.5f;

        [Tooltip("Maximum total impulse applied to the ball per FixedUpdate. A 1 kg ball at 25 m/s needs about 25 N*s to stop.")]
        [Min(0f)] public float maxReactionImpulse = 10f;

        [Tooltip("How much collision correction is converted into velocity-opposing catch impulse. Helps avoid symmetric particle corrections cancelling out.")]
        [Min(0f)] public float velocityOpposingImpulseScale = 1f;

        [Header("Soft Catch Reaction (Phase 4.6)")]
        [Tooltip("Use a damped catch model for ball reaction. This favors slowing the ball and limits immediate rebound speed.")]
        public bool enableSoftCatchReaction = true;

        [Tooltip("How much of the position-correction reaction remains as elastic pushback. Lower values let the ball press deeper into the net.")]
        [Range(0f, 1f)] public float elasticReactionScale = 0.813f;

        [Tooltip("Fraction of current ball velocity that contact damping may remove per FixedUpdate. Higher values catch harder; lower values allow deeper travel.")]
        [Range(0f, 1f)] public float catchVelocityDamping = 0.573f;

        [Tooltip("Maximum ball speed allowed in the reaction impulse direction immediately after contact. Prevents the net from kicking the ball away too quickly.")]
        [Min(0f)] public float maxContactReboundSpeed = 1.5f;

        [Header("Ground Contact")]
        [Tooltip("Prevent free net particles from moving below a horizontal ground plane.")]
        public bool enableGroundCollision = true;

        [Tooltip("Optional transform whose world Y is used as the ground height. Leave empty to use groundHeight.")]
        public Transform groundTransform;

        [Tooltip("World-space Y height of the ground plane when groundTransform is empty.")]
        public float groundHeight = 0f;

        [Tooltip("Small offset above the ground plane to reduce visual z-fighting and re-penetration.")]
        [Min(0f)] public float groundSkin = 0.005f;

        [Tooltip("Horizontal velocity damping for particles touching the ground. Higher values make the bottom of the net drag and pile up more.")]
        [Range(0f, 1f)] public float groundFriction = 0.35f;

        [Tooltip("Vertical velocity kept after hitting the ground. 0 = no bounce, 1 = perfectly elastic bounce.")]
        [Range(0f, 1f)] public float groundBounce = 0f;

        [Header("Bottom Soft Tether")]
        [Tooltip("Automatically soft-tether the lowest free net particles so the bottom drags on the floor instead of being lifted as one loose ring.")]
        public bool enableBottomSoftTether = true;

        [Tooltip("Free particles within this world-space height above the lowest free rest particle are treated as the bottom/tail region.")]
        [Min(0f)] public float bottomDetectHeight = 0.18f;

        [Tooltip("XPBD-like compliance for bottom particles returning toward their rest area. Lower = stronger tether, higher = looser floor pile.")]
        [Min(0f)] public float bottomTetherCompliance = 0.002f;

        [Tooltip("Maximum distance bottom particles may drift from their rest area before an extra cap correction is applied. 0 disables the cap.")]
        [Min(0f)] public float bottomTetherMaxDistance = 0.75f;

        [Tooltip("Extra maximum lift above the bottom particle's rest height before a soft downward cap is applied. 0 disables the lift cap.")]
        [Min(0f)] public float bottomMaxLiftHeight = 0.45f;

        [Tooltip("Ground friction used for bottom particles while they touch the floor. Higher values make the bottom drag and gather more.")]
        [Range(0f, 1f)] public float bottomGroundFriction = 0.75f;

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
        private Vector3 _previousBallCenter;
        private Vector3 _currentBallCenter;
        private Vector3 _sweepStartCenter;
        private Vector3 _sweepEndCenter;
        private bool _hasPreviousBallCenter;
        private int[] _spreadVisit;
        private int[] _spreadFrontier;
        private int[] _spreadNextFrontier;
        private int _spreadVisitToken;
        private bool[] _groundContacts;
        private bool[] _bottomTetherParticles;
        private Vector3[] _bottomRestWorld;
        private int _bottomTetherCount;

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
            _spreadVisit = new int[n];
            _spreadFrontier = new int[n];
            _spreadNextFrontier = new int[n];
            _groundContacts = new bool[n];
            _bottomTetherParticles = new bool[n];
            _bottomRestWorld = new Vector3[n];

            // Particles start at rest pose, in world space.
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = transform.TransformPoint(_data.restPositionsLocal[i]);
                _positions[i] = wp;
                _predicted[i] = wp;
                _velocities[i] = Vector3.zero;
                _pinnedRestWorld[i] = wp;
            }

            InitializeBottomTethers();
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
            PrepareBallSweep();

            int s = Mathf.Max(1, substeps);
            float dt = fullDt / s;

            for (int step = 0; step < s; step++)
            {
                SetSubstepBallSweep(step, s);
                Substep(dt);
            }

            ApplyBallReactionImpulse();
            StoreBallSweepEnd();
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

            RefreshBottomTetherRestWorld();
        }

        private void InitializeBottomTethers()
        {
            if (_data == null || _bottomTetherParticles == null) return;

            System.Array.Clear(_bottomTetherParticles, 0, _bottomTetherParticles.Length);
            _bottomTetherCount = 0;

            float lowest = float.PositiveInfinity;
            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_data.invMass[i] <= 0f) continue;

                float y = transform.TransformPoint(_data.restPositionsLocal[i]).y;
                if (y < lowest) lowest = y;
            }

            if (float.IsPositiveInfinity(lowest)) return;

            float limit = lowest + bottomDetectHeight;
            for (int i = 0; i < n; i++)
            {
                Vector3 rest = transform.TransformPoint(_data.restPositionsLocal[i]);
                _bottomRestWorld[i] = rest;

                if (_data.invMass[i] <= 0f || rest.y > limit) continue;

                _bottomTetherParticles[i] = true;
                _bottomTetherCount++;
            }

            Debug.Log($"[GoalNetSimulator] Bottom soft tether particles: {_bottomTetherCount} (detectHeight={bottomDetectHeight:0.###}m).", this);
        }

        private void RefreshBottomTetherRestWorld()
        {
            if (_data == null || _bottomRestWorld == null) return;

            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_bottomTetherParticles != null && _bottomTetherParticles[i])
                {
                    _bottomRestWorld[i] = transform.TransformPoint(_data.restPositionsLocal[i]);
                }
            }
        }

        private void Substep(float dt)
        {
            int n = _data.particleCount;
            float dt2 = dt * dt;
            float distanceAlphaTilde = distanceCompliance / dt2;
            float bendingAlphaTilde = bendingCompliance / dt2;
            float bottomTetherAlphaTilde = bottomTetherCompliance / dt2;

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
            System.Array.Clear(_groundContacts, 0, _groundContacts.Length);

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

                SolveBallCollision(dt, iter);
                SolveBottomSoftTethers(bottomTetherAlphaTilde);
                SolveGroundCollision();
            }

            // 3. Recover velocities, commit predicted -> positions.
            float invDt = 1f / dt;
            for (int i = 0; i < n; i++)
            {
                if (_data.isPinned[i]) continue;
                _velocities[i] = (_predicted[i] - _positions[i]) * invDt;
                ApplyGroundVelocityResponse(i);
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
                _hasPreviousBallCenter = false;
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
                _hasPreviousBallCenter = false;
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

        private void PrepareBallSweep()
        {
            if (_collisionSphere == null)
            {
                _hasPreviousBallCenter = false;
                return;
            }

            _currentBallCenter = GetCollisionSphereCenter();
            if (!_hasPreviousBallCenter)
            {
                _previousBallCenter = _currentBallCenter;
                _hasPreviousBallCenter = true;
            }

            _sweepStartCenter = _previousBallCenter;
            _sweepEndCenter = _currentBallCenter;
        }

        private void SetSubstepBallSweep(int step, int substepCount)
        {
            if (!_hasPreviousBallCenter || substepCount <= 0)
            {
                _sweepStartCenter = _currentBallCenter;
                _sweepEndCenter = _currentBallCenter;
                return;
            }

            float a = step / (float)substepCount;
            float b = (step + 1) / (float)substepCount;
            _sweepStartCenter = Vector3.Lerp(_previousBallCenter, _currentBallCenter, a);
            _sweepEndCenter = Vector3.Lerp(_previousBallCenter, _currentBallCenter, b);
        }

        private void StoreBallSweepEnd()
        {
            if (_collisionSphere == null)
            {
                _hasPreviousBallCenter = false;
                return;
            }

            _previousBallCenter = _currentBallCenter;
            _hasPreviousBallCenter = true;
        }

        private Vector3 GetCollisionSphereCenter()
        {
            Transform sphereTransform = _collisionSphere.transform;
            return sphereTransform.TransformPoint(_collisionSphere.center);
        }

        private float GetCollisionSphereRadius()
        {
            Transform sphereTransform = _collisionSphere.transform;
            Vector3 lossyScale = sphereTransform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z));
            return _collisionSphere.radius * maxScale + collisionSkin + collisionRadiusPadding;
        }

        private void SolveBallCollision(float dt, int solverIteration)
        {
            if (!enableBallCollision || _collisionSphere == null) return;

            Vector3 center = _sweepEndCenter;
            float radius = GetCollisionSphereRadius();
            float searchRadius = radius + collisionSearchMargin;
            float searchRadiusSqr = searchRadius * searchRadius;
            float radiusSqr = radius * radius;

            int n = _data.particleCount;
            int directContacts = 0;
            for (int i = 0; i < n; i++)
            {
                if (_data.invMass[i] <= 0f) continue;

                Vector3 offset = _predicted[i] - center;
                float d2 = offset.sqrMagnitude;
                if (d2 > searchRadiusSqr || d2 >= radiusSqr) continue;

                if (d2 > 1e-10f)
                {
                    Vector3 oldPosition = _predicted[i];
                    Vector3 correction = center + offset * (radius / Mathf.Sqrt(d2)) - oldPosition;
                    _predicted[i] = oldPosition + correction;
                    AccumulateBallReaction(correction, dt);
                    SpreadCollisionCorrection(i, correction, dt);
                    ApplyBallImpactDrive(i, dt);
                }
                else
                {
                    Vector3 oldPosition = _predicted[i];
                    Vector3 correction = center + Vector3.up * radius - oldPosition;
                    _predicted[i] = oldPosition + correction;
                    AccumulateBallReaction(correction, dt);
                    SpreadCollisionCorrection(i, correction, dt);
                    ApplyBallImpactDrive(i, dt);
                }

                _lastCollisionCount++;
                directContacts++;
            }

            if (directContacts > 0)
            {
                ApplyPocketPressureField(center, radius, dt);
            }

            if (solverIteration < sweptCollisionPassesPerSubstep)
            {
                SolveSweptBallCollision(dt, radius);
            }
        }

        private void SolveSweptBallCollision(float dt, float baseRadius)
        {
            if (!enableSweptBallCollision) return;

            Vector3 sweep = _sweepEndCenter - _sweepStartCenter;
            if (sweep.sqrMagnitude <= 1e-8f) return;

            float radius = baseRadius + sweptCollisionSkin;
            float radiusSqr = radius * radius;
            int sweptContacts = 0;

            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_data.invMass[i] <= 0f) continue;

                Vector3 closest = ClosestPointOnSegment(_predicted[i], _sweepStartCenter, _sweepEndCenter);
                Vector3 offset = _predicted[i] - closest;
                float d2 = offset.sqrMagnitude;
                if (d2 >= radiusSqr) continue;

                Vector3 correction = BuildSweptCorrection(_predicted[i], closest, offset, d2, radius);
                if (correction.sqrMagnitude <= 1e-12f) continue;

                _predicted[i] += correction;
                AccumulateBallReaction(correction, dt, sweptCollisionReactionScale);
                SpreadCollisionCorrection(i, correction, dt);
                ApplyBallImpactDrive(i, dt);
                sweptContacts++;
            }

            if (enableSweptEdgeCollision)
            {
                sweptContacts += SolveSweptEdgeCollision(dt, radius, radiusSqr);
            }

            if (sweptContacts > 0)
            {
                ApplyPocketPressureField(_sweepEndCenter, baseRadius, dt);
            }
        }

        private int SolveSweptEdgeCollision(float dt, float radius, float radiusSqr)
        {
            var edges = _data.edges;
            int contactCount = 0;
            int maxContacts = Mathf.Max(1, maxSweptEdgeContactsPerPass);
            Vector3 min = Vector3.Min(_sweepStartCenter, _sweepEndCenter) - Vector3.one * radius;
            Vector3 max = Vector3.Max(_sweepStartCenter, _sweepEndCenter) + Vector3.one * radius;

            for (int i = 0; i < edges.Length; i++)
            {
                GoalNetMesh.Edge edge = edges[i];
                int a = edge.a;
                int b = edge.b;
                float wa = _data.invMass[a];
                float wb = _data.invMass[b];
                if (wa + wb <= 0f) continue;

                Vector3 pa = _predicted[a];
                Vector3 pb = _predicted[b];
                if (SegmentOutsideBounds(pa, pb, min, max)) continue;

                ClosestSegmentPoints(_sweepStartCenter, _sweepEndCenter, pa, pb, out Vector3 sweepPoint, out Vector3 edgePoint, out float edgeT);

                Vector3 offset = edgePoint - sweepPoint;
                float d2 = offset.sqrMagnitude;
                if (d2 >= radiusSqr) continue;

                Vector3 correction = BuildSweptCorrection(edgePoint, sweepPoint, offset, d2, radius);
                if (correction.sqrMagnitude <= 1e-12f) continue;

                float edgeWa = 1f - edgeT;
                float edgeWb = edgeT;
                float denom = wa * edgeWa * edgeWa + wb * edgeWb * edgeWb;
                if (denom <= 1e-8f) continue;

                Vector3 corrA = correction * (wa * edgeWa / denom);
                Vector3 corrB = correction * (wb * edgeWb / denom);

                if (wa > 0f) _predicted[a] += corrA;
                if (wb > 0f) _predicted[b] += corrB;

                AccumulateBallReaction(correction, dt, sweptCollisionReactionScale);
                if (sweptEdgeInfluenceSpread)
                {
                    SpreadCollisionCorrection(a, corrA, dt);
                    SpreadCollisionCorrection(b, corrB, dt);
                }
                contactCount++;
                if (contactCount >= maxContacts) break;
            }

            return contactCount;
        }

        private static bool SegmentOutsideBounds(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
        {
            return (a.x < min.x && b.x < min.x) ||
                   (a.x > max.x && b.x > max.x) ||
                   (a.y < min.y && b.y < min.y) ||
                   (a.y > max.y && b.y > max.y) ||
                   (a.z < min.z && b.z < min.z) ||
                   (a.z > max.z && b.z > max.z);
        }

        private Vector3 BuildSweptCorrection(Vector3 point, Vector3 closestOnSweep, Vector3 offset, float distanceSqr, float radius)
        {
            Vector3 normal;
            if (distanceSqr > 1e-10f)
            {
                normal = offset / Mathf.Sqrt(distanceSqr);
            }
            else
            {
                Vector3 fallback = point - _sweepEndCenter;
                normal = fallback.sqrMagnitude > 1e-10f ? fallback.normalized : Vector3.up;
            }

            Vector3 target = closestOnSweep + normal * radius;
            Vector3 correction = target - point;
            float maxCorrection = sweptCollisionMaxCorrection;
            if (maxCorrection > 0f)
            {
                float magnitude = correction.magnitude;
                if (magnitude > maxCorrection)
                {
                    correction *= maxCorrection / magnitude;
                }
            }

            return correction;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abSqr = ab.sqrMagnitude;
            if (abSqr <= 1e-10f) return a;

            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / abSqr);
            return a + ab * t;
        }

        private static void ClosestSegmentPoints(
            Vector3 p1,
            Vector3 q1,
            Vector3 p2,
            Vector3 q2,
            out Vector3 c1,
            out Vector3 c2,
            out float t)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            float s;

            if (a <= 1e-10f && e <= 1e-10f)
            {
                s = 0f;
                t = 0f;
            }
            else if (a <= 1e-10f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-10f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;

                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        private void AccumulateBallReaction(Vector3 particleCorrection, float dt)
        {
            AccumulateBallReaction(particleCorrection, dt, 1f);
        }

        private void AccumulateBallReaction(Vector3 particleCorrection, float dt, float scale)
        {
            if (!enableBallReaction || _collisionBody == null || dt <= 0f) return;
            if (scale <= 0f) return;

            Vector3 particleImpulse = particleCorrection * (collisionParticleMass * scale / dt);
            _pendingBallImpulse -= particleImpulse;
            _pendingVelocityOpposingImpulse += particleImpulse.magnitude;
        }

        private void SpreadCollisionCorrection(int sourceParticle, Vector3 directCorrection, float dt)
        {
            if (!enableCollisionInfluenceSpread || directCorrection.sqrMagnitude <= 1e-12f) return;
            if (_data.particleNeighbors == null || _data.particleNeighbors.Length == 0) return;

            int rings = Mathf.Clamp(collisionSpreadRings, 0, 3);
            if (rings <= 0) return;

            float firstRingScale = Mathf.Clamp01(collisionSpreadStrength);
            if (firstRingScale <= 0f) return;

            if (_spreadVisitToken == int.MaxValue)
            {
                System.Array.Clear(_spreadVisit, 0, _spreadVisit.Length);
                _spreadVisitToken = 0;
            }
            _spreadVisitToken++;

            int currentCount = 1;
            _spreadFrontier[0] = sourceParticle;
            _spreadVisit[sourceParticle] = _spreadVisitToken;

            float ringScale = firstRingScale;
            float falloff = Mathf.Clamp01(collisionSpreadFalloff);

            for (int ring = 1; ring <= rings; ring++)
            {
                int nextCount = 0;
                for (int i = 0; i < currentCount; i++)
                {
                    int particle = _spreadFrontier[i];
                    int[] neighbors = _data.particleNeighbors[particle];

                    for (int n = 0; n < neighbors.Length; n++)
                    {
                        int neighbor = neighbors[n];
                        if (_spreadVisit[neighbor] == _spreadVisitToken) continue;

                        _spreadVisit[neighbor] = _spreadVisitToken;
                        _spreadNextFrontier[nextCount++] = neighbor;

                        if (_data.invMass[neighbor] <= 0f) continue;

                        Vector3 correction = directCorrection * ringScale;
                        _predicted[neighbor] += correction;
                        AccumulateBallReaction(correction, dt, collisionSpreadReactionScale);
                    }
                }

                if (nextCount == 0) return;

                int[] swap = _spreadFrontier;
                _spreadFrontier = _spreadNextFrontier;
                _spreadNextFrontier = swap;
                currentCount = nextCount;
                ringScale *= falloff;
                if (ringScale <= 1e-4f) return;
            }
        }

        private void ApplyBallImpactDrive(int sourceParticle, float dt)
        {
            if (!enableBallImpactDrive || _collisionBody == null || dt <= 0f) return;

            Vector3 velocity = _collisionBody.velocity;
            if (velocity.sqrMagnitude <= 1e-8f) return;

            float solverPasses = Mathf.Max(1, iterations);
            Vector3 drive = velocity * (dt * ballImpactDriveStrength / solverPasses);
            float maxStep = ballImpactDriveMaxStep;
            if (maxStep > 0f)
            {
                float magnitude = drive.magnitude;
                if (magnitude > maxStep)
                {
                    drive *= maxStep / magnitude;
                }
            }

            if (drive.sqrMagnitude <= 1e-12f) return;

            ApplyDrivenCorrection(sourceParticle, drive, dt, ballImpactDriveReactionScale);

            if (_data.particleNeighbors == null || _data.particleNeighbors.Length == 0) return;

            int rings = Mathf.Clamp(ballImpactDriveRings, 0, 3);
            if (rings <= 0) return;

            if (_spreadVisitToken == int.MaxValue)
            {
                System.Array.Clear(_spreadVisit, 0, _spreadVisit.Length);
                _spreadVisitToken = 0;
            }
            _spreadVisitToken++;

            int currentCount = 1;
            _spreadFrontier[0] = sourceParticle;
            _spreadVisit[sourceParticle] = _spreadVisitToken;

            float ringScale = Mathf.Clamp01(ballImpactDriveFalloff);

            for (int ring = 1; ring <= rings; ring++)
            {
                int nextCount = 0;
                for (int i = 0; i < currentCount; i++)
                {
                    int particle = _spreadFrontier[i];
                    int[] neighbors = _data.particleNeighbors[particle];

                    for (int n = 0; n < neighbors.Length; n++)
                    {
                        int neighbor = neighbors[n];
                        if (_spreadVisit[neighbor] == _spreadVisitToken) continue;

                        _spreadVisit[neighbor] = _spreadVisitToken;
                        _spreadNextFrontier[nextCount++] = neighbor;

                        ApplyDrivenCorrection(neighbor, drive * ringScale, dt, ballImpactDriveReactionScale);
                    }
                }

                if (nextCount == 0) return;

                int[] swap = _spreadFrontier;
                _spreadFrontier = _spreadNextFrontier;
                _spreadNextFrontier = swap;
                currentCount = nextCount;
                ringScale *= Mathf.Clamp01(ballImpactDriveFalloff);
                if (ringScale <= 1e-4f) return;
            }
        }

        private void ApplyDrivenCorrection(int particle, Vector3 correction, float dt, float reactionScale)
        {
            if (_data.invMass[particle] <= 0f) return;

            _predicted[particle] += correction;
            AccumulateBallReaction(correction, dt, reactionScale);
        }

        private void ApplyPocketPressureField(Vector3 center, float radius, float dt)
        {
            if (!enablePocketPressureField || _collisionBody == null || dt <= 0f) return;

            Vector3 velocity = _collisionBody.velocity;
            float speed = velocity.magnitude;
            if (speed <= 1e-5f) return;

            float extraRadius = pocketPressureRadius;
            if (extraRadius <= 0f) return;

            float influenceRadius = radius + extraRadius;
            float influenceRadiusSqr = influenceRadius * influenceRadius;
            float invExtraRadius = 1f / extraRadius;
            float falloffPower = Mathf.Max(0.25f, pocketPressureFalloffPower);

            float solverPasses = Mathf.Max(1, iterations);
            Vector3 baseDrive = velocity * (dt * pocketPressureStrength / solverPasses);
            float maxStep = pocketPressureMaxStep;
            if (maxStep > 0f)
            {
                float baseMagnitude = baseDrive.magnitude;
                if (baseMagnitude > maxStep)
                {
                    baseDrive *= maxStep / baseMagnitude;
                }
            }

            if (baseDrive.sqrMagnitude <= 1e-12f) return;

            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_data.invMass[i] <= 0f) continue;

                Vector3 offset = _predicted[i] - center;
                float d2 = offset.sqrMagnitude;
                if (d2 > influenceRadiusSqr) continue;

                float distance = Mathf.Sqrt(d2);
                float outsideSurface = Mathf.Max(0f, distance - radius);
                float t = 1f - Mathf.Clamp01(outsideSurface * invExtraRadius);
                float weight = Mathf.Pow(t, falloffPower);
                if (weight <= 1e-4f) continue;

                ApplyDrivenCorrection(i, baseDrive * weight, dt, pocketPressureReactionScale);
            }
        }

        private void SolveGroundCollision()
        {
            if (!enableGroundCollision) return;

            float floorY = GetGroundY() + groundSkin;
            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (_data.invMass[i] <= 0f) continue;

                Vector3 p = _predicted[i];
                if (p.y >= floorY) continue;

                p.y = floorY;
                _predicted[i] = p;
                _groundContacts[i] = true;
            }
        }

        private void SolveBottomSoftTethers(float alphaTilde)
        {
            if (!enableBottomSoftTether || _bottomTetherCount <= 0) return;

            float stiffness = 1f / (1f + Mathf.Max(0f, alphaTilde));
            float maxDistance = bottomTetherMaxDistance;
            float maxLift = bottomMaxLiftHeight;
            float floorY = GetGroundY() + groundSkin;

            int n = _data.particleCount;
            for (int i = 0; i < n; i++)
            {
                if (!_bottomTetherParticles[i] || _data.invMass[i] <= 0f) continue;

                Vector3 target = _bottomRestWorld[i];
                if (enableGroundCollision && target.y < floorY)
                {
                    target.y = floorY;
                }

                Vector3 p = _predicted[i];
                Vector3 toTarget = target - p;
                if (toTarget.sqrMagnitude > 1e-12f)
                {
                    _predicted[i] = p + toTarget * stiffness;
                    p = _predicted[i];
                }

                if (maxDistance > 0f)
                {
                    Vector3 fromTarget = p - target;
                    float dist = fromTarget.magnitude;
                    if (dist > maxDistance && dist > 1e-6f)
                    {
                        _predicted[i] = target + fromTarget * (maxDistance / dist);
                        p = _predicted[i];
                    }
                }

                if (maxLift > 0f)
                {
                    float liftLimit = Mathf.Max(target.y, _bottomRestWorld[i].y) + maxLift;
                    if (p.y > liftLimit)
                    {
                        p.y = Mathf.Lerp(p.y, liftLimit, stiffness);
                        _predicted[i] = p;
                    }
                }
            }
        }

        private void ApplyGroundVelocityResponse(int particle)
        {
            if (_groundContacts == null || !_groundContacts[particle]) return;

            Vector3 v = _velocities[particle];
            if (v.y < 0f)
            {
                v.y = -v.y * groundBounce;
            }

            float friction = groundFriction;
            if (enableBottomSoftTether && _bottomTetherParticles != null && _bottomTetherParticles[particle])
            {
                friction = Mathf.Max(friction, bottomGroundFriction);
            }

            float tangentKeep = 1f - friction;
            v.x *= tangentKeep;
            v.z *= tangentKeep;
            _velocities[particle] = v;
        }

        private float GetGroundY()
        {
            return groundTransform != null ? groundTransform.position.y : groundHeight;
        }

        private void ApplyBallReactionImpulse()
        {
            if (!enableBallReaction || _collisionBody == null || _collisionBody.isKinematic) return;

            Vector3 impulse = enableSoftCatchReaction
                ? BuildSoftCatchImpulse()
                : _pendingBallImpulse * reactionImpulseScale;

            Vector3 velocity = _collisionBody.velocity;
            if (!enableSoftCatchReaction && _pendingVelocityOpposingImpulse > 0f && velocity.sqrMagnitude > 1e-8f)
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

        private Vector3 BuildSoftCatchImpulse()
        {
            Vector3 velocity = _collisionBody.velocity;
            Vector3 impulse = _pendingBallImpulse * reactionImpulseScale * elasticReactionScale;

            float speed = velocity.magnitude;
            if (_pendingVelocityOpposingImpulse > 0f && speed > 1e-5f)
            {
                float requestedDamping = _pendingVelocityOpposingImpulse * reactionImpulseScale * velocityOpposingImpulseScale;
                float velocityDampingLimit = _collisionBody.mass * speed * Mathf.Clamp01(catchVelocityDamping);
                float dampingImpulse = Mathf.Min(requestedDamping, velocityDampingLimit);
                impulse += -velocity.normalized * dampingImpulse;
            }

            impulse = LimitContactRebound(velocity, impulse);
            return impulse;
        }

        private Vector3 LimitContactRebound(Vector3 velocity, Vector3 impulse)
        {
            float maxRebound = maxContactReboundSpeed;
            if (maxRebound <= 0f || impulse.sqrMagnitude <= 1e-10f) return impulse;

            Vector3 reactionDirection = impulse.normalized;
            float mass = Mathf.Max(_collisionBody.mass, 1e-5f);
            float currentAlongReaction = Vector3.Dot(velocity, reactionDirection);
            float impulseAlongReaction = Vector3.Dot(impulse, reactionDirection);
            float predictedAlongReaction = currentAlongReaction + impulseAlongReaction / mass;

            if (predictedAlongReaction <= maxRebound) return impulse;

            float allowedImpulseAlongReaction = Mathf.Max(0f, (maxRebound - currentAlongReaction) * mass);
            Vector3 along = reactionDirection * impulseAlongReaction;
            Vector3 tangent = impulse - along;
            return tangent + reactionDirection * allowedImpulseAlongReaction;
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
            RefreshBottomTetherRestWorld();
            WriteBackToMesh();
        }

        [ContextMenu("Rebuild Bottom Soft Tethers")]
        public void RebuildBottomSoftTethers()
        {
            if (!_initialized) return;
            InitializeBottomTethers();
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
            enableSweptBallCollision = true;
            enableSweptEdgeCollision = true;
            sweptCollisionSkin = 0.02f;
            sweptCollisionMaxCorrection = 0.12f;
            sweptCollisionReactionScale = 0.35f;
            sweptCollisionPassesPerSubstep = 2;
            maxSweptEdgeContactsPerPass = 24;
            sweptEdgeInfluenceSpread = false;
            enableCollisionInfluenceSpread = true;
            collisionSpreadStrength = 0.35f;
            collisionSpreadRings = 2;
            collisionSpreadFalloff = 0.5f;
            collisionSpreadReactionScale = 0.2f;
            enableBallImpactDrive = true;
            ballImpactDriveStrength = 0.45f;
            ballImpactDriveMaxStep = 0.035f;
            ballImpactDriveRings = 2;
            ballImpactDriveFalloff = 0.55f;
            ballImpactDriveReactionScale = 0.05f;
            enablePocketPressureField = true;
            pocketPressureRadius = 0.55f;
            pocketPressureStrength = 0.9f;
            pocketPressureMaxStep = 0.08f;
            pocketPressureFalloffPower = 1f;
            pocketPressureReactionScale = 0.04f;
            enableBallReaction = true;
            collisionParticleMass = 0.43f;
            reactionImpulseScale = 0.5f;
            maxReactionImpulse = 10f;
            velocityOpposingImpulseScale = 1f;
            enableSoftCatchReaction = true;
            elasticReactionScale = 0.813f;
            catchVelocityDamping = 0.573f;
            maxContactReboundSpeed = 1.5f;
            enableGroundCollision = true;
            groundHeight = 0f;
            groundSkin = 0.005f;
            groundFriction = 0.35f;
            groundBounce = 0f;
            enableBottomSoftTether = true;
            bottomDetectHeight = 0.18f;
            bottomTetherCompliance = 0.002f;
            bottomTetherMaxDistance = 0.75f;
            bottomMaxLiftHeight = 0.45f;
            bottomGroundFriction = 0.75f;
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
            enableSweptBallCollision = true;
            enableSweptEdgeCollision = true;
            sweptCollisionSkin = 0.025f;
            sweptCollisionMaxCorrection = 0.16f;
            sweptCollisionReactionScale = 0.4f;
            sweptCollisionPassesPerSubstep = 2;
            maxSweptEdgeContactsPerPass = 40;
            sweptEdgeInfluenceSpread = false;
            enableCollisionInfluenceSpread = true;
            collisionSpreadStrength = 0.45f;
            collisionSpreadRings = 2;
            collisionSpreadFalloff = 0.55f;
            collisionSpreadReactionScale = 0.25f;
            enableBallImpactDrive = true;
            ballImpactDriveStrength = 0.55f;
            ballImpactDriveMaxStep = 0.05f;
            ballImpactDriveRings = 2;
            ballImpactDriveFalloff = 0.6f;
            ballImpactDriveReactionScale = 0.08f;
            enablePocketPressureField = true;
            pocketPressureRadius = 0.65f;
            pocketPressureStrength = 1.1f;
            pocketPressureMaxStep = 0.1f;
            pocketPressureFalloffPower = 0.9f;
            pocketPressureReactionScale = 0.05f;
            enableBallReaction = true;
            collisionParticleMass = 0.5f;
            reactionImpulseScale = 0.5f;
            maxReactionImpulse = 10f;
            velocityOpposingImpulseScale = 1.25f;
            enableSoftCatchReaction = true;
            elasticReactionScale = 0.813f;
            catchVelocityDamping = 0.573f;
            maxContactReboundSpeed = 1.5f;
            enableGroundCollision = true;
            groundHeight = 0f;
            groundSkin = 0.005f;
            groundFriction = 0.45f;
            groundBounce = 0f;
            enableBottomSoftTether = true;
            bottomDetectHeight = 0.2f;
            bottomTetherCompliance = 0.0015f;
            bottomTetherMaxDistance = 0.85f;
            bottomMaxLiftHeight = 0.5f;
            bottomGroundFriction = 0.8f;
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
