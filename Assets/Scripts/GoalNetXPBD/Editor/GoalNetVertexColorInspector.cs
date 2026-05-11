using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GoalNetXPBD.EditorTools
{
    /// <summary>
    /// Editor window that visualizes vertex-color information on the goal-net mesh.
    /// Helps confirm that the "black = pinned" convention actually holds for SM_GoalNet.fbx
    /// before we wire it into the XPBD solver.
    ///
    /// Workflow:
    ///   1. Select the GameObject that holds the goal-net MeshFilter / SkinnedMeshRenderer.
    ///   2. Open Tools/Cloth_AI/Inspect Goal Net Vertex Colors.
    ///   3. Read the histogram, tweak the black/white thresholds, and look at the Scene
    ///      gizmos: red = pinned, green = free-but-marked-white, blue = other / unmarked.
    ///
    /// If the mesh has NO vertex colors at all, the window reports an error and the
    /// XPBD pipeline will refuse to run until the artist authors them.
    /// </summary>
    public class GoalNetVertexColorInspector : EditorWindow
    {
        // --- State (per-window) ---
        private GameObject _target;
        private Mesh _mesh;
        private Color[] _colors;
        private int _vertexCount;

        private float _blackThreshold = 0.1f;   // RGB <= this => "black" => pinned
        private float _whiteThreshold = 0.9f;   // RGB >= this => "white" => free
        private bool _drawGizmos = true;
        private float _gizmoSize = 0.02f;
        private bool _showOtherCount = true;

        // Quick stats
        private int _blackCount;
        private int _whiteCount;
        private int _otherCount;
        private bool _hasVertexColors;
        private string _meshSource = "(none)";

        // Cached transform reference (mesh-space → world-space for gizmos).
        private Transform _meshTransform;

        // Static reference so SceneGUI delegate can find the active window.
        private static GoalNetVertexColorInspector _activeWindow;

        [MenuItem("Tools/Cloth_AI/Inspect Goal Net Vertex Colors")]
        public static void Open()
        {
            var win = GetWindow<GoalNetVertexColorInspector>("Net Vertex Colors");
            win.minSize = new Vector2(360f, 360f);
            win.RefreshFromSelection();
            win.Show();
        }

        private void OnEnable()
        {
            _activeWindow = this;
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += OnSelectionChanged;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= OnSelectionChanged;
            if (_activeWindow == this) _activeWindow = null;
        }

        private void OnSelectionChanged()
        {
            RefreshFromSelection();
            Repaint();
            SceneView.RepaintAll();
        }

        private void RefreshFromSelection()
        {
            _target = Selection.activeGameObject;
            _mesh = null;
            _meshTransform = null;
            _colors = null;
            _vertexCount = 0;
            _meshSource = "(none)";
            _hasVertexColors = false;
            _blackCount = _whiteCount = _otherCount = 0;

            if (_target == null) return;

            MeshFilter mf = _target.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                _mesh = mf.sharedMesh;
                _meshTransform = mf.transform;
                _meshSource = $"MeshFilter on {mf.gameObject.name}";
            }
            else
            {
                SkinnedMeshRenderer smr = _target.GetComponentInChildren<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                {
                    _mesh = smr.sharedMesh;
                    _meshTransform = smr.transform;
                    _meshSource = $"SkinnedMeshRenderer on {smr.gameObject.name}";
                }
            }

            if (_mesh == null) return;

            _vertexCount = _mesh.vertexCount;
            _colors = _mesh.colors; // returns Color[] — empty array if mesh has no vertex colors
            _hasVertexColors = _colors != null && _colors.Length == _vertexCount;
            RecountCategories();
        }

        private void RecountCategories()
        {
            _blackCount = _whiteCount = _otherCount = 0;
            if (!_hasVertexColors) return;

            for (int i = 0; i < _colors.Length; i++)
            {
                if (IsBlack(_colors[i])) _blackCount++;
                else if (IsWhite(_colors[i])) _whiteCount++;
                else _otherCount++;
            }
        }

        private bool IsBlack(Color c) =>
            c.r <= _blackThreshold && c.g <= _blackThreshold && c.b <= _blackThreshold;

        private bool IsWhite(Color c) =>
            c.r >= _whiteThreshold && c.g >= _whiteThreshold && c.b >= _whiteThreshold;

        // ----------------- Editor Window UI -----------------

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Goal Net Vertex Color Inspector", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select the GameObject that holds the goal-net mesh.\n" +
                "Black vertices (RGB <= threshold) are the pin candidates the XPBD solver will use.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.ObjectField("Selected", _target, typeof(GameObject), true);
            EditorGUILayout.LabelField("Mesh source", _meshSource);

            if (_mesh == null)
            {
                EditorGUILayout.HelpBox(
                    "No mesh found on the current selection. Select a GameObject with a MeshFilter or SkinnedMeshRenderer.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("Vertex count", _vertexCount.ToString());

            if (!_hasVertexColors)
            {
                EditorGUILayout.HelpBox(
                    "This mesh has NO vertex colors. The XPBD solver requires vertex colors " +
                    "to identify pin points (black = pinned). Ask the artist to author vertex " +
                    "colors on the front-facing edges of the goal frame, then re-import the FBX.",
                    MessageType.Error);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Thresholds", EditorStyles.boldLabel);
            float newBlack = EditorGUILayout.Slider(
                new GUIContent("Black threshold", "RGB <= this counts as pinned (black)."),
                _blackThreshold, 0f, 0.5f);
            float newWhite = EditorGUILayout.Slider(
                new GUIContent("White threshold", "RGB >= this counts as 'unambiguously free'."),
                _whiteThreshold, 0.5f, 1f);
            if (!Mathf.Approximately(newBlack, _blackThreshold) ||
                !Mathf.Approximately(newWhite, _whiteThreshold))
            {
                _blackThreshold = newBlack;
                _whiteThreshold = newWhite;
                RecountCategories();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Counts", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Pinned (black) : {_blackCount}");
            EditorGUILayout.LabelField($"Free   (white) : {_whiteCount}");
            EditorGUILayout.LabelField($"Other          : {_otherCount}");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Gizmo display", EditorStyles.boldLabel);
            _drawGizmos = EditorGUILayout.Toggle("Draw in Scene", _drawGizmos);
            _gizmoSize = EditorGUILayout.Slider("Gizmo radius", _gizmoSize, 0.005f, 0.1f);
            _showOtherCount = EditorGUILayout.Toggle("Show 'other' verts", _showOtherCount);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh"))
                {
                    RefreshFromSelection();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Print Color Histogram"))
                {
                    PrintHistogram();
                }
                if (GUILayout.Button("Frame Pinned"))
                {
                    FramePinned();
                }
            }

            if (_blackCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "No vertices are classified as 'black' at the current threshold. " +
                    "Either raise the threshold a bit, or ask the artist to paint pin points.",
                    MessageType.Warning);
            }

            // Force scene repaint while window is focused so threshold edits are immediate.
            SceneView.RepaintAll();
        }

        private void PrintHistogram()
        {
            if (!_hasVertexColors) return;
            // Bucket by quantized color (1 decimal).
            var bins = new Dictionary<Color32, int>();
            for (int i = 0; i < _colors.Length; i++)
            {
                Color c = _colors[i];
                Color32 key = new Color32(
                    (byte)(Mathf.RoundToInt(c.r * 10f) * 25),
                    (byte)(Mathf.RoundToInt(c.g * 10f) * 25),
                    (byte)(Mathf.RoundToInt(c.b * 10f) * 25),
                    (byte)255);
                if (!bins.ContainsKey(key)) bins[key] = 0;
                bins[key]++;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Cloth_AI] Vertex color histogram for '{_mesh.name}' ({_vertexCount} verts, {bins.Count} buckets):");
            foreach (var kv in bins)
            {
                sb.AppendLine($"  RGB({kv.Key.r/255f:F1}, {kv.Key.g/255f:F1}, {kv.Key.b/255f:F1}) -> {kv.Value}");
            }
            Debug.Log(sb.ToString());
        }

        private void FramePinned()
        {
            if (!_hasVertexColors || _meshTransform == null || _blackCount == 0) return;
            Vector3[] verts = _mesh.vertices;
            Bounds b = new Bounds();
            bool init = false;
            for (int i = 0; i < verts.Length; i++)
            {
                if (!IsBlack(_colors[i])) continue;
                Vector3 wp = _meshTransform.TransformPoint(verts[i]);
                if (!init) { b = new Bounds(wp, Vector3.zero); init = true; }
                else b.Encapsulate(wp);
            }
            if (init && SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.Frame(b, false);
            }
        }

        // ----------------- Scene gizmo -----------------

        private static void OnSceneGUI(SceneView sv)
        {
            var w = _activeWindow;
            if (w == null || !w._drawGizmos) return;
            if (w._mesh == null || w._meshTransform == null) return;
            if (!w._hasVertexColors) return;

            Vector3[] verts = w._mesh.vertices;
            Color[] cols = w._colors;
            float r = w._gizmoSize;

            for (int i = 0; i < verts.Length; i++)
            {
                Color c = cols[i];
                bool isBlack = c.r <= w._blackThreshold && c.g <= w._blackThreshold && c.b <= w._blackThreshold;
                bool isWhite = c.r >= w._whiteThreshold && c.g >= w._whiteThreshold && c.b >= w._whiteThreshold;

                if (isBlack)        Handles.color = Color.red;
                else if (isWhite)   Handles.color = Color.green;
                else if (w._showOtherCount) Handles.color = new Color(0.3f, 0.5f, 1f, 1f);
                else continue;

                Vector3 wp = w._meshTransform.TransformPoint(verts[i]);
                Handles.SphereHandleCap(0, wp, Quaternion.identity, r * 2f, EventType.Repaint);
            }
        }
    }
}
