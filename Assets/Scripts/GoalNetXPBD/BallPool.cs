using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GoalNetXPBD
{
    /// <summary>
    /// Simple fixed-size object pool for soccer balls.
    /// Pre-instantiates a pool of inactive balls; Get() activates one and Return() resets+deactivates it.
    /// Each Get() also schedules an automatic return after autoReturnSeconds.
    /// Designed for the single-ball / few-ball test scenario described in CLAUDE.md.
    /// </summary>
    [DisallowMultipleComponent]
    public class BallPool : MonoBehaviour
    {
        [Header("Pool Setup")]
        [Tooltip("Prefab to instantiate. Must contain a Rigidbody and a Collider (SphereCollider recommended).")]
        public GameObject ballPrefab;

        [Tooltip("Number of balls created up-front. The pool size is fixed; if every ball is in flight, the oldest is recycled.")]
        [Min(1)] public int poolSize = 8;

        [Tooltip("How many seconds after launch before a ball is automatically returned to the pool.")]
        [Min(0.1f)] public float autoReturnSeconds = 5f;

        [Tooltip("Optional parent transform for inactive balls. If null, the pool itself is used.")]
        public Transform inactiveParent;

        // Internal data
        private readonly Queue<GameObject> _available = new Queue<GameObject>();
        private readonly List<GameObject> _all = new List<GameObject>();
        private readonly Dictionary<GameObject, Coroutine> _autoReturnRoutines = new Dictionary<GameObject, Coroutine>();

        private bool _initialized;

        private void Awake()
        {
            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            if (ballPrefab == null)
            {
                Debug.LogWarning("[BallPool] ballPrefab is not assigned. Pool will be empty until you set it.", this);
                _initialized = true;
                return;
            }

            Transform parent = inactiveParent != null ? inactiveParent : transform;
            for (int i = 0; i < poolSize; i++)
            {
                GameObject go = Instantiate(ballPrefab, parent);
                go.name = $"{ballPrefab.name}_Pooled_{i:00}";
                go.SetActive(false);
                _all.Add(go);
                _available.Enqueue(go);
            }
            _initialized = true;
        }

        /// <summary>
        /// Take a ball from the pool, place it at the given pose with given linear velocity, activate it,
        /// and schedule an automatic return.
        /// If the pool is empty, the oldest in-flight ball is forcibly recycled to make room.
        /// </summary>
        public GameObject Get(Vector3 position, Quaternion rotation, Vector3 linearVelocity)
        {
            EnsureInitialized();
            if (_all.Count == 0) return null;

            GameObject go = null;
            if (_available.Count > 0)
            {
                go = _available.Dequeue();
            }
            else
            {
                // Pool exhausted: recycle the oldest active ball.
                // We pick the first one we find that is currently active.
                for (int i = 0; i < _all.Count; i++)
                {
                    if (_all[i] != null && _all[i].activeSelf)
                    {
                        ForceReturn(_all[i]);
                        go = _all[i];
                        // ForceReturn put it back into _available, dequeue again.
                        if (_available.Count > 0) _available.Dequeue();
                        break;
                    }
                }
            }
            if (go == null) return null;

            // Reset transform
            go.transform.SetParent(null, worldPositionStays: false);
            go.transform.position = position;
            go.transform.rotation = rotation;
            go.transform.localScale = ballPrefab != null ? ballPrefab.transform.localScale : Vector3.one;

            // Reset and apply velocity
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                go.SetActive(true);
                rb.velocity = linearVelocity;
                rb.WakeUp();
            }
            else
            {
                go.SetActive(true);
                Debug.LogWarning("[BallPool] Pooled ball has no Rigidbody — velocity will not be applied.", go);
            }

            // Schedule auto-return
            if (_autoReturnRoutines.TryGetValue(go, out Coroutine running) && running != null)
            {
                StopCoroutine(running);
            }
            _autoReturnRoutines[go] = StartCoroutine(AutoReturnRoutine(go, autoReturnSeconds));

            return go;
        }

        /// <summary>
        /// Manually return a ball to the pool right away.
        /// </summary>
        public void Return(GameObject go)
        {
            if (go == null) return;
            ForceReturn(go);
        }

        /// <summary>
        /// Recall every active ball at once (used by the README "press R to reset" workflow).
        /// </summary>
        public void ReturnAll()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i] != null && _all[i].activeSelf)
                {
                    ForceReturn(_all[i]);
                }
            }
        }

        private void ForceReturn(GameObject go)
        {
            if (_autoReturnRoutines.TryGetValue(go, out Coroutine running) && running != null)
            {
                StopCoroutine(running);
                _autoReturnRoutines[go] = null;
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.Sleep();
            }

            Transform parent = inactiveParent != null ? inactiveParent : transform;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.SetActive(false);

            // Avoid duplicates in the queue.
            if (!_available.Contains(go))
            {
                _available.Enqueue(go);
            }
        }

        private IEnumerator AutoReturnRoutine(GameObject go, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (go != null && go.activeSelf)
            {
                ForceReturn(go);
            }
        }

        // Inspector helper.
        public int AvailableCount => _available.Count;
        public int TotalCount => _all.Count;
    }
}
