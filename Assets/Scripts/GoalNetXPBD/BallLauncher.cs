using UnityEngine;

namespace GoalNetXPBD
{
    /// <summary>
    /// Spawns a ball from a BallPool, places it at <see cref="spawnPoint"/>,
    /// and shoots it toward <see cref="aimPoint"/> at <see cref="launchSpeed"/>.
    ///
    /// Drive this from a uGUI Button OnClick -> BallLauncher.LaunchBall(),
    /// or from the keyboard (Space by default) for quick iteration.
    /// </summary>
    [DisallowMultipleComponent]
    public class BallLauncher : MonoBehaviour
    {
        [Header("Pool")]
        [Tooltip("Pool that supplies the ball instances. If left empty, the launcher will look for one on the same GameObject.")]
        public BallPool pool;

        [Header("Trajectory")]
        [Tooltip("Where the ball appears when launched.")]
        public Transform spawnPoint;

        [Tooltip("World-space target the ball is aimed at. Direction = (aimPoint - spawnPoint).normalized.")]
        public Transform aimPoint;

        [Tooltip("Initial speed of the ball, in meters per second.")]
        [Min(0f)] public float launchSpeed = 25f;

        [Tooltip("Optional initial spin (local angular velocity) applied to the ball Rigidbody.")]
        public Vector3 initialAngularVelocity = Vector3.zero;

        [Header("Input (optional)")]
        [Tooltip("If true, pressing the launchKey will also fire a ball — handy during testing.")]
        public bool enableKeyboardLaunch = true;
        public KeyCode launchKey = KeyCode.Space;

        [Tooltip("If true, pressing the resetKey returns every active ball to the pool.")]
        public bool enableKeyboardReset = true;
        public KeyCode resetKey = KeyCode.R;

        private void Awake()
        {
            if (pool == null) pool = GetComponent<BallPool>();
        }

        private void Update()
        {
            if (enableKeyboardLaunch && Input.GetKeyDown(launchKey))
            {
                LaunchBall();
            }
            if (enableKeyboardReset && Input.GetKeyDown(resetKey) && pool != null)
            {
                pool.ReturnAll();
            }
        }

        /// <summary>
        /// Public entry point. Wire this to a uGUI Button's OnClick event.
        /// </summary>
        public void LaunchBall()
        {
            if (!ValidateRefs()) return;

            Vector3 origin = spawnPoint.position;
            Vector3 toTarget = aimPoint.position - origin;
            if (toTarget.sqrMagnitude < 1e-6f)
            {
                Debug.LogWarning("[BallLauncher] aimPoint coincides with spawnPoint; using spawnPoint.forward instead.", this);
                toTarget = spawnPoint.forward;
            }
            Vector3 dir = toTarget.normalized;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            Vector3 vel = dir * launchSpeed;

            GameObject ball = pool.Get(origin, rot, vel);
            if (ball == null) return;

            // Optional spin.
            if (initialAngularVelocity != Vector3.zero)
            {
                Rigidbody rb = ball.GetComponent<Rigidbody>();
                if (rb != null) rb.angularVelocity = initialAngularVelocity;
            }
        }

        private bool ValidateRefs()
        {
            if (pool == null)
            {
                Debug.LogError("[BallLauncher] No BallPool assigned (and none on this GameObject). Cannot launch.", this);
                return false;
            }
            if (spawnPoint == null)
            {
                Debug.LogError("[BallLauncher] spawnPoint is not assigned. Drag a Transform into the field.", this);
                return false;
            }
            if (aimPoint == null)
            {
                Debug.LogError("[BallLauncher] aimPoint is not assigned. Drag a Transform into the field.", this);
                return false;
            }
            if (pool.ballPrefab == null)
            {
                Debug.LogError("[BallLauncher] BallPool has no ballPrefab assigned.", pool);
                return false;
            }
            return true;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (spawnPoint != null && aimPoint != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(spawnPoint.position, 0.08f);
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(aimPoint.position, 0.08f);
                Gizmos.color = Color.green;
                Gizmos.DrawLine(spawnPoint.position, aimPoint.position);
            }
        }
#endif
    }
}
